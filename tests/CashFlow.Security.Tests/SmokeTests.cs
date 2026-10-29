using System.Net;
using System.Text.Json.Nodes;
using CashFlow.Security.Tests.Support;

namespace CashFlow.Security.Tests;

/// <summary>Caminho feliz ponta a ponta: token real do Keycloak → gateway (JWT + escopo) → mTLS → API (JWT de novo) → 201/200.</summary>
[Collection(SecurityCollection.Name)]
public sealed class SmokeTests(SecurityFixture fx)
{
    [Fact]
    public async Task ClientCredentialsToken_CarriesTheClaimsTheApisRelyOn()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write consolidation.read");
        var payload = Jwt.Payload(token);

        Assert.Equal(fx.Keycloak.Issuer, (string?)payload["iss"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)payload["sub"]), "sub ausente: Caller.TryFrom recusaria o token");
        Assert.Equal(KeycloakFixture.MerchantAClient, (string?)payload["azp"]);
        Assert.Equal(KeycloakFixture.MerchantA.ToString(), (string?)payload["merchant_id"]);
        Assert.Contains(KeycloakFixture.Audience, Audiences(payload["aud"]));
        var scopes = ((string?)payload["scope"] ?? string.Empty).Split(' ');
        Assert.Contains("transactions.write", scopes);
        Assert.Contains("consolidation.read", scopes);
        Assert.DoesNotContain("transactions.read", scopes); // escopo opcional não pedido não vem
        Assert.Equal("RS256", (string?)Jwt.Header(token)["alg"]);
    }

    [Fact]
    public async Task ThroughTheGateway_PostAndGet_Succeed()
    {
        var token = await fx.Keycloak.MerchantATokenAsync();

        using var created = await Requests.PostTransactionAsync(fx.Gateway.Edge, KeycloakFixture.MerchantA, token, Requests.CreditJson("12.34"));
        var body = await Requests.JsonAsync(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("12.34", (string?)body["amount"]);
        Assert.Equal(KeycloakFixture.MerchantA, body["merchantId"]!.GetValue<Guid>());
        Assert.Equal("nosniff", created.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.False(created.Headers.Contains("Server"));

        using var daily = await Requests.GetAsync(fx.Gateway.Edge, Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01"), token);
        Assert.Equal(HttpStatusCode.OK, daily.StatusCode);
    }

    [Fact]
    public async Task DirectlyAtTheApi_WithGatewayCertificateAndToken_Succeeds()
    {
        var token = await fx.Keycloak.MerchantATokenAsync("transactions.write");
        using var client = fx.DirectTransactions(fx.Pki.GatewayClient.Certificate);

        using var created = await Requests.PostTransactionAsync(client, KeycloakFixture.MerchantA, token);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private static IEnumerable<string> Audiences(JsonNode? aud) => aud switch
    {
        JsonArray array => array.Select(a => (string)a!),
        JsonValue value => [value.GetValue<string>()],
        _ => [],
    };
}
