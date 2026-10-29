using Microsoft.Extensions.Configuration;

namespace CashFlow.Hosting.Security;

/// <summary>
/// Configuração do esquema JwtBearer (security.md 3.3). Tudo explícito e fail-closed: sem MetadataAddress ou
/// ValidIssuer a partida falha; ValidateIssuer=false não existe como opção; algoritmos simétricos e "none" são
/// recusados mesmo se configurados. Lida de Authentication:JwtBearer.
/// </summary>
public sealed class JwtBearerSettings
{
    public const string Section = "Authentication:JwtBearer";
    public const string DefaultAudience = "cashflow-api";
    public const string AllowHttpMetadataKey = $"{Section}:AllowHttpMetadata";

    private static readonly HashSet<string> AllowedAlgorithms = new(StringComparer.Ordinal)
    {
        "RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512",
    };

    /// <summary>URL interna do documento OIDC (…/.well-known/openid-configuration); de onde vem o JWKS.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Issuer esperado, comparado por igualdade exata com a claim iss. Obrigatório (G1-14).</summary>
    public string? ValidIssuer { get; set; }

    public string ValidAudience { get; set; } = DefaultAudience;

    /// <summary>
    /// Admite MetadataAddress em http (Keycloak em modo dev dentro da rede do Compose). Padrão false: com MetadataAddress
    /// em http e sem esta flag a partida falha em QUALQUER ambiente (não há exceção por Development); com a flag, a
    /// partida registra aviso e o uso fica como GAP declarado (security.md 11).
    /// </summary>
    public bool AllowHttpMetadata { get; set; }

    /// <summary>Tolerância de relógio em segundos (padrão 30; máximo 300).</summary>
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>Lista fechada de algoritmos de assinatura aceitos (padrão RS256 e ES256).</summary>
    public string[] ValidAlgorithms { get; set; } = ["RS256", "ES256"];

    /// <summary>Intervalo do refresh automático do documento OIDC/JWKS (padrão 60 min).</summary>
    public int MetadataRefreshMinutes { get; set; } = 60;

    /// <summary>
    /// Valores aceitos do cabeçalho JOSE typ (lista fechada). Padrão: "JWT" (o que o Keycloak 26 escreve no CABEÇALHO dos
    /// access tokens; "Bearer" é a claim typ do payload, não o cabeçalho) e "at+jwt" (RFC 9068, opção por cliente no
    /// Keycloak). Um ID token ("ID"), um logout token ("Logout") ou um token sem typ nunca vale como access token, mesmo
    /// com aud/iss certos. Verificado em TokenClockAndKeyRotationTests.
    /// </summary>
    public string[] ValidTokenTypes { get; set; } = ["JWT", "at+jwt"];

    public static JwtBearerSettings Load(IConfiguration configuration)
    {
        var settings = configuration.GetSection(Section).Get<JwtBearerSettings>() ?? new JwtBearerSettings();
        settings.Validate();
        return settings;
    }

    public Uri MetadataUri => new(MetadataAddress!, UriKind.Absolute);

    public bool MetadataIsHttp => MetadataUri.Scheme == Uri.UriSchemeHttp;

    public void Validate()
    {
        if (!Uri.TryCreate(MetadataAddress, UriKind.Absolute, out var metadata)
            || (metadata.Scheme != Uri.UriSchemeHttps && metadata.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{Section}:MetadataAddress deve ser uma URL absoluta http(s) do documento OIDC.");
        }

        if (metadata.Scheme == Uri.UriSchemeHttp && !AllowHttpMetadata)
        {
            throw new InvalidOperationException(
                $"{Section}:MetadataAddress usa http; isso exige {AllowHttpMetadataKey}=true explicitamente (GAP declarado, só para o IdP local).");
        }

        if (string.IsNullOrWhiteSpace(ValidIssuer))
        {
            throw new InvalidOperationException($"{Section}:ValidIssuer é obrigatório (comparação exata com a claim iss; G1-14).");
        }

        if (string.IsNullOrWhiteSpace(ValidAudience))
        {
            throw new InvalidOperationException($"{Section}:ValidAudience é obrigatório.");
        }

        if (ClockSkewSeconds is < 0 or > 300)
        {
            throw new InvalidOperationException($"{Section}:ClockSkewSeconds deve estar entre 0 e 300.");
        }

        if (ValidAlgorithms.Length == 0 || ValidAlgorithms.Any(a => !AllowedAlgorithms.Contains(a)))
        {
            throw new InvalidOperationException(
                $"{Section}:ValidAlgorithms deve listar só algoritmos assimétricos ({string.Join(", ", AllowedAlgorithms)}); 'none' e HS* são recusados.");
        }

        if (MetadataRefreshMinutes is < 1 or > 24 * 60)
        {
            throw new InvalidOperationException($"{Section}:MetadataRefreshMinutes deve estar entre 1 e 1440.");
        }

        if (ValidTokenTypes.Length == 0 || ValidTokenTypes.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException($"{Section}:ValidTokenTypes deve listar ao menos um typ aceito (padrão JWT, at+jwt).");
        }
    }
}
