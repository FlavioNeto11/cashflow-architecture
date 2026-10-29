using System.Text.Json.Nodes;
using CashFlow.Transactions.Api.Application;
using CashFlow.Transactions.Api.Domain;

namespace CashFlow.Transactions.Api.Tests.Domain;

public sealed class OutboxFactoryTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private static Transaction NewTransaction() =>
        Transaction.Register(Guid.CreateVersion7(), EntryType.Credit, Money.Create(35.2m, "BRL").Value, null, null, new Author("s", "c"), Now).Value;

    [Fact]
    public void Create_WithoutTraceparent_KeepsTheKeyWithJsonNull()
    {
        // Comportamento atual: sem Activity o cabeçalho "traceparent" é gravado explicitamente como null
        // (a chave existe). O publisher de F4 deve tratar null como "sem contexto de trace".
        var message = OutboxFactory.Create(NewTransaction(), traceparent: null);

        var headers = JsonNode.Parse(message.Headers)!.AsObject();

        Assert.True(headers.ContainsKey("traceparent"));
        Assert.Null(headers["traceparent"]);
        Assert.Equal(message.Id.ToString(), (string?)headers["message-id"]);
    }

    [Fact]
    public void Create_WithTraceparent_StoresItVerbatim()
    {
        const string traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        var message = OutboxFactory.Create(NewTransaction(), traceparent);

        Assert.Equal(traceparent, (string?)JsonNode.Parse(message.Headers)!["traceparent"]);
    }

    [Fact]
    public void Create_EventIdIsTheMessageIdAndThePayloadEventId_AndRowStartsPending()
    {
        var transaction = NewTransaction();

        var message = OutboxFactory.Create(transaction, null);
        var payload = JsonNode.Parse(message.Payload)!;

        Assert.Equal(message.Id, payload["eventId"]!.GetValue<Guid>());
        Assert.Equal(transaction.Id, message.AggregateId);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(transaction.OccurredAt, message.NextAttemptAt);
        Assert.Equal("35.20", payload["amount"]!.GetValue<string>());
    }
}
