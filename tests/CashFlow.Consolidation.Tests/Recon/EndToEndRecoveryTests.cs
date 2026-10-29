extern alias worker;

using System.Net;
using System.Text.Json;
using CashFlow.Consolidation.Tests.Messaging;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Messaging;
using CashFlow.ReconTool;
using CashFlow.Transactions.Api.Tests.Support;
using Npgsql;
using Xunit.Abstractions;
using WorkerMetrics = worker::CashFlow.Consolidation.Worker.WorkerMetrics;

namespace CashFlow.Consolidation.Tests.Recon;

/// <summary>
/// Ponta a ponta com os quatro componentes reais (transactions-api + publisher do outbox, broker, consolidation-worker,
/// consolidation-api) e o recon-tool: T-14a (broker fora, reconcile antecipa, a entrega tardia é duplicata) e T-03
/// (republicação do outbox absorvida pelo Inbox). Entrega at-least-once; o que se prova é o efeito idempotente.
/// </summary>
[Collection(BrokerCollection.Name)]
public sealed class EndToEndRecoveryTests(RabbitMqFixture broker, DatabasesFixture databases, ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(120);
    private static readonly DateOnly Day = Events.Day;
    private const string DayText = "2026-10-20";

    private static string Entry(string type, string amount) => ApiClient.CreateJson(type, amount, $"\"effectiveDate\":\"{DayText}\"");

    private static async Task PostAsync(TransactionsHost host, Guid merchant, string type, string amount)
    {
        using var response = await ApiClient.PostTransactionAsync(host.Business, merchant, Entry(type, amount), ApiClient.NewKey());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<int> ReconcileAsync(Guid merchant, string expectedFragment)
    {
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ReconCli.SourceKey] = databases.Pooled("recon_ro", DatabasesFixture.TransactionsDb, 2),
            [ReconCli.DefaultKey] = databases.Pooled("recon_writer", DatabasesFixture.ConsolidationDb, 2),
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await ReconCli.RunAsync(
            ["reconcile", "--merchant", merchant.ToString(), "--from", DayText, "--to", DayText],
            key => config.GetValueOrDefault(key), stdout, stderr, CancellationToken.None);
        output.WriteLine($"reconcile => {code}{Environment.NewLine}{stdout}{stderr}");
        Assert.Contains(expectedFragment, stdout.ToString(), StringComparison.Ordinal);
        return code;
    }

    private async Task<long> PublishedAsync(Guid merchant) =>
        await databases.ScalarAsync<long>(
            DatabasesFixture.TransactionsDb, "SELECT count(*) FROM outbox_messages WHERE merchant_id = @m AND published_at IS NOT NULL", ("m", merchant));

    private static async Task<JsonElement> DailyAsync(ConsolidationApiHost api, Guid merchant)
    {
        using var response = await ApiClient.GetAsync(
            api.Business, ConsolidationApiHost.DailyUrl(merchant, $"date={DayText}&include=closingBalance"), Identity.For(merchant, "consolidation.read"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// T-14a: com o broker fora, 100.00 e 35.20 são aceitos (eventos pendentes no outbox); o reconcile antecipa a
    /// projeção a partir da fonte; quando o broker volta, o publisher entrega os eventos e o worker os reconhece como
    /// duplicatas: 100.00 de crédito, não 200.00. Um retroativo de 5.00 leva o fechamento a 69.80, e um novo reconcile
    /// encontra 0 divergências.
    /// </summary>
    [Fact]
    public async Task T14a_BrokerDown_ReconcileCatchesUp_LateDeliveryIsDuplicate_RetroactiveGives69_80()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var transactions = new TransactionsHost(databases, broker).Start();
        await using var worker = new WorkerHost(databases, broker).Start();
        await using var api = new ConsolidationApiHost(databases).Start();
        var merchant = Guid.CreateVersion7();

        await broker.StopAsync();
        try
        {
            await PostAsync(transactions, merchant, "CREDIT", "100.00");
            await PostAsync(transactions, merchant, "DEBIT", "35.20");
            Assert.Equal(0, await PublishedAsync(merchant));
            Assert.Null(await Projection.DailyAsync(databases, merchant, Day));

            Assert.Equal(ReconCli.Ok, await ReconcileAsync(merchant, "catch_up=2"));

            var caughtUp = await DailyAsync(api, merchant);
            Assert.Equal("100.00", caughtUp.GetProperty("credits").GetString());
            Assert.Equal("64.80", caughtUp.GetProperty("closingBalance").GetString());
        }
        finally
        {
            await broker.StartAsync();
        }

        await Eventually.WaitAsync(() => PublishedAsync(merchant), n => n == 2, Wait, "2 eventos publicados após a volta do broker");
        await Eventually.WaitAsync(() => Task.FromResult(metrics.Total("inbox_duplicates_total")), v => v >= 2, Wait, "entrega tardia como duplicata");
        Assert.Equal(0, metrics.Total("consolidation_applied_total"));
        Assert.Equal(new DailyRow(100.00m, 35.20m, 2), await Projection.DailyAsync(databases, merchant, Day));

        await PostAsync(transactions, merchant, "CREDIT", "5.00");
        await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Day), r => r?.Entries == 3, Wait, "retroativo aplicado");

        var after = await DailyAsync(api, merchant);
        Assert.Equal("105.00", after.GetProperty("credits").GetString());
        Assert.Equal("69.80", after.GetProperty("closingBalance").GetString());
        Assert.Equal(ReconCli.Ok, await ReconcileAsync(merchant, "divergencias=0"));
        Assert.Equal(1, metrics.Total("consolidation_applied_total"));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
    }

    /// <summary>T-03 replay: published_at volta a NULL (como numa queda entre o confirm e a transação B) => republicação.</summary>
    [Fact]
    public async Task T03_OutboxReplay_IsRepublished_AndAbsorbedAsDuplicate()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var transactions = new TransactionsHost(databases, broker).Start();
        await using var worker = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();

        await PostAsync(transactions, merchant, "CREDIT", "100.00");
        await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Day), r => r?.Entries == 1, Wait, "lançamento aplicado");

        await using (var connection = await databases.OpenSuperuserAsync(DatabasesFixture.TransactionsDb))
        await using (var replay = new NpgsqlCommand(
            "UPDATE outbox_messages SET published_at = NULL, next_attempt_at = now(), locked_until = NULL, locked_by = NULL WHERE merchant_id = @m",
            connection))
        {
            replay.Parameters.AddWithValue("m", merchant);
            Assert.Equal(1, await replay.ExecuteNonQueryAsync());
        }

        await Eventually.WaitAsync(() => PublishedAsync(merchant), n => n == 1, Wait, "republicado");
        await Eventually.WaitAsync(() => Task.FromResult(metrics.Total("inbox_duplicates_total")), v => v >= 1, Wait, "republicação como duplicata");

        Assert.Equal(1, metrics.Total("consolidation_applied_total"));
        Assert.Equal(new DailyRow(100.00m, 0m, 1), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Equal(1, await Projection.InboxCountAsync(databases, merchant));
        Assert.Equal(ReconCli.Ok, await ReconcileAsync(merchant, "divergencias=0"));
    }
}
