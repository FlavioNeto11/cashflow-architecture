using CashFlow.Hosting.Security;

namespace CashFlow.Transactions.Api.Http;

/// <summary>
/// Todas as respostas de erro saem daqui, em application/problem+json. O 404 de recurso inexistente e o
/// 404 de outro tenant usam a MESMA função, sem detalhe: é o que torna os dois indistinguíveis (ADR-0006).
/// </summary>
internal static class Problems
{
    public static IResult Unauthorized() => SecurityProblems.Unauthorized();

    public static IResult Forbidden() => SecurityProblems.Forbidden();

    public static IResult NotFound() => SecurityProblems.NotFound();

    public static IResult BadRequest(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Requisição inválida.", detail: detail);

    public static IResult PayloadTooLarge(int maxBytes) =>
        Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Corpo da requisição grande demais.",
            detail: $"O limite é {maxBytes} bytes.");

    public static IResult Unprocessable(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Requisição não processável.", detail: detail);

    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity, title: "Dados inválidos.");

    public static IResult Conflict(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Conflito.", detail: detail);

    /// <summary>Operação com a mesma chave ainda em andamento: o cliente repete depois de Retry-After.</summary>
    public static IResult Busy(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "1";
        return Conflict("Operação com a mesma Idempotency-Key em processamento; repita em instantes.");
    }
}
