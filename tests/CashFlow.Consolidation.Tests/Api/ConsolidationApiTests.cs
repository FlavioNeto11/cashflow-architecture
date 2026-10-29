extern alias consapi;

using System.Net;
using System.Text.Json;
using CashFlow.Consolidation.Tests.Messaging;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Messaging;
using CashFlow.Transactions.Api.Tests.Support;
using ConsolidationMetrics = consapi::CashFlow.Consolidation.Api.Http.ConsolidationMetrics;

namespace CashFlow.Consolidation.Tests.Api;

/// <summary>
/// consolidation-api real (cons_ro) lendo a projeção escrita pelo consolidation-worker real a partir de eventos no
/// broker: oráculos de target.md 8.2, contrato (dinheiro em string), autorização e validação.
/// </summary>
[Collection(BrokerCollection.Name)]
public sealed class ConsolidationApiTests(RabbitMqFixture broker, DatabasesFixture databases)
{
    private const string Read = "consolidation.read";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private static Identity Reader(Guid merchant) => Identity.For(merchant, Read);

    private static async Task<JsonElement> GetDailyAsync(HttpClient client, Guid merchant, string query)
    {
        using var response = await ApiClient.GetAsync(client, ConsolidationApiHost.DailyUrl(merchant, query), Reader(merchant));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string Money(JsonElement body, string property)
    {
        var value = body.GetProperty(property);
        Assert.Equal(JsonValueKind.String, value.ValueKind); // nunca número JSON (precisão)
        return value.GetString()!;
    }

    private async Task ApplyAsync(Guid merchant, int expectedEntries, DateOnly date, params TestEvent[] events)
    {
        foreach (var message in events)
        {
            await Events.PublishAsync(broker, message);
        }

        await Eventually.WaitAsync(
            () => Projection.DailyAsync(databases, merchant, date), r => r?.Entries == expectedEntries, Wait, $"{expectedEntries} lançamentos em {date}");
    }

    [Fact]
    public async Task Daily_Oracle_100_00_Minus_35_20_Is_64_80_WithStringMoneyAndAsOf()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var worker = new WorkerHost(databases, broker).Start();
        await using var api = new ConsolidationApiHost(databases).Start();
        var merchant = Guid.CreateVersion7();

        await ApplyAsync(merchant, 2, Events.Day, TestEvent.Credit(merchant, "100.00", Events.Day), TestEvent.Debit(merchant, "35.20", Events.Day));
        var body = await GetDailyAsync(api.Business, merchant, "date=2026-10-20");

        Assert.Equal(merchant, body.GetProperty("merchantId").GetGuid());
        Assert.Equal("2026-10-20", body.GetProperty("date").GetString());
        Assert.Equal("BRL", body.GetProperty("currency").GetString());
        Assert.Equal("100.00", Money(body, "credits"));
        Assert.Equal("35.20", Money(body, "debits"));
        Assert.Equal("64.80", Money(body, "dailyNet"));
        Assert.Equal(2, body.GetProperty("entriesCount").GetInt32());
        Assert.Equal(JsonValueKind.String, body.GetProperty("asOf").ValueKind);
        Assert.False(body.TryGetProperty("closingBalance", out _)); // só com include=closingBalance
        Assert.Equal(HttpStatusCode.OK, (await api.Health.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Daily_DateWithoutMovement_ReturnsZeros_AndNullAsOf()
    {
        await using var api = new ConsolidationApiHost(databases).Start();
        var merchant = Guid.CreateVersion7();

        var body = await GetDailyAsync(api.Business, merchant, "date=2026-10-21");

        Assert.Equal("0.00", Money(body, "credits"));
        Assert.Equal("0.00", Money(body, "debits"));
        Assert.Equal("0.00", Money(body, "dailyNet"));
        Assert.Equal(0, body.GetProperty("entriesCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("asOf").ValueKind);
    }

    /// <summary>Oráculo de target.md 8.2: 64.80 → 74.80 no dia 2; retroativo de 5.00 no dia 1 → 79.80 no dia 2.</summary>
    [Fact]
    public async Task ClosingBalance_IsCumulativeDailyNet_AndAbsorbsRetroactiveEntries()
    {
        await broker.PurgeAsync(Topology.ConsolidationQueue, Topology.DeadLetterQueue);
        await using var worker = new WorkerHost(databases, broker).Start();
        await using var api = new ConsolidationApiHost(databases).Start();
        var merchant = Guid.CreateVersion7();
        var day1 = Events.Day;
        var day2 = day1.AddDays(1);

        await ApplyAsync(merchant, 2, day1, TestEvent.Credit(merchant, "100.00", day1), TestEvent.Debit(merchant, "35.20", day1));
        await ApplyAsync(merchant, 1, day2, TestEvent.Credit(merchant, "10.00", day2));

        var before = await GetDailyAsync(api.Business, merchant, "date=2026-10-19&include=closingBalance");
        Assert.Equal("0.00", Money(before, "closingBalance"));
        var first = await GetDailyAsync(api.Business, merchant, "date=2026-10-20&include=closingBalance");
        Assert.Equal("64.80", Money(first, "dailyNet"));
        Assert.Equal("64.80", Money(first, "closingBalance"));
        Assert.Equal("ZERO_AT_FIRST_DATE", first.GetProperty("openingBalanceAssumption").GetString());
        var second = await GetDailyAsync(api.Business, merchant, "date=2026-10-21&include=closingBalance");
        Assert.Equal("10.00", Money(second, "dailyNet"));
        Assert.Equal("74.80", Money(second, "closingBalance"));

        await ApplyAsync(merchant, 3, day1, TestEvent.Credit(merchant, "5.00", day1)); // retroativo no dia 1

        Assert.Equal("69.80", Money(await GetDailyAsync(api.Business, merchant, "date=2026-10-20&include=closingBalance"), "closingBalance"));
        var after = await GetDailyAsync(api.Business, merchant, "date=2026-10-21&include=closingBalance");
        Assert.Equal("10.00", Money(after, "dailyNet"));
        Assert.Equal("79.80", Money(after, "closingBalance"));
    }

    [Fact]
    public async Task Authorization_401WithoutIdentity_403WithoutScope_404CrossTenantCounted()
    {
        using var metrics = new MetricCapture(ConsolidationMetrics.MeterName);
        await using var api = new ConsolidationApiHost(databases).Start();
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        using var anonymous = await ApiClient.GetAsync(api.Business, ConsolidationApiHost.DailyUrl(mine, "date=2026-10-20"), null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrongScope = await ApiClient.GetAsync(
            api.Business, ConsolidationApiHost.DailyUrl(mine, "date=2026-10-20"), Identity.For(mine, "transactions.read transactions.write"));
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);

        using var noMerchantClaim = await ApiClient.GetAsync(
            api.Business, ConsolidationApiHost.DailyUrl(mine, "date=2026-10-20"), new Identity("user-test", "client-test", null, Read));
        Assert.Equal(HttpStatusCode.Forbidden, noMerchantClaim.StatusCode);

        using var crossTenant = await ApiClient.GetAsync(api.Business, ConsolidationApiHost.DailyUrl(other, "date=2026-10-20"), Reader(mine));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
        Assert.Equal(1, metrics.Total("authz_cross_tenant_denied_total"));

        // Cross-tenant e data inválida do próprio tenant: o 404 vem antes da validação (não revela nada do outro tenant).
        using var crossTenantInvalid = await ApiClient.GetAsync(api.Business, ConsolidationApiHost.DailyUrl(other, "date=x"), Reader(mine));
        Assert.Equal(HttpStatusCode.NotFound, crossTenantInvalid.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("date=")]
    [InlineData("date=2026-13-01")]
    [InlineData("date=20261020")]
    [InlineData("date=20/10/2026")]
    [InlineData("date=2026-10-20&date=2026-10-21")]
    [InlineData("date=2026-10-20&include=closing")]
    [InlineData("date=2026-10-20&include=closingBalance&include=closingBalance")]
    public async Task InvalidQuery_Returns422ValidationProblem(string query)
    {
        await using var api = new ConsolidationApiHost(databases).Start();
        var merchant = Guid.CreateVersion7();

        using var response = await ApiClient.GetAsync(api.Business, ConsolidationApiHost.DailyUrl(merchant, query), Reader(merchant));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("Development", null, "Authentication:Mode")]
    [InlineData("Production", "DevelopmentHeaders", "Authentication:Mode")]
    [InlineData("Development", "JwtBearer", "Authentication:JwtBearer:MetadataAddress")] // F5: JwtBearer sem IdP configurado não sobe
    public void Startup_IsFailClosed_WithoutAValidAuthenticationMode(string environment, string? mode, string expectedMessagePart)
    {
        using var api = new ConsolidationApiHost(databases) { Environment = environment, AuthenticationMode = mode };

        var failure = Assert.ThrowsAny<Exception>(() => api.Start());

        Assert.Contains(expectedMessagePart, failure.ToString(), StringComparison.Ordinal);
    }
}
