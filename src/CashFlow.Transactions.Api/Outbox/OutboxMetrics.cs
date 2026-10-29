using System.Diagnostics.Metrics;
using CashFlow.Transactions.Api.Security;

namespace CashFlow.Transactions.Api.Outbox;

/// <summary>
/// Métricas do publisher (ADR-0002 item 1; RNF de observabilidade). Os gauges leem valores em cache atualizados pelo
/// laço do publisher: a coleta de métricas nunca abre conexão com o banco.
/// </summary>
public sealed class OutboxMetrics
{
    private long _pending;
    private double _oldestAgeSeconds;

    public OutboxMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(TransactionsMetrics.MeterName);
        Published = meter.CreateCounter<long>("outbox_published_total", description: "Eventos do outbox publicados com confirmação do broker.");
        Failures = meter.CreateCounter<long>("outbox_publish_failures_total", description: "Falhas de publicação, por motivo.");
        meter.CreateObservableGauge("outbox_pending", () => Interlocked.Read(ref _pending), description: "Eventos do outbox ainda não publicados.");
        meter.CreateObservableGauge(
            "outbox_oldest_age_seconds", () => Volatile.Read(ref _oldestAgeSeconds), unit: "s",
            description: "Idade do evento pendente mais antigo.");
    }

    public Counter<long> Published { get; }

    public Counter<long> Failures { get; }

    public void RecordFailure(string reason) => Failures.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void UpdateBacklog(long pending, double oldestAgeSeconds)
    {
        Interlocked.Exchange(ref _pending, pending);
        Volatile.Write(ref _oldestAgeSeconds, oldestAgeSeconds);
    }
}
