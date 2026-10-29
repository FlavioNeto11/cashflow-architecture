using CashFlow.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CashFlow.Consolidation.Worker;

/// <summary>
/// Readiness do consumidor (P0 da rodada 3): Unhealthy quando não há consumidor registrado na fila há mais de
/// NotConsumingUnhealthySeconds (pausa por banco fora, broker fora ou supervisor travado). /health/live continua sem
/// dependências (Hosting compartilhado): o worker não deve ser reiniciado por queda de banco ou broker.
/// </summary>
public sealed class ConsumerHealthCheck(ConsolidationConsumer consumer, ConsumerOptions options) : IHealthCheck
{
    public const string Name = "consumer";

    public static readonly string[] Tags = [PostgresExtensions.ReadyTag];

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var idle = consumer.NotConsumingFor;
        var limit = TimeSpan.FromSeconds(options.NotConsumingUnhealthySeconds);
        return Task.FromResult(idle is { } elapsed && elapsed > limit
            ? HealthCheckResult.Unhealthy($"sem consumidor registrado há {(int)elapsed.TotalSeconds} s")
            : HealthCheckResult.Healthy());
    }
}
