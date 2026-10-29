using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CashFlow.Hosting.Security;
using Microsoft.Extensions.Logging;

namespace CashFlow.Hosting.Tls;

/// <summary>
/// Decide se o certificado de cliente apresentado ao listener de negócio é o do gateway (security.md 6):
/// cadeia construída SOMENTE contra a CA local (CustomRootTrust; o store do SO não conta), validade no tempo,
/// EKU clientAuth presente, SAN DNS em AllowedClientNames e thumbprint fora da blocklist. Qualquer recusa conta em
/// mtls_handshake_failed_total{side=server,reason} e vai ao log só com thumbprint e motivo (nunca a chave).
/// </summary>
public sealed class ClientCertificateValidator
{
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    private readonly X509Certificate2Collection _trustedRoots;
    private readonly IReadOnlyList<string> _allowedNames;
    private readonly ThumbprintBlocklist? _blocklist;
    private readonly SecurityMetrics? _metrics;
    private readonly ILogger? _logger;

    public ClientCertificateValidator(
        X509Certificate2Collection trustedRoots, IReadOnlyList<string> allowedNames,
        ThumbprintBlocklist? blocklist = null, SecurityMetrics? metrics = null, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(trustedRoots.Count);
        ArgumentOutOfRangeException.ThrowIfZero(allowedNames.Count);
        _trustedRoots = trustedRoots;
        _allowedNames = allowedNames;
        _blocklist = blocklist;
        _metrics = metrics;
        _logger = logger;
    }

    public static ClientCertificateValidator FromSettings(TlsSettings settings, SecurityMetrics? metrics, ILogger? logger) =>
        new(
            CertificateFiles.LoadCertificates(settings.ClientCa!),
            settings.AllowedClientNameList,
            settings.BlockedThumbprintsFile is null ? null : new ThumbprintBlocklist(settings.BlockedThumbprintsFile),
            metrics,
            logger);

    /// <summary>Callback de Kestrel (HttpsConnectionAdapterOptions.ClientCertificateValidation).</summary>
    public bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var reason = Evaluate(certificate, errors);
        if (reason is null)
        {
            return true;
        }

        _metrics?.MtlsFailed("server", reason);
        _logger?.LogWarning(
            "Certificado de cliente recusado no listener de negócio: motivo {Reason}, thumbprint {Thumbprint}.",
            reason, certificate?.Thumbprint ?? "(ausente)");
        return false;
    }

    /// <summary>Motivo da recusa, ou null se aceito. Separado do callback para ser testável sem Kestrel.</summary>
    public string? Evaluate(X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return "missing";
        }

        if (_blocklist?.Contains(certificate.Thumbprint) == true)
        {
            return "blocked_thumbprint";
        }

        if (!HasClientAuthEku(certificate))
        {
            return "eku";
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(_trustedRoots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // CA local sem CRL/OCSP (GAP parcial: blocklist)
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthOid));
        if (!chain.Build(certificate))
        {
            return ChainReason(chain);
        }

        return _allowedNames.Any(name => certificate.MatchesHostname(name, allowWildcards: false, allowCommonName: false))
            ? null
            : "name_mismatch";
    }

    private static bool HasClientAuthEku(X509Certificate2 certificate)
    {
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is not null && eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ClientAuthOid);
    }

    private static string ChainReason(X509Chain chain)
    {
        var flags = X509ChainStatusFlags.NoError;
        foreach (var status in chain.ChainStatus)
        {
            flags |= status.Status;
        }

        if (flags.HasFlag(X509ChainStatusFlags.NotTimeValid) || flags.HasFlag(X509ChainStatusFlags.NotTimeNested))
        {
            return "expired";
        }

        if (flags.HasFlag(X509ChainStatusFlags.NotValidForUsage))
        {
            return "eku";
        }

        if (flags.HasFlag(X509ChainStatusFlags.UntrustedRoot) || flags.HasFlag(X509ChainStatusFlags.PartialChain)
            || flags.HasFlag(X509ChainStatusFlags.NotSignatureValid))
        {
            return "untrusted_ca";
        }

        return "chain_invalid";
    }
}

/// <summary>Thumbprints SHA-1 recusados, lidos de arquivo e relidos quando a data de modificação muda (sem reinício).</summary>
public sealed class ThumbprintBlocklist(string path)
{
    private readonly Lock _gate = new();
    private DateTime _loadedAt = DateTime.MinValue;
    private HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);

    public bool Contains(string thumbprint)
    {
        Reload();
        return _blocked.Contains(thumbprint);
    }

    public int Count
    {
        get
        {
            Reload();
            return _blocked.Count;
        }
    }

    private void Reload()
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (stamp == _loadedAt)
        {
            return;
        }

        lock (_gate)
        {
            if (stamp == _loadedAt)
            {
                return;
            }

            var entries = File.Exists(path) ? File.ReadAllLines(path) : [];
            _blocked = entries
                .Select(line => line.Split('#')[0].Replace(":", string.Empty, StringComparison.Ordinal).Trim())
                .Where(line => line.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _loadedAt = stamp;
        }
    }
}
