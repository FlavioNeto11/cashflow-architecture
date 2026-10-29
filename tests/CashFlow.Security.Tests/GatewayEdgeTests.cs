using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CashFlow.Security.Tests.Support;

namespace CashFlow.Security.Tests;

/// <summary>
/// Higiene da borda (security.md 5): corpo acima de 16 KB → 413 antes de autenticar; corpo chunked (Transfer-Encoding sem
/// Content-Length) → 411; /health/* e qualquer caminho fora das rotas → 404; destino fora → 502; cabeçalhos de segurança
/// e nenhum "Server" em TODAS essas respostas; leitura exige transactions.read; Idempotency-Key atravessa o gateway
/// (replay devolve a mesma transação). O POST sem corpo nenhum (não é 411) está em HeaderAllowlistTests, com o eco.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class GatewayEdgeTests(SecurityFixture fx)
{
    [Fact]
    public async Task BodyAboveLimit_Returns413_EvenWithoutToken()
    {
        var huge = $$"""{"type":"CREDIT","amount":"1.00","description":"{{new string('x', 17 * 1024)}}"}""";

        using var response = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token: null, huge);

        await Requests.AssertProblemAsync(response, 413);
        Requests.AssertSecurityHeaders(response);
    }

    /// <summary>
    /// 411 só para corpo com Transfer-Encoding e sem Content-Length (o gateway precisa do tamanho declarado para aplicar o
    /// limite antes de ler). Um POST sem corpo algum não é 411 (RFC 9112 6.3): segue para a API, que decide (P2-4).
    /// </summary>
    [Fact]
    public async Task ChunkedBodyWithoutContentLength_Returns411()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Requests.TransactionsUrl(KeycloakFixture.MerchantA))
        {
            Content = new StringContent(Requests.CreditJson(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.TransferEncodingChunked = true;

        using var response = await fx.Gateway.Edge.SendAsync(request);

        await Requests.AssertProblemAsync(response, 411);
        Requests.AssertSecurityHeaders(response);
    }

    /// <summary>
    /// 502 do próprio YARP (destino sem ninguém escutando) também sai com os cabeçalhos de segurança e sem "Server": o
    /// EdgeMiddleware é o primeiro do pipeline e o ResponseHeaderRemove tem When=Always (P2-1/P2-2).
    /// </summary>
    [Fact]
    public async Task UpstreamUnreachable_Returns502_WithSecurityHeaders()
    {
        var deadPort = HostSettings.FreePort();
        using var gateway = new GatewayHost(fx.Keycloak, fx.Pki, deadPort, deadPort).Start();
        await SecurityFixture.WaitReadyAsync(gateway.Health, "gateway (destino inexistente)");
        var token = await fx.Keycloak.MerchantATokenAsync();

        using var response = await Requests.GetAsync(gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01"), token);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Requests.AssertSecurityHeaders(response);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/api/v1")]
    [InlineData("/api/v1/merchants/33333333-3333-4333-8333-333333333333/unknown")]
    [InlineData("/api/v1/merchants/not-a-guid/transactions")]
    public async Task PathsOutsideTheRoutes_Return404(string path)
    {
        var token = await fx.Keycloak.MerchantATokenAsync();

        using var response = await Requests.GetAsync(fx.Gateway.Edge, path, token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// 401 da borda, 200 e 404 vindos da API (o 404 de outro tenant é a resposta em que a auditoria achou "Server: Kestrel"),
    /// 404 do isolamento de listener (/health/ready na porta de negócio, antes de qualquer rota) e 404 de caminho sem rota.
    /// 413, 411, 429 e 502 são verificados nos seus próprios testes com o mesmo AssertSecurityHeaders.
    /// </summary>
    [Fact]
    public async Task EveryResponse_CarriesSecurityHeaders_AndNoServerHeader()
    {
        var token = await fx.Keycloak.MerchantATokenAsync();
        using var denied = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token: null);
        using var proxied = await Requests.GetAsync(fx.Gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01"), token);
        using var proxiedNotFound = await Requests.GetAsync(fx.Gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantB, "2026-01-01"), token);
        using var healthOnBusinessPort = await Requests.GetAsync(fx.Gateway.Edge, "/health/ready", token: null);
        using var unrouted = await Requests.GetAsync(fx.Gateway.Edge, "/api/v1", token);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, proxied.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, proxiedNotFound.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, healthOnBusinessPort.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unrouted.StatusCode);
        foreach (var response in new[] { denied, proxied, proxiedNotFound, healthOnBusinessPort, unrouted })
        {
            Requests.AssertSecurityHeaders(response);
        }
    }

    [Fact]
    public async Task ReadingTransactions_RequiresReadScope()
    {
        var writeOnly = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        var readOnly = await fx.Keycloak.MerchantATokenAsync("transactions.read");
        using var created = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, writeOnly);
        var id = (await Requests.JsonAsync(created))["id"]!.GetValue<Guid>();
        var url = $"{Requests.TransactionsUrl(KeycloakFixture.MerchantA)}/{id}";

        using var forbidden = await Requests.GetAsync(fx.Gateway.Edge, url, writeOnly);
        using var allowed = await Requests.GetAsync(fx.Gateway.Edge, url, readOnly);
        using var writeForbidden = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, readOnly);

        await Requests.AssertProblemAsync(forbidden, 403);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(id, (await Requests.JsonAsync(allowed))["id"]!.GetValue<Guid>());
        await Requests.AssertProblemAsync(writeForbidden, 403);
    }

    [Fact]
    public async Task IdempotencyKey_CrossesTheGateway_ReplayReturnsSameTransaction()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        var key = Guid.NewGuid().ToString();

        using var first = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token, Requests.CreditJson("5.00"), key);
        using var replay = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token, Requests.CreditJson("5.00"), key);
        using var conflict = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token, Requests.CreditJson("6.00"), key);
        using var noKey = await Requests.SendAsync(fx.Gateway.Edge, HttpMethod.Post, Requests.TransactionsUrl(KeycloakFixture.MerchantA), token, Requests.CreditJson(), key: null);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal((await Requests.JsonAsync(first))["id"]!.GetValue<Guid>(), (await Requests.JsonAsync(replay))["id"]!.GetValue<Guid>());
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noKey.StatusCode);
    }

    [Fact]
    public async Task DevelopmentHeaders_AtTheGateway_NeverAuthenticate()
    {
        using var response = await Requests.SendAsync(
            fx.Gateway.Edge, HttpMethod.Post, Requests.TransactionsUrl(KeycloakFixture.MerchantA), token: null, Requests.CreditJson(), Guid.NewGuid().ToString(),
            request =>
            {
                request.Headers.Add("X-Dev-Sub", "intruso");
                request.Headers.Add("X-Dev-Client-Id", "intruso");
                request.Headers.Add("X-Dev-Merchant-Id", KeycloakFixture.MerchantA.ToString());
                request.Headers.Add("X-Dev-Scope", "transactions.write");
            });

        await Requests.AssertProblemAsync(response, 401);
    }
}
