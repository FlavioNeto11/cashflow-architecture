using System.Net;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Messaging;
using CashFlow.Transactions.Api.Tests.Support;
using Npgsql;
using RabbitMQ.Client;
using Xunit.Abstractions;

namespace CashFlow.Consolidation.Tests.Messaging;

/// <summary>
/// Publisher do outbox ponta a ponta: API real + Postgres real + RabbitMQ real (mesmos arquivos de infra/).
/// Cobre a publicação confirmada, T-05 (broker fora: lançamento aceito, evento pendente, readiness saudável, publicação
/// após a volta) e T-15 (unroutable: evento não é descartado; tentativas crescem; publica quando o binding volta).
/// </summary>
[Collection(BrokerCollection.Name)]
public sealed class OutboxPublisherTests(RabbitMqFixture broker, DatabasesFixture databases, ITestOutputHelper output)
{
    private const string Credit = """{"type":"CREDIT","amount":"100.00"}""";

    private sealed record OutboxRow(Guid Id, bool Published, int Attempts, string? LastError, string Payload)
    {
        public override string ToString() => $"published={Published} attempts={Attempts} last_error={LastError ?? "null"}";
    }

    [Fact]
    public async Task RegisteredTransaction_IsPublishedWithConfirm_WithIdentityHeaders_AndMarkedPublished()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var host = new TransactionsHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();

        var response = await ApiClient.PostTransactionAsync(host.Business, merchant, Credit, ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var row = await Eventually.WaitAsync(() => OutboxAsync(merchant), r => r?.Published == true, TimeSpan.FromSeconds(30), "published_at preenchido");
        Assert.Equal(0, row!.Attempts);
        Assert.Null(row.LastError);

        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        var delivery = await channel.BasicGetAsync(Topology.ConsolidationQueue, autoAck: true);
        Assert.NotNull(delivery);
        Assert.Equal(row.Id.ToString(), delivery.BasicProperties.MessageId);
        Assert.Equal(TransactionRegisteredEvent.Type, delivery.BasicProperties.Type);
        Assert.Equal(TransactionRegisteredEvent.ContentType, delivery.BasicProperties.ContentType);
        Assert.Equal(DeliveryModes.Persistent, delivery.BasicProperties.DeliveryMode);
        Assert.Equal(merchant.ToString(), AmqpHeaders.GetString(delivery.BasicProperties.Headers, HeaderNames.MerchantId));
        Assert.Equal(Topology.TransactionRegisteredRoutingKey, delivery.RoutingKey);

        // O corpo publicado é o payload gravado na mesma transação do lançamento, e passa no contrato do consumidor.
        var validation = TransactionRegisteredContract.Validate(delivery.Body.Span, merchant.ToString(), row.Id.ToString());
        Assert.Null(validation.Reason);
        Assert.Equal(100.00m, validation.Event!.Amount);
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
    }

    [Fact]
    public async Task T05_BrokerDown_TransactionAccepted_EventPending_ReadyHealthy_ThenPublishedOnce()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var host = new TransactionsHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();

        await broker.StopAsync();
        try
        {
            var response = await ApiClient.PostTransactionAsync(host.Business, merchant, Credit, ApiClient.NewKey());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var pending = await Eventually.WaitAsync(
                () => OutboxAsync(merchant), r => r is { Published: false, Attempts: >= 1 }, TimeSpan.FromSeconds(30),
                "tentativa registrada com o broker fora");
            output.WriteLine($"Broker fora: {pending}");
            Assert.Equal("broker_unavailable", pending!.LastError);

            Assert.Equal(HttpStatusCode.OK, (await host.Health.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await host.Health.GetAsync("/health/live")).StatusCode);
        }
        finally
        {
            await broker.StartAsync();
        }

        var published = await Eventually.WaitAsync(
            () => OutboxAsync(merchant), r => r?.Published == true, TimeSpan.FromSeconds(120), "publicação após a volta do broker");
        output.WriteLine($"Broker de volta: {published}");
        Assert.Equal(1u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
        await broker.PurgeAsync(Topology.ConsolidationQueue);
    }

    [Fact]
    public async Task T15_Unroutable_IsNeverDropped_AttemptsGrow_ThenPublishedWhenBindingReturns()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var host = new TransactionsHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();

        await using (var connection = await broker.ConnectAsOperatorAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueUnbindAsync(Topology.ConsolidationQueue, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey);
            try
            {
                var response = await ApiClient.PostTransactionAsync(host.Business, merchant, Credit, ApiClient.NewKey());
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);

                var failing = await Eventually.WaitAsync(
                    () => OutboxAsync(merchant), r => r is { Published: false, Attempts: >= 2 }, TimeSpan.FromSeconds(30),
                    "duas tentativas unroutable");
                output.WriteLine($"Sem binding: {failing}");
                Assert.Equal("unroutable", failing!.LastError);
                Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
            }
            finally
            {
                // A coleção compartilha o broker: o binding volta mesmo se o teste falhar.
                await channel.QueueBindAsync(Topology.ConsolidationQueue, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey);
            }
        }

        var published = await Eventually.WaitAsync(
            () => OutboxAsync(merchant), r => r?.Published == true, TimeSpan.FromSeconds(90), "publicação após restaurar o binding");
        output.WriteLine($"Binding restaurado: {published}");
        Assert.Equal(1u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
        await broker.PurgeAsync(Topology.ConsolidationQueue);
    }

    private async Task<OutboxRow?> OutboxAsync(Guid merchant)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.TransactionsDb);
        await using var command = new NpgsqlCommand(
            "SELECT id, published_at IS NOT NULL, attempts, last_error, payload::text FROM outbox_messages WHERE merchant_id = @m", connection);
        command.Parameters.AddWithValue("m", merchant);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var row = new OutboxRow(reader.GetGuid(0), reader.GetBoolean(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));
        Assert.False(await reader.ReadAsync(), "um lançamento, um evento no outbox");
        return row;
    }
}
