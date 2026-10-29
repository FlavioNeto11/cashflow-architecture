using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CashFlow.Security.Tests.Support;

/// <summary>
/// "API" de teste atrás do gateway: Kestrel com o certificado de servidor da PKI de teste (SAN localhost/127.0.0.1, CA de
/// teste — o lado cliente do mTLS do gateway aceita) que devolve, em JSON, exatamente os cabeçalhos de requisição que
/// recebeu. É o único jeito de PROVAR em runtime o que a allowlist do YARP deixa passar (auditoria F5, P2-3). Não exige
/// certificado de cliente: o que se mede aqui é o encaminhamento, não o mTLS (T-17 cobre isso).
/// </summary>
public sealed class EchoUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    public EchoUpstream(TestPki pki)
    {
        Port = HostSettings.FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, Port, listener => listener.UseHttps(pki.TransactionsServer.Certificate)));
        _app = builder.Build();
        _app.Map("/{**path}", async (HttpContext http) =>
        {
            // Nomes como chegaram no fio (em HTTP/2 vêm em minúsculas, salvo os que o Kestrel canoniza): quem lê compara sem caixa.
            var headers = new JsonObject();
            foreach (var header in http.Request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
            {
                headers[header.Key] = string.Join(", ", header.Value.ToArray());
            }

            var body = await new StreamReader(http.Request.Body).ReadToEndAsync();
            var payload = new JsonObject
            {
                ["method"] = http.Request.Method,
                ["path"] = http.Request.Path.Value,
                ["protocol"] = http.Request.Protocol,
                ["headers"] = headers,
                ["bodyLength"] = body.Length,
                ["declaredContentLength"] = http.Request.ContentLength,
            };
            http.Response.ContentType = "application/json";
            await http.Response.WriteAsync(payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        });
    }

    public int Port { get; }

    /// <summary>Valor do cabeçalho ecoado, sem distinguir caixa no nome (HTTP/2 transporta nomes em minúsculas); null se não chegou.</summary>
    public static string? Header(JsonObject echoedHeaders, string name) =>
        echoedHeaders.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();

    public Task StartAsync() => _app.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
