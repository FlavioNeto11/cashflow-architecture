using System.Text.Json;
using CashFlow.Transactions.Api.Domain;
using CashFlow.Transactions.Api.Http;
using CashFlow.Transactions.Api.Persistence;

namespace CashFlow.Transactions.Api.Application;

/// <summary>
/// Monta a linha do outbox com o evento TransactionRegistered v1 (target.md 8.3). Formato FIXO: o publisher
/// de F4 só lê e atualiza estas linhas. Estorno é um evento comum, com entryType invertido e reversalOf.
/// </summary>
public static class OutboxFactory
{
    public const string EventType = "TransactionRegistered";
    public const int EventVersion = 1;

    public static OutboxMessage Create(Transaction transaction, string? traceparent)
    {
        var eventId = Guid.CreateVersion7();

        var payload = new TransactionRegisteredV1(
            eventId, EventType, EventVersion, transaction.OccurredAt, transaction.MerchantId, transaction.Id,
            transaction.EntryType.ToWire(), Money.FormatAmount(transaction.Amount), transaction.Currency,
            transaction.EffectiveDate, transaction.ReversalOf);

        var headers = new Dictionary<string, string?>
        {
            ["message-id"] = eventId.ToString(),
            ["traceparent"] = traceparent,
            ["merchant_id"] = transaction.MerchantId.ToString(),
            ["content-type"] = "application/json",
        };

        return new OutboxMessage
        {
            Id = eventId,
            MerchantId = transaction.MerchantId,
            AggregateId = transaction.Id,
            Type = EventType,
            Version = EventVersion,
            Payload = JsonSerializer.Serialize(payload, Json.Output),
            Headers = JsonSerializer.Serialize(headers, Json.Output),
            OccurredAt = transaction.OccurredAt,
            NextAttemptAt = transaction.OccurredAt,
            Attempts = 0,
        };
    }
}

/// <summary>Payload do evento v1. A ordem dos campos segue a tabela de target.md 8.3.</summary>
public sealed record TransactionRegisteredV1(
    Guid EventId,
    string Type,
    int Version,
    DateTime OccurredAt,
    Guid MerchantId,
    Guid TransactionId,
    string EntryType,
    string Amount,
    string Currency,
    DateOnly EffectiveDate,
    Guid? ReversalOf);
