using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CashFlow.Hosting.Security;

/// <summary>
/// Fonte única do documento OIDC/JWKS do processo: o handler JwtBearer e o health check usam a MESMA instância,
/// logo o cache é compartilhado e nenhuma requisição de negócio autenticada chama o IdP (security.md 3.3). Refresh
/// automático a cada MetadataRefreshMinutes; refresh forçado por kid desconhecido limitado a um por RefreshInterval
/// (5 min). Se um refresh falhar, o último documento válido continua em uso (processo aquecido sobrevive ao IdP fora).
/// Única exceção: uma falha por chave desconhecida SEM documento em cache (cold start com IdP fora) faz
/// JwtBearerSetup.KeyNotFoundReasonAsync tentar o IdP mais uma vez (até 2 s) só para classificar a métrica.
/// </summary>
public sealed class OidcConfigurationSource
{
    public OidcConfigurationSource(JwtBearerSettings settings)
    {
        Manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            settings.MetadataAddress!,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = !settings.AllowHttpMetadata })
        {
            AutomaticRefreshInterval = TimeSpan.FromMinutes(settings.MetadataRefreshMinutes),
            RefreshInterval = TimeSpan.FromMinutes(5),
        };
    }

    public ConfigurationManager<OpenIdConnectConfiguration> Manager { get; }
}

/// <summary>
/// Readiness do G1-15: enquanto o processo não obteve o documento OIDC e ao menos uma chave de assinatura, o
/// serviço não está pronto (cold start com IdP fora = GAP declarado). Depois de obtido, a resposta vem do cache.
/// </summary>
public sealed class OidcMetadataHealthCheck(OidcConfigurationSource source) : IHealthCheck
{
    public const string Name = "oidc-metadata";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            var configuration = await source.Manager.GetConfigurationAsync(timeout.Token);
            return configuration.SigningKeys.Count > 0
                ? HealthCheckResult.Healthy("documento OIDC e JWKS em cache")
                : HealthCheckResult.Unhealthy("documento OIDC sem chaves de assinatura");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Mensagem sem a URL completa nem corpo: só o tipo do erro.
            return HealthCheckResult.Unhealthy($"metadados OIDC/JWKS indisponíveis ({ex.GetType().Name})");
        }
    }
}
