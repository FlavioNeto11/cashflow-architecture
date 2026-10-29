namespace CashFlow.Gateway;

/// <summary>
/// Configuração do gateway (seção Gateway). Os números de rate limiting, corpo e tempo são os de security.md 5 e são
/// HIPÓTESES de projeto (não há volumetria fornecida além dos 50 RPS do consolidado): configuráveis para o F7 ajustar
/// com evidência e para os testes usarem limites pequenos.
/// </summary>
public sealed class GatewaySettings
{
    public const string Section = "Gateway";

    public UpstreamSettings Upstream { get; set; } = new();

    public LimitSettings Limits { get; set; } = new();

    public RateLimitSettings RateLimits { get; set; } = new();

    public static GatewaySettings Load(IConfiguration configuration)
    {
        var settings = configuration.GetSection(Section).Get<GatewaySettings>() ?? new GatewaySettings();
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        Upstream.Validate();
        Limits.Validate();
        RateLimits.Validate();
    }
}

/// <summary>
/// Destinos internos e mTLS de cliente (security.md 6): com destinos https, certificado de cliente e CA do servidor são
/// obrigatórios; destinos http só com AllowPlainHttp=true explícito (perfil de desenvolvimento), nunca com certificado
/// configurado (evita achar que há mTLS onde não há).
/// </summary>
public sealed class UpstreamSettings
{
    public string TransactionsApi { get; set; } = "https://transactions-api:8080";

    public string ConsolidationApi { get; set; } = "https://consolidation-api:8080";

    /// <summary>Certificado de cliente do gateway (PEM) apresentado às APIs.</summary>
    public string? ClientCertificate { get; set; }

    public string? ClientKey { get; set; }

    /// <summary>CA que emitiu os certificados de servidor das APIs (PEM).</summary>
    public string? ServerCa { get; set; }

    /// <summary>Permite destinos http (sem TLS) — só para o perfil de desenvolvimento; GAP declarado quando ligado.</summary>
    public bool AllowPlainHttp { get; set; }

    public int MaxConnectionsPerServer { get; set; } = 200;

    public Uri TransactionsApiUri => new(TransactionsApi, UriKind.Absolute);

    public Uri ConsolidationApiUri => new(ConsolidationApi, UriKind.Absolute);

    public bool MutualTlsEnabled => TransactionsApiUri.Scheme == Uri.UriSchemeHttps;

    public void Validate()
    {
        var uris = new[] { (nameof(TransactionsApi), TransactionsApi), (nameof(ConsolidationApi), ConsolidationApi) };
        foreach (var (name, value) in uris)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException($"{GatewaySettings.Section}:Upstream:{name} deve ser uma URL absoluta http(s).");
            }
        }

        if (TransactionsApiUri.Scheme != ConsolidationApiUri.Scheme)
        {
            throw new InvalidOperationException($"{GatewaySettings.Section}:Upstream: os dois destinos devem usar o mesmo esquema (https ou http).");
        }

        if (MutualTlsEnabled)
        {
            RequireFile(ClientCertificate, nameof(ClientCertificate));
            RequireFile(ClientKey, nameof(ClientKey));
            RequireFile(ServerCa, nameof(ServerCa));
        }
        else
        {
            if (!AllowPlainHttp)
            {
                throw new InvalidOperationException(
                    $"{GatewaySettings.Section}:Upstream usa http sem TLS; isso exige {GatewaySettings.Section}:Upstream:AllowPlainHttp=true explicitamente (só desenvolvimento).");
            }

            if (!string.IsNullOrWhiteSpace(ClientCertificate) || !string.IsNullOrWhiteSpace(ServerCa))
            {
                throw new InvalidOperationException(
                    $"{GatewaySettings.Section}:Upstream: certificados configurados com destinos http; use https ou remova os certificados.");
            }
        }

        if (MaxConnectionsPerServer is < 1 or > 10_000)
        {
            throw new InvalidOperationException($"{GatewaySettings.Section}:Upstream:MaxConnectionsPerServer deve estar entre 1 e 10000.");
        }
    }

    private static void RequireFile(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{GatewaySettings.Section}:Upstream:{name} é obrigatório com destinos https (mTLS).");
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{GatewaySettings.Section}:Upstream:{name}: arquivo não encontrado ({Path.GetFileName(path)}).");
        }
    }
}

public sealed class LimitSettings
{
    /// <summary>Corpo máximo aceito (security.md 5): 16 KB; acima disso 413 antes de qualquer encaminhamento.</summary>
    public long MaxBodyBytes { get; set; } = 16 * 1024;

    public int GetTimeoutSeconds { get; set; } = 5;

    public int PostTimeoutSeconds { get; set; } = 10;

    public void Validate()
    {
        if (MaxBodyBytes is < 1024 or > 1024 * 1024)
        {
            throw new InvalidOperationException($"{GatewaySettings.Section}:Limits:MaxBodyBytes deve estar entre 1 KB e 1 MB.");
        }

        if (GetTimeoutSeconds is < 1 or > 120 || PostTimeoutSeconds is < 1 or > 120)
        {
            throw new InvalidOperationException($"{GatewaySettings.Section}:Limits: timeouts devem estar entre 1 e 120 segundos.");
        }
    }
}

/// <summary>Taxas por segundo (token bucket, reposição a cada segundo). Partição: merchant_id do token validado; IP para anônimos.</summary>
public sealed class RateLimitSettings
{
    public int TransactionsWritePerSecond { get; set; } = 20;

    public int TransactionsWriteBurst { get; set; } = 40;

    public int TransactionsReadPerSecond { get; set; } = 30;

    public int ConsolidationReadPerMerchantPerSecond { get; set; } = 60;

    /// <summary>Teto global da rota de consolidado (RF-04: 50 RPS de pico com folga; HIPÓTESE).</summary>
    public int ConsolidationReadGlobalPerSecond { get; set; } = 120;

    public int AnonymousPerIpPerSecond { get; set; } = 20;

    public void Validate()
    {
        foreach (var (name, value) in new[]
        {
            (nameof(TransactionsWritePerSecond), TransactionsWritePerSecond), (nameof(TransactionsWriteBurst), TransactionsWriteBurst),
            (nameof(TransactionsReadPerSecond), TransactionsReadPerSecond), (nameof(ConsolidationReadPerMerchantPerSecond), ConsolidationReadPerMerchantPerSecond),
            (nameof(ConsolidationReadGlobalPerSecond), ConsolidationReadGlobalPerSecond), (nameof(AnonymousPerIpPerSecond), AnonymousPerIpPerSecond),
        })
        {
            if (value is < 1 or > 100_000)
            {
                throw new InvalidOperationException($"{GatewaySettings.Section}:RateLimits:{name} deve estar entre 1 e 100000.");
            }
        }

        if (TransactionsWriteBurst < TransactionsWritePerSecond)
        {
            throw new InvalidOperationException($"{GatewaySettings.Section}:RateLimits:TransactionsWriteBurst deve ser >= TransactionsWritePerSecond.");
        }
    }
}
