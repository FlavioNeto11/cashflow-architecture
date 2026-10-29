using Yarp.ReverseProxy.Forwarder;

namespace CashFlow.Gateway;

/// <summary>
/// Higiene da borda (security.md 5): corpo acima do limite → 413 antes de autenticar ou encaminhar; corpo de comprimento
/// desconhecido (Transfer-Encoding sem Content-Length) → 411, porque a checagem antecipada precisa do comprimento
/// declarado. Requisição sem corpo (nem Content-Length nem Transfer-Encoding; RFC 9112 §6.3) passa: não há nada a medir,
/// o Kestrel ainda aplica MaxRequestBodySize e a API decide (400 se exigir corpo). Cabeçalhos de segurança em toda
/// resposta (este middleware é o primeiro do pipeline). O "Server: Kestrel" é desligado no Kestrel (Program.cs) e o da API
/// removido por transform do YARP (ProxyRoutes, When=Always). Nada aqui lê o corpo nem o token.
/// </summary>
internal sealed class EdgeMiddleware(RequestDelegate next, LimitSettings limits)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var response = context.Response;
        response.OnStarting(static state =>
        {
            var headers = ((HttpResponse)state).Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            return Task.CompletedTask;
        }, response);

        var request = context.Request;
        if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method) || HttpMethods.IsPatch(request.Method))
        {
            if (request.ContentLength is null && request.Headers.TransferEncoding.Count > 0)
            {
                // Só corpo de comprimento desconhecido; POST sem corpo algum segue para a API (ver sumário da classe).
                await Results.Problem(statusCode: StatusCodes.Status411LengthRequired, title: "Content-Length é obrigatório.").ExecuteAsync(context);
                return;
            }

            if (request.ContentLength > limits.MaxBodyBytes)
            {
                await Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Corpo da requisição excede o limite.",
                    detail: $"Limite: {limits.MaxBodyBytes} bytes.").ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }
}

/// <summary>
/// Depois do proxy: erro de encaminhamento (destino fora, handshake mTLS recusado, tempo esgotado) vira problem+json
/// com o status que o YARP já decidiu (502/504), sem detalhes do destino para o cliente; o detalhe fica no log do YARP.
/// </summary>
internal static class ForwarderErrorResponse
{
    public static async Task WriteAsync(HttpContext context)
    {
        var error = context.Features.Get<IForwarderErrorFeature>();
        if (error is null || context.Response.HasStarted)
        {
            return;
        }

        var status = context.Response.StatusCode;
        var title = status == StatusCodes.Status504GatewayTimeout ? "Tempo limite ao chamar o serviço." : "Serviço indisponível.";
        await Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
    }
}
