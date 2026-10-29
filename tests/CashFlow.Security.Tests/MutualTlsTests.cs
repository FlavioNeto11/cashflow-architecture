using System.Net;
using System.Security.Authentication;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Hosting.Security;
using CashFlow.Security.Tests.Support;

namespace CashFlow.Security.Tests;

/// <summary>
/// T-17 (mTLS gateway → APIs): o listener de negócio da API só completa o handshake com o certificado do gateway
/// (CA local, EKU clientAuth, SAN gateway.internal, fora da blocklist). Cada negativo é uma conexão REAL recusada
/// (sem resposta HTTP) e, quando o certificado chega ao validador, mtls_handshake_failed_total{side=server,reason}.
/// Do lado cliente, o gateway recusa a API cujo certificado não fecha na sua CA (502 + side=client).
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class MutualTlsTests(SecurityFixture fx)
{
    [Fact]
    public async Task WithoutClientCertificate_HandshakeIsRefused()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        using var client = fx.DirectTransactions(clientCertificate: null);

        await AssertHandshakeRefusedAsync(client, token);
    }

    [Theory]
    [InlineData("foreign-ca", "untrusted_ca")]
    [InlineData("expired", "expired")]
    [InlineData("wrong-san", "name_mismatch")]
    [InlineData("wrong-eku", "eku")]
    [InlineData("blocked", "blocked_thumbprint")]
    public async Task InvalidClientCertificate_HandshakeIsRefused_AndCounted(string certificate, string reason)
    {
        var leaf = certificate switch
        {
            "foreign-ca" => fx.Pki.ForeignClient,
            "expired" => fx.Pki.ExpiredClient,
            "wrong-san" => fx.Pki.WrongSanClient,
            "wrong-eku" => fx.Pki.WrongEkuClient,
            _ => fx.Pki.BlockedClient,
        };
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var client = fx.DirectTransactions(leaf.Certificate);

        await AssertHandshakeRefusedAsync(client, token);

        await Eventually.WaitAsync(
            () => Task.FromResult(metrics.Total("mtls_handshake_failed_total", "reason", reason)),
            total => total >= 1,
            TimeSpan.FromSeconds(5),
            $"mtls_handshake_failed_total{{reason={reason}}} >= 1");
        Assert.True(metrics.Total("mtls_handshake_failed_total", "side", "server") >= 1);
    }

    [Fact]
    public async Task PlaintextHttp_OnTheTlsPort_IsRefused()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{fx.Transactions.BusinessPort}"), Timeout = TimeSpan.FromSeconds(10) };

        var error = await Record.ExceptionAsync(() => client.GetAsync("/api/v1"));

        Assert.NotNull(error);
    }

    [Fact]
    public async Task HealthPort_StaysPlainHttp_WithoutCertificate()
    {
        using var live = await fx.Transactions.Health.GetAsync("/health/live");
        using var api = await fx.Transactions.Health.GetAsync("/api/v1");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode); // nenhuma rota de negócio na porta de saúde
    }

    [Fact]
    public async Task ConsolidationApi_AlsoRequiresTheGatewayCertificate()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("consolidation.read");
        using var without = fx.DirectConsolidation(clientCertificate: null);
        using var foreign = fx.DirectConsolidation(fx.Pki.ForeignClient.Certificate);
        using var gateway = fx.DirectConsolidation(fx.Pki.GatewayClient.Certificate);
        var url = Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01");

        Assert.NotNull(await Record.ExceptionAsync(() => Requests.GetAsync(without, url, token)));
        Assert.NotNull(await Record.ExceptionAsync(() => Requests.GetAsync(foreign, url, token)));
        using var ok = await Requests.GetAsync(gateway, url, token);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Gateway_RefusesApiCertificate_NotIssuedByItsCa()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var distrusting = new GatewayHost(fx.Keycloak, fx.Pki, fx.Transactions.BusinessPort, fx.Consolidation.BusinessPort)
        {
            ServerCaPath = fx.Pki.ForeignCaPath,
        }.Start();
        await SecurityFixture.WaitReadyAsync(distrusting.Health, "gateway (CA errada)");

        using var response = await Requests.PostTransactionAsync(distrusting.Edge, KeycloakFixture.MerchantA, token);

        await Requests.AssertProblemAsync(response, 502);
        Assert.Equal(1, metrics.Total("mtls_handshake_failed_total", "side", "client"));
        Assert.Equal(1, metrics.Total("mtls_handshake_failed_total", "reason", "untrusted_ca"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("127.0.0.1", body); // nada do destino vaza para o cliente
    }

    private static async Task AssertHandshakeRefusedAsync(HttpClient client, string token)
    {
        var error = await Record.ExceptionAsync(() => Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, token));

        Assert.NotNull(error);
        Assert.True(
            error is HttpRequestException or IOException or AuthenticationException,
            $"esperava falha de conexão/handshake, veio {error.GetType().Name}: {error.Message}");
    }
}
