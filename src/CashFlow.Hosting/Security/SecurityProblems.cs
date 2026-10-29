using Microsoft.AspNetCore.Http;

namespace CashFlow.Hosting.Security;

/// <summary>
/// Respostas de segurança comuns às APIs, em application/problem+json. O 404 de outro tenant e o 404 de recurso
/// inexistente saem da MESMA função, sem detalhe: é o que os torna indistinguíveis (ADR-0006).
/// </summary>
public static class SecurityProblems
{
    public static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Não autenticado.");

    public static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Acesso negado.");

    public static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Recurso não encontrado.");
}
