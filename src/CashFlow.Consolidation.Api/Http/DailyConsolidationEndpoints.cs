using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashFlow.Hosting.Security;

namespace CashFlow.Consolidation.Api.Http;

/// <summary>Escopos OAuth2 da API de consolidado; cada um é também o nome da política de autorização.</summary>
public static class ConsolidationScopes
{
    public const string Read = "consolidation.read";
}

/// <summary>Meter do serviço (negações cross-tenant do TenantFilter).</summary>
public static class ConsolidationMetrics
{
    public const string MeterName = "CashFlow.Consolidation";
}

/// <summary>Contrato de target.md 8.2: dinheiro como string com 2 casas, nunca número JSON.</summary>
public sealed record DailyConsolidationResponse(
    Guid MerchantId,
    string Date,
    string Currency,
    string Credits,
    string Debits,
    string DailyNet,
    int EntriesCount,
    DateTime? AsOf,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClosingBalance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OpeningBalanceAssumption);

public static class DailyConsolidationEndpoints
{
    public const string ClosingBalanceInclude = "closingBalance";

    /// <summary>Hipótese de DEC-01/G1-01: saldo de abertura 0 antes da primeira data com movimento.</summary>
    public const string OpeningBalanceAssumption = "ZERO_AT_FIRST_DATE";

    private static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapDailyConsolidationEndpoints(this IEndpointRouteBuilder app)
    {
        // Ordem: authn (401) → escopo (403) → rota = claim (404, TenantFilter) → validação (422).
        app.MapGroup("/api/v1/merchants/{merchantId:guid}")
            .AddEndpointFilter<TenantFilter>()
            .MapGet("/consolidations/daily", GetDailyAsync)
            .RequireAuthorization(ConsolidationScopes.Read);
        return app;
    }

    private static async Task<IResult> GetDailyAsync(
        Guid merchantId, HttpContext http, ConsolidationQueries queries, CancellationToken cancellationToken)
    {
        var query = http.Request.Query;
        var errors = new Dictionary<string, string[]>();

        // 422 (não 400), como os filtros from/to da transactions-api: o pedido é sintaticamente HTTP válido e o
        // problema é o valor do parâmetro.
        DateOnly date = default;
        if (query["date"].Count != 1
            || !DateOnly.TryParseExact(query["date"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            errors["date"] = ["Informe um único 'date' no formato yyyy-MM-dd."];
        }

        var include = query["include"];
        var includeClosing = include.Count == 1 && include.ToString() == ClosingBalanceInclude;
        if (include.Count > 0 && !includeClosing)
        {
            errors["include"] = [$"Valor aceito: '{ClosingBalanceInclude}'."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Dados inválidos.");
        }

        var reading = await queries.DailyAsync(merchantId, date, includeClosing, cancellationToken);
        http.Response.Headers.CacheControl = "no-store"; // security.md 5 (no-store no MVP): dado financeiro fora de caches
        return Results.Json(
            new DailyConsolidationResponse(
                merchantId,
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "BRL",
                Money(reading.Credits),
                Money(reading.Debits),
                Money(reading.Credits - reading.Debits),
                reading.EntriesCount,
                reading.AsOf,
                reading.Closing is { } closing ? Money(closing) : null,
                includeClosing ? OpeningBalanceAssumption : null),
            Output);
    }

    private static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);
}
