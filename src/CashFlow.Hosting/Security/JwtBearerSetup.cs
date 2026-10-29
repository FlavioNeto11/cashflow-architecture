using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace CashFlow.Hosting.Security;

/// <summary>
/// Esquema JwtBearer (F5, security.md 3.3): assinatura via JWKS em cache, iss exato, aud obrigatória, exp/nbf com
/// tolerância curta, algoritmos e typ fechados, MapInboundClaims=false (as claims chegam com os nomes do token: sub, azp,
/// merchant_id, scope — as mesmas que Caller lê). Respostas 401/403 em problem+json, contadas em SecurityMetrics.
/// </summary>
internal static class JwtBearerSetup
{
    private const string ClientAuthReason = "missing_token";
    private const string UnknownKeyReason = "unknown_signing_key";
    private const string MetadataUnavailableReason = "metadata_unavailable";

    public static void Add(WebApplicationBuilder builder, JwtBearerSettings settings)
    {
        if (settings.MetadataIsHttp)
        {
            // Aviso ruidoso de propósito: só o IdP local em modo dev justifica metadados em http (GAP; security.md 11).
            var logger = LoggerFactory.Create(logging => logging.AddJsonConsole()).CreateLogger("CashFlow.Hosting.Security");
            logger.LogWarning(
                "{Key}=true: documento OIDC/JWKS obtido por HTTP sem TLS ({Host}). Aceitável só com o IdP local do Compose; em qualquer outro ambiente isto é um GAP de segurança.",
                JwtBearerSettings.AllowHttpMetadataKey, settings.MetadataUri.Host);
        }

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<OidcConfigurationSource>();
        builder.Services.AddHealthChecks().AddCheck<OidcMetadataHealthCheck>(OidcMetadataHealthCheck.Name, tags: [PostgresExtensions.ReadyTag]);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = !settings.AllowHttpMetadata;
                options.MetadataAddress = settings.MetadataAddress!;
                options.IncludeErrorDetails = false;
                options.SaveToken = false;
                options.RefreshOnIssuerKeyNotFound = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.ValidIssuer,
                    ValidateAudience = true,
                    ValidAudience = settings.ValidAudience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = settings.ValidAlgorithms,
                    ValidTypes = settings.ValidTokenTypes, // typ fechado: ID/Refresh token ou token sem typ → 401 invalid_type
                    ClockSkew = TimeSpan.FromSeconds(settings.ClockSkewSeconds),
                    NameClaimType = Caller.SubClaim,
                };
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        // Falhas que não são do token (ex.: metadados OIDC indisponíveis no cold start) viram 401 em vez de
                        // 500: o chamador não pode ser autenticado, e a readiness já expõe a causa (G1-15).
                        if (context.Exception is not SecurityTokenException)
                        {
                            context.Fail(context.Exception);
                        }

                        return Task.CompletedTask;
                    },
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        var reason = Reason(context.AuthenticateFailure);
                        if (reason == UnknownKeyReason)
                        {
                            reason = await KeyNotFoundReasonAsync(context.HttpContext.RequestServices.GetRequiredService<OidcConfigurationSource>(), context.HttpContext.RequestAborted);
                        }

                        context.HttpContext.RequestServices.GetRequiredService<SecurityMetrics>().AuthenticationFailed(reason);
                        context.Response.Headers.WWWAuthenticate = reason == ClientAuthReason
                            ? "Bearer realm=\"cashflow\""
                            : "Bearer realm=\"cashflow\", error=\"invalid_token\"";
                        await SecurityProblems.Unauthorized().ExecuteAsync(context.HttpContext);
                    },
                    OnForbidden = async context =>
                    {
                        context.HttpContext.RequestServices.GetRequiredService<SecurityMetrics>().AuthorizationDenied("scope");
                        await SecurityProblems.Forbidden().ExecuteAsync(context.HttpContext);
                    },
                };
            });

        // O handler e o health check compartilham o mesmo ConfigurationManager (cache único por processo).
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<OidcConfigurationSource>((options, source) => options.ConfigurationManager = source.Manager);
    }

    /// <summary>Motivo agregado para a métrica, sem nenhum dado do token.</summary>
    internal static string Reason(Exception? failure) => failure switch
    {
        null => ClientAuthReason,
        SecurityTokenExpiredException => "expired",
        SecurityTokenNotYetValidException => "not_yet_valid",
        SecurityTokenInvalidAudienceException => "invalid_audience",
        SecurityTokenInvalidIssuerException => "invalid_issuer",
        SecurityTokenInvalidAlgorithmException => "invalid_algorithm",
        SecurityTokenInvalidTypeException => "invalid_type",
        SecurityTokenSignatureKeyNotFoundException => UnknownKeyReason,
        SecurityTokenInvalidSignatureException => "invalid_signature",
        SecurityTokenMalformedException => "malformed",
        SecurityTokenException => "invalid_token",
        AggregateException aggregate when aggregate.InnerExceptions.Count > 0 => Reason(aggregate.InnerExceptions[0]),
        _ => MetadataUnavailableReason,
    };

    /// <summary>
    /// Chave de assinatura desconhecida tem duas causas distintas: kid novo (rotação no IdP; o handler já pediu refresh)
    /// ou processo que nunca obteve o JWKS (cold start com o IdP fora, G1-15). O JsonWebTokenHandler engole a falha de
    /// obtenção do documento (IDX10261) e segue sem chaves, por isso a exceção é a mesma nos dois casos. Distingue-se
    /// pelo cache compartilhado: com documento em cache a chamada volta sem rede (fica unknown_signing_key); sem documento,
    /// tenta o IdP mais uma vez (até 2 s) e, falhando, reporta metadata_unavailable. Só executa em falha de autenticação.
    /// </summary>
    private static async Task<string> KeyNotFoundReasonAsync(OidcConfigurationSource source, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await source.Manager.GetConfigurationAsync(timeout.Token);
            return UnknownKeyReason;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return MetadataUnavailableReason;
        }
    }
}
