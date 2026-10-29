extern alias worker;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CashFlow.Consolidation.Tests.Messaging;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Messaging;
using CashFlow.ReconTool;
using Npgsql;
using RabbitMQ.Client;
using Xunit.Abstractions;
using WorkerMetrics = worker::CashFlow.Consolidation.Worker.WorkerMetrics;

namespace CashFlow.Consolidation.Tests.Recon;

/// <summary>
/// recon-tool real (ReconCli) com os papéis REAIS recon_ro (fonte) e recon_writer (projeção/Inbox/trilha): valida as
/// concessões, a reconciliação por linha (catch_up, corrected_corruption, anomaly, out_of_retention, lock_timeout),
/// o rebuild e o redrive da DLQ (trilha antes de publicar, confirm antes do ACK, sem cabeçalhos x-*).
/// Entrega at-least-once: o que se prova é o efeito idempotente (Inbox), nunca entrega exatamente uma vez.
/// </summary>
[Collection(BrokerCollection.Name)]
public sealed class ReconToolTests(RabbitMqFixture broker, DatabasesFixture databases, ITestOutputHelper output)
{
    private const short Credit = 1;
    private const short Debit = 2;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);
    private static readonly DateOnly Day = Events.Day;

    private sealed record Run(int ExitCode, string Stdout, string Stderr);

    private sealed record RunRow(int Divergences, Dictionary<string, int> Kinds, JsonElement Details)
    {
        public string Scope => Details.GetProperty("scope").GetString()!;
    }

    private async Task<Run> RunAsync(params string[] args)
    {
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ReconCli.SourceKey] = databases.Pooled("recon_ro", DatabasesFixture.TransactionsDb, 2),
            [ReconCli.DefaultKey] = databases.Pooled("recon_writer", DatabasesFixture.ConsolidationDb, 2),
            [RabbitMqOptions.HostKey] = broker.Options.Host,
            [RabbitMqOptions.PortKey] = broker.Options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [RabbitMqOptions.UserKey] = broker.Options.User,
            [RabbitMqOptions.PasswordKey] = broker.Options.Password,
        };
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await ReconCli.RunAsync(args, key => config.GetValueOrDefault(key), stdout, stderr, CancellationToken.None);
        output.WriteLine($"$ recon-tool {string.Join(' ', args)} => {code}{Environment.NewLine}{stdout}{stderr}");
        return new Run(code, stdout.ToString(), stderr.ToString());
    }

    private Task<Run> ReconcileAsync(Guid merchant, string mode = "reconcile") =>
        RunAsync(mode, "--merchant", merchant.ToString(), "--from", "2026-10-01", "--to", "2026-10-31");

    private static void AssertNoMoney(Run run, params string[] amounts)
    {
        foreach (var amount in amounts)
        {
            Assert.DoesNotContain(amount, run.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(amount, run.Stderr, StringComparison.Ordinal);
        }
    }

    /// <summary>Lançamento confirmado direto na fonte (superusuário: simula o que a transactions-api já gravou).</summary>
    private async Task<Guid> SeedAsync(Guid merchant, short entryType, decimal amount, DateTime? occurredAt = null, DateOnly? date = null)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.TransactionsDb);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO transactions (id, merchant_id, entry_type, amount, currency, effective_date, occurred_at, created_by_sub, created_by_client_id, created_at)
            VALUES (@id, @m, @t, @a, 'BRL', @d, @o, 'test-sub', 'test-client', now())
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("m", merchant);
        command.Parameters.AddWithValue("t", entryType);
        command.Parameters.AddWithValue("a", amount);
        command.Parameters.AddWithValue("d", date ?? Day);
        command.Parameters.AddWithValue("o", occurredAt ?? DateTime.UtcNow);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private async Task ConsolidationSqlAsync(string sql, Guid merchant, params (string Name, object Value)[] parameters)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("m", merchant);
        command.Parameters.AddWithValue("d", Day);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<RunRow>> RunsAsync(Guid merchant)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb);
        await using var command = new NpgsqlCommand(
            "SELECT divergences, kinds::text, details::text FROM reconciliation_runs WHERE merchant_id = @m ORDER BY started_at, id", connection);
        command.Parameters.AddWithValue("m", merchant);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<RunRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new RunRow(
                reader.GetInt32(0),
                JsonSerializer.Deserialize<Dictionary<string, int>>(reader.GetString(1))!,
                JsonDocument.Parse(reader.GetString(2)).RootElement.Clone()));
        }

        return rows;
    }

    private async Task<List<(Guid Id, string Consumer, bool NullEventId)>> InboxAsync(Guid merchant)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb);
        await using var command = new NpgsqlCommand(
            "SELECT transaction_id, consumer, event_id IS NULL FROM processed_messages WHERE merchant_id = @m ORDER BY transaction_id", connection);
        command.Parameters.AddWithValue("m", merchant);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(Guid, string, bool)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
        }

        return rows;
    }

    // ---------------------------------------------------------------- reconcile

    /// <summary>
    /// Eventos nunca entregues (ex.: broker fora): reconcile soma M = S \ I e grava o Inbox; a entrega TARDIA dos mesmos
    /// lançamentos pelo worker é reconhecida como duplicata (100.00, não 200.00); a segunda execução tem 0 divergências.
    /// </summary>
    [Fact]
    public async Task Reconcile_CatchUp_ThenLateDeliveryIsDuplicate_AndSecondRunHasZeroDivergences()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        var merchant = Guid.CreateVersion7();
        var credit = await SeedAsync(merchant, Credit, 100.00m);
        var debit = await SeedAsync(merchant, Debit, 35.20m);

        var first = await ReconcileAsync(merchant);

        Assert.Equal(ReconCli.Ok, first.ExitCode);
        AssertNoMoney(first, "100.00", "35.20", "64.80");
        Assert.Contains("catch_up=2", first.Stdout, StringComparison.Ordinal);
        Assert.Equal(new DailyRow(100.00m, 35.20m, 2), await Projection.DailyAsync(databases, merchant, Day));
        var inbox = await InboxAsync(merchant);
        Assert.Equal(2, inbox.Count);
        Assert.Equal(CashFlow.Consolidation.Persistence.ConsolidationSchema.ReconConsumer, Reconciler.Consumer);
        Assert.All(inbox, e => Assert.Equal((Reconciler.Consumer, true), (e.Consumer, e.NullEventId)));
        var runs = await RunsAsync(merchant);
        var row = Assert.Single(runs, r => r.Scope == "row");
        Assert.Equal(2, row.Divergences);
        Assert.Equal(2, row.Kinds["catch_up"]);
        Assert.Equal("100.00", row.Details.GetProperty("after").GetProperty("credits").GetString());
        Assert.Equal(2, Assert.Single(runs, r => r.Scope == "execution").Divergences);

        // Entrega tardia dos MESMOS lançamentos (outros event ids, como uma republicação do outbox): sem efeito.
        await using (var host = new WorkerHost(databases, broker).Start())
        {
            await Events.PublishAsync(broker, new TestEvent(Guid.CreateVersion7(), merchant, credit, "CREDIT", "100.00", Day, DateTime.UtcNow));
            await Events.PublishAsync(broker, new TestEvent(Guid.CreateVersion7(), merchant, debit, "DEBIT", "35.20", Day, DateTime.UtcNow));
            await Eventually.WaitAsync(() => Task.FromResult(metrics.Total("inbox_duplicates_total")), v => v >= 2, Wait, "2 duplicatas no worker");
        }

        Assert.Equal(0, metrics.Total("consolidation_applied_total"));
        Assert.Equal(new DailyRow(100.00m, 35.20m, 2), await Projection.DailyAsync(databases, merchant, Day));

        var second = await ReconcileAsync(merchant);

        Assert.Equal(ReconCli.Ok, second.ExitCode);
        Assert.Contains("divergencias=0", second.Stdout, StringComparison.Ordinal);
        Assert.Equal(0, (await RunsAsync(merchant)).Last(r => r.Scope == "execution").Divergences);
        Assert.Equal(new DailyRow(100.00m, 35.20m, 2), await Projection.DailyAsync(databases, merchant, Day));
    }

    [Fact]
    public async Task Reconcile_CorruptedTotals_WithCompleteInbox_AreRewrittenAbsolutely()
    {
        var merchant = Guid.CreateVersion7();
        await SeedAsync(merchant, Credit, 100.00m);
        await SeedAsync(merchant, Debit, 35.20m);
        Assert.Equal(ReconCli.Ok, (await ReconcileAsync(merchant)).ExitCode);
        await ConsolidationSqlAsync(
            "UPDATE daily_consolidations SET credits = credits + 1.00, entries_count = 7 WHERE merchant_id = @m AND effective_date = @d", merchant);

        var run = await ReconcileAsync(merchant);

        Assert.Equal(ReconCli.Ok, run.ExitCode);
        Assert.Equal(new DailyRow(100.00m, 35.20m, 2), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Equal(2, (await InboxAsync(merchant)).Count);
        var row = (await RunsAsync(merchant)).Last(r => r.Scope == "row");
        Assert.Equal(1, row.Divergences);
        Assert.Equal(1, row.Kinds["corrected_corruption"]);
        Assert.Equal("101.00", row.Details.GetProperty("before").GetProperty("credits").GetString());
        Assert.Equal(ReconCli.Ok, (await ReconcileAsync(merchant)).ExitCode);
        Assert.Equal(0, (await RunsAsync(merchant)).Last(r => r.Scope == "execution").Divergences);
    }

    /// <summary>E = I \ S: lançamento no Inbox sem fonte. ROLLBACK (nem a linha criada pelo upsert do lock fica).</summary>
    [Fact]
    public async Task Reconcile_InboxEntryWithoutSource_IsAnomaly_RolledBack_AndRecordedSeparately()
    {
        var merchant = Guid.CreateVersion7();
        await ConsolidationSqlAsync(
            "INSERT INTO processed_messages VALUES (@t, NULL, @m, @d, 'consolidation-worker', now())", merchant, ("t", Guid.CreateVersion7()));

        var run = await ReconcileAsync(merchant);

        Assert.Equal(ReconCli.Attention, run.ExitCode);
        Assert.Contains("aborted", run.Stdout, StringComparison.Ordinal);
        Assert.Null(await Projection.DailyAsync(databases, merchant, Day));
        var row = Assert.Single(await RunsAsync(merchant), r => r.Scope == "row");
        Assert.Equal(1, row.Kinds["anomaly"]);
        Assert.Equal(0, row.Divergences);
    }

    /// <summary>Fonte com occurred_at além da guarda de 29 d: o Inbox pode ter sido expurgado, somar seria arriscado.</summary>
    [Fact]
    public async Task Reconcile_OutOfRetention_IsAborted_ButRebuildRestoresTheRow()
    {
        var merchant = Guid.CreateVersion7();
        await SeedAsync(merchant, Credit, 100.00m, DateTime.UtcNow.AddDays(-40));
        await SeedAsync(merchant, Debit, 35.20m);

        var reconcile = await ReconcileAsync(merchant);

        Assert.Equal(ReconCli.Attention, reconcile.ExitCode);
        Assert.Contains("out_of_retention=1", reconcile.Stdout, StringComparison.Ordinal);
        Assert.Null(await Projection.DailyAsync(databases, merchant, Day));
        Assert.Empty(await InboxAsync(merchant));

        var rebuild = await ReconcileAsync(merchant, "rebuild");

        Assert.Equal(ReconCli.Ok, rebuild.ExitCode);
        AssertNoMoney(rebuild, "100.00", "35.20", "64.80");
        Assert.Equal(new DailyRow(100.00m, 35.20m, 2), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Equal(2, (await InboxAsync(merchant)).Count);
        Assert.Equal(1, (await RunsAsync(merchant)).Last(r => r.Scope == "row").Kinds["rebuild"]);

        // A guarda só vale para o que seria SOMADO: com o Inbox completo, a linha antiga segue reconciliável.
        var again = await ReconcileAsync(merchant);
        Assert.Equal(ReconCli.Ok, again.ExitCode);
        Assert.Equal(0, (await RunsAsync(merchant)).Last(r => r.Scope == "execution").Divergences);
    }

    /// <summary>Linha travada por outra transação: lock_timeout (2 s do papel) => "linha ativa, repetir", nada gravado.</summary>
    [Fact]
    public async Task Reconcile_RowLockedByAnotherTransaction_AbortsWithLockTimeout_ThenSucceeds()
    {
        var merchant = Guid.CreateVersion7();
        await SeedAsync(merchant, Credit, 100.00m);
        Assert.Equal(ReconCli.Ok, (await ReconcileAsync(merchant)).ExitCode);
        await SeedAsync(merchant, Credit, 5.00m);

        Run locked;
        await using (var holder = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb))
        await using (var transaction = await holder.BeginTransactionAsync())
        {
            await using (var lockRow = new NpgsqlCommand(
                "SELECT 1 FROM daily_consolidations WHERE merchant_id = @m AND effective_date = @d FOR UPDATE", holder, transaction))
            {
                lockRow.Parameters.AddWithValue("m", merchant);
                lockRow.Parameters.AddWithValue("d", Day);
                await lockRow.ExecuteNonQueryAsync();
            }

            locked = await ReconcileAsync(merchant);
            await transaction.RollbackAsync();
        }

        Assert.Equal(ReconCli.Attention, locked.ExitCode);
        Assert.Contains("lock_timeout=1", locked.Stdout, StringComparison.Ordinal);
        Assert.Contains("linha ativa, repetir", locked.Stdout, StringComparison.Ordinal);
        Assert.Contains("posse_lock_ms=0", locked.Stdout, StringComparison.Ordinal); // o lock nunca foi obtido
        Assert.Equal(new DailyRow(100.00m, 0m, 1), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Equal(1, (await RunsAsync(merchant)).Last(r => r.Scope == "row").Kinds["lock_timeout"]);

        var retry = await ReconcileAsync(merchant);

        Assert.Equal(ReconCli.Ok, retry.ExitCode);
        Assert.Equal(new DailyRow(105.00m, 0m, 2), await Projection.DailyAsync(databases, merchant, Day));
    }

    [Fact]
    public async Task Rebuild_ReplacesInboxWithExactlyTheSource_AndWritesAbsoluteTotals()
    {
        var merchant = Guid.CreateVersion7();
        var credit = await SeedAsync(merchant, Credit, 100.00m, DateTime.UtcNow.AddMinutes(-5));
        var debit = await SeedAsync(merchant, Debit, 35.20m);
        Assert.Equal(ReconCli.Ok, (await ReconcileAsync(merchant)).ExitCode);
        await ConsolidationSqlAsync(
            "INSERT INTO processed_messages VALUES (@t, NULL, @m, @d, 'consolidation-worker', now())", merchant, ("t", Guid.CreateVersion7()));
        await ConsolidationSqlAsync("UPDATE daily_consolidations SET debits = 0, entries_count = 1 WHERE merchant_id = @m AND effective_date = @d", merchant);

        var run = await ReconcileAsync(merchant, "rebuild");

        Assert.Equal(ReconCli.Ok, run.ExitCode);
        Assert.Equal(new DailyRow(100.00m, 35.20m, 2), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Equal(new[] { credit, debit }.Order(), (await InboxAsync(merchant)).Select(e => e.Id));
        var lastApplied = await databases.ScalarAsync<DateTime>(
            DatabasesFixture.ConsolidationDb, "SELECT last_applied_at FROM daily_consolidations WHERE merchant_id = @m", ("m", merchant));
        var maxOccurred = await databases.ScalarAsync<DateTime>(
            DatabasesFixture.TransactionsDb, "SELECT max(occurred_at) FROM transactions WHERE merchant_id = @m", ("m", merchant));
        Assert.Equal(maxOccurred, lastApplied);
        var row = (await RunsAsync(merchant)).Last(r => r.Scope == "row");
        Assert.Equal(1, row.Kinds["rebuild"]);
        Assert.Equal(2, row.Divergences); // id extra no Inbox + resíduo nos totais
    }

    [Theory]
    [InlineData]
    [InlineData("reconcile")]
    [InlineData("reconcile", "--merchant", "x", "--from", "2026-10-01", "--to", "2026-10-31")]
    [InlineData("reconcile", "--merchant", "0199a000-0000-7000-8000-000000000001", "--from", "2026-10-31", "--to", "2026-10-01")]
    [InlineData("reconcile", "--merchant", "0199a000-0000-7000-8000-000000000001", "--from", "2025-01-01", "--to", "2026-10-31")]
    [InlineData("rebuild", "--merchant", "0199a000-0000-7000-8000-000000000001", "--from", "2026-10-01")]
    [InlineData("dlq", "redrive")]
    [InlineData("dlq", "redrive", "--id")]
    [InlineData("dlq", "purge", "--all")]
    [InlineData("dlq", "purge", "--all", "--reason", " ")]
    [InlineData("dlq", "purge", "--id", "x", "--reason", "linha\nquebrada")]
    public async Task InvalidArguments_ExitWithUsageError(params string[] args)
    {
        Assert.Equal(ReconCli.UsageError, (await RunAsync(args)).ExitCode);
    }

    [Fact]
    public async Task MissingConnectionString_ExitsWithFailure_NamingTheKeyOnly()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var code = await ReconCli.RunAsync(
            ["reconcile", "--merchant", Guid.CreateVersion7().ToString(), "--from", "2026-10-01", "--to", "2026-10-31"],
            _ => null, stdout, stderr, CancellationToken.None);

        Assert.Equal(ReconCli.Failure, code);
        Assert.Contains(ReconCli.DefaultKey, stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// P2-11: porta do broker não numérica é erro de configuração (FormatException => 64), não falha de execução.
    /// RabbitMqOptions.From é lido antes de qualquer conexão no redrive.
    /// </summary>
    [Fact]
    public async Task InvalidBrokerPort_ExitsWithUsageError()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ReconCli.DefaultKey] = databases.Pooled("recon_writer", DatabasesFixture.ConsolidationDb, 2),
            [RabbitMqOptions.HostKey] = broker.Options.Host,
            [RabbitMqOptions.PortKey] = "porta",
            [RabbitMqOptions.UserKey] = broker.Options.User,
            [RabbitMqOptions.PasswordKey] = broker.Options.Password,
        };

        var code = await ReconCli.RunAsync(["dlq", "redrive", "--all"], key => config.GetValueOrDefault(key), stdout, stderr, CancellationToken.None);

        Assert.Equal(ReconCli.UsageError, code);
        Assert.Contains("configuração inválida", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(broker.Options.Password, stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>P2-11: cancelamento (Ctrl-C) sai com 1 e mensagem curta, sem exceção não tratada.</summary>
    [Fact]
    public async Task Cancelled_ExitsWithFailure()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var config = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ReconCli.SourceKey] = databases.Pooled("recon_ro", DatabasesFixture.TransactionsDb, 2),
            [ReconCli.DefaultKey] = databases.Pooled("recon_writer", DatabasesFixture.ConsolidationDb, 2),
        };

        var code = await ReconCli.RunAsync(
            ["reconcile", "--merchant", Guid.CreateVersion7().ToString(), "--from", "2026-10-01", "--to", "2026-10-31"],
            key => config.GetValueOrDefault(key), stdout, stderr, cancelled.Token);

        Assert.Equal(ReconCli.Failure, code);
        Assert.Contains("cancelado", stderr.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// P1-1(c): 22003 durante o reconcile (soma de M acima do teto da coluna) aborta a linha como projection_overflow
    /// (exit 2, rollback: nem projeção nem Inbox mudam). O rebuild grava o absoluto da fonte, que cabe.
    /// </summary>
    [Fact]
    public async Task Reconcile_ProjectionOverflow_AbortsWithAttention_ThenRebuildRestoresTheRow()
    {
        var merchant = Guid.CreateVersion7();
        var maxAmount = CashFlow.Transactions.Api.Domain.Money.MaxAmount;
        await SeedAsync(merchant, Credit, maxAmount);
        var ceiling = await Projection.CreditsColumnCeilingAsync(databases);
        await Projection.SeedDailyAsync(databases, merchant, Day, ceiling, entries: 0); // corrompida e sem Inbox

        var run = await ReconcileAsync(merchant);

        Assert.Equal(ReconCli.Attention, run.ExitCode);
        AssertNoMoney(run, Projection.MoneyCeiling);
        var row = Assert.Single(await RunsAsync(merchant), r => r.Scope == "row");
        Assert.Equal(1, row.Kinds["projection_overflow"]);
        Assert.Equal(new DailyRow(ceiling, 0m, 0), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Empty(await InboxAsync(merchant));

        var rebuild = await ReconcileAsync(merchant, "rebuild");

        Assert.Equal(ReconCli.Ok, rebuild.ExitCode);
        Assert.Equal(new DailyRow(maxAmount, 0m, 1), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Single(await InboxAsync(merchant));
    }

    // ---------------------------------------------------------------- dlq redrive

    private static TestEvent NewEvent(Guid merchant, DateTime? occurredAt = null) =>
        new(Guid.CreateVersion7(), merchant, Guid.CreateVersion7(), "CREDIT", "100.00", Day, occurredAt ?? DateTime.UtcNow);

    /// <summary>Simula a cópia que o worker faz para cashflow.dlx (propriedades originais + diagnósticos x-*).</summary>
    private async Task DeadLetterAsync(TestEvent message, string reason)
    {
        await using var connection = await broker.ConnectAsync("tests-dlx");
        await using var channel = await connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions);
        var properties = new BasicProperties
        {
            MessageId = message.EventId.ToString(),
            ContentType = TransactionRegisteredEvent.ContentType,
            Type = TransactionRegisteredEvent.Type,
            Timestamp = new AmqpTimestamp(1_790_000_000),
            Headers = new Dictionary<string, object?>
            {
                [HeaderNames.MerchantId] = message.MerchantId.ToString(),
                [HeaderNames.Traceparent] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
                ["event_version"] = "1",
                [HeaderNames.Reason] = reason,
                [HeaderNames.ReasonDetail] = "version",
                [HeaderNames.OriginalRoutingKey] = Topology.TransactionRegisteredRoutingKey,
                [HeaderNames.Worker] = "worker-test",
                [HeaderNames.FailedAt] = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["x-delivery-count"] = 3,
            },
        };
        Assert.Equal(PublishOutcome.Confirmed, await ConfirmedPublisher.PublishAsync(
            channel, new PublicationAddress(ExchangeType.Topic, Topology.DeadLetterExchange, Topology.TransactionRegisteredRoutingKey),
            properties, Encoding.UTF8.GetBytes(message.Json), TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    private async Task<List<BasicGetResult>> DrainAsync(string queue)
    {
        await using var connection = await broker.ConnectAsync("tests-drain");
        await using var channel = await connection.CreateChannelAsync();
        var results = new List<BasicGetResult>();
        while (await channel.BasicGetAsync(queue, autoAck: true) is { } result)
        {
            results.Add(result);
        }

        return results;
    }

    private Task WaitMessagesAsync(string queue, uint expected) =>
        Eventually.WaitAsync(() => broker.QueueStateAsync(queue), s => s.Messages == expected, Wait, $"{expected} em {queue}");

    [Fact]
    public async Task Redrive_RepublishesWithOriginalIdentity_WithoutXHeaders_ThenAcksTheDlq()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        var merchant = Guid.CreateVersion7();
        var message = NewEvent(merchant);
        await DeadLetterAsync(message, DeadLetterReasons.UnsupportedVersion);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);

        var run = await RunAsync("dlq", "redrive", "--all");

        Assert.Equal(ReconCli.Ok, run.ExitCode);
        AssertNoMoney(run, "100.00");
        await WaitMessagesAsync(Topology.DeadLetterQueue, 0);
        var republished = Assert.Single(await DrainAsync(Topology.ConsolidationQueue));
        var properties = republished.BasicProperties;
        Assert.Equal(message.EventId.ToString(), properties.MessageId);
        Assert.Equal(TransactionRegisteredEvent.ContentType, properties.ContentType);
        Assert.Equal(TransactionRegisteredEvent.Type, properties.Type);
        Assert.Equal(1_790_000_000, properties.Timestamp.UnixTime);
        Assert.Equal(Topology.TransactionRegisteredRoutingKey, republished.RoutingKey);
        Assert.Equal(message.Json, Encoding.UTF8.GetString(republished.Body.Span));
        Assert.Equal(merchant.ToString(), AmqpHeaders.GetString(properties.Headers, HeaderNames.MerchantId));
        Assert.Equal("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01", AmqpHeaders.GetString(properties.Headers, HeaderNames.Traceparent));
        Assert.Equal("1", AmqpHeaders.GetString(properties.Headers, "event_version"));
        // Nenhum x-* da passagem anterior. A única exceção é o x-dotnet-pub-seq-no que o próprio RabbitMQ.Client 7 grava em
        // TODA publicação com rastreamento de confirmações (correlação do basic.return); o redrive remove o antigo e o
        // cliente grava um novo.
        Assert.Equal(
            ["x-dotnet-pub-seq-no"],
            properties.Headers!.Keys.Where(key => key.StartsWith("x-", StringComparison.Ordinal)));

        var trail = Assert.Single(await RunsAsync(merchant));
        Assert.Equal(1, trail.Kinds["dlq_redrive"]);
        Assert.Equal(message.EventId.ToString(), trail.Details.GetProperty("messageId").GetString());
        Assert.Equal(DeadLetterReasons.UnsupportedVersion, trail.Details.GetProperty("xReason").GetString());
    }

    /// <summary>Sem binding: return do broker => trilha gravada, sem ACK, mensagem continua na DLQ, saída 1.</summary>
    [Fact]
    public async Task Redrive_Unroutable_IsNotAcked_StaysInDlq_AndFailureIsRecorded()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        var merchant = Guid.CreateVersion7();
        await DeadLetterAsync(NewEvent(merchant), DeadLetterReasons.UnsupportedVersion);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);

        Run run;
        await using (var connection = await broker.ConnectAsOperatorAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueUnbindAsync(Topology.ConsolidationQueue, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey);
            try
            {
                run = await RunAsync("dlq", "redrive", "--all");
            }
            finally
            {
                await channel.QueueBindAsync(Topology.ConsolidationQueue, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey);
            }
        }

        Assert.Equal(ReconCli.Failure, run.ExitCode);
        Assert.Contains("Returned", run.Stdout, StringComparison.Ordinal);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
        var kinds = (await RunsAsync(merchant)).SelectMany(r => r.Kinds.Keys).ToList();
        Assert.Equal(["dlq_redrive", "redrive_failed"], kinds); // trilha ANTES de publicar, falha depois
        await broker.PurgeAsync(Topology.DeadLetterQueue);
    }

    [Fact]
    public async Task Redrive_RefusesStaleAndOutOfRetention_LeavingThemInDlq()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        var merchant = Guid.CreateVersion7();
        await DeadLetterAsync(NewEvent(merchant), DeadLetterReasons.Stale);
        await DeadLetterAsync(NewEvent(merchant, DateTime.UtcNow.AddDays(-40)), DeadLetterReasons.UnsupportedVersion);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 2);

        var run = await RunAsync("dlq", "redrive", "--all");

        Assert.Equal(ReconCli.Attention, run.ExitCode);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 2);
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
        var runs = await RunsAsync(merchant);
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal(1, r.Kinds["out_of_retention"]));
        await broker.PurgeAsync(Topology.DeadLetterQueue);
    }

    /// <summary>P1-1(b): projection_overflow não é transitório; o redrive recusa e a mensagem fica na DLQ.</summary>
    [Fact]
    public async Task Redrive_RefusesProjectionOverflow_LeavingItInDlq()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        var merchant = Guid.CreateVersion7();
        await DeadLetterAsync(NewEvent(merchant), DeadLetterReasons.ProjectionOverflow);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);

        var run = await RunAsync("dlq", "redrive", "--all");

        Assert.Equal(ReconCli.Attention, run.ExitCode);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
        var trail = Assert.Single(await RunsAsync(merchant));
        Assert.Equal(1, trail.Kinds["projection_overflow"]);
        await broker.PurgeAsync(Topology.DeadLetterQueue);
    }

    [Fact]
    public async Task RedriveById_RepublishesOnlyThatMessage_AndUnknownIdIsReported()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        var merchant = Guid.CreateVersion7();
        var target = NewEvent(merchant);
        var other = NewEvent(merchant);
        await DeadLetterAsync(other, DeadLetterReasons.UnsupportedVersion);
        await DeadLetterAsync(target, DeadLetterReasons.UnsupportedVersion);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 2);

        var run = await RunAsync("dlq", "redrive", "--id", target.EventId.ToString());
        var unknown = await RunAsync("dlq", "redrive", "--id", Guid.CreateVersion7().ToString());

        Assert.Equal(ReconCli.Ok, run.ExitCode);
        Assert.Equal(ReconCli.Attention, unknown.ExitCode);
        Assert.Equal(target.EventId.ToString(), Assert.Single(await DrainAsync(Topology.ConsolidationQueue)).BasicProperties.MessageId);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);
        Assert.Equal(other.EventId.ToString(), Assert.Single(await DrainAsync(Topology.DeadLetterQueue)).BasicProperties.MessageId);
    }

    /// <summary>
    /// PF-09: descarte auditado. Cada mensagem removida da DLQ tem uma linha dlq_purge em reconciliation_runs com
    /// message-id, x-reason, routing key, SHA-256 do corpo e o motivo do operador; nada é republicado e nenhum valor
    /// vai para a saída.
    /// </summary>
    [Fact]
    public async Task Purge_RecordsTrailPerMessage_ThenAcks_WithoutRepublishing()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        var merchant = Guid.CreateVersion7();
        var first = NewEvent(merchant);
        var second = NewEvent(merchant);
        await DeadLetterAsync(first, DeadLetterReasons.UnsupportedVersion);
        await DeadLetterAsync(second, DeadLetterReasons.Stale);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 2);

        var run = await RunAsync("dlq", "purge", "--all", "--reason", "incidente de teste: payload descartado");

        Assert.Equal(ReconCli.Ok, run.ExitCode);
        AssertNoMoney(run, "100.00");
        await WaitMessagesAsync(Topology.DeadLetterQueue, 0);
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
        var trails = await RunsAsync(merchant);
        Assert.Equal(2, trails.Count);
        Assert.All(trails, t =>
        {
            Assert.Equal(1, t.Kinds["dlq_purge"]);
            Assert.Equal(0, t.Divergences);
            Assert.Equal("dlq_purge", t.Details.GetProperty("mode").GetString());
            Assert.Equal("incidente de teste: payload descartado", t.Details.GetProperty("operatorReason").GetString());
            Assert.Equal(Topology.TransactionRegisteredRoutingKey, t.Details.GetProperty("routingKey").GetString());
        });
        var byId = trails.ToDictionary(t => t.Details.GetProperty("messageId").GetString()!);
        Assert.Equal(DeadLetterReasons.UnsupportedVersion, byId[first.EventId.ToString()].Details.GetProperty("xReason").GetString());
        Assert.Equal(DeadLetterReasons.Stale, byId[second.EventId.ToString()].Details.GetProperty("xReason").GetString());
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first.Json))),
            byId[first.EventId.ToString()].Details.GetProperty("bodySha256").GetString());
    }

    [Fact]
    public async Task PurgeById_RemovesOnlyThatMessage_AndUnknownIdIsReported()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        var merchant = Guid.CreateVersion7();
        var target = NewEvent(merchant);
        var other = NewEvent(merchant);
        await DeadLetterAsync(other, DeadLetterReasons.UnsupportedVersion);
        await DeadLetterAsync(target, DeadLetterReasons.UnsupportedVersion);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 2);

        var run = await RunAsync("dlq", "purge", "--id", target.EventId.ToString(), "--reason", "teste");
        var unknown = await RunAsync("dlq", "purge", "--id", Guid.CreateVersion7().ToString(), "--reason", "teste");

        Assert.Equal(ReconCli.Ok, run.ExitCode);
        Assert.Equal(ReconCli.Attention, unknown.ExitCode);
        var trail = Assert.Single(await RunsAsync(merchant));
        Assert.Equal(target.EventId.ToString(), trail.Details.GetProperty("messageId").GetString());
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);
        Assert.Equal(other.EventId.ToString(), Assert.Single(await DrainAsync(Topology.DeadLetterQueue)).BasicProperties.MessageId);
    }

    /// <summary>
    /// Recuperação de ponta a ponta da queda entre COMMIT e ACK repetida: a entrega volta sem ACK seis vezes (canal
    /// fechado, como a queda do processo), o broker a manda à DLQ pelo x-delivery-limit (só x-death, sem
    /// x-original-routing-key); o redrive usa a routing key do x-death e o worker aplica UMA vez. Uma segunda cópia
    /// (redrive repetido após queda entre confirm e ACK) é absorvida pelo Inbox: 100.00, não 200.00.
    /// </summary>
    [Fact]
    public async Task DeliveryLimitDeadLetter_RedrivenViaXDeath_IsAppliedOnce_AndRepeatedRedriveIsDuplicate()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        var merchant = Guid.CreateVersion7();
        var message = NewEvent(merchant);
        await Events.PublishAsync(broker, message);
        await using (var connection = await broker.ConnectAsync())
        {
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                await using var channel = await connection.CreateChannelAsync();
                // Após o fechamento do canal anterior a fila quorum reenfileira a entrega de forma assíncrona: um basic.get
                // imediato pode voltar vazio (observado de forma intermitente); espera a redelivery ficar visível.
                await Eventually.WaitAsync(
                    () => channel.BasicGetAsync(Topology.ConsolidationQueue, autoAck: false), got => got is not null, Wait,
                    $"entrega {attempt} visível para basic.get");
                await channel.CloseAsync();
            }
        }

        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);
        var first = await RunAsync("dlq", "redrive", "--all");
        Assert.Equal(ReconCli.Ok, first.ExitCode);
        var trail = Assert.Single(await RunsAsync(merchant));
        Assert.Equal(Topology.TransactionRegisteredRoutingKey, trail.Details.GetProperty("routingKey").GetString());
        Assert.Equal(JsonValueKind.Array, trail.Details.GetProperty("xDeath").ValueKind);

        // Mesma mensagem de novo na DLQ (cópia que sobraria se o ACK da DLQ se perdesse depois do confirm).
        await DeadLetterAsync(message, DeadLetterReasons.UnsupportedVersion);
        await WaitMessagesAsync(Topology.DeadLetterQueue, 1);
        Assert.Equal(ReconCli.Ok, (await RunAsync("dlq", "redrive", "--all")).ExitCode);

        await using (var host = new WorkerHost(databases, broker).Start())
        {
            await Eventually.WaitAsync(() => Task.FromResult(metrics.Total("inbox_duplicates_total")), v => v >= 1, Wait, "segunda cópia como duplicata");
        }

        Assert.Equal(1, metrics.Total("consolidation_applied_total"));
        Assert.Equal(new DailyRow(100.00m, 0m, 1), await Projection.DailyAsync(databases, merchant, Day));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
    }
}
