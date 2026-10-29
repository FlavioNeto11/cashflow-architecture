using System.Diagnostics.Metrics;

namespace CashFlow.Hosting.Security;

/// <summary>
/// Métricas de segurança comuns a gateway e APIs (security.md 9): authn_failed_total{reason},
/// authz_denied_total{kind}, rate_limited_total{route} e mtls_handshake_failed_total{side,reason}.
/// Meter próprio ("CashFlow.Security"), registrado no OpenTelemetry por AddCashFlowDefaults.
/// Nunca recebe o token, o cabeçalho Authorization nem dados do certificado além do motivo da recusa.
/// </summary>
public sealed class SecurityMetrics
{
    public const string MeterName = "CashFlow.Security";

    public SecurityMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        AuthnFailed = meter.CreateCounter<long>("authn_failed_total", description: "Requisições recusadas com 401, por motivo.");
        AuthzDenied = meter.CreateCounter<long>("authz_denied_total", description: "Requisições recusadas com 403, por tipo.");
        RateLimited = meter.CreateCounter<long>("rate_limited_total", description: "Requisições recusadas com 429, por rota.");
        MtlsHandshakeFailed = meter.CreateCounter<long>(
            "mtls_handshake_failed_total", description: "Handshakes mTLS recusados (lado servidor: certificado de cliente; lado cliente: certificado do upstream).");
    }

    public Counter<long> AuthnFailed { get; }

    public Counter<long> AuthzDenied { get; }

    public Counter<long> RateLimited { get; }

    public Counter<long> MtlsHandshakeFailed { get; }

    public void AuthenticationFailed(string reason) => AuthnFailed.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void AuthorizationDenied(string kind) => AuthzDenied.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void RateLimitExceeded(string route) => RateLimited.Add(1, new KeyValuePair<string, object?>("route", route));

    public void MtlsFailed(string side, string reason) =>
        MtlsHandshakeFailed.Add(1, new KeyValuePair<string, object?>("side", side), new KeyValuePair<string, object?>("reason", reason));
}
