using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using CashFlow.Security.Tests.Support;
using Xunit.Abstractions;

namespace CashFlow.Security.Tests;

/// <summary>
/// Prova em runtime da allowlist de cabeçalhos do gateway (security.md 5; auditoria F5, P2-3): um gateway real na frente de
/// um eco TLS que devolve os cabeçalhos recebidos. O que o cliente injeta (X-Dev-*, X-Forwarded-For forjado, Cookie,
/// cabeçalho arbitrário) não chega ao destino; o que a API precisa (Authorization, Idempotency-Key, Content-Type,
/// Content-Length, traceparent) chega. Também fixa o fato de que um POST sem corpo não é 411 (P2-4).
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class HeaderAllowlistTests(SecurityFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private EchoUpstream _echo = null!;
    private GatewayHost _gateway = null!;

    public async Task InitializeAsync()
    {
        _echo = new EchoUpstream(fx.Pki);
        await _echo.StartAsync();
        _gateway = new GatewayHost(fx.Keycloak, fx.Pki, _echo.Port, _echo.Port).Start();
        await SecurityFixture.WaitReadyAsync(_gateway.Health, "gateway (eco)");
    }

    public async Task DisposeAsync()
    {
        _gateway.Dispose();
        await _echo.DisposeAsync();
    }

    [Fact]
    public async Task OnlyAllowlistedHeaders_ReachTheDestination()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        var key = Guid.NewGuid().ToString();
        var json = Requests.CreditJson();
        const string clientTraceId = "0af7651916cd43dd8448eb211c80319c";
        const string clientTraceparent = $"00-{clientTraceId}-b7ad6b7169203331-01";

        // Activity do servidor (gateway) desta requisição, para comparar com o traceparent que chegou ao destino.
        var gatewayTraces = new List<(string TraceId, string? ParentId)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("url.path")?.ToString() == Requests.TransactionsUrl(KeycloakFixture.MerchantA) && activity.GetTagItem("server.port")?.ToString() == HostSettings.Port(_gateway.BusinessPort))
                {
                    lock (gatewayTraces)
                    {
                        gatewayTraces.Add((activity.TraceId.ToHexString(), activity.ParentId));
                    }
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        // Cliente sem propagação automática de Activity: o DiagnosticsHandler do HttpClient substituiria o traceparent
        // manual pelo da Activity do processo de teste (observado), e o que se quer medir é o cabeçalho do CLIENTE.
        using var rawClient = new HttpClient(new SocketsHttpHandler { ActivityHeadersPropagator = null }) { BaseAddress = _gateway.Edge.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };

        using var response = await Requests.SendAsync(
            rawClient, HttpMethod.Post, Requests.TransactionsUrl(KeycloakFixture.MerchantA), token, json, key,
            request =>
            {
                request.Headers.Add("X-Dev-Sub", "intruso");
                request.Headers.Add("X-Dev-Merchant-Id", KeycloakFixture.MerchantB.ToString());
                request.Headers.Add("X-Forwarded-For", "10.0.0.1");
                request.Headers.Add("X-Forwarded-Host", "evil.example");
                request.Headers.Add("X-Real-IP", "10.0.0.1");
                request.Headers.Add("X-Arbitrary-Header", "qualquer");
                request.Headers.Add("Cookie", "session=abc");
                request.Headers.Add("traceparent", clientTraceparent);
                request.Headers.UserAgent.ParseAdd("security-tests/1.0");
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = await Requests.JsonAsync(response);
        var headers = (JsonObject)echoed["headers"]!;
        var names = headers.Select(h => h.Key).ToList();

        Assert.Equal("POST", (string?)echoed["method"]);
        Assert.Equal(Requests.TransactionsUrl(KeycloakFixture.MerchantA), (string?)echoed["path"]);
        Assert.Equal("HTTP/2", (string?)echoed["protocol"]); // salto gateway → API negociado em h2 sobre TLS (nomes de cabeçalho em minúsculas no fio)

        // Chega o que a API precisa (o valor do Authorization não é inspecionado além do esquema: nada de token em log).
        Assert.StartsWith("Bearer ", EchoUpstream.Header(headers, "Authorization"));
        Assert.Equal(key, EchoUpstream.Header(headers, "Idempotency-Key"));
        Assert.StartsWith("application/json", EchoUpstream.Header(headers, "Content-Type"));
        Assert.Equal(json.Length, (int?)echoed["bodyLength"]);

        // traceparent (target.md 10: "aceito só como pai, nunca confiado para autorização"): o gateway adota o trace-id do
        // cliente como pai e envia ao destino um traceparent próprio (mesmo trace-id, span-id do gateway), não o do cliente.
        var forwardedTraceparent = EchoUpstream.Header(headers, "traceparent");
        var gateway = Assert.Single(gatewayTraces);
        output.WriteLine($"traceparent do cliente: {clientTraceparent}");
        output.WriteLine($"traceparent no destino: {forwardedTraceparent}");
        output.WriteLine($"gateway server span: traceId={gateway.TraceId} parentId={gateway.ParentId}");
        Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", forwardedTraceparent);
        Assert.Equal(clientTraceparent, gateway.ParentId);
        Assert.Equal(clientTraceId, gateway.TraceId);
        Assert.StartsWith($"00-{clientTraceId}-", forwardedTraceparent);
        Assert.NotEqual(clientTraceparent, forwardedTraceparent);

        // Content-Length declarado atravessa (o corpo não vira chunked no salto gateway → API).
        Assert.Equal(json.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), EchoUpstream.Header(headers, "Content-Length"));
        Assert.Equal(json.Length, (long?)echoed["declaredContentLength"]);
        Assert.DoesNotContain(names, n => n.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase));

        // Não chega nada injetado pelo cliente nem identidade de proxy forjável.
        Assert.DoesNotContain(names, n => n.StartsWith("X-Dev-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("Forwarded", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("X-Arbitrary-Header", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("User-Agent", StringComparison.OrdinalIgnoreCase));

        // Host é o do destino (YARP reescreve), não o da borda.
        Assert.Equal($"127.0.0.1:{_echo.Port}", EchoUpstream.Header(headers, "Host"));
    }

    /// <summary>POST sem corpo nenhum (nem Content-Length nem Transfer-Encoding) não é 411: chega à API, que decide (RFC 9112 6.3; P2-4).</summary>
    [Fact]
    public async Task BodylessPost_IsForwarded_Not411()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");

        using var response = await Requests.SendAsync(_gateway.Edge, HttpMethod.Post, Requests.TransactionsUrl(KeycloakFixture.MerchantA), token, json: null, key: Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // o eco respondeu: a requisição atravessou o gateway
        var echoed = await Requests.JsonAsync(response);
        Assert.Equal("POST", (string?)echoed["method"]);
        Assert.Equal(0, (int?)echoed["bodyLength"]);
    }
}
