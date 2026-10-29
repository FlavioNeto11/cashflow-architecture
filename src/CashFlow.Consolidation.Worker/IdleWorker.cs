namespace CashFlow.Consolidation.Worker;

/// <summary>
/// Placeholder do scaffold F2: só registra início e parada e respeita o CancellationToken.
/// O consumidor AMQP, o Inbox e a projeção entram em F4.
/// </summary>
public sealed partial class IdleWorker(ILogger<IdleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogStopping(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "consolidation-worker iniciado (scaffold F2, sem consumo)")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "consolidation-worker encerrando")]
    private static partial void LogStopping(ILogger logger);
}
