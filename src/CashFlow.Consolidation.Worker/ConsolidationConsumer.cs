using System.Diagnostics;
using System.Globalization;
using CashFlow.Messaging;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CashFlow.Consolidation.Worker;

/// <summary>Configuração do consumidor ("Consolidation:Worker").</summary>
public sealed class ConsumerOptions
{
    public const string Section = "Consolidation:Worker";

    public ushort Prefetch { get; set; } = 16;

    public int DeadLetterTimeoutSeconds { get; set; } = 5;

    public int MaxTransientRetries { get; set; } = 3;

    /// <summary>
    /// Intervalo em que o supervisor reavalia o estado mesmo sem sinal: pausado e sem entrega retida => retoma.
    /// Rede de segurança contra sinal de retomada perdido (P0 da rodada 3).
    /// </summary>
    public int ResumeCheckSeconds { get; set; } = 5;

    /// <summary>/health/ready fica Unhealthy quando não há consumidor registrado há mais que isto (pausa ou desconexão).</summary>
    public int NotConsumingUnhealthySeconds { get; set; } = 60;
}

/// <summary>
/// Consumidor de transaction.registered.v1 (ADR-0002 itens 3 a 6). Entrega at-least-once; efeito idempotente pelo
/// Inbox (nunca "exactly-once"). Regras: ACK só depois do COMMIT (ou da confirmação da cópia na DLQ); nunca NACK;
/// mensagem inválida, ou erro determinístico do banco (SQLSTATE 22/23, p.ex. 22003 = projection_overflow), vai à DLQ
/// (não é transitória; ACK só após o confirm, sem pausa); falha de infraestrutura NUNCA vai à DLQ: o consumo pausa
/// (basic.cancel no mesmo canal, a entrega aberta é mantida sem ACK), o banco é sondado com backoff e a entrega
/// aberta é processada antes de voltar a consumir. O canal nunca é fechado para pausar.
/// </summary>
public sealed class ConsolidationConsumer(
    ConsolidationStore store,
    WorkerMetrics metrics,
    RabbitMqOptions rabbitMq,
    ConsumerOptions options,
    ILogger<ConsolidationConsumer> logger) : BackgroundService
{
    private const string LockNotAvailable = "55P03";
    private const string DeadlockDetected = "40P01";
    private const string NumericValueOutOfRange = "22003";

    private readonly string _instance = Environment.MachineName;
    private readonly SemaphoreSlim _resumeSignal = new(0);
    private readonly SemaphoreSlim _publishGate = new(1, 1);

    private IConnection? _connection;
    private IChannel? _publishChannel;
    private AsyncEventingBasicConsumer? _consumer;
    // Ordem de publicação (P2-2): quem consome grava _consumerTag ANTES de _paused = false (escrita volátil);
    // PauseAsync lê _paused (leitura volátil) ANTES de _consumerTag, então nunca vê "não pausado" com a tag antiga.
    private volatile string? _consumerTag;
    private volatile bool _paused;
    private bool _databaseFailing;
    private string _brokerState = "unknown";
    private CancellationToken _stopping;
    private bool _lastApplied; // só lido no mesmo callback (dispatcher com concorrência 1)
    private int _holding; // entregas retidas numa pausa (sem ACK, sondando o banco); só retoma com 0
    private long _notConsumingSince = Stopwatch.GetTimestamp(); // 0 = consumidor registrado

    private enum Decision
    {
        Ack,
        Pause,
        Abandon,
    }

    /// <summary>
    /// Só para teste: chamado depois do COMMIT de um evento aplicado e antes do ACK, com o canal da entrega
    /// (simula a queda do processo entre COMMIT e ACK). Nulo em produção.
    /// </summary>
    internal Func<IChannel, Task>? AfterCommitBeforeAck { get; set; }

    /// <summary>Há quanto tempo não há consumidor registrado (pausa, desconexão ou partida); nulo se consumindo.</summary>
    public TimeSpan? NotConsumingFor =>
        Interlocked.Read(ref _notConsumingSince) is var since and not 0 ? Stopwatch.GetElapsedTime(since) : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        _stopping = stoppingToken;
        var backoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        logger.LogInformation("consolidation-worker iniciado (broker {Broker}, fila {Queue}).", rabbitMq, Topology.ConsolidationQueue);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeUntilDisconnectedAsync(backoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var topologyMissing = exception is RabbitMQ.Client.Exceptions.OperationInterruptedException { ShutdownReason.ReplyCode: 404 };
                BrokerTransition(topologyMissing ? "topology_missing" : "unavailable", exception);
            }

            await CloseAsync();
            try
            {
                await Task.Delay(backoff.Next(), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await CloseAsync();
        logger.LogInformation("consolidation-worker encerrando.");
    }

    /// <summary>
    /// Conecta, valida a topologia (passivo), consome e supervisiona até a conexão ou o canal caírem.
    /// P0 (rodada 3): a espera pelo sinal de retomada é desta SESSÃO. Antes, ao cair a conexão, o WaitAsync pendente
    /// da sessão antiga continuava na fila do SemaphoreSlim e recebia o Release seguinte; o supervisor da sessão nova
    /// nunca acordava e o worker ficava vivo sem consumidor. Agora a espera é cancelada ao sair (sem consumir sinal) e o
    /// supervisor reavalia o ESTADO a cada ResumeCheckSeconds: pausado e sem entrega retida => basic.consume.
    /// </summary>
    private async Task ConsumeUntilDisconnectedAsync(Backoff backoff, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            var factory = rabbitMq.CreateFactory($"consolidation-worker-{_instance}");
            factory.ConsumerDispatchConcurrency = 1; // uma entrega por vez: a pausa segura a fila deste consumidor
            _connection = await factory.CreateConnectionAsync(stoppingToken);
            var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _connection.ConnectionShutdownAsync += (_, _) =>
            {
                lost.TrySetResult();
                return Task.CompletedTask;
            };

            var channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
            channel.ChannelShutdownAsync += (_, _) =>
            {
                lost.TrySetResult();
                return Task.CompletedTask;
            };

            await channel.QueueDeclarePassiveAsync(Topology.ConsolidationQueue, stoppingToken);
            await channel.BasicQosAsync(0, options.Prefetch, global: false, stoppingToken);
            _consumer = new AsyncEventingBasicConsumer(channel);
            _consumer.ReceivedAsync += OnReceivedAsync;
            _consumerTag = await channel.BasicConsumeAsync(Topology.ConsolidationQueue, autoAck: false, _consumer, stoppingToken);
            _paused = false; // depois da tag: ver a nota em _consumerTag
            Interlocked.Exchange(ref _notConsumingSince, 0);
            backoff.Reset();
            BrokerTransition("connected", null);

            // Supervisor fora do dispatcher: a retomada (basic.consume) não roda dentro do callback de entrega.
            var check = TimeSpan.FromSeconds(Math.Max(1, options.ResumeCheckSeconds));
            Task resume = _resumeSignal.WaitAsync(session.Token);
            while (true)
            {
                var completed = await Task.WhenAny(lost.Task, resume, Task.Delay(check, stoppingToken));
                if (completed == lost.Task)
                {
                    throw new InvalidOperationException("Conexão ou canal com o broker encerrado.");
                }

                stoppingToken.ThrowIfCancellationRequested();
                if (resume.IsCompleted)
                {
                    await resume; // propaga cancelamento
                    resume = _resumeSignal.WaitAsync(session.Token);
                }

                if (_paused && Volatile.Read(ref _holding) == 0 && channel.IsOpen)
                {
                    // Tag nova gravada antes de liberar _paused (P2-2). Se uma entrega pausar na janela entre o
                    // consume-ok e a linha seguinte, PauseAsync ainda vê _paused = true e não cancela; com
                    // ConsumerDispatchConcurrency = 1 o dispatcher fica preso na sonda e nada mais é processado.
                    _consumerTag = await channel.BasicConsumeAsync(Topology.ConsolidationQueue, autoAck: false, _consumer, stoppingToken);
                    _paused = false;
                    Interlocked.Exchange(ref _notConsumingSince, 0);
                    logger.LogInformation("Consumo retomado na fila {Queue}.", Topology.ConsolidationQueue);
                }
            }
        }
        finally
        {
            // Retira da fila do SemaphoreSlim a espera desta sessão sem consumir sinal (causa-raiz do P0).
            await session.CancelAsync();
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        var channel = ((AsyncEventingBasicConsumer)sender).Channel;
        var probeBackoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        long pausedAt = 0;
        try
        {
            while (true)
            {
                if (_stopping.IsCancellationRequested || !channel.IsOpen)
                {
                    return; // sem ACK: o broker reentrega; o Inbox absorve se já foi aplicado
                }

                _lastApplied = false;
                var decision = await DecideAsync(delivery);
                if (decision == Decision.Abandon)
                {
                    return;
                }

                if (decision == Decision.Ack)
                {
                    if (_lastApplied && AfterCommitBeforeAck is { } hook)
                    {
                        await hook(channel);
                    }

                    await AckAsync(channel, delivery);
                    return;
                }

                if (pausedAt == 0)
                {
                    pausedAt = Stopwatch.GetTimestamp();
                    Interlocked.Increment(ref _holding); // antes de _paused = true: o supervisor não retoma no meio
                    await PauseAsync(channel);
                }

                await WaitForDatabaseAsync(channel, probeBackoff);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // parada: a entrega fica sem ACK e volta à fila
        }
        catch (Exception exception)
        {
            // Nada escapa do callback: uma exceção aqui deixaria a entrega presa sem ACK até o canal cair.
            logger.LogError(exception, "Falha inesperada no tratamento da entrega {MessageId}; sem ACK.", delivery.BasicProperties.MessageId);
        }
        finally
        {
            if (pausedAt != 0)
            {
                metrics.PausedSeconds.Add(Stopwatch.GetElapsedTime(pausedAt).TotalSeconds);
                Interlocked.Decrement(ref _holding);
                _resumeSignal.Release();
            }
        }
    }

    private async Task<Decision> DecideAsync(BasicDeliverEventArgs delivery)
    {
        var properties = delivery.BasicProperties;
        var validation = properties.Type is { } type && type != TransactionRegisteredEvent.Type
            ? EventValidation.Reject(DeadLetterReasons.UnknownType, "type")
            : TransactionRegisteredContract.Validate(
                delivery.Body.Span, AmqpHeaders.GetString(properties.Headers, HeaderNames.MerchantId), properties.MessageId);

        if (validation.Event is not { } message)
        {
            return await DeadLetterAsync(delivery, validation.Reason!, validation.Field);
        }

        ApplyResult result;
        var attempt = 0;
        while (true)
        {
            try
            {
                result = await store.ApplyAsync(message, _stopping);
                DatabaseRecovered();
                break;
            }
            catch (PostgresException error) when (error.SqlState is LockNotAvailable or DeadlockDetected && attempt < options.MaxTransientRetries)
            {
                attempt++;
                metrics.RecordTransientRetry(error.SqlState);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(100, 2001)), _stopping);
            }
            catch (PostgresException error) when (IsDeterministic(error.SqlState))
            {
                // Classes 22 (dado) e 23 (restrição): repetir dá o mesmo erro, então pausar travaria a fila (poison).
                // A transação já sofreu rollback (nada no Inbox nem na projeção). Só SQLSTATE e nome da restrição
                // saem no cabeçalho/log: o DETAIL do Postgres traz a linha com valores financeiros.
                DatabaseRecovered();
                var overflow = error.SqlState == NumericValueOutOfRange;
                var detail = overflow ? error.SqlState : error.ConstraintName ?? error.SqlState;
                logger.LogWarning(
                    "Erro determinístico do banco (SQLSTATE {SqlState}, restrição {Constraint}) no lançamento {TransactionId}; enviando à DLQ.",
                    error.SqlState, error.ConstraintName ?? "-", message.TransactionId);
                return await DeadLetterAsync(
                    delivery, overflow ? DeadLetterReasons.ProjectionOverflow : DeadLetterReasons.ConstraintViolation, detail);
            }
            catch (Exception error) when (error is not OperationCanceledException || !_stopping.IsCancellationRequested)
            {
                DatabaseFailing(error);
                return Decision.Pause;
            }
        }

        switch (result)
        {
            case ApplyResult.Applied:
                _lastApplied = true;
                metrics.Applied.Add(1);
                metrics.Lag.Record(Math.Max(0, (DateTime.UtcNow - message.OccurredAt).TotalSeconds));
                return Decision.Ack;
            case ApplyResult.Duplicate:
                metrics.Duplicates.Add(1);
                logger.LogInformation("Reentrega do lançamento {TransactionId} reconhecida pelo Inbox; sem efeito.", message.TransactionId);
                return Decision.Ack;
            default:
                return await DeadLetterAsync(delivery, DeadLetterReasons.Stale, "occurredAt");
        }
    }

    /// <summary>
    /// Cópia para cashflow.dlx com a routing key original e os diagnósticos x-*; message-id e payload preservados.
    /// ACK do original só depois do confirm. Return, NACK, timeout ou canal caído = falha de infraestrutura (pausa).
    /// </summary>
    private async Task<Decision> DeadLetterAsync(BasicDeliverEventArgs delivery, string reason, string? field)
    {
        var properties = new BasicProperties(delivery.BasicProperties);
        var headers = delivery.BasicProperties.Headers is { } original
            ? new Dictionary<string, object?>(original, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        headers[HeaderNames.Reason] = reason;
        if (field is not null)
        {
            headers[HeaderNames.ReasonDetail] = field;
        }

        headers[HeaderNames.OriginalRoutingKey] = delivery.RoutingKey;
        headers[HeaderNames.Worker] = _instance;
        headers[HeaderNames.FailedAt] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        properties.Headers = headers;

        await _publishGate.WaitAsync(_stopping);
        try
        {
            if (_publishChannel is not { IsOpen: true })
            {
                _publishChannel = _connection is { IsOpen: true } connection
                    ? await connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions, _stopping)
                    : null;
            }

            if (_publishChannel is null)
            {
                return Decision.Abandon; // conexão caiu: a entrega volta à fila sozinha
            }

            var outcome = await ConfirmedPublisher.PublishAsync(
                _publishChannel, new PublicationAddress(ExchangeType.Topic, Topology.DeadLetterExchange, delivery.RoutingKey),
                properties, delivery.Body, TimeSpan.FromSeconds(options.DeadLetterTimeoutSeconds), _stopping);
            if (outcome != PublishOutcome.Confirmed)
            {
                logger.LogError(
                    "Cópia da mensagem {MessageId} para a DLQ não confirmada ({Outcome}); consumo pausado, sem ACK.",
                    delivery.BasicProperties.MessageId, outcome);
                return Decision.Pause;
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            logger.LogError(error, "Falha ao publicar a mensagem {MessageId} na DLQ; consumo pausado, sem ACK.", delivery.BasicProperties.MessageId);
            _publishChannel = null;
            return Decision.Pause;
        }
        finally
        {
            _publishGate.Release();
        }

        metrics.RecordDeadLetter(reason);
        logger.LogWarning(
            "Mensagem {MessageId} enviada à DLQ (motivo {Reason}, campo {Field}).", delivery.BasicProperties.MessageId, reason, field ?? "-");
        return Decision.Ack;
    }

    /// <summary>
    /// SQLSTATE das classes 22 (data exception) e 23 (integrity constraint). O 23505 não chega aqui pelo caminho
    /// normal: o Inbox usa ON CONFLICT DO NOTHING (duplicata = resultado, não exceção) e a projeção ON CONFLICT DO
    /// UPDATE; se chegasse, também seria determinístico.
    /// </summary>
    private static bool IsDeterministic(string sqlState) =>
        sqlState.StartsWith("22", StringComparison.Ordinal) || sqlState.StartsWith("23", StringComparison.Ordinal);

    private async Task AckAsync(IChannel channel, BasicDeliverEventArgs delivery)
    {
        try
        {
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
        }
        catch (Exception error)
        {
            // O efeito já está commitado; a reentrega será reconhecida pelo Inbox (duplicata, sem efeito).
            logger.LogWarning(error, "ACK da mensagem {MessageId} não enviado; a reentrega será absorvida pelo Inbox.", delivery.BasicProperties.MessageId);
        }
    }

    private async Task PauseAsync(IChannel channel)
    {
        if (_paused)
        {
            return;
        }

        _paused = true;
        Interlocked.CompareExchange(ref _notConsumingSince, Stopwatch.GetTimestamp(), 0);
        logger.LogWarning("Falha de infraestrutura: consumo pausado (basic.cancel); a entrega aberta fica sem ACK.");
        if (_consumerTag is { } tag && channel.IsOpen)
        {
            // noWait: o cancel-ok não é esperado dentro do callback do dispatcher.
            await channel.BasicCancelAsync(tag, noWait: true);
        }
    }

    private async Task WaitForDatabaseAsync(IChannel channel, Backoff backoff)
    {
        while (channel.IsOpen)
        {
            await Task.Delay(backoff.Next(), _stopping);
            try
            {
                await store.ProbeAsync(_stopping);
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException || !_stopping.IsCancellationRequested)
            {
                DatabaseFailing(error);
            }
        }
    }

    private void DatabaseFailing(Exception error)
    {
        if (!_databaseFailing)
        {
            _databaseFailing = true;
            logger.LogError(error, "Banco consolidation_db indisponível para o worker; consumo pausado até a sonda passar.");
        }
    }

    private void DatabaseRecovered()
    {
        if (_databaseFailing)
        {
            _databaseFailing = false;
            logger.LogInformation("Banco consolidation_db acessível novamente.");
        }
    }

    private void BrokerTransition(string state, Exception? error)
    {
        if (state == _brokerState)
        {
            return;
        }

        var previous = _brokerState;
        _brokerState = state;
        switch (state)
        {
            case "connected":
                logger.LogInformation("Worker consumindo {Queue} em {Broker} (antes: {Previous}).", Topology.ConsolidationQueue, rabbitMq, previous);
                break;
            case "topology_missing":
                logger.LogError(error, "Fila {Queue} ausente no broker; o worker não cria topologia e tentará novamente.", Topology.ConsolidationQueue);
                break;
            default:
                logger.LogWarning("Broker {Broker} indisponível para o worker ({Error}); reconectando com backoff.", rabbitMq, error?.GetType().Name);
                break;
        }
    }

    private async Task CloseAsync()
    {
        var connection = _connection;
        _connection = null;
        _publishChannel = null;
        _consumer = null;
        _consumerTag = null;
        Interlocked.CompareExchange(ref _notConsumingSince, Stopwatch.GetTimestamp(), 0);
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception error)
        {
            logger.LogDebug(error, "Falha ao fechar a conexão antiga com o broker (ignorada).");
        }
    }

    public override void Dispose()
    {
        _resumeSignal.Dispose();
        _publishGate.Dispose();
        base.Dispose();
    }
}
