using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace CashFlow.Consolidation.Tests.Support;

/// <summary>Soma as medições de instrumentos de um Meter, por nome do instrumento e (opcionalmente) por rótulo.</summary>
public sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, double> _totals = new(StringComparer.Ordinal);

    public MetricCapture(string meterName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument.Name, value, tags));
        _listener.Start();
    }

    public double Total(string instrument) => _totals.GetValueOrDefault(instrument);

    public double Total(string instrument, string tag, string value) => _totals.GetValueOrDefault($"{instrument}|{tag}={value}");

    public void Dispose() => _listener.Dispose();

    private void Add(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        _totals.AddOrUpdate(instrument, value, (_, current) => current + value);
        foreach (var tag in tags)
        {
            _totals.AddOrUpdate($"{instrument}|{tag.Key}={tag.Value}", value, (_, current) => current + value);
        }
    }
}
