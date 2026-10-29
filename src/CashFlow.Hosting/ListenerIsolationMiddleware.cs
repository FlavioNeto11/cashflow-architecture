using Microsoft.AspNetCore.Http;

namespace CashFlow.Hosting;

/// <summary>
/// Isola os dois listeners pela porta local da conexão (não pelo cabeçalho Host, que o cliente controla
/// e que carrega a porta publicada no host, não a do contêiner).
/// Porta de saúde: só /health/live e /health/ready; o resto responde 404.
/// Porta de negócio: /health/* responde 404.
/// </summary>
internal sealed class ListenerIsolationMiddleware(RequestDelegate next, ListenerPorts ports)
{
    private static readonly PathString HealthPrefix = new("/health");
    private static readonly PathString Live = new("/health/live");
    private static readonly PathString Ready = new("/health/ready");

    public Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var onHealthListener = context.Connection.LocalPort == ports.Health;

        var allowed = onHealthListener
            ? path.Equals(Live, StringComparison.OrdinalIgnoreCase) || path.Equals(Ready, StringComparison.OrdinalIgnoreCase)
            : !path.StartsWithSegments(HealthPrefix, StringComparison.OrdinalIgnoreCase);

        if (allowed)
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
