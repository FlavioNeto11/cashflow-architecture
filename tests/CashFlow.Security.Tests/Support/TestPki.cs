using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CashFlow.Hosting.Tls;

namespace CashFlow.Security.Tests.Support;

/// <summary>
/// PKI de teste gerada em memória (equivalente ao infra/pki/make-certs.sh, mas sem openssl): CA local, certificados de
/// servidor das APIs (SAN localhost/127.0.0.1), certificado de cliente do gateway (SAN gateway.internal, EKU clientAuth)
/// e os negativos de T-17: outra CA, expirado, SAN errado, EKU errado e thumbprint bloqueado. Gravada em PEM num diretório
/// temporário porque os hosts leem arquivos (Hosting:Tls:*, Gateway:Upstream:*).
/// </summary>
public sealed class TestPki : IDisposable
{
    public const string GatewayName = "gateway.internal";
    private const string ServerAuth = "1.3.6.1.5.5.7.3.1";
    private const string ClientAuth = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _foreignCa;

    public TestPki()
    {
        Directory = Path.Combine(Path.GetTempPath(), "cashflow-security-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);

        _ca = CreateCa("CashFlow Test CA");
        _foreignCa = CreateCa("Outra CA (negativo)");
        CaPath = WritePem("ca.crt", _ca.ExportCertificatePem());
        ForeignCaPath = WritePem("foreign-ca.crt", _foreignCa.ExportCertificatePem());

        var now = DateTimeOffset.UtcNow;
        TransactionsServer = Leaf("transactions-api", _ca, ServerAuth, ["localhost"], now.AddHours(-1), now.AddDays(30), withLoopbackIp: true);
        ConsolidationServer = Leaf("consolidation-api", _ca, ServerAuth, ["localhost"], now.AddHours(-1), now.AddDays(30), withLoopbackIp: true);
        GatewayClient = Leaf("gateway-client", _ca, ClientAuth, [GatewayName], now.AddHours(-1), now.AddDays(30));
        ForeignClient = Leaf("neg-foreign-ca", _foreignCa, ClientAuth, [GatewayName], now.AddHours(-1), now.AddDays(30));
        ExpiredClient = Leaf("neg-expired", _ca, ClientAuth, [GatewayName], now.AddDays(-30), now.AddDays(-1));
        WrongSanClient = Leaf("neg-wrong-san", _ca, ClientAuth, ["intruso.internal"], now.AddHours(-1), now.AddDays(30));
        WrongEkuClient = Leaf("neg-wrong-eku", _ca, ServerAuth, [GatewayName], now.AddHours(-1), now.AddDays(30));
        BlockedClient = Leaf("neg-blocked", _ca, ClientAuth, [GatewayName], now.AddHours(-1), now.AddDays(30));

        BlocklistPath = Path.Combine(Directory, "blocked-thumbprints.txt");
        File.WriteAllText(BlocklistPath, $"# bloqueado pelo teste\n{BlockedClient.Certificate.Thumbprint}\n");
    }

    public string Directory { get; }

    public string CaPath { get; }

    public string ForeignCaPath { get; }

    public string BlocklistPath { get; }

    public LeafCertificate TransactionsServer { get; }

    public LeafCertificate ConsolidationServer { get; }

    public LeafCertificate GatewayClient { get; }

    public LeafCertificate ForeignClient { get; }

    public LeafCertificate ExpiredClient { get; }

    public LeafCertificate WrongSanClient { get; }

    public LeafCertificate WrongEkuClient { get; }

    public LeafCertificate BlockedClient { get; }

    public X509Certificate2Collection TrustedRoots => [X509CertificateLoader.LoadCertificate(_ca.RawData)];

    /// <summary>
    /// HttpClient que fala TLS com uma API: confia SOMENTE na CA de teste (cadeia; nunca "aceitar qualquer certificado") e
    /// apresenta, se dado, um certificado de cliente — exatamente o que o gateway faz.
    /// </summary>
    public HttpClient CreateTlsClient(Uri baseAddress, X509Certificate2? clientCertificate)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromSeconds(1) };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => IsIssuedByTestCa(certificate);
        if (clientCertificate is not null)
        {
            handler.SslOptions.ClientCertificates = [clientCertificate];
            handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate;
        }

        return new HttpClient(handler, disposeHandler: true) { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public void Dispose()
    {
        _ca.Dispose();
        _foreignCa.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // diretório temporário; o SO limpa
        }
    }

    private bool IsIssuedByTestCa(X509Certificate? certificate)
    {
        if (certificate is null)
        {
            return false;
        }

        using var leaf = new X509Certificate2(certificate);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(TrustedRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
    }

    private static X509Certificate2 CreateCa(string commonName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var now = DateTimeOffset.UtcNow;
        using var ca = request.CreateSelfSigned(now.AddDays(-60), now.AddYears(2)); // antes do notBefore do negativo "expirado"
        return CertificateFiles.WithUsableKey(ca);
    }

    private LeafCertificate Leaf(
        string name, X509Certificate2 issuer, string eku, string[] dnsNames, DateTimeOffset notBefore, DateTimeOffset notAfter, bool withLoopbackIp = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsNames[0]}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dns in dnsNames)
        {
            san.AddDnsName(dns);
        }

        if (withLoopbackIp)
        {
            san.AddIpAddress(IPAddress.Loopback);
        }

        request.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(issuer, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        var certificate = CertificateFiles.WithUsableKey(withKey);

        return new LeafCertificate(
            certificate,
            WritePem($"{name}.crt", issued.ExportCertificatePem()),
            WritePem($"{name}.key", key.ExportPkcs8PrivateKeyPem()));
    }

    private string WritePem(string fileName, string pem)
    {
        var path = Path.Combine(Directory, fileName);
        File.WriteAllText(path, pem);
        return path;
    }
}

/// <summary>Certificado com chave privada utilizável em handshake, mais os caminhos PEM que os hosts leem.</summary>
public sealed record LeafCertificate(X509Certificate2 Certificate, string CertificatePath, string KeyPath);
