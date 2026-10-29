using System.Net;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Security.Tests.Support;

namespace CashFlow.Security.Tests;

/// <summary>
/// T-28 (isolamento de tenant com tokens reais): o merchant da rota tem de ser o merchant_id do token. Cruzar tenants
/// dá 404 idêntico ao de recurso inexistente (ADR-0006) nas duas APIs, pelo gateway e direto, e conta
/// authz_cross_tenant_denied_total no Meter do serviço; o rate limit do gateway é por merchant (um tenant em 429 não
/// afeta o outro); um merchant_id adulterado no token é 401 (assinatura), nunca outro tenant.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TenantIsolationTests(SecurityFixture fx)
{
    [Fact]
    public async Task MerchantA_CannotWriteIntoMerchantB_404IdenticalToNotFound()
    {
        var tokenA = await fx.Keycloak.MerchantATokenAsync();
        using var metrics = new MetricCapture("CashFlow.Transactions");

        using var crossTenant = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantB, tokenA);
        using var missing = await Requests.GetAsync(fx.Gateway.Edge, $"{Requests.TransactionsUrl(KeycloakFixture.MerchantA)}/{Guid.NewGuid()}", tokenA);

        await Requests.AssertProblemAsync(crossTenant, 404);
        await Requests.AssertProblemAsync(missing, 404);
        var crossBody = await Requests.JsonAsync(crossTenant);
        var missingBody = await Requests.JsonAsync(missing);
        Assert.Equal(missingBody["title"]?.ToString(), crossBody["title"]?.ToString());
        Assert.Equal(missingBody["type"]?.ToString(), crossBody["type"]?.ToString());
        Assert.Null(crossBody["detail"]);
        Assert.Equal(1, metrics.Total("authz_cross_tenant_denied_total"));
    }

    [Fact]
    public async Task MerchantA_CannotReadMerchantBTransactions()
    {
        var tokenA = await fx.Keycloak.MerchantATokenAsync();
        var tokenB = await fx.Keycloak.MerchantBTokenAsync();
        using var createdByB = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantB, tokenB, Requests.CreditJson("77.00"));
        Assert.Equal(HttpStatusCode.Created, createdByB.StatusCode);
        var id = (await Requests.JsonAsync(createdByB))["id"]!.GetValue<Guid>();

        using var byOwner = await Requests.GetAsync(fx.Gateway.Edge, $"{Requests.TransactionsUrl(KeycloakFixture.MerchantB)}/{id}", tokenB);
        using var byIntruder = await Requests.GetAsync(fx.Gateway.Edge, $"{Requests.TransactionsUrl(KeycloakFixture.MerchantB)}/{id}", tokenA);
        using var viaOwnRoute = await Requests.GetAsync(fx.Gateway.Edge, $"{Requests.TransactionsUrl(KeycloakFixture.MerchantA)}/{id}", tokenA);
        using var list = await Requests.GetAsync(fx.Gateway.Edge, Requests.TransactionsUrl(KeycloakFixture.MerchantB), tokenA);

        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        await Requests.AssertProblemAsync(byIntruder, 404);
        await Requests.AssertProblemAsync(viaOwnRoute, 404); // id de B pela rota de A: a consulta é sempre filtrada pelo tenant
        await Requests.AssertProblemAsync(list, 404);
    }

    [Fact]
    public async Task MerchantA_CannotReadMerchantBConsolidation_GatewayAndDirect()
    {
        var tokenA = await fx.Keycloak.MerchantATokenAsync();
        using var metrics = new MetricCapture("CashFlow.Consolidation");
        using var direct = fx.DirectConsolidation(fx.Pki.GatewayClient.Certificate);

        using var own = await Requests.GetAsync(fx.Gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01"), tokenA);
        using var cross = await Requests.GetAsync(fx.Gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantB, "2026-01-01"), tokenA);
        using var crossDirect = await Requests.GetAsync(direct, Requests.DailyUrl(KeycloakFixture.MerchantB, "2026-01-01"), tokenA);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        await Requests.AssertProblemAsync(cross, 404);
        await Requests.AssertProblemAsync(crossDirect, 404);
        Assert.Equal(2, metrics.Total("authz_cross_tenant_denied_total"));
    }

    [Fact]
    public async Task TamperedMerchantClaim_IsRejectedBySignature_NotTreatedAsOtherTenant()
    {
        var tokenA = await fx.Keycloak.MerchantATokenAsync();
        var forged = Jwt.WithTamperedMerchant(tokenA, KeycloakFixture.MerchantB);

        using var response = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantB, forged);

        await Requests.AssertProblemAsync(response, 401);
    }

    [Fact]
    public async Task RateLimit_IsPerMerchant_OneTenantThrottledDoesNotAffectTheOther()
    {
        var tokenA = await fx.Keycloak.MerchantATokenAsync();
        var tokenB = await fx.Keycloak.MerchantBTokenAsync();
        using var metrics = new MetricCapture(Hosting.Security.SecurityMetrics.MeterName);
        var url = Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01");

        // Limite do gateway de teste: 3/s por merchant na rota de consolidado; 12 em paralelo garantem 429 para A.
        var burst = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Requests.GetAsync(fx.Gateway.Edge, url, tokenA)));
        try
        {
            var statuses = burst.Select(r => r.StatusCode).ToList();
            Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
            Assert.Contains(HttpStatusCode.OK, statuses);
            var throttled = burst.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.NotNull(throttled.Headers.RetryAfter);
            Assert.Equal("application/problem+json", throttled.Content.Headers.ContentType?.MediaType);
            Requests.AssertSecurityHeaders(throttled); // 429 nasce no rate limiter, antes do proxy: também sem "Server"
            Assert.True(metrics.Total("rate_limited_total", "route", "consolidation-read") >= 1);

            using var other = await Requests.GetAsync(fx.Gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantB, "2026-01-01"), tokenB);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        }
        finally
        {
            foreach (var response in burst)
            {
                response.Dispose();
            }

            await Task.Delay(TimeSpan.FromSeconds(1.5)); // o balde de A (3/s) volta a encher antes do próximo teste
        }
    }
}
