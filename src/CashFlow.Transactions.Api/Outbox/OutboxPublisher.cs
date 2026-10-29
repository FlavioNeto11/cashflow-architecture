using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CashFlow.Messaging;
using RabbitMQ.Client;

namespace CashFlow.Transactions.Api.Outbox;

/// <summary>Configuração do publisher ("Outbox:Publisher"). Desligável só para testes que não têm broker.</summary>
public sealed class OutboxPublisherOptions
{
    public const string Section = "Outbox:Publisher";

    public bool Enabled { get; set; } = true;

    public int BatchSize { get; set; } = 5;

    public int LeaseSeconds { get; set; } = 30;

    public int PublishTimeoutSeconds { get; set; } = 5;

    public int IdlePollMilliseconds { get; set; } = 500;

    public int BacklogRefreshSeconds { get; set; } = 5;
}

/// <summary>
/// Publisher do outbox (ADR-0002 item 1). Garantia: at-least-once do outbox para o broker; um evento pode ser
/// publicado mais de uma vez (timeout de confirm, lease expirado, queda entre o confirm e a transação B) e o consumidor
/// é idempotente pelo Inbox. Nunca cria topologia; nunca derruba o host; o readiness da API não depende do broker.
/// </summary>
public sealed class OutboxPublisher(
    OutboxStore store,
    OutboxMetrics metrics,
    OutboxPublisherOptions options,
    RabbitMqOptions rabbitMq,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    private static readonly PublicationAddress RegisteredAddress =
        new(ExchangeType.Topic, Topology.TransactionsExchange, Topology.TransactionRegisteredRoutingKey);

    // Folga antes do fim do lease: não começa uma publicação que poderia terminar com o lease já de outra instância.
    private static readonly TimeSpan LeaseSafetyMargin = TimeSpan.FromSeconds(2);

    private readonly string _instance = BuildInstanceId();
    private BrokerLink? _broker;
    private long _nextBacklogRefresh;
    private bool _databaseFailing;

    internal string Instance => _instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // não segura a partida do host
        await using var broker = new BrokerLink(rabbitMq, _instance, logger);
        _broker = broker;
        var databaseBackoff = new Backoff(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        logger.LogInformation("Publisher do outbox iniciado (instância {Instance}, broker {Broker}).", _instance, rabbitMq);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshBacklogIfDueAsync(stoppingToken);
                var handled = await PublishBatchAsync(broker, stoppingToken);
                if (_databaseFailing)
                {
                    _databaseFailing = false;
                    logger.LogInformation("Publisher do outbox voltou a acessar o banco.");
                }

                databaseBackoff.Reset();
                if (handled == 0)
                {
                    await Task.Delay(options.IdlePollMilliseconds, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Banco indisponível (ou falha inesperada): registra uma vez por mudança de estado e tenta de novo.
                if (!_databaseFailing)
                {
                    _databaseFailing = true;
                    logger.LogError(exception, "Publisher do outbox sem acesso ao banco; tentando novamente com backoff.");
                }

                try
                {
                    await Task.Delay(databaseBackoff.Next(), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task RefreshBacklogIfDueAsync(CancellationToken cancellationToken)
    {
        if (Stopwatch.GetTimestamp() < _nextBacklogRefresh)
        {
            return;
        }

        var (pending, oldest) = await store.BacklogAsync(cancellationToken);
        metrics.UpdateBacklog(pending, oldest);
        _nextBacklogRefresh = Stopwatch.GetTimestamp() + (long)(options.BacklogRefreshSeconds * Stopwatch.Frequency);
    }

    /// <summary>Um ciclo: transação A (lease), publicação com confirm, transação B ou agendamento de nova tentativa.</summary>
    private async Task<int> PublishBatchAsync(BrokerLink broker, CancellationToken stoppingToken)
    {
        var lease = TimeSpan.FromSeconds(options.LeaseSeconds);
        var started = Stopwatch.GetTimestamp();
        var batch = await store.ClaimAsync(_instance, options.BatchSize, lease, stoppingToken);
        if (batch.Messages.Count == 0)
        {
            return 0;
        }

        var budget = batch.LeaseRemaining - LeaseSafetyMargin;
        var next = 0; // primeira mensagem ainda não tratada; o finally devolve o lease das restantes
        try
        {
            var channel = await broker.GetChannelAsync(stoppingToken);
            for (; next < batch.Messages.Count; next++)
            {
                if (stoppingToken.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= budget)
                {
                    break; // fim do lease próximo ou parada
                }

                var message = batch.Messages[next];
                channel = await PublishOneAsync(broker, channel, message, stoppingToken);
            }
        }
        finally
        {
            // Devolve o restante sem contar tentativa (fim do lease, parada ou falha inesperada). Best effort: se o
            // banco também caiu, o lease expira sozinho em LeaseSeconds.
            if (next < batch.Messages.Count)
            {
                try
                {
                    await store.ReleaseAsync([.. batch.Messages.Skip(next).Select(m => m.Id)], _instance, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Não foi possível devolver o lease; expira em {LeaseSeconds} s.", options.LeaseSeconds);
                }
            }
        }

        return batch.Messages.Count;
    }

    private async Task<IChannel?> PublishOneAsync(BrokerLink broker, IChannel? channel, ClaimedMessage message, CancellationToken stoppingToken)
    {
        if (!TryBuild(message, out var address, out var properties, out var invalidReason))
        {
            // Erro na primeira tentativa; depois Debug, para não repetir o mesmo Error a cada backoff.
            logger.Log(
                message.Attempts == 0 ? LogLevel.Error : LogLevel.Debug,
                "Evento {EventId} do outbox não publicável ({Reason}); ficará pendente para análise.", message.Id, invalidReason);
            await FailAsync(message, invalidReason);
            return channel;
        }

        if (channel is null)
        {
            await FailAsync(message, "broker_unavailable");
            return null;
        }

        PublishOutcome outcome;
        try
        {
            outcome = await ConfirmedPublisher.PublishAsync(
                channel, address, properties, Encoding.UTF8.GetBytes(message.Payload),
                TimeSpan.FromSeconds(options.PublishTimeoutSeconds), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Parada no meio da publicação: resultado desconhecido; o finally do lote devolve o lease (talvez duplique).
            throw;
        }
        catch (Exception exception)
        {
            broker.Invalidate(exception);
            await FailAsync(message, "broker_unavailable");
            return null;
        }

        switch (outcome)
        {
            case PublishOutcome.Confirmed:
                // Transação B não é cancelada pela parada: abandoná-la após o confirm só geraria publicação duplicada.
                await store.MarkPublishedAsync(message.Id, CancellationToken.None);
                metrics.Published.Add(1);
                return channel;
            case PublishOutcome.Returned:
                // Sem binding para a routing key: falha de topologia, não do evento. Nunca descarta.
                logger.LogError(
                    "Evento {EventId} devolvido pelo broker (unroutable) em {Exchange}/{RoutingKey}.",
                    message.Id, address.ExchangeName, address.RoutingKey);
                await FailAsync(message, "unroutable");
                return channel;
            case PublishOutcome.Nacked:
                await FailAsync(message, "nacked");
                return channel;
            default:
                // Timeout do confirm: estado do canal incerto; recria.
                broker.Invalidate(null);
                await FailAsync(message, "confirm_timeout");
                return null;
        }
    }

    private async Task FailAsync(ClaimedMessage message, string reason)
    {
        metrics.RecordFailure(reason);
        var retryAfter = Backoff.ForAttempt(message.Attempts + 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        await store.MarkFailedAsync(message.Id, _instance, retryAfter, reason, CancellationToken.None);
    }

    /// <summary>Monta endereço e propriedades AMQP a partir da linha (target.md 8.3). O payload vai como gravado.</summary>
    internal static bool TryBuild(ClaimedMessage message, out PublicationAddress address, out BasicProperties properties, out string reason)
    {
        address = RegisteredAddress;
        properties = new BasicProperties();
        reason = string.Empty;
        if (message is not { Type: TransactionRegisteredEvent.Type, Version: TransactionRegisteredEvent.Version })
        {
            reason = "unknown_type";
            return false;
        }

        Dictionary<string, string?>? headers;
        try
        {
            headers = JsonSerializer.Deserialize<Dictionary<string, string?>>(message.Headers);
        }
        catch (JsonException)
        {
            headers = null;
        }

        if (headers is null)
        {
            reason = "invalid_headers";
            return false;
        }

        properties.MessageId = headers.GetValueOrDefault(HeaderNames.MessageId) ?? message.Id.ToString();
        properties.ContentType = headers.GetValueOrDefault(HeaderNames.ContentType) ?? TransactionRegisteredEvent.ContentType;
        properties.Type = message.Type;
        properties.Timestamp = new AmqpTimestamp(new DateTimeOffset(DateTime.SpecifyKind(message.OccurredAt, DateTimeKind.Utc)).ToUnixTimeSeconds());
        var amqpHeaders = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [HeaderNames.MerchantId] = headers.GetValueOrDefault(HeaderNames.MerchantId) ?? message.MerchantId.ToString(),
            ["version"] = message.Version.ToString(CultureInfo.InvariantCulture),
        };

        // traceparent pode ser null (requisição sem trace) ou faltar: só vai quando existe.
        if (headers.GetValueOrDefault(HeaderNames.Traceparent) is { Length: > 0 } traceparent)
        {
            amqpHeaders[HeaderNames.Traceparent] = traceparent;
        }

        properties.Headers = amqpHeaders;
        return true;
    }

    private static string BuildInstanceId()
    {
        var id = $"{Environment.MachineName}-{Guid.NewGuid():N}";
        return id.Length <= 64 ? id : id[^64..];
    }

    /// <summary>Conexão e canal com confirms; reconexão com backoff; log só na mudança de estado.</summary>
    private sealed class BrokerLink(RabbitMqOptions rabbitMq, string instance, ILogger logger) : IAsyncDisposable
    {
        private readonly Backoff _backoff = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        private IConnection? _connection;
        private IChannel? _channel;
        private long _nextAttempt;
        private string _state = "unknown";

        public async Task<IChannel?> GetChannelAsync(CancellationToken cancellationToken)
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (Stopwatch.GetTimestamp() < _nextAttempt)
            {
                return null;
            }

            await CloseAsync();
            try
            {
                _connection = await rabbitMq.CreateFactory($"transactions-api-outbox-{instance}").CreateConnectionAsync(cancellationToken);
                _channel = await _connection.CreateChannelAsync(ConfirmedPublisher.ChannelOptions, cancellationToken);

                // Declaração PASSIVA: confirma que a topologia existe sem criá-la; 404 fecha o canal.
                await _channel.ExchangeDeclarePassiveAsync(Topology.TransactionsExchange, cancellationToken);
                _backoff.Reset();
                Transition("connected", null);
                return _channel;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var topologyMissing = exception is RabbitMQ.Client.Exceptions.OperationInterruptedException { ShutdownReason.ReplyCode: 404 };
                Transition(topologyMissing ? "topology_missing" : "unavailable", exception);
                await CloseAsync();
                _nextAttempt = Stopwatch.GetTimestamp() + (long)(_backoff.Next().TotalSeconds * Stopwatch.Frequency);
                return null;
            }
        }

        public void Invalidate(Exception? exception)
        {
            Transition("unavailable", exception);
            _channel = null; // a próxima chamada fecha a conexão antiga e reconecta
        }

        public ValueTask DisposeAsync() => new(CloseAsync());

        private async Task CloseAsync()
        {
            var channel = _channel;
            var connection = _connection;
            _channel = null;
            _connection = null;
            try
            {
                if (channel is not null)
                {
                    await channel.DisposeAsync();
                }

                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Falha ao fechar conexão antiga com o broker (ignorada).");
            }
        }

        private void Transition(string state, Exception? exception)
        {
            if (state == _state)
            {
                return;
            }

            var previous = _state;
            _state = state;
            switch (state)
            {
                case "connected":
                    logger.LogInformation("Publisher do outbox conectado ao broker {Broker} (antes: {Previous}).", rabbitMq, previous);
                    break;
                case "topology_missing":
                    logger.LogError(exception, "Exchange {Exchange} ausente no broker; o publisher não cria topologia e tentará novamente.", Topology.TransactionsExchange);
                    break;
                default:
                    logger.LogWarning(
                        "Broker {Broker} indisponível para o publisher do outbox ({Error}); eventos seguem pendentes no outbox.",
                        rabbitMq, exception?.GetType().Name ?? "confirm_timeout");
                    break;
            }
        }
    }
}
