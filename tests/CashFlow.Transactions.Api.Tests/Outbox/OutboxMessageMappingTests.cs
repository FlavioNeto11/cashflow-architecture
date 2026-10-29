using CashFlow.Messaging;
using CashFlow.Transactions.Api.Outbox;

namespace CashFlow.Transactions.Api.Tests.Outbox;

/// <summary>Mapeamento linha do outbox => mensagem AMQP (target.md 8.3), sem broker.</summary>
public sealed class OutboxMessageMappingTests
{
    private static readonly Guid Merchant = Guid.Parse("7c2d0a6e-3f4b-4f7a-9d55-0d2a2d6f1a10");

    private static ClaimedMessage Row(string headers, string type = "TransactionRegistered", int version = 1) =>
        new(Guid.Parse("0b0e7f0c-1e57-4a43-9d8f-3c5a1f0e2b11"), Merchant, type, version, "{}", headers,
            new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc), 0);

    private static string Headers(string traceparentJson) =>
        $$"""{"message-id":"0b0e7f0c-1e57-4a43-9d8f-3c5a1f0e2b11","merchant_id":"{{Merchant}}","content-type":"application/json"{{traceparentJson}}}""";

    [Fact]
    public void TryBuild_MapsIdentityHeadersAndTraceparent()
    {
        var ok = OutboxPublisher.TryBuild(
            Row(Headers(""","traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" """)),
            out var address, out var properties, out _);

        Assert.True(ok);
        Assert.Equal(Topology.TransactionsExchange, address.ExchangeName);
        Assert.Equal(Topology.TransactionRegisteredRoutingKey, address.RoutingKey);
        Assert.Equal("0b0e7f0c-1e57-4a43-9d8f-3c5a1f0e2b11", properties.MessageId);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal("TransactionRegistered", properties.Type);
        Assert.Equal(Merchant.ToString(), properties.Headers![HeaderNames.MerchantId]);
        Assert.Equal("1", properties.Headers["version"]);
        Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", properties.Headers[HeaderNames.Traceparent]);
    }

    [Theory]
    [InlineData(""","traceparent":null""")]
    [InlineData("")]
    public void TryBuild_ToleratesNullOrMissingTraceparent(string traceparentJson)
    {
        var ok = OutboxPublisher.TryBuild(Row(Headers(traceparentJson)), out _, out var properties, out _);

        Assert.True(ok);
        Assert.False(properties.Headers!.ContainsKey(HeaderNames.Traceparent));
    }

    [Theory]
    [InlineData("OtherEvent", 1, "unknown_type")]
    [InlineData("TransactionRegistered", 2, "unknown_type")]
    public void TryBuild_RejectsUnknownTypeOrVersion(string type, int version, string reason)
    {
        Assert.False(OutboxPublisher.TryBuild(Row(Headers(""), type, version), out _, out _, out var actual));
        Assert.Equal(reason, actual);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"message-id":1}""")]
    public void TryBuild_RejectsInvalidHeaders(string headers)
    {
        Assert.False(OutboxPublisher.TryBuild(Row(headers), out _, out _, out var reason));
        Assert.Equal("invalid_headers", reason);
    }

    [Fact]
    public void Backoff_StaysWithinBounds()
    {
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            var delay = Backoff.ForAttempt(attempt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
            Assert.InRange(delay.TotalSeconds, 1, 60);
        }

        Assert.True(Backoff.ForAttempt(20, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)).TotalSeconds >= 30);
    }
}
