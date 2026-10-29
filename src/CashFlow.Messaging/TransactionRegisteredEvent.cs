using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CashFlow.Messaging;

/// <summary>Evento TransactionRegistered v1 já validado (target.md 8.3). Valor sempre positivo; o sinal vem do tipo.</summary>
public sealed record TransactionRegisteredEvent(
    Guid EventId,
    DateTime OccurredAt,
    Guid MerchantId,
    Guid TransactionId,
    bool IsCredit,
    decimal Amount,
    DateOnly EffectiveDate,
    Guid? ReversalOf)
{
    public const string Type = "TransactionRegistered";
    public const int Version = 1;
    public const string Currency = "BRL";
    public const string ContentType = "application/json";
}

/// <summary>
/// Motivos (conjunto fechado: vira rótulo de métrica e cabeçalho x-reason) pelos quais uma mensagem vai à DLQ.
/// Nenhum deles é transitório: reprocessar a mesma mensagem daria o mesmo resultado.
/// </summary>
public static class DeadLetterReasons
{
    public const string MalformedJson = "malformed_json";
    public const string UnknownType = "unknown_type";
    public const string UnsupportedVersion = "unsupported_version";
    public const string InvalidPayload = "invalid_payload";
    public const string MerchantMismatch = "merchant_mismatch";
    public const string MessageIdMismatch = "message_id_mismatch";
    public const string Stale = "stale";

    /// <summary>SQLSTATE 22003 ao somar na projeção: não transitório; exige rebuild/decisão sobre o teto.</summary>
    public const string ProjectionOverflow = "projection_overflow";

    /// <summary>Demais SQLSTATE das classes 22/23 (dado ou restrição de integridade): determinístico.</summary>
    public const string ConstraintViolation = "constraint_violation";
}

/// <summary>Resultado da validação: o evento ou o motivo (e o campo, sem o valor) da rejeição.</summary>
public readonly record struct EventValidation(TransactionRegisteredEvent? Event, string? Reason, string? Field)
{
    public bool IsValid => Event is not null;

    public static EventValidation Ok(TransactionRegisteredEvent value) => new(value, null, null);

    public static EventValidation Reject(string reason, string? field = null) => new(null, reason, field);
}

/// <summary>
/// Validação estrita do contrato no consumidor (ADR-0002 item 5): tipo/versão conhecidos, BRL, valor como string
/// decimal com escala ≤ 2 e &gt; 0, UUIDs válidos, entryType CREDIT|DEBIT, effectiveDate yyyy-MM-dd e
/// merchantId do payload igual ao cabeçalho merchant_id (proteção cross-tenant no transporte).
/// </summary>
public static partial class TransactionRegisteredContract
{
    // numeric(18,2): até 16 dígitos inteiros, até 2 decimais, sem sinal, expoente ou separador de milhar.
    [GeneratedRegex(@"^(0|[1-9][0-9]{0,15})(\.[0-9]{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();

    public static EventValidation Validate(ReadOnlySpan<byte> body, string? headerMerchantId, string? messageId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException)
        {
            return EventValidation.Reject(DeadLetterReasons.MalformedJson);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return EventValidation.Reject(DeadLetterReasons.MalformedJson);
            }

            if (!TryString(root, "type", out var type) || type != TransactionRegisteredEvent.Type)
            {
                return EventValidation.Reject(DeadLetterReasons.UnknownType, "type");
            }

            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber) || versionNumber != TransactionRegisteredEvent.Version)
            {
                return EventValidation.Reject(DeadLetterReasons.UnsupportedVersion, "version");
            }

            if (!TryGuid(root, "eventId", out var eventId))
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "eventId");
            }

            if (!TryGuid(root, "merchantId", out var merchantId))
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "merchantId");
            }

            if (!TryGuid(root, "transactionId", out var transactionId))
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "transactionId");
            }

            if (!TryString(root, "occurredAt", out var occurredText)
                || !DateTimeOffset.TryParse(occurredText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var occurredAt))
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "occurredAt");
            }

            if (!TryString(root, "entryType", out var entryType) || entryType is not ("CREDIT" or "DEBIT"))
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "entryType");
            }

            if (!TryString(root, "amount", out var amountText) || !AmountPattern().IsMatch(amountText)
                || !decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                || amount <= 0)
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "amount");
            }

            if (!TryString(root, "currency", out var currency) || currency != TransactionRegisteredEvent.Currency)
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "currency");
            }

            if (!TryString(root, "effectiveDate", out var dateText)
                || !DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var effectiveDate))
            {
                return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "effectiveDate");
            }

            Guid? reversalOf = null;
            if (root.TryGetProperty("reversalOf", out var reversal) && reversal.ValueKind != JsonValueKind.Null)
            {
                if (reversal.ValueKind != JsonValueKind.String || !Guid.TryParseExact(reversal.GetString(), "D", out var reversalId))
                {
                    return EventValidation.Reject(DeadLetterReasons.InvalidPayload, "reversalOf");
                }

                reversalOf = reversalId;
            }

            if (!Guid.TryParseExact(headerMerchantId, "D", out var headerMerchant) || headerMerchant != merchantId)
            {
                return EventValidation.Reject(DeadLetterReasons.MerchantMismatch, HeaderNames.MerchantId);
            }

            if (!Guid.TryParseExact(messageId, "D", out var messageGuid) || messageGuid != eventId)
            {
                return EventValidation.Reject(DeadLetterReasons.MessageIdMismatch, HeaderNames.MessageId);
            }

            return EventValidation.Ok(new TransactionRegisteredEvent(
                eventId, occurredAt.UtcDateTime, merchantId, transactionId, entryType == "CREDIT", amount, effectiveDate, reversalOf));
        }
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return TryString(root, name, out var text) && Guid.TryParseExact(text, "D", out value) && value != Guid.Empty;
    }
}
