using System.Globalization;
using System.Text.Json;
using CashFlow.Transactions.Api.Application;
using CashFlow.Hosting.Security;
using CashFlow.Transactions.Api.Security;

namespace CashFlow.Transactions.Api.Http;

public static class TransactionEndpoints
{
    private const string Base = "/api/v1/merchants";

    public static WebApplication MapTransactionEndpoints(this WebApplication app)
    {
        // Ordem de decisão: autenticação (401) → escopo (403) pela política do endpoint → tenant (404) pelo filtro do grupo.
        var merchant = app.MapGroup($"{Base}/{{merchantId:guid}}").AddEndpointFilter<TenantFilter>();

        merchant.MapPost("/transactions", RegisterAsync).RequireAuthorization(TransactionScopes.Write);
        merchant.MapPost("/transactions/{id:guid}/reversal", ReverseAsync).RequireAuthorization(TransactionScopes.Write);
        merchant.MapGet("/transactions", ListAsync).RequireAuthorization(TransactionScopes.Read);
        merchant.MapGet("/transactions/{id:guid}", GetAsync).RequireAuthorization(TransactionScopes.Read);

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        Guid merchantId, HttpContext http, TransactionService service, CancellationToken cancellationToken)
    {
        if (!RequestParsing.TryReadIdempotencyKey(http.Request, out var key, out var keyError))
        {
            return keyError!;
        }

        var body = await RequestParsing.ReadBodyAsync(http.Request, cancellationToken);
        if (body is null)
        {
            return Problems.PayloadTooLarge(RequestParsing.MaxBodyBytes);
        }

        // Ordem: hash (JSON bem formado) → replay → leitura estrita e validação. O replay vem antes da validação
        // para que uma regra mais estrita publicada depois não transforme o retry de um pedido já aceito em 400/422.
        string hash;
        try
        {
            hash = RequestHasher.Compute("POST", $"{Base}/{merchantId:D}/transactions", body);
        }
        catch (JsonException)
        {
            return Problems.BadRequest("JSON malformado, com membro desconhecido ou duplicado.");
        }

        // TenantFilter já garantiu o chamador; a checagem explícita evita depender da ordem dos filtros (P2-11).
        if (!Caller.TryFrom(http.User, out var caller))
        {
            return Problems.Forbidden();
        }

        if (await service.FindReplayAsync(caller, key, hash, cancellationToken) is { } replay)
        {
            return ToResult(replay, http, merchantId);
        }

        CreateTransactionRequest request;
        try
        {
            request = RequestParsing.DeserializeCreate(body);
        }
        catch (JsonException)
        {
            return Problems.BadRequest("JSON malformado, com membro desconhecido ou duplicado.");
        }

        var command = RequestParsing.ToCommand(request);
        if (!command.IsSuccess)
        {
            return Problems.Validation(command.Errors!);
        }

        var outcome = await service.RegisterAsync(caller, key, hash, command.Value, cancellationToken);
        return ToResult(outcome, http, merchantId);
    }

    private static async Task<IResult> ReverseAsync(
        Guid merchantId, Guid id, HttpContext http, TransactionService service, CancellationToken cancellationToken)
    {
        if (!RequestParsing.TryReadIdempotencyKey(http.Request, out var key, out var keyError))
        {
            return keyError!;
        }

        var body = await RequestParsing.ReadBodyAsync(http.Request, cancellationToken);
        if (body is null)
        {
            return Problems.PayloadTooLarge(RequestParsing.MaxBodyBytes);
        }

        string hash;
        try
        {
            hash = RequestHasher.Compute("POST", $"{Base}/{merchantId:D}/transactions/{id:D}/reversal", body);
        }
        catch (JsonException)
        {
            return Problems.BadRequest("JSON malformado, com membro desconhecido ou duplicado.");
        }

        // TenantFilter já garantiu o chamador; a checagem explícita evita depender da ordem dos filtros (P2-11).
        if (!Caller.TryFrom(http.User, out var caller))
        {
            return Problems.Forbidden();
        }

        if (await service.FindReplayAsync(caller, key, hash, cancellationToken) is { } replay)
        {
            return ToResult(replay, http, merchantId);
        }

        ReverseTransactionRequest request;
        try
        {
            request = RequestParsing.DeserializeReverse(body);
        }
        catch (JsonException)
        {
            return Problems.BadRequest("JSON malformado, com membro desconhecido ou duplicado.");
        }

        var reason = RequestParsing.ToReason(request);
        if (!reason.IsSuccess)
        {
            return Problems.Validation(reason.Errors!);
        }

        var outcome = await service.ReverseAsync(caller, id, key, hash, reason.Value, cancellationToken);
        return ToResult(outcome, http, merchantId);
    }

    private static async Task<IResult> GetAsync(
        Guid merchantId, Guid id, TransactionQueries queries, CancellationToken cancellationToken)
    {
        // merchantId já é igual ao claim (TenantFilter); a consulta filtra por merchant e id.
        var detail = await queries.GetAsync(merchantId, id, cancellationToken);
        return detail is null ? Problems.NotFound() : Results.Json(detail, Json.Output);
    }

    private static async Task<IResult> ListAsync(
        Guid merchantId, HttpContext http, TransactionQueries queries, CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        var errors = new Dictionary<string, string[]>();

        var from = ParseDate(query["from"], "from", errors);
        var to = ParseDate(query["to"], "to", errors);
        if (from is { } f && to is { } t && f > t)
        {
            errors["from"] = ["'from' deve ser menor ou igual a 'to'."];
        }

        var page = ParseInt(query["page"], "page", 1, errors, min: 1, max: int.MaxValue);
        var pageSize = ParseInt(query["pageSize"], "pageSize", TransactionQueries.DefaultPageSize, errors, min: 1, max: TransactionQueries.MaxPageSize);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var result = await queries.ListAsync(merchantId, from, to, page, pageSize, cancellationToken);
        return Results.Json(result, Json.Output);
    }

    private static IResult ToResult(Outcome outcome, HttpContext http, Guid merchantId)
    {
        switch (outcome)
        {
            case Outcome.Stored stored:
                http.Response.Headers.Location = $"{Base}/{merchantId:D}/transactions/{stored.TransactionId:D}";
                if (stored.Replayed)
                {
                    http.Response.Headers["Idempotency-Replayed"] = "true";
                }

                // Corpo guardado em jsonb: o Postgres normaliza ordem das chaves e espaços, então o replay é
                // semanticamente igual ao 201 original, não idêntico byte a byte.
                return Results.Text(stored.Body, "application/json", statusCode: stored.Status);
            case Outcome.Invalid invalid:
                return Problems.Validation(invalid.Errors);
            case Outcome.KeyReusedWithDifferentPayload:
                return Problems.Unprocessable("A Idempotency-Key já foi usada com um payload diferente.");
            case Outcome.NotFound:
                return Problems.NotFound();
            case Outcome.AlreadyReversed:
                return Problems.Conflict("O lançamento já foi estornado.");
            case Outcome.Busy:
                return Problems.Busy(http);
            default:
                throw new InvalidOperationException($"Resultado não tratado: {outcome.GetType().Name}.");
        }
    }

    private static DateOnly? ParseDate(string? text, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors[field] = ["Use o formato yyyy-MM-dd."];
        return null;
    }

    private static int ParseInt(string? text, string field, int fallback, Dictionary<string, string[]> errors, int min, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            errors[field] = [$"Deve ser um inteiro entre {min} e {max}."];
            return fallback;
        }

        return value;
    }
}
