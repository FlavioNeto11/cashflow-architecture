using System.Globalization;
using System.Threading.RateLimiting;
using CashFlow.Hosting.Security;
using Microsoft.AspNetCore.RateLimiting;
using Yarp.ReverseProxy.Model;

namespace CashFlow.Gateway;

/// <summary>
/// Rate limiting (security.md 5), Microsoft.AspNetCore.RateLimiting. Partição por merchant_id do token já validado
/// (UseAuthentication vem antes); requisições anônimas particionam por IP com a taxa de anônimos. O teto global da
/// rota de consolidado fica no GlobalLimiter, particionado pela rota do YARP, e vale SÓ para requisições autenticadas:
/// o middleware consulta o global antes da política do endpoint e a ficha de token bucket não volta quando a política
/// recusa, então um IP anônimo acima do teto esgotava o balde de todos os merchants (auditoria F7, P1-1; provado por
/// GlobalLimiterAnonymousTests). Isentar o anônimo é seguro porque ele já é limitado por IP e nunca chega ao upstream
/// (toda rota exige escopo: 401). Pelo mesmo motivo o limite POR MERCHANT do consolidado não fica na política do
/// endpoint: ele é o primeiro elo de um GlobalLimiter encadeado (por merchant e depois global), e o excesso de um
/// merchant é recusado antes de tocar no balde global (PF-18; provado por MerchantBurstGlobalLimiterTests). A ficha por
/// merchant de uma requisição que o elo global recusa não volta; só importa com o teto global já esgotado.
/// 429 em problem+json com Retry-After e rate_limited_total{route}.
/// </summary>
internal static class GatewayRateLimiting
{
    private const string AnonymousPrefix = "ip:";
    private const string MerchantPrefix = "merchant:";

    public static IServiceCollection AddGatewayRateLimiting(this IServiceCollection services, RateLimitSettings settings) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;

            options.AddPolicy(ProxyRoutes.TransactionsWriteRoute, http =>
                PerCaller(http, settings, settings.TransactionsWritePerSecond, settings.TransactionsWriteBurst));
            options.AddPolicy(ProxyRoutes.TransactionsReadRoute, http =>
                PerCaller(http, settings, settings.TransactionsReadPerSecond, settings.TransactionsReadPerSecond));
            // Merchant autenticado do consolidado: limitado no GlobalLimiter encadeado abaixo; aqui só o anônimo (por IP).
            options.AddPolicy(ProxyRoutes.ConsolidationReadRoute, http =>
                Merchant(http) is not null
                    ? RateLimitPartition.GetNoLimiter("consolidation:merchant-in-global")
                    : PerCaller(http, settings, settings.ConsolidationReadPerMerchantPerSecond, settings.ConsolidationReadPerMerchantPerSecond));

            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(http =>
                    ConsolidationMerchant(http) is { } merchant
                        ? TokenBucket(MerchantPrefix + merchant, settings.ConsolidationReadPerMerchantPerSecond, settings.ConsolidationReadPerMerchantPerSecond)
                        : RateLimitPartition.GetNoLimiter("merchant:none")),
                PartitionedRateLimiter.Create<HttpContext, string>(http =>
                    ConsolidationMerchant(http) is not null
                        ? TokenBucket("global:" + ProxyRoutes.ConsolidationReadRoute, settings.ConsolidationReadGlobalPerSecond, settings.ConsolidationReadGlobalPerSecond)
                        : RateLimitPartition.GetNoLimiter("global:none")));
        });

    /// <summary>merchant_id do token já validado; null para anônimo ou token sem o claim (esses ficam no balde por IP).</summary>
    private static string? Merchant(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true ? http.User.FindFirst(Caller.MerchantClaim)?.Value : null;

    private static string? ConsolidationMerchant(HttpContext http) =>
        RouteId(http) == ProxyRoutes.ConsolidationReadRoute ? Merchant(http) : null;

    private static RateLimitPartition<string> PerCaller(HttpContext http, RateLimitSettings settings, int perSecond, int burst)
    {
        var merchant = Merchant(http);
        return merchant is not null
            ? TokenBucket(MerchantPrefix + merchant, perSecond, burst)
            : TokenBucket(AnonymousPrefix + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), settings.AnonymousPerIpPerSecond, settings.AnonymousPerIpPerSecond);
    }

    private static RateLimitPartition<string> TokenBucket(string key, int perSecond, int burst) =>
        RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = burst,
            TokensPerPeriod = perSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0, // sem fila: excesso recebe 429 imediato (previsível sob carga)
            AutoReplenishment = true,
        });

    private static string RouteId(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<RouteModel>()?.Config.RouteId ?? "unmatched";

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        http.RequestServices.GetRequiredService<SecurityMetrics>().RateLimitExceeded(RouteId(http));
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 1;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Limite de requisições excedido.").ExecuteAsync(http);
    }
}
