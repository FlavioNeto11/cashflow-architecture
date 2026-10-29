using System.Diagnostics;
using System.Net;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Hosting.Security;
using CashFlow.Security.Tests.Support;
using Xunit.Abstractions;

namespace CashFlow.Security.Tests;

/// <summary>
/// T-07 (IdP fora): com o Keycloak PARADO, processos aquecidos seguem validando tokens pelo JWKS em cache (gateway e
/// APIs) e continuam prontos; um processo que sobe a frio não fica pronto (503) e responde 401
/// authn_failed_total{reason=metadata_unavailable} em vez de 500 (G1-15, GAP declarado); quando o IdP volta, ele se
/// recupera sozinho em menos de 90 s sem reinício.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class IdpOutageTests(SecurityFixture fx, ITestOutputHelper output)
{
    [Fact]
    public async Task WarmProcessesSurvive_ColdStartDegrades_AndRecoversWhenIdpReturns()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        var clock = Stopwatch.StartNew();

        await fx.Keycloak.StopAsync();
        output.WriteLine($"[{clock.Elapsed:mm\\:ss}] Keycloak parado");
        TransactionsApiHost? cold = null;
        try
        {
            // 1) Aquecidos: gateway + API com JWKS em cache seguem servindo e prontos.
            using (var viaGateway = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token))
            {
                Assert.Equal(HttpStatusCode.Created, viaGateway.StatusCode);
            }

            using (var ready = await fx.Transactions.Health.GetAsync("/health/ready"))
            {
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
            }

            output.WriteLine($"[{clock.Elapsed:mm\\:ss}] processos aquecidos: 201 pelo gateway e readiness 200 com IdP fora");

            // 2) Frio: novo processo da API sem conseguir o documento OIDC.
            cold = new TransactionsApiHost(fx.Databases, fx.Keycloak, fx.Pki).Start();
            using var metrics = new MetricCapture(SecurityMetrics.MeterName);
            using (var ready = await cold.Health.GetAsync("/health/ready"))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
                // O corpo é o padrão do MapHealthChecks ("Unhealthy"): o nome do check (oidc-metadata) fica só no log do
                // processo, não na resposta — nada sobre a topologia interna sai pela porta de saúde.
                Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
            }

            using (var live = await cold.Health.GetAsync("/health/live"))
            {
                Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            }

            using var coldClient = fx.Pki.CreateTlsClient(cold.BusinessUri, fx.Pki.GatewayClient.Certificate);
            using (var unauthenticated = await Requests.PostTransactionAsync(coldClient, KeycloakFixture.MerchantA, token))
            {
                await Requests.AssertProblemAsync(unauthenticated, 401);
            }

            Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "metadata_unavailable"));
            output.WriteLine($"[{clock.Elapsed:mm\\:ss}] processo frio: readiness 503 (oidc-metadata), live 200, token válido -> 401 metadata_unavailable");

            // 3) IdP volta: recuperação sem reinício.
            await fx.Keycloak.StartAsync();
            output.WriteLine($"[{clock.Elapsed:mm\\:ss}] Keycloak de volta (container reiniciado, mesma porta/issuer)");
            var recovery = Stopwatch.StartNew();
            await SecurityFixture.WaitReadyAsync(cold.Health, "transactions-api (frio) após o IdP voltar", seconds: 90);
            output.WriteLine($"[{clock.Elapsed:mm\\:ss}] processo frio pronto {recovery.Elapsed.TotalSeconds:0.0} s depois do IdP voltar");
            Assert.True(recovery.Elapsed < TimeSpan.FromSeconds(90));

            using var recovered = await Requests.PostTransactionAsync(coldClient, KeycloakFixture.MerchantA, token);
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        }
        finally
        {
            cold?.Dispose();
            if (!await IsKeycloakUpAsync())
            {
                await fx.Keycloak.StartAsync();
            }
        }
    }

    private async Task<bool> IsKeycloakUpAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync(fx.Keycloak.MetadataAddress);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}
