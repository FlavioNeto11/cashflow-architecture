using System.Globalization;
using System.Text;
using CashFlow.Messaging;
using RabbitMQ.Client;

namespace CashFlow.Consolidation.Tests.Support;

/// <summary>Um TransactionRegistered v1 no formato do OutboxFactory (target.md 8.3), para publicar direto no broker.</summary>
public sealed record TestEvent(
    Guid EventId, Guid MerchantId, Guid TransactionId, string EntryType, string Amount, DateOnly EffectiveDate, DateTime OccurredAt)
{
    public static TestEvent Credit(Guid merchant, string amount, DateOnly date) =>
        new(Guid.CreateVersion7(), merchant, Guid.CreateVersion7(), "CREDIT", amount, date, DateTime.UtcNow);

    public static TestEvent Debit(Guid merchant, string amount, DateOnly date) =>
        new(Guid.CreateVersion7(), merchant, Guid.CreateVersion7(), "DEBIT", amount, date, DateTime.UtcNow);

    public string Json =>
        $$"""{"eventId":"{{EventId}}","type":"TransactionRegistered","version":1,"occurredAt":"{{OccurredAt.ToString("O", CultureInfo.InvariantCulture)}}","merchantId":"{{MerchantId}}","transactionId":"{{TransactionId}}","entryType":"{{EntryType}}","amount":"{{Amount}}","currency":"BRL","effectiveDate":"{{EffectiveDate:yyyy-MM-dd}}","reversalOf":null}""";
}

public static class Events
{
    public static readonly DateOnly Day = new(2026, 10, 20);

    /// <summary>Publica com confirm no exchange de lançamentos, com as propriedades/cabeçalhos do publisher real.</summary>
    public static async Task PublishAsync(
        RabbitMqFixture broker, TestEvent message, string? body = null, string? headerMerchant = null, string? messageId = null)
    {
        await using var connection = await broker.ConnectAsync("tests-publisher");
        await using var channel = await connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions);
        var properties = new BasicProperties
        {
            MessageId = messageId ?? message.EventId.ToString(),
            ContentType = TransactionRegisteredEvent.ContentType,
            Type = TransactionRegisteredEvent.Type,
            Headers = new Dictionary<string, object?> { [HeaderNames.MerchantId] = headerMerchant ?? message.MerchantId.ToString() },
        };
        var outcome = await ConfirmedPublisher.PublishAsync(
            channel, new PublicationAddress(ExchangeType.Topic, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey),
            properties, Encoding.UTF8.GetBytes(body ?? message.Json), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(PublishOutcome.Confirmed, outcome);
    }
}
