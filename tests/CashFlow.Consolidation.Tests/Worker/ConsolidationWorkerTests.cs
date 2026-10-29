extern alias worker;

using System.Net;
using CashFlow.Consolidation.Tests.Messaging;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Messaging;
using Npgsql;
using RabbitMQ.Client;
using Xunit.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ConsumerHealthCheck = worker::CashFlow.Consolidation.Worker.ConsumerHealthCheck;
using WorkerMetrics = worker::CashFlow.Consolidation.Worker.WorkerMetrics;

namespace CashFlow.Consolidation.Tests.Worker;

/// <summary>
/// consolidation-worker real contra Postgres e RabbitMQ reais. Entrega at-least-once; o que se prova aqui é o EFEITO
/// idempotente (Inbox), a DLQ para mensagens inválidas e a pausa (sem ACK, sem DLQ) em falha de infraestrutura.
/// </summary>
[Collection(BrokerCollection.Name)]
public sealed class ConsolidationWorkerTests(RabbitMqFixture broker, DatabasesFixture databases, ITestOutputHelper output)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Oracle_Credit100_Debit35_20_GivesNet64_80()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var host = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();

        await Events.PublishAsync(broker, TestEvent.Credit(merchant, "100.00", Events.Day));
        await Events.PublishAsync(broker, TestEvent.Debit(merchant, "35.20", Events.Day));

        var row = await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Events.Day), r => r?.Entries == 2, Wait, "2 lançamentos aplicados");
        Assert.Equal(100.00m, row!.Credits);
        Assert.Equal(35.20m, row.Debits);
        Assert.Equal(64.80m, row.Net);
        Assert.Equal(2, await Projection.InboxCountAsync(databases, merchant));
    }

    [Fact]
    public async Task T10_DuplicateDelivery_IsAppliedOnce_AndCountedAsDuplicate()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var host = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();
        var message = TestEvent.Credit(merchant, "100.00", Events.Day);

        await Events.PublishAsync(broker, message);
        await Events.PublishAsync(broker, message); // mesma mensagem publicada duas vezes (ex.: confirm perdido no publisher)

        await Eventually.WaitAsync(() => Task.FromResult(metrics.Total("inbox_duplicates_total")), v => v >= 1, Wait, "duplicata reconhecida");
        var row = await Projection.DailyAsync(databases, merchant, Events.Day);
        Assert.Equal(new DailyRow(100.00m, 0m, 1), row);
        Assert.Equal(1, await Projection.InboxCountAsync(databases, merchant));
        Assert.Equal(1, metrics.Total("consolidation_applied_total"));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
    }

    [Fact]
    public async Task T13_InvalidPayload_AndMerchantMismatch_GoToDlqWithDiagnostics_AndNothingIsApplied()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var host = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();
        var badAmount = TestEvent.Credit(merchant, "1.234", Events.Day);
        var mismatch = TestEvent.Credit(merchant, "10.00", Events.Day);

        await Events.PublishAsync(broker, badAmount);
        await Events.PublishAsync(broker, mismatch, headerMerchant: Guid.CreateVersion7().ToString());

        var dead = await DrainDeadLettersAsync(expected: 2);
        var byId = dead.ToDictionary(d => d.BasicProperties.MessageId!);
        AssertDeadLetter(byId[badAmount.EventId.ToString()], DeadLetterReasons.InvalidPayload, "amount", badAmount.Json);
        AssertDeadLetter(byId[mismatch.EventId.ToString()], DeadLetterReasons.MerchantMismatch, HeaderNames.MerchantId, mismatch.Json);
        Assert.Equal(1, metrics.Total("dlq_messages_total", "reason", DeadLetterReasons.InvalidPayload));
        Assert.Equal(1, metrics.Total("dlq_messages_total", "reason", DeadLetterReasons.MerchantMismatch));
        Assert.Null(await Projection.DailyAsync(databases, merchant, Events.Day));
        Assert.Equal(0, await Projection.InboxCountAsync(databases, merchant));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
    }

    [Fact]
    public async Task StaleEvent_BeyondInboxRetention_GoesToDlqAsStale()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var host = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();
        var stale = TestEvent.Credit(merchant, "10.00", Events.Day) with { OccurredAt = DateTime.UtcNow.AddDays(-40) };

        await Events.PublishAsync(broker, stale);

        var dead = Assert.Single(await DrainDeadLettersAsync(expected: 1));
        AssertDeadLetter(dead, DeadLetterReasons.Stale, "occurredAt", stale.Json);
        Assert.Null(await Projection.DailyAsync(databases, merchant, Events.Day));
    }

    /// <summary>
    /// Dois lançamentos no teto de Money para o mesmo comerciante/dia: com numeric(18,2) a soma estourava a projeção
    /// (22003, poison). Com numeric(24,2) (migração WidenProjectionTotals) os dois são aplicados.
    /// </summary>
    [Fact]
    public async Task TwoCreditsAtMoneyCeiling_SameMerchantAndDay_AreBothApplied_InWidenedProjection()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var host = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();

        await Events.PublishAsync(broker, TestEvent.Credit(merchant, Projection.MoneyCeiling, Events.Day));
        await Events.PublishAsync(broker, TestEvent.Credit(merchant, Projection.MoneyCeiling, Events.Day));

        var row = await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Events.Day), r => r?.Entries == 2, Wait, "2 lançamentos no teto aplicados");
        Assert.Equal(19_999_999_999_999_999.98m, row!.Credits);
        Assert.Equal(2, await Projection.InboxCountAsync(databases, merchant));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
    }

    /// <summary>
    /// P1-1: SQLSTATE 22003 (soma acima do teto da coluna) é determinístico. Antes a entrega pausava, a sonda passava e
    /// o mesmo erro voltava para sempre (poison travando a fila). Agora vai à DLQ (projection_overflow), ACK após o
    /// confirm, SEM pausa, e o próximo evento de outro comerciante é aplicado. A linha é posta no teto da coluna lido
    /// do catálogo, então o caminho 22003 é exercitado de fato qualquer que seja a precisão.
    /// </summary>
    [Fact]
    public async Task ProjectionOverflow_22003_GoesToDlq_WithoutPause_AndNextMerchantIsApplied()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var host = new WorkerHost(databases, broker).Start();
        var full = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var ceiling = await Projection.CreditsColumnCeilingAsync(databases);
        await Projection.SeedDailyAsync(databases, full, Events.Day, ceiling, entries: 1);
        await Eventually.WaitAsync(() => broker.RegisteredConsumersAsync(Topology.ConsolidationQueue), n => n == 1, Wait, "worker consumindo");
        var overflow = TestEvent.Credit(full, Projection.MoneyCeiling, Events.Day);

        await Events.PublishAsync(broker, overflow);
        var dead = Assert.Single(await DrainDeadLettersAsync(expected: 1));
        await Events.PublishAsync(broker, TestEvent.Credit(other, "10.00", Events.Day));

        AssertDeadLetter(dead, DeadLetterReasons.ProjectionOverflow, "22003", overflow.Json);
        var applied = await Eventually.WaitAsync(() => Projection.DailyAsync(databases, other, Events.Day), r => r?.Entries == 1, Wait, "outro comerciante aplicado");
        Assert.Equal(new DailyRow(10.00m, 0m, 1), applied);
        Assert.Equal(1, await broker.RegisteredConsumersAsync(Topology.ConsolidationQueue));
        Assert.Equal(0, metrics.Total("consumer_paused_seconds")); // nenhuma pausa registrada
        Assert.Equal(1, metrics.Total("dlq_messages_total", "reason", DeadLetterReasons.ProjectionOverflow));
        Assert.Equal(new DailyRow(ceiling, 0m, 1), await Projection.DailyAsync(databases, full, Events.Day)); // rollback
        Assert.Equal(0, await Projection.InboxCountAsync(databases, full));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.ConsolidationQueue)).Messages);
    }

    /// <summary>
    /// Classe 23 (aqui 23514, CHECK temporária NOT VALID restrita a um comerciante do teste): DLQ como
    /// constraint_violation com o NOME da restrição no x-reason-detail (sem valores), sem pausa.
    /// </summary>
    [Fact]
    public async Task CheckViolation_23514_GoesToDlqAsConstraintViolation_WithoutPause()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var host = new WorkerHost(databases, broker).Start();
        var blocked = Guid.CreateVersion7();
        var constraint = $"ck_test_{blocked:N}";
        await AlterProjectionAsync($"ALTER TABLE daily_consolidations ADD CONSTRAINT {constraint} CHECK (merchant_id <> '{blocked}') NOT VALID");
        try
        {
            var message = TestEvent.Credit(blocked, "10.00", Events.Day);
            await Events.PublishAsync(broker, message);

            var dead = Assert.Single(await DrainDeadLettersAsync(expected: 1));
            AssertDeadLetter(dead, DeadLetterReasons.ConstraintViolation, constraint, message.Json);
        }
        finally
        {
            await AlterProjectionAsync($"ALTER TABLE daily_consolidations DROP CONSTRAINT IF EXISTS {constraint}");
        }

        Assert.Equal(0, metrics.Total("consumer_paused_seconds"));
        Assert.Equal(1, metrics.Total("dlq_messages_total", "reason", DeadLetterReasons.ConstraintViolation));
        Assert.Null(await Projection.DailyAsync(databases, blocked, Events.Day));
        Assert.Equal(0, await Projection.InboxCountAsync(databases, blocked));
    }

    /// <summary>
    /// Queda entre COMMIT e ACK: a conexão cai depois do COMMIT e antes do ACK; o broker reentrega; um NOVO processo
    /// do worker recebe a reentrega, o Inbox a reconhece e a projeção continua 100.00 (não 200.00).
    /// </summary>
    [Fact]
    public async Task CrashAfterCommitBeforeAck_RedeliveryIsDuplicate_NoDoubleCount()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        var merchant = Guid.CreateVersion7();
        var message = TestEvent.Credit(merchant, "100.00", Events.Day);
        var crashed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var first = new WorkerHost(databases, broker).Start(async channel =>
        {
            // Derruba as conexões pelo broker (como a morte do processo) e só devolve com o canal já fechado:
            // o ACK seguinte não tem como chegar.
            _ = Task.Run(() => broker.RabbitmqctlAsync("close_all_connections", "simulated crash after commit"));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (channel.IsOpen && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            crashed.TrySetResult();
        }))
        {
            await Events.PublishAsync(broker, message);
            await crashed.Task.WaitAsync(Wait);
            Assert.Equal(new DailyRow(100.00m, 0m, 1), await Projection.DailyAsync(databases, merchant, Events.Day));
        }

        // O primeiro processo acabou sem ACK. Normalmente a mensagem está de volta à fila; se o primeiro processo
        // chegou a reconectar antes de ser descartado, ele mesmo já recebeu a reentrega como duplicata.
        var pending = await broker.QueueStateAsync(Topology.ConsolidationQueue);
        output.WriteLine($"Após a queda: fila={pending.Messages} duplicatas={metrics.Total("inbox_duplicates_total")}");
        Assert.True(pending.Messages == 1 || metrics.Total("inbox_duplicates_total") == 1);

        await using var second = new WorkerHost(databases, broker).Start();
        await Eventually.WaitAsync(() => Task.FromResult(metrics.Total("inbox_duplicates_total")), v => v >= 1, Wait, "reentrega reconhecida como duplicata");
        await Eventually.WaitAsync(() => broker.QueueStateAsync(Topology.ConsolidationQueue), s => s.Messages == 0, Wait, "fila vazia");

        Assert.Equal(new DailyRow(100.00m, 0m, 1), await Projection.DailyAsync(databases, merchant, Events.Day));
        Assert.Equal(1, await Projection.InboxCountAsync(databases, merchant));
        Assert.Equal(1, metrics.Total("consolidation_applied_total"));
        Assert.Equal(1, metrics.Total("inbox_duplicates_total"));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
    }

    /// <summary>
    /// Banco fora (ALLOW_CONNECTIONS false + conexões derrubadas): sem ACK, consumidor cancelado (contagem 0), nada na
    /// DLQ; ao voltar, a entrega aberta e as já recebidas são aplicadas uma única vez e o consumo é retomado.
    /// </summary>
    [Fact]
    public async Task DatabaseDown_PausesWithoutAckOrDlq_ThenAppliesEachEventOnce()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var host = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();
        await Eventually.WaitAsync(() => broker.QueueStateAsync(Topology.ConsolidationQueue), s => s.Consumers == 1, Wait, "worker consumindo");

        await SetConsolidationConnectionsAsync(allow: false);
        try
        {
            await Events.PublishAsync(broker, TestEvent.Credit(merchant, "10.00", Events.Day));
            await Events.PublishAsync(broker, TestEvent.Credit(merchant, "20.00", Events.Day));
            await Events.PublishAsync(broker, TestEvent.Debit(merchant, "5.00", Events.Day));

            // basic.cancel: o consumidor sai de list_consumers; a fila quorum ainda o conta enquanto houver entregas sem ACK.
            await Eventually.WaitAsync(() => broker.RegisteredConsumersAsync(Topology.ConsolidationQueue), n => n == 0, Wait, "consumidor cancelado");
            var paused = await broker.QueueStateAsync(Topology.ConsolidationQueue);
            output.WriteLine($"Pausado: prontas={paused.Messages} consumidores(quorum)={paused.Consumers}");
            await Task.Delay(TimeSpan.FromSeconds(3)); // a pausa se mantém com o banco fora
            Assert.Equal(0, await broker.RegisteredConsumersAsync(Topology.ConsolidationQueue));
            var still = await broker.QueueStateAsync(Topology.ConsolidationQueue);
            Assert.Equal(paused.Messages, still.Messages); // nada mais é entregue enquanto pausado
            Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
            Assert.Equal(0, metrics.Total("consolidation_applied_total"));
            Assert.Equal(HttpStatusCode.OK, (await host.Health.GetAsync("/health/live")).StatusCode);
        }
        finally
        {
            await SetConsolidationConnectionsAsync(allow: true);
        }

        var row = await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Events.Day), r => r?.Entries == 3, Wait, "3 eventos aplicados após a volta");
        await Eventually.WaitAsync(() => broker.QueueStateAsync(Topology.ConsolidationQueue), s => s is { Consumers: 1, Messages: 0 }, Wait, "consumo retomado");
        Assert.Equal(1, await broker.RegisteredConsumersAsync(Topology.ConsolidationQueue));
        Assert.Equal(new DailyRow(30.00m, 5.00m, 3), row);
        Assert.Equal(3, await Projection.InboxCountAsync(databases, merchant));
        Assert.Equal(3, metrics.Total("consolidation_applied_total"));
        Assert.Equal(0, metrics.Total("inbox_duplicates_total"));
        Assert.Equal(0u, (await broker.QueueStateAsync(Topology.DeadLetterQueue)).Messages);
        Assert.True(metrics.Total("consumer_paused_seconds") > 0);
        output.WriteLine($"consumer_paused_seconds={metrics.Total("consumer_paused_seconds"):0.0}");
    }

    /// <summary>
    /// P0 (Compose, rodada 3 da verificação): depois de uma reconexão ao broker, a pausa por banco fora não era mais
    /// retomada (worker vivo, fila com mensagem pronta, zero consumidores). Causa: o supervisor da sessão anterior deixava
    /// um _resumeSignal.WaitAsync pendente; o SemaphoreSlim entregava a ele o Release da retomada e o supervisor da
    /// sessão nova nunca acordava. Reprodução determinística: derruba a conexão do worker (close_connection, como um
    /// restart do broker), espera a nova sessão, pausa com duas entregas em buffer, volta o banco e exige consumidor
    /// re-registrado (list_consumers) e a 3ª mensagem consumida.
    /// </summary>
    [Fact]
    public async Task DatabaseDown_AfterBrokerReconnect_ResumesConsuming_AndThirdMessageIsApplied()
    {
        const string WorkerConnection = "consolidation-worker-";
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        // ResumeCheckSeconds alto: só o sinal da sessão pode retomar (isola a causa-raiz da rede de segurança periódica).
        await using var host = new WorkerHost(databases, broker, new Dictionary<string, string> { ["Consolidation:Worker:ResumeCheckSeconds"] = "3600" }).Start();
        var merchant = Guid.CreateVersion7();
        await Eventually.WaitAsync(() => broker.RegisteredConsumersAsync(Topology.ConsolidationQueue), n => n == 1, Wait, "worker consumindo");

        var closed = await broker.CloseConnectionsAsync(WorkerConnection);
        Assert.NotEmpty(closed);
        await Eventually.WaitAsync(() => broker.ConnectionPidsAsync(WorkerConnection), p => p.Count == 1 && !closed.Contains(p[0]), Wait, "worker reconectado");
        await Eventually.WaitAsync(() => broker.RegisteredConsumersAsync(Topology.ConsolidationQueue), n => n == 1, Wait, "consumidor da sessão nova");

        await SetConsolidationConnectionsAsync(allow: false);
        try
        {
            await Events.PublishAsync(broker, TestEvent.Credit(merchant, "1.00", Events.Day));
            await Events.PublishAsync(broker, TestEvent.Credit(merchant, "2.00", Events.Day));
            await Eventually.WaitAsync(() => broker.RegisteredConsumersAsync(Topology.ConsolidationQueue), n => n == 0, Wait, "consumo pausado");

            // Readiness do consumidor: pausado além do limite (1 s aqui) => Unhealthy.
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Assert.NotNull(host.Consumer.NotConsumingFor);
            Assert.Equal(HealthStatus.Unhealthy, (await ConsumerCheck(host).CheckHealthAsync(new HealthCheckContext())).Status);
        }
        finally
        {
            await SetConsolidationConnectionsAsync(allow: true);
        }

        await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Events.Day), r => r?.Entries == 2, Wait, "2 entregas em buffer aplicadas");
        await Eventually.WaitAsync(
            () => broker.RegisteredConsumersAsync(Topology.ConsolidationQueue), n => n == 1, TimeSpan.FromSeconds(30), "consumidor re-registrado após a pausa");
        await Events.PublishAsync(broker, TestEvent.Credit(merchant, "0.10", Events.Day));

        var row = await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Events.Day), r => r?.Entries == 3, Wait, "3ª mensagem consumida");
        Assert.Equal(new DailyRow(3.10m, 0m, 3), row);
        Assert.Equal(3, metrics.Total("consolidation_applied_total"));
        Assert.Equal(0, metrics.Total("inbox_duplicates_total"));
        Assert.Equal(1, await broker.RegisteredConsumersAsync(Topology.ConsolidationQueue));
        Assert.Null(host.Consumer.NotConsumingFor);
        Assert.Equal(HealthStatus.Healthy, (await ConsumerCheck(host).CheckHealthAsync(new HealthCheckContext())).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.Health.GetAsync("/health/ready")).StatusCode);
    }

    private static ConsumerHealthCheck ConsumerCheck(WorkerHost host) =>
        new(host.Consumer, new worker::CashFlow.Consolidation.Worker.ConsumerOptions { NotConsumingUnhealthySeconds = 1 });

    [Fact]
    public async Task BrokerDown_LiveStaysHealthy_ThenWorkerReconnectsAndConsumes()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var host = new WorkerHost(databases, broker, new Dictionary<string, string> { ["Consolidation:Worker:NotConsumingUnhealthySeconds"] = "1" }).Start();
        var merchant = Guid.CreateVersion7();
        await Eventually.WaitAsync(() => broker.QueueStateAsync(Topology.ConsolidationQueue), s => s.Consumers == 1, Wait, "worker consumindo");
        Assert.Equal(HttpStatusCode.OK, (await host.Health.GetAsync("/health/ready")).StatusCode);

        await broker.StopAsync();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(HttpStatusCode.OK, (await host.Health.GetAsync("/health/live")).StatusCode);

            // Banco no ar: só o check "consumer" (sem consumidor há mais de 1 s) pode derrubar a readiness.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Health.GetAsync("/health/ready")).StatusCode);
        }
        finally
        {
            await broker.StartAsync();
        }

        await Events.PublishAsync(broker, TestEvent.Credit(merchant, "10.00", Events.Day));
        var row = await Eventually.WaitAsync(() => Projection.DailyAsync(databases, merchant, Events.Day), r => r is not null, TimeSpan.FromSeconds(90), "aplicado após reconexão");
        Assert.Equal(new DailyRow(10.00m, 0m, 1), row);
        Assert.Equal(HttpStatusCode.OK, (await host.Health.GetAsync("/health/ready")).StatusCode);
    }

    /// <summary>T-09/T-06: dois workers concorrentes, mesma linha (merchant, dia), cada evento entregue duas vezes.</summary>
    [Fact]
    public async Task T09_TwoConcurrentWorkers_EachEventPublishedTwice_SumIsExact()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        using var metrics = new MetricCapture(WorkerMetrics.MeterName);
        await using var first = new WorkerHost(databases, broker).Start();
        await using var second = new WorkerHost(databases, broker).Start();
        var merchant = Guid.CreateVersion7();
        await Eventually.WaitAsync(() => broker.QueueStateAsync(Topology.ConsolidationQueue), s => s.Consumers == 2, Wait, "dois consumidores");

        const int count = 40;
        var events = Enumerable.Range(1, count)
            .Select(i => i % 4 == 0 ? TestEvent.Debit(merchant, "1.10", Events.Day) : TestEvent.Credit(merchant, $"{i}.25", Events.Day))
            .ToList();
        foreach (var message in events.Concat(events))
        {
            await Events.PublishAsync(broker, message);
        }

        var expectedCredits = events.Where(e => e.EntryType == "CREDIT").Sum(e => decimal.Parse(e.Amount, System.Globalization.CultureInfo.InvariantCulture));
        var expectedDebits = events.Where(e => e.EntryType == "DEBIT").Sum(e => decimal.Parse(e.Amount, System.Globalization.CultureInfo.InvariantCulture));
        await Eventually.WaitAsync(
            () => Task.FromResult(metrics.Total("consolidation_applied_total") + metrics.Total("inbox_duplicates_total")),
            v => v >= 2 * count, TimeSpan.FromSeconds(120), "todas as entregas tratadas");

        Assert.Equal(new DailyRow(expectedCredits, expectedDebits, count), await Projection.DailyAsync(databases, merchant, Events.Day));
        Assert.Equal(count, await Projection.InboxCountAsync(databases, merchant));
        Assert.Equal(count, metrics.Total("consolidation_applied_total"));
        Assert.Equal(count, metrics.Total("inbox_duplicates_total"));
        output.WriteLine($"retries transitórios={metrics.Total("worker_transient_retries_total")}");
    }

    private static void AssertDeadLetter(BasicGetResult dead, string reason, string field, string originalJson)
    {
        var headers = dead.BasicProperties.Headers;
        Assert.Equal(reason, AmqpHeaders.GetString(headers, HeaderNames.Reason));
        Assert.Equal(field, AmqpHeaders.GetString(headers, HeaderNames.ReasonDetail));
        Assert.Equal(Topology.TransactionRegisteredRoutingKey, AmqpHeaders.GetString(headers, HeaderNames.OriginalRoutingKey));
        Assert.False(string.IsNullOrEmpty(AmqpHeaders.GetString(headers, HeaderNames.Worker)));
        Assert.False(string.IsNullOrEmpty(AmqpHeaders.GetString(headers, HeaderNames.FailedAt)));
        Assert.Equal(Topology.TransactionRegisteredRoutingKey, dead.RoutingKey);
        Assert.Equal(originalJson, System.Text.Encoding.UTF8.GetString(dead.Body.Span));
    }

    private async Task<List<BasicGetResult>> DrainDeadLettersAsync(int expected)
    {
        await Eventually.WaitAsync(() => broker.QueueStateAsync(Topology.DeadLetterQueue), s => s.Messages >= expected, Wait, $"{expected} na DLQ");
        await using var connection = await broker.ConnectAsync("tests-dlq");
        await using var channel = await connection.CreateChannelAsync();
        var results = new List<BasicGetResult>();
        while (await channel.BasicGetAsync(Topology.DeadLetterQueue, autoAck: true) is { } result)
        {
            results.Add(result);
        }

        Assert.Equal(expected, results.Count);
        return results;
    }

    private async Task AlterProjectionAsync(string ddl)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb);
        await using var command = new NpgsqlCommand(ddl, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetConsolidationConnectionsAsync(bool allow)
    {
        await using var connection = await databases.OpenSuperuserAsync("postgres");
        await using (var alter = new NpgsqlCommand($"ALTER DATABASE consolidation_db ALLOW_CONNECTIONS {(allow ? "true" : "false")}", connection))
        {
            await alter.ExecuteNonQueryAsync();
        }

        if (!allow)
        {
            await using var terminate = new NpgsqlCommand(
                "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = 'consolidation_db' AND pid <> pg_backend_pid()", connection);
            output.WriteLine($"Conexões derrubadas em consolidation_db: {await terminate.ExecuteScalarAsync()}");
        }
    }
}
