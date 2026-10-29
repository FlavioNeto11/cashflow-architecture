using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CashFlow.Hosting.Security;

/// <summary>Acessos negados porque o merchant da rota não é o do token (ADR-0006), no Meter de cada serviço.</summary>
public sealed class TenantAccessMetrics
{
    public TenantAccessMetrics(IMeterFactory meterFactory, string meterName)
    {
        var meter = meterFactory.Create(meterName);
        CrossTenantDenied = meter.CreateCounter<long>(
            "authz_cross_tenant_denied_total", description: "Requisições negadas por merchant da rota diferente do claim.");
    }

    public Counter<long> CrossTenantDenied { get; }
}

/// <summary>
/// Exige merchantId da rota == claim merchant_id. Divergência → 404 idêntico ao de recurso inexistente
/// (não revela se o outro tenant existe), com métrica e log de auditoria sem PII (só ids técnicos).
/// Roda depois da autorização por escopo, então o principal já é autenticado e completo.
/// </summary>
public sealed class TenantFilter(TenantAccessMetrics metrics, ILogger<TenantFilter> logger) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Caller.TryFrom(http.User, out var caller)
            || !Guid.TryParse(http.GetRouteValue("merchantId")?.ToString(), out var routeMerchantId))
        {
            return ValueTask.FromResult<object?>(SecurityProblems.Forbidden());
        }

        if (caller.MerchantId != routeMerchantId)
        {
            metrics.CrossTenantDenied.Add(1);
            logger.LogWarning(
                "Acesso cross-tenant negado: merchant da rota {RouteMerchantId}, merchant do token {TokenMerchantId}, cliente {ClientId}, {Method} {Path}.",
                routeMerchantId, caller.MerchantId, caller.ClientId, http.Request.Method, http.GetEndpoint()?.DisplayName);
            return ValueTask.FromResult<object?>(SecurityProblems.NotFound());
        }

        return next(context);
    }
}
