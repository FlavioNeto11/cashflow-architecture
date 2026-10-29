using System.Diagnostics.Metrics;

namespace CashFlow.Consolidation.Worker;

/// <summary>Métricas do consumidor (RNF de observabilidade; ADR-0002). Rótulos só de conjuntos fechados.</summary>
public sealed class WorkerMetrics
{
    public const string MeterName = "CashFlow.Consolidation.Worker";

    public WorkerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        Applied = meter.CreateCounter<long>("consolidation_applied_total", description: "Eventos aplicados à projeção (COMMIT).");
        Duplicates = meter.CreateCounter<long>("inbox_duplicates_total", description: "Reentregas reconhecidas pelo Inbox (sem efeito).");
        DeadLettered = meter.CreateCounter<long>("dlq_messages_total", description: "Mensagens enviadas à DLQ, por motivo.");
        TransientRetries = meter.CreateCounter<long>("worker_transient_retries_total", description: "Novas tentativas em processo por SQLSTATE transitório.");
        PausedSeconds = meter.CreateCounter<double>("consumer_paused_seconds", unit: "s", description: "Tempo acumulado com o consumo pausado por falha de infraestrutura.");
        Lag = meter.CreateHistogram<double>("consolidation_lag_seconds", unit: "s", description: "occurredAt do evento até o COMMIT na projeção.");
    }

    public Counter<long> Applied { get; }

    public Counter<long> Duplicates { get; }

    public Counter<long> DeadLettered { get; }

    public Counter<long> TransientRetries { get; }

    public Counter<double> PausedSeconds { get; }

    public Histogram<double> Lag { get; }

    public void RecordDeadLetter(string reason) => DeadLettered.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void RecordTransientRetry(string sqlState) => TransientRetries.Add(1, new KeyValuePair<string, object?>("sqlstate", sqlState));
}
