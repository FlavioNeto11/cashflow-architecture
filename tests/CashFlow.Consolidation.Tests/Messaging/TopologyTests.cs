using System.Text;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace CashFlow.Consolidation.Tests.Messaging;

/// <summary>
/// Bloco 2 (F4): a topologia vem de infra/rabbitmq/definitions.json carregado pelo broker, o usuário da aplicação
/// existe mesmo com load_definitions (entrypoint.sh) e os argumentos das filas são os do ADR-0002.
/// </summary>
[Collection(BrokerCollection.Name)]
public sealed class TopologyTests(RabbitMqFixture broker)
{
    [Fact]
    public async Task ApplicationUser_FromEnvironment_CanConnect_WithDefinitionsLoaded()
    {
        await using var connection = await broker.ConnectAsync();

        Assert.True(connection.IsOpen);
        var users = await broker.RabbitmqctlAsync("list_users");
        Assert.Contains(RabbitMqFixture.User, users, StringComparison.Ordinal);
        Assert.DoesNotContain("guest", users, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangesAndQueues_ExistForPassiveDeclaration()
    {
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.ExchangeDeclarePassiveAsync(Topology.TransactionsExchange);
        await channel.ExchangeDeclarePassiveAsync(Topology.DeadLetterExchange);
        await channel.QueueDeclarePassiveAsync(Topology.ConsolidationQueue);
        await channel.QueueDeclarePassiveAsync(Topology.DeadLetterQueue);
    }

    [Fact]
    public async Task PassiveDeclare_OfMissingExchange_ClosesTheChannelWith404()
    {
        // Usuário de operação: o da aplicação não tem configure, e o broker checa permissão antes da existência (403).
        await using var connection = await broker.ConnectAsOperatorAsync();
        await using var channel = await connection.CreateChannelAsync();

        var error = await Assert.ThrowsAsync<OperationInterruptedException>(() => channel.ExchangeDeclarePassiveAsync("nao.existe"));

        Assert.Equal(404, (int)error.ShutdownReason!.ReplyCode);
        Assert.False(channel.IsOpen);
        Assert.True(connection.IsOpen);
    }

    /// <summary>F6 (C-20): o usuário da aplicação não configura topologia nem escreve em filas; só o de operação é administrator.</summary>
    [Fact]
    public async Task ApplicationUser_HasLeastPrivilege_NoConfigure_NoQueueWrite_NoManagementTag()
    {
        await using var connection = await broker.ConnectAsync();

        await using (var channel = await connection.CreateChannelAsync())
        {
            var declare = await Assert.ThrowsAsync<OperationInterruptedException>(() => channel.ExchangeDeclareAsync("nao.existe", ExchangeType.Topic));
            Assert.Equal(403, (int)declare.ShutdownReason!.ReplyCode);
        }

        await using (var channel = await connection.CreateChannelAsync())
        {
            var unbind = await Assert.ThrowsAsync<OperationInterruptedException>(() =>
                channel.QueueUnbindAsync(Topology.ConsolidationQueue, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey));
            Assert.Equal(403, (int)unbind.ShutdownReason!.ReplyCode);
        }

        var permissions = await broker.RabbitmqctlAsync("list_permissions");
        var app = permissions.Split('\n').Single(l => l.StartsWith(RabbitMqFixture.User + "\t", StringComparison.Ordinal)).Split('\t');
        Assert.Equal("^$", app[1]);
        var users = await broker.RabbitmqctlAsync("list_users");
        Assert.Contains(RabbitMqFixture.User + "\t[]", users, StringComparison.Ordinal);
        Assert.Contains(RabbitMqFixture.OpsUser + "\t[administrator]", users, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainQueue_IsQuorumWithDeliveryLimitDlxAndNoMaxLength()
    {
        var queues = await broker.RabbitmqctlAsync("list_queues", "name", "type", "arguments");
        var main = queues.Split('\n').Single(l => l.StartsWith(Topology.ConsolidationQueue + "\t", StringComparison.Ordinal));

        Assert.Contains("quorum", main, StringComparison.Ordinal);
        Assert.Contains("{\"x-delivery-limit\",5}", main, StringComparison.Ordinal);
        Assert.Contains("{\"x-dead-letter-exchange\",\"cashflow.dlx\"}", main, StringComparison.Ordinal);
        Assert.Contains("{\"x-dead-letter-strategy\",\"at-least-once\"}", main, StringComparison.Ordinal);
        Assert.Contains("{\"x-overflow\",\"reject-publish\"}", main, StringComparison.Ordinal);
        Assert.DoesNotContain("x-max-length", main, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeadLetterQueue_IsClassic()
    {
        var queues = await broker.RabbitmqctlAsync("list_queues", "name", "type", "arguments");
        var dlq = queues.Split('\n').Single(l => l.StartsWith(Topology.DeadLetterQueue + "\t", StringComparison.Ordinal));

        // Coluna "type" (não os argumentos: fila classic declarada sem x-queue-type não o lista).
        Assert.Equal("classic", dlq.Split('\t')[1].Trim());
    }

    [Fact]
    public async Task ConsumerTimeout_IsThirtyMinutes()
    {
        var value = await broker.RabbitmqctlAsync("eval", "application:get_env(rabbit, consumer_timeout).");

        Assert.Equal("{ok,1800000}", value.Trim());
    }

    [Fact]
    public async Task ConfirmedPublish_RoutesToMainQueue_AndUnroutableIsReturned()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions);
        var body = Encoding.UTF8.GetBytes("{}");

        var routed = await ConfirmedPublisher.PublishAsync(
            channel, new PublicationAddress(ExchangeType.Topic, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey),
            new BasicProperties { MessageId = Guid.NewGuid().ToString() }, body, TimeSpan.FromSeconds(5), CancellationToken.None);
        var unroutable = await ConfirmedPublisher.PublishAsync(
            channel, new PublicationAddress(ExchangeType.Topic, Topology.TransactionsExchange, "sem.rota.v9"),
            new BasicProperties { MessageId = Guid.NewGuid().ToString() }, body, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(PublishOutcome.Confirmed, routed);
        Assert.Equal(PublishOutcome.Returned, unroutable);
        Assert.True(channel.IsOpen);
        Assert.Equal(1u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
        await broker.PurgeAsync(Topology.ConsolidationQueue);
    }

    [Fact]
    public async Task AnyRoutingKeyOnTheDlx_ReachesTheDlq()
    {
        await broker.PurgeAsync(Topology.DeadLetterQueue);
        await using var connection = await broker.ConnectAsync();
        await using var channel = await connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions);

        var outcome = await ConfirmedPublisher.PublishAsync(
            channel, new PublicationAddress(ExchangeType.Topic, Topology.DeadLetterExchange, Topology.TransactionRegisteredRoutingKey),
            new BasicProperties(), Encoding.UTF8.GetBytes("{}"), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(PublishOutcome.Confirmed, outcome);
        Assert.Equal(1u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
        await broker.PurgeAsync(Topology.DeadLetterQueue);
    }
}

[CollectionDefinition(Name)]
public sealed class BrokerCollection : ICollectionFixture<RabbitMqFixture>, ICollectionFixture<DatabasesFixture>
{
    public const string Name = "broker";
}
