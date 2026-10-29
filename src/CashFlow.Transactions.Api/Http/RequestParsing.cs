using System.Globalization;
using System.Text.Json;
using CashFlow.Transactions.Api.Domain;

namespace CashFlow.Transactions.Api.Http;

public sealed record RegisterCommand(EntryType Type, Money Money, DateOnly? EffectiveDate, string? Description);

/// <summary>Leitura do corpo (com limite) e conversão dos JSON em comandos. Sintaxe → 400; conteúdo → 422.</summary>
internal static class RequestParsing
{
    public const int MaxBodyBytes = 8 * 1024;
    public const int MaxIdempotencyKeyLength = 255;
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Lê o corpo no máximo até <see cref="MaxBodyBytes"/>. Devolve null quando excede (413).</summary>
    public static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        // O limite do Kestrel (MaxRequestBodySize) não vale no TestServer, e o corpo é lido aqui de qualquer forma.
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static bool TryReadIdempotencyKey(HttpRequest request, out string key, out IResult? error)
    {
        key = string.Empty;
        var values = request.Headers[IdempotencyKeyHeader];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
        {
            error = Problems.BadRequest($"O cabeçalho {IdempotencyKeyHeader} é obrigatório (um único valor não vazio).");
            return false;
        }

        if (values[0]!.Length > MaxIdempotencyKeyLength)
        {
            error = Problems.BadRequest($"{IdempotencyKeyHeader} excede {MaxIdempotencyKeyLength} caracteres.");
            return false;
        }

        key = values[0]!;
        error = null;
        return true;
    }

    /// <summary>JSON malformado, membro desconhecido ou duplicado, corpo não-objeto: <see cref="JsonException"/> (400).</summary>
    public static CreateTransactionRequest DeserializeCreate(byte[] body) =>
        JsonSerializer.Deserialize<CreateTransactionRequest>(body, Json.Input)
        ?? throw new JsonException("O corpo deve ser um objeto JSON.");

    public static ReverseTransactionRequest DeserializeReverse(byte[] body) =>
        body.Length == 0 || IsBlank(body)
            ? new ReverseTransactionRequest(null)
            : JsonSerializer.Deserialize<ReverseTransactionRequest>(body, Json.Input)
                ?? throw new JsonException("O corpo deve ser um objeto JSON.");

    public static Result<RegisterCommand> ToCommand(CreateTransactionRequest request)
    {
        var errors = new ValidationErrors();

        var type = default(EntryType);
        if (TryGetString(request.Type, "type", required: true, errors, out var typeText)
            && !EntryTypeExtensions.TryParseWire(typeText, out type))
        {
            errors.Add("type", "Use CREDIT ou DEBIT.");
        }

        TryGetString(request.Currency, "currency", required: false, errors, out var currency);
        TryGetString(request.Description, "description", required: false, errors, out var description);

        var amount = 0m;
        var amountParsed = false;
        if (request.Amount is not { ValueKind: JsonValueKind.String } amountElement)
        {
            errors.Add("amount", request.Amount is null or { ValueKind: JsonValueKind.Null }
                ? "Campo obrigatório."
                : "O valor deve ser uma string decimal (ex.: \"100.00\"), não um número JSON.");
        }
        else if (!Money.TryParseAmount(amountElement.GetString(), out amount))
        {
            errors.Add("amount", "Valor decimal inválido (ex.: \"100.00\").");
        }
        else
        {
            amountParsed = true;
        }

        DateOnly? effectiveDate = null;
        if (TryGetString(request.EffectiveDate, "effectiveDate", required: false, errors, out var dateText) && dateText is not null)
        {
            if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                effectiveDate = date;
            }
            else
            {
                errors.Add("effectiveDate", "Use o formato yyyy-MM-dd.");
            }
        }

        var money = default(Money);
        if (amountParsed)
        {
            var moneyResult = Money.Create(amount, currency);
            if (moneyResult.IsSuccess)
            {
                money = moneyResult.Value;
            }
            else
            {
                errors.AddRange(moneyResult.Errors!);
            }
        }

        return errors.Any
            ? Result<RegisterCommand>.Fail(errors)
            : Result<RegisterCommand>.Ok(new RegisterCommand(type, money, effectiveDate, description));
    }

    public static Result<string?> ToReason(ReverseTransactionRequest request)
    {
        var errors = new ValidationErrors();
        TryGetString(request.Reason, "reason", required: false, errors, out var reason);
        return errors.Any ? Result<string?>.Fail(errors) : Result<string?>.Ok(reason);
    }

    private static bool TryGetString(JsonElement? element, string field, bool required, ValidationErrors errors, out string? value)
    {
        value = null;
        if (element is null or { ValueKind: JsonValueKind.Null })
        {
            if (required)
            {
                errors.Add(field, "Campo obrigatório.");
            }

            return !required;
        }

        if (element.Value.ValueKind != JsonValueKind.String)
        {
            errors.Add(field, "Deve ser uma string.");
            return false;
        }

        value = element.Value.GetString();
        return true;
    }

    private static bool IsBlank(byte[] body) => body.All(b => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n');
}
