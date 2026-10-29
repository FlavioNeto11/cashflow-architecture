using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using CashFlow.Hosting.Security;
using CashFlow.Hosting.Tls;
using Yarp.ReverseProxy.Forwarder;

namespace CashFlow.Gateway;

/// <summary>
/// HttpClient do YARP para as APIs internas (security.md 6, lado cliente do mTLS): apresenta o certificado de cliente do
/// gateway e aceita o certificado de servidor SOMENTE se a cadeia fecha na CA local (CustomRootTrust) e o SAN bate com
/// o host do destino. Nunca DangerousAcceptAnyServerCertificateValidator. Recusas contam em
/// mtls_handshake_failed_total{side=client,reason}. Com destinos http (perfil dev) não toca no handler.
/// </summary>
internal sealed class UpstreamHttpClientFactory : ForwarderHttpClientFactory
{
    internal static readonly TimeSpan PooledConnectionLifetime = TimeSpan.FromMinutes(2);

    private readonly UpstreamSettings _settings;
    private readonly SecurityMetrics _metrics;
    private readonly ILogger<UpstreamHttpClientFactory> _logger;
    private readonly X509Certificate2? _clientCertificate;
    private readonly X509Certificate2Collection? _serverCa;

    public UpstreamHttpClientFactory(UpstreamSettings settings, SecurityMetrics metrics, ILogger<UpstreamHttpClientFactory> logger)
    {
        _settings = settings;
        _metrics = metrics;
        _logger = logger;
        if (settings.MutualTlsEnabled)
        {
            _clientCertificate = CertificateFiles.LoadWithKey(settings.ClientCertificate!, settings.ClientKey!);
            _serverCa = CertificateFiles.LoadCertificates(settings.ServerCa!);
            logger.LogInformation(
                "mTLS para as APIs: certificado de cliente {Subject} (expira {NotAfter:u}); servidores validados contra CA local.",
                _clientCertificate.Subject, _clientCertificate.NotAfter.ToUniversalTime());
        }
        else
        {
            logger.LogWarning(
                "Gateway:Upstream:AllowPlainHttp=true: as chamadas às APIs seguem em HTTP sem TLS/mTLS. Aceitável só no perfil de desenvolvimento (GAP declarado).");
        }
    }

    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);

        // Conexões TLS do pool sobrevivem a mudanças no lado servidor (blocklist de thumbprint, rotação de certificado):
        // o handshake só se repete em conexão nova. Vida máxima curta (HIPÓTESE 2 min) limita essa janela sem reinício
        // do gateway; achado do roteiro F5 (seção 7: cliente bloqueado seguia 200 pela conexão já aberta).
        handler.PooledConnectionLifetime = PooledConnectionLifetime;
        if (_clientCertificate is null)
        {
            return;
        }

        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        handler.SslOptions.ClientCertificates = [_clientCertificate];
        handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => _clientCertificate;
        handler.SslOptions.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
        handler.SslOptions.RemoteCertificateValidationCallback = ValidateServerCertificate;
    }

    /// <summary>Motivo da recusa (null = aceito). Separado para ser testável sem rede.</summary>
    internal string? EvaluateServerCertificate(X509Certificate2? certificate, string expectedHost, SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return "missing";
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_serverCa!);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")); // serverAuth
        if (!chain.Build(certificate))
        {
            return chain.ChainStatus.Any(s => s.Status.HasFlag(X509ChainStatusFlags.NotTimeValid)) ? "expired" : "untrusted_ca";
        }

        if (string.IsNullOrWhiteSpace(expectedHost))
        {
            return "name_mismatch"; // sem host esperado não há como afirmar o nome; MatchesHostname("") lançaria
        }

        return certificate.MatchesHostname(expectedHost, allowWildcards: false, allowCommonName: false) ? null : "name_mismatch";
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        // Com SocketsHttpHandler o sender é o SslStream (TargetHostName = host da URI de destino); HttpRequestMessage só
        // chega quando o callback vem de HttpClientHandler. Visto em docs/testing/evidence/f5/dotnet-test-spike-smoke.log.
        var expectedHost = sender switch
        {
            SslStream ssl => ssl.TargetHostName,
            HttpRequestMessage request => request.RequestUri?.Host ?? string.Empty,
            _ => string.Empty,
        };
        using var leaf = certificate is null ? null : new X509Certificate2(certificate);
        var reason = EvaluateServerCertificate(leaf, expectedHost, errors);
        if (reason is null)
        {
            return true;
        }

        _metrics.MtlsFailed("client", reason);
        _logger.LogWarning(
            "Certificado de servidor recusado para {Host}: motivo {Reason}, thumbprint {Thumbprint}.", expectedHost, reason, leaf?.Thumbprint ?? "(ausente)");
        return false;
    }
}
