namespace CashFlow.Messaging;

/// <summary>Backoff exponencial com jitter (metade fixa + metade aleatória), limitado a [min, max].</summary>
public sealed class Backoff(TimeSpan min, TimeSpan max)
{
    private int _attempt;

    public TimeSpan Next() => ForAttempt(++_attempt, min, max);

    public void Reset() => _attempt = 0;

    public static TimeSpan ForAttempt(int attempt, TimeSpan min, TimeSpan max)
    {
        var exponential = min.TotalSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 30));
        var ceiling = Math.Min(max.TotalSeconds, exponential);
        var jittered = (ceiling / 2) + (Random.Shared.NextDouble() * ceiling / 2);
        return TimeSpan.FromSeconds(Math.Clamp(jittered, min.TotalSeconds, max.TotalSeconds));
    }
}
