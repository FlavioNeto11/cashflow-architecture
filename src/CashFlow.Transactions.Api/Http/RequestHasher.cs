using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace CashFlow.Transactions.Api.Http;

/// <summary>
/// Hash de idempotência (ADR-0005): SHA-256 hex (64 caracteres) de método + rota + corpo canônico.
/// Canônico = JSON compacto com as chaves de cada objeto em ordem ordinal; assim a ordem das chaves e os
/// espaços não mudam o hash, mas qualquer valor diferente muda. A rota entra para que a mesma chave usada em
/// POST /transactions e em POST /transactions/{id}/reversal não colida.
/// </summary>
public static class RequestHasher
{
    public static string Compute(string method, string route, ReadOnlySpan<byte> body)
    {
        var input = $"{method}\n{route}\n{Canonicalize(body)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    /// <exception cref="JsonException">JSON inválido, com propriedade duplicada ou UTF-8 inválido.</exception>
    public static string Canonicalize(ReadOnlySpan<byte> body)
    {
        // PF-17: o leitor de JSON só decodifica UTF-8 ao ler a string (InvalidOperationException → 500). Corpo que não é
        // UTF-8 válido é erro de sintaxe: JsonException, como o JSON malformado (400).
        if (!Utf8.IsValid(body))
        {
            throw new JsonException("O corpo não é UTF-8 válido.");
        }

        // Reversal aceita corpo vazio; vale o mesmo que "{}".
        if (Encoding.UTF8.GetString(body).AsSpan().Trim().IsEmpty)
        {
            return "{}";
        }

        using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { AllowDuplicateProperties = false });
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    Write(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
