using System.Collections;
using System.Globalization;
using System.Text;
using RabbitMQ.Client;

namespace CashFlow.Messaging;

/// <summary>Leitura de cabeçalhos AMQP: strings chegam do broker como byte[] UTF-8.</summary>
public static class AmqpHeaders
{
    public static string? GetString(IDictionary<string, object?>? headers, string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var value))
        {
            return null;
        }

        return value switch
        {
            null => null,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// Converte um valor de cabeçalho (inclusive x-death: listas de tabelas) em tipos serializáveis em JSON,
    /// para registrar a trilha do redrive sem perder a informação de origem.
    /// </summary>
    public static object? ToPlain(object? value) => value switch
    {
        null => null,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        string text => text,
        AmqpTimestamp timestamp => DateTimeOffset.FromUnixTimeSeconds(timestamp.UnixTime).ToString("O", CultureInfo.InvariantCulture),
        IDictionary<string, object?> table => table.ToDictionary(pair => pair.Key, pair => ToPlain(pair.Value), StringComparer.Ordinal),
        IEnumerable list => list.Cast<object?>().Select(ToPlain).ToList(),
        _ => value,
    };
}
