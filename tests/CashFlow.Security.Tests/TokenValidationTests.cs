using System.Net;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Hosting.Security;
using CashFlow.Security.Tests.Support;

namespace CashFlow.Security.Tests;

/// <summary>
/// T-18 (JWT): a API, falada diretamente com o certificado do gateway (para isolar a validação do token da do gateway),
/// recusa com 401 problem+json e conta authn_failed_total{reason}: sem token, expirado, aud errada, iss errado,
/// alg=none, HS256 (confusão de algoritmo), payload adulterado; e com 403 authz_denied_total{kind=scope} quando o token
/// é válido mas não tem o escopo. Os mesmos negativos de borda são repetidos pelo gateway.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TokenValidationTests(SecurityFixture fx)
{
    private static readonly string[] SignatureOrAlgorithmReasons = ["invalid_algorithm", "invalid_signature", "malformed", "invalid_token", "unknown_signing_key"];

    [Fact]
    public async Task WithoutToken_Api_Returns401_AndCountsMissingToken()
    {
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);

        using var response = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, token: null);

        await Requests.AssertProblemAsync(response, 401);
        Assert.Equal("Bearer realm=\"cashflow\"", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "missing_token"));
    }

    [Fact]
    public async Task ExpiredToken_Returns401_AndCountsExpired()
    {
        var token = await fx.Keycloak.TokenAsync(KeycloakFixture.ShortLivedClient, KeycloakFixture.ProbeSecret, "transactions.write");
        var exp = DateTimeOffset.FromUnixTimeSeconds((long)Jwt.Payload(token)["exp"]!);
        Assert.True(exp - DateTimeOffset.UtcNow < TimeSpan.FromSeconds(5), "o cliente-sonda deveria emitir token de 1 s");
        var wait = exp + TimeSpan.FromSeconds(2) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }

        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);
        using var response = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, token);

        await Requests.AssertProblemAsync(response, 401);
        Assert.Contains("invalid_token", response.Headers.WwwAuthenticate.ToString());
        Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "expired"));
    }

    [Fact]
    public async Task TokenWithoutApiAudience_Returns401_AndCountsInvalidAudience()
    {
        var token = await fx.Keycloak.TokenAsync(KeycloakFixture.NoAudienceClient, KeycloakFixture.ProbeSecret, "transactions.write");
        Assert.DoesNotContain("cashflow-api", Jwt.Payload(token)["aud"]?.ToJsonString() ?? string.Empty);

        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);
        using var response = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, token);

        await Requests.AssertProblemAsync(response, 401);
        Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "invalid_audience"));
    }

    [Fact]
    public async Task TokenFromAnotherIssuer_SameKeys_Returns401_AndCountsInvalidIssuer()
    {
        // Mesmo Keycloak, mesmas chaves, mas iss diferente (hostname dinâmico): só a comparação exata de iss barra.
        var token = await fx.Keycloak.TokenAsync(KeycloakFixture.MerchantAClient, KeycloakFixture.MerchantASecret, "transactions.write", fx.Keycloak.AlternateHost);
        Assert.NotEqual(fx.Keycloak.Issuer, (string?)Jwt.Payload(token)["iss"]);

        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);
        using var response = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, token);

        await Requests.AssertProblemAsync(response, 401);
        Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "invalid_issuer"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("tampered")]
    public async Task ForgedToken_Returns401_WithoutLeakingWhy(string forgery)
    {
        var genuine = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        var forged = forgery switch
        {
            "none" => Jwt.WithAlgNone(genuine),
            "HS256" => Jwt.WithHs256(genuine),
            _ => Jwt.WithTamperedMerchant(genuine, KeycloakFixture.MerchantB),
        };

        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);
        using var response = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantB, forged);

        await Requests.AssertProblemAsync(response, 401);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("IDX", body); // sem detalhes do IdentityModel para o cliente
        Assert.Equal(1, metrics.Total("authn_failed_total"));
        Assert.Equal(0, metrics.Total("authn_failed_total", "reason", "missing_token"));
        Assert.Equal(1, SignatureOrAlgorithmReasons.Sum(reason => metrics.Total("authn_failed_total", "reason", reason)));
    }

    [Fact]
    public async Task ValidTokenWithoutScope_Returns403_AndCountsScope()
    {
        var readOnly = await fx.Keycloak.MerchantATokenAsync("transactions.read");
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);

        using var response = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, readOnly);

        await Requests.AssertProblemAsync(response, 403);
        Assert.Equal(1, metrics.Total("authz_denied_total", "kind", "scope"));
    }

    [Fact]
    public async Task TokenWithNoBusinessScopes_Returns403()
    {
        var bare = await fx.Keycloak.MerchantATokenAsync(string.Empty);
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);

        using var response = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, bare);

        await Requests.AssertProblemAsync(response, 403);
    }

    [Fact]
    public async Task DevelopmentHeaders_AreIgnoredInProduction()
    {
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);

        using var response = await Requests.SendAsync(
            client, HttpMethod.Post, Requests.TransactionsUrl(KeycloakFixture.MerchantA), token: null, Requests.CreditJson(), Guid.NewGuid().ToString(),
            request =>
            {
                request.Headers.Add("X-Dev-Sub", "intruso");
                request.Headers.Add("X-Dev-Client-Id", "intruso");
                request.Headers.Add("X-Dev-Merchant-Id", KeycloakFixture.MerchantA.ToString());
                request.Headers.Add("X-Dev-Scope", "transactions.write");
            });

        await Requests.AssertProblemAsync(response, 401);
    }

    [Fact]
    public async Task Gateway_WithoutToken_Returns401_BeforeForwarding()
    {
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        using var response = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token: null);

        await Requests.AssertProblemAsync(response, 401);
        Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "missing_token")); // só o gateway contou: nada chegou à API
    }

    [Fact]
    public async Task Gateway_WrongScope_Returns403_BeforeForwarding()
    {
        var writeOnly = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        using var response = await Requests.GetAsync(fx.Gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01"), writeOnly);

        await Requests.AssertProblemAsync(response, 403);
        Assert.Equal(1, metrics.Total("authz_denied_total", "kind", "scope"));
    }

    [Fact]
    public async Task Gateway_ForgedToken_Returns401()
    {
        var genuine = await fx.Keycloak.MerchantATokenAsync("transactions.write");

        using var response = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, Jwt.WithAlgNone(genuine));

        await Requests.AssertProblemAsync(response, 401);
    }
}
