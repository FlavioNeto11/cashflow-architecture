using System.Text;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Messaging;
using RabbitMQ.Client;
using Xunit.Abstractions;

namespace CashFlow.Consolidation.Tests.Messaging;

/// <summary>
/// Caracterização (medida, não suposta) do x-delivery-limit=5 da fila quorum no RabbitMQ 4.4: uma entrega devolvida
/// sem ACK porque o canal fechou (queda do worker entre COMMIT e ACK) CONTA como entrega; na 6ª devolução a mensagem
/// vai para a DLQ pelo dead-lettering at-least-once (não é descartada). Consequência registrada no relatório: quedas
/// repetidas do worker levam o evento à DLQ, de onde o redrive o reaplica sem dupla contagem (Inbox).
/// </summary>
[Collection(BrokerCollection.Name)]
public sealed class DeliveryLimitCharacterizationTests(RabbitMqFixture broker, ITestOutputHelper output)
{
    [Fact]
    public async Task UnackedDelivery_ReturnedByChannelCloseSixTimes_IsDeadLetteredNotDropped()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var connection = await broker.ConnectAsync();
        var messageId = Guid.NewGuid().ToString();
        await using (var publish = await connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions))
        {
            Assert.Equal(PublishOutcome.Confirmed, await ConfirmedPublisher.PublishAsync(
                publish, new PublicationAddress(ExchangeType.Topic, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey),
                new BasicProperties { MessageId = messageId }, Encoding.UTF8.GetBytes("{}"), TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        var deliveryCounts = new List<string>();
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            await using var channel = await connection.CreateChannelAsync();
            var get = await channel.BasicGetAsync(Topology.ConsolidationQueue, autoAck: false);
            Assert.NotNull(get);
            Assert.Equal(attempt > 1, get.Redelivered);
            deliveryCounts.Add(AmqpHeaders.GetString(get.BasicProperties.Headers, "x-delivery-count") ?? "-");
            await channel.CloseAsync(); // devolve sem ACK nem NACK, como a queda do processo
        }

        output.WriteLine("x-delivery-count por entrega: " + string.Join(", ", deliveryCounts));
        Assert.Equal(["-", "1", "2", "3", "4", "5"], deliveryCounts);

        await using (var dlq = await connection.CreateChannelAsync())
        {
            BasicGetResult? dead = null;
            for (var i = 0; i < 20 && dead is null; i++)
            {
                dead = await dlq.BasicGetAsync(Topology.DeadLetterQueue, autoAck: true);
                if (dead is null)
                {
                    await Task.Delay(100);
                }
            }

            Assert.NotNull(dead);
            Assert.Equal(messageId, dead.BasicProperties.MessageId);
            Assert.True(dead.BasicProperties.Headers?.ContainsKey("x-death"));
        }

        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
    }
}
