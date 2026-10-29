namespace CashFlow.Transactions.Api.Persistence;

/// <summary>
/// Linha do outbox (target.md 8.3). F3 só INSERE; o publisher (F4) atualiza published_at, attempts,
/// next_attempt_at, last_error e o lease (locked_*). O formato é fixo para F4 não precisar migrar dados.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public Guid MerchantId { get; set; }

    public Guid AggregateId { get; set; }

    public string Type { get; set; } = string.Empty;

    public int Version { get; set; }

    public string Payload { get; set; } = string.Empty;

    public string Headers { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    public DateTime? PublishedAt { get; set; }

    public int Attempts { get; set; }

    public DateTime NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    public DateTime? LockedUntil { get; set; }

    public string? LockedBy { get; set; }
}
