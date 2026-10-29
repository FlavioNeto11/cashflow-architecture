using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json.Nodes;
using CashFlow.Transactions.Api.Security;
using CashFlow.Transactions.Api.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CashFlow.Transactions.Api.Tests.Api;

/// <summary>
/// Autenticação (401), escopo (403) e isolamento de tenant (404 idêntico ao de inexistente) com a identidade
/// de desenvolvimento por cabeçalhos. A mesma autorização valerá para JWT em F5: só o esquema muda.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AuthorizationTests(ApiFixture fixture)
{
    private const string Credit = """{"type":"CREDIT","amount":"10.00"}""";

    private readonly HttpClient _client = fixture.Factory.CreateClient();

    [Fact]
    public async Task WithoutPrincipal_AllEndpoints_Return401()
    {
        var merchant = Guid.CreateVersion7();
        var url = ApiClient.TransactionsUrl(merchant);

        var post = await ApiClient.SendAsync(_client, HttpMethod.Post, url, Credit, ApiClient.NewKey(), identity: null);
        var reversal = await ApiClient.SendAsync(_client, HttpMethod.Post, $"{url}/{Guid.NewGuid()}/reversal", null, ApiClient.NewKey(), identity: null);
        var list = await ApiClient.GetAsync(_client, url, identity: null);
        var get = await ApiClient.GetAsync(_client, $"{url}/{Guid.NewGuid()}", identity: null);

        Assert.All([post, reversal, list, get], r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(0, await CountAsync("transactions", merchant));
    }

    [Theory]
    [InlineData("transactions.read", "POST")]
    [InlineData("other.scope", "POST")]
    [InlineData("", "POST")]
    [InlineData("transactions.write", "GET")]
    [InlineData("transactions.writeX", "POST")]
    public async Task WithoutTheRequiredScope_Returns403(string scope, string method)
    {
        var merchant = Guid.CreateVersion7();
        var identity = Identity.For(merchant, scope);

        var response = method == "POST"
            ? await ApiClient.PostTransactionAsync(_client, merchant, Credit, ApiClient.NewKey(), identity)
            : await ApiClient.GetAsync(_client, ApiClient.TransactionsUrl(merchant), identity);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAsync("transactions", merchant));
    }

    [Theory]
    [InlineData(null, "client", true)]
    [InlineData("sub", null, true)]
    [InlineData("sub", "client", false)]
    public async Task IncompleteClaims_Return403(string? sub, string? clientId, bool hasMerchant)
    {
        var merchant = Guid.CreateVersion7();
        var identity = new Identity(sub, clientId, hasMerchant ? merchant : null, Identity.ReadWrite);

        var response = await ApiClient.PostTransactionAsync(_client, merchant, Credit, ApiClient.NewKey(), identity);

        // Sem sub o esquema nem autentica (401); sem azp/client_id ou merchant_id autentica, mas não autoriza (403).
        Assert.Equal(sub is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await CountAsync("transactions", merchant));
    }

    [Fact]
    public async Task ReadOnlyScope_CanRead_WriteOnlyScope_CanWrite()
    {
        var merchant = Guid.CreateVersion7();
        var write = Identity.For(merchant, "transactions.write");
        var read = Identity.For(merchant, "transactions.read");

        var created = await ApiClient.PostTransactionAsync(_client, merchant, Credit, ApiClient.NewKey(), write);
        var listed = await ApiClient.GetAsync(_client, ApiClient.TransactionsUrl(merchant), read);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
    }

    [Fact]
    public async Task RecordedAuthor_ComesFromTheTokenClaims_NotFromTheBody()
    {
        var merchant = Guid.CreateVersion7();
        var identity = new Identity("sub-do-token", "client-do-token", merchant, Identity.ReadWrite);

        await ApiClient.PostTransactionAsync(_client, merchant, Credit, ApiClient.NewKey(), identity);

        await using var connection = fixture.OpenTxApp();
        await using var command = new NpgsqlCommand(
            "SELECT created_by_sub, created_by_client_id FROM transactions WHERE merchant_id = @m", connection);
        command.Parameters.AddWithValue("m", merchant);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("sub-do-token", reader.GetString(0));
        Assert.Equal("client-do-token", reader.GetString(1));
    }

    [Fact]
    public async Task CrossTenant_Write_Returns404_WritesNothingForEitherMerchant_AndCountsTheDenial()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        using var metrics = new CounterCapture(TransactionsMetrics.MeterName, "authz_cross_tenant_denied_total");

        var response = await ApiClient.PostTransactionAsync(_client, other, Credit, ApiClient.NewKey(), Identity.For(mine));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, metrics.Total);
        Assert.Equal(0, await CountAsync("transactions", mine));
        Assert.Equal(0, await CountAsync("transactions", other));
        Assert.Equal(0, await CountAsync("idempotency_keys", other));
        Assert.Equal(0, await CountAsync("outbox_messages", other));
    }

    [Fact]
    public async Task CrossTenant_Denial_IsIndistinguishableFromANonexistentResource()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var theirs = await ApiClient.JsonAsync(await ApiClient.PostTransactionAsync(_client, other, Credit, ApiClient.NewKey()));
        var theirId = theirs["id"]!.GetValue<Guid>();
        var me = Identity.For(mine);

        var crossTenant = await ApiClient.GetAsync(_client, $"{ApiClient.TransactionsUrl(other)}/{theirId}", me);
        var sameTenantUnknown = await ApiClient.GetAsync(_client, $"{ApiClient.TransactionsUrl(mine)}/{theirId}", me);
        var unknownEverywhere = await ApiClient.GetAsync(_client, $"{ApiClient.TransactionsUrl(mine)}/{Guid.CreateVersion7()}", me);

        Assert.All([crossTenant, sameTenantUnknown, unknownEverywhere], r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        var bodies = await Task.WhenAll(new[] { crossTenant, sameTenantUnknown, unknownEverywhere }.Select(ApiClient.JsonAsync));
        Assert.All(bodies, b =>
        {
            Assert.Equal((string?)bodies[0]["type"], (string?)b["type"]);
            Assert.Equal((string?)bodies[0]["title"], (string?)b["title"]);
            Assert.Equal((string?)bodies[0]["detail"], (string?)b["detail"]);
        });
    }

    [Fact]
    public async Task CrossTenant_List_Returns404_AndReversalOfAnotherMerchantsTransaction_Returns404AndChangesNothing()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var theirs = await ApiClient.JsonAsync(await ApiClient.PostTransactionAsync(_client, other, Credit, ApiClient.NewKey()));
        var theirId = theirs["id"]!.GetValue<Guid>();
        var me = Identity.For(mine);

        var list = await ApiClient.GetAsync(_client, ApiClient.TransactionsUrl(other), me);
        var reverseViaTheirRoute = await ApiClient.PostReversalAsync(_client, other, theirId, null, ApiClient.NewKey(), me);
        var reverseViaMyRoute = await ApiClient.PostReversalAsync(_client, mine, theirId, null, ApiClient.NewKey(), me);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reverseViaTheirRoute.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, reverseViaMyRoute.StatusCode);
        Assert.Equal(1, await CountAsync("transactions", other));
        Assert.Equal(0, await CountAsync("transactions", mine));
    }

    [Fact]
    public async Task CrossTenant_List_NeverLeaksRowsOfAnotherMerchant()
    {
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        await ApiClient.PostTransactionAsync(_client, a, Credit, ApiClient.NewKey());
        await ApiClient.PostTransactionAsync(_client, b, Credit, ApiClient.NewKey());
        await ApiClient.PostTransactionAsync(_client, b, Credit, ApiClient.NewKey());

        var listA = await ApiClient.JsonAsync(await ApiClient.GetAsync(_client, ApiClient.TransactionsUrl(a), Identity.For(a)));

        Assert.Equal(1, (int)listA["total"]!);
        Assert.All(listA["items"]!.AsArray(), i => Assert.Equal(a, i!["merchantId"]!.GetValue<Guid>()));
    }

    [Fact]
    public async Task CrossTenant_Denial_IsAuditedInTheLog_WithoutLeakingTheBodyOrTheKey()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new TransactionsApiFactory(fixture.TxAppConnectionString, fixture.TxAppConnectionString)
        {
            LogProvider = logs,
        };
        var client = factory.CreateClient();
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        var response = await ApiClient.PostTransactionAsync(client, other, Credit, "chave-sensivel-123", Identity.For(mine));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var audit = Assert.Single(logs.Messages, m => m.Level == LogLevel.Warning && m.Text.Contains("cross-tenant", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(other.ToString(), audit.Text);
        Assert.Contains(mine.ToString(), audit.Text);
        Assert.DoesNotContain("chave-sensivel-123", audit.Text);
        Assert.DoesNotContain("10.00", audit.Text);
    }

    private async Task<long> CountAsync(string table, Guid merchant)
    {
        await using var connection = fixture.OpenTxApp();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE merchant_id = @m", connection);
        command.Parameters.AddWithValue("m", merchant);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

/// <summary>Soma as medições de um contador de um Meter (o contador do processo é estático).</summary>
internal sealed class CounterCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _total;

    public CounterCapture(string meterName, string instrumentName)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref _total, measurement));
        _listener.Start();
    }

    public long Total => Interlocked.Read(ref _total);

    public void Dispose() => _listener.Dispose();
}

internal sealed record CapturedLog(LogLevel Level, string Category, string Text);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<CapturedLog> _messages = new();

    public IReadOnlyCollection<CapturedLog> Messages => [.. _messages];

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, _messages);

    public void Dispose()
    {
    }

    private sealed class Capture(string category, System.Collections.Concurrent.ConcurrentQueue<CapturedLog> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue(new CapturedLog(logLevel, category, formatter(state, exception)));
    }
}
