using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CashFlow.Transactions.Api.Domain;
using CashFlow.Transactions.Api.Tests.Support;
using Npgsql;

namespace CashFlow.Transactions.Api.Tests.Api;

/// <summary>Registro, validação, consulta e estorno de ponta a ponta (HTTP em memória + Postgres 17 real).</summary>
[Collection(ApiCollection.Name)]
public sealed class RegistrationTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    private async Task<(HttpResponseMessage Response, JsonNode Body)> PostAsync(Guid merchant, string json, string? key = null)
    {
        var response = await ApiClient.PostTransactionAsync(_client, merchant, json, key ?? ApiClient.NewKey());
        return (response, await ApiClient.JsonAsync(response));
    }

    private Task<HttpResponseMessage> GetAsync(Guid merchant, string path = "") =>
        ApiClient.GetAsync(_client, ApiClient.TransactionsUrl(merchant) + path, Identity.For(merchant));

    private static decimal Signed(JsonNode item)
    {
        var amount = decimal.Parse((string)item["amount"]!, CultureInfo.InvariantCulture);
        return (string?)item["type"] == "CREDIT" ? amount : -amount;
    }

    [Fact]
    public async Task Oracle_Credit100_Debit35_20_ListSumsTo64_80()
    {
        var merchant = Guid.CreateVersion7();

        var (credit, creditBody) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "100.00"));
        var (debit, debitBody) = await PostAsync(merchant, ApiClient.CreateJson("DEBIT", "35.20"));

        Assert.Equal(HttpStatusCode.Created, credit.StatusCode);
        Assert.Equal(HttpStatusCode.Created, debit.StatusCode);
        Assert.Equal("100.00", (string?)creditBody["amount"]);
        Assert.Equal("35.20", (string?)debitBody["amount"]);
        Assert.Equal("BRL", (string?)creditBody["currency"]);
        Assert.Equal(merchant, creditBody["merchantId"]!.GetValue<Guid>());
        Assert.Equal($"/api/v1/merchants/{merchant}/transactions/{creditBody["id"]}", credit.Headers.Location!.OriginalString);

        var list = await ApiClient.JsonAsync(await GetAsync(merchant));
        var items = list["items"]!.AsArray();
        Assert.Equal(2, (int)list["total"]!);
        Assert.Equal(64.80m, items.Sum(i => Signed(i!)));
    }

    [Fact]
    public async Task Created_BodyEqualsWhatGetReturns_AndEffectiveDateIsSaoPaulo()
    {
        var merchant = Guid.CreateVersion7();
        var (response, body) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "10.50"));

        var fetched = await ApiClient.JsonAsync(await GetAsync(merchant, $"/{body["id"]}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        foreach (var field in new[] { "id", "merchantId", "type", "amount", "currency", "effectiveDate", "occurredAt", "createdAt" })
        {
            Assert.Equal(body[field]!.ToJsonString(), fetched[field]!.ToJsonString());
        }

        var expectedDate = BusinessCalendar.DateOf(DateTime.UtcNow);
        var effective = DateOnly.Parse((string)body["effectiveDate"]!, CultureInfo.InvariantCulture);
        Assert.InRange(effective.DayNumber, expectedDate.DayNumber - 1, expectedDate.DayNumber);
    }

    [Fact]
    public async Task RetroactiveEffectiveDate_IsAccepted()
    {
        var merchant = Guid.CreateVersion7();

        var (response, body) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "5.00", "\"effectiveDate\":\"2020-02-29\""));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("2020-02-29", (string?)body["effectiveDate"]);
    }

    [Fact]
    public async Task FutureEffectiveDate_Returns422_AndPersistsNothing()
    {
        var merchant = Guid.CreateVersion7();

        var (response, body) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "5.00", "\"effectiveDate\":\"2999-01-01\""));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(body["errors"]!["effectiveDate"]);
        Assert.Equal(0, await CountAsync("transactions", merchant));
        Assert.Equal(0, await CountAsync("outbox_messages", merchant));
        Assert.Equal(0, await CountAsync("idempotency_keys", merchant));
    }

    [Theory]
    [InlineData("""{"type":"CREDIT","amount":"100.001"}""", "amount")]
    [InlineData("""{"type":"CREDIT","amount":"0"}""", "amount")]
    [InlineData("""{"type":"CREDIT","amount":"-1"}""", "amount")]
    [InlineData("""{"type":"CREDIT","amount":100.00}""", "amount")]
    [InlineData("""{"type":"CREDIT","amount":"1e3"}""", "amount")]
    [InlineData("""{"type":"CREDIT"}""", "amount")]
    [InlineData("""{"type":"CREDIT","amount":"1.00","currency":"USD"}""", "currency")]
    [InlineData("""{"type":"X","amount":"1.00"}""", "type")]
    [InlineData("""{"type":"credit","amount":"1.00"}""", "type")]
    [InlineData("""{"amount":"1.00"}""", "type")]
    [InlineData("""{"type":"CREDIT","amount":"1.00","effectiveDate":"29/10/2026"}""", "effectiveDate")]
    public async Task InvalidBody_Returns422_WithTheFieldName(string json, string field)
    {
        var merchant = Guid.CreateVersion7();

        var (response, body) = await PostAsync(merchant, json);

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(body["errors"]![field]);
        Assert.Equal(0, await CountAsync("transactions", merchant));
    }

    [Theory]
    [InlineData("""{"type":"CREDIT","amount":"1.00","merchantId":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"type":"CREDIT","amount":"1.00","id":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"type":"CREDIT","amount":"1.00","occurredAt":"2020-01-01T00:00:00Z"}""")]
    [InlineData("""{"type":"CREDIT","amount":"1.00","amount":"2.00"}""")]
    [InlineData("""{"type":"CREDIT","amount":""")]
    [InlineData("""not json""")]
    public async Task UnknownDuplicateOrMalformedMembers_Return400(string json)
    {
        var merchant = Guid.CreateVersion7();

        var (response, _) = await PostAsync(merchant, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAsync("transactions", merchant));
    }

    /// <summary>
    /// PF-17 (F7, passo B): byte UTF-8 inválido no corpo dava 500 (a decodificação falhava fora do tratamento de JSON).
    /// É erro de sintaxe do corpo: 400 em problem+json, nada gravado, para registro e estorno.
    /// </summary>
    [Theory]
    [InlineData("description")]
    [InlineData("type")]
    [InlineData("key")]
    public async Task InvalidUtf8InBody_Returns400AndWritesNothing(string where)
    {
        var merchant = Guid.CreateVersion7();
        byte[] invalid = [0xC3, 0x28];
        byte[] body = where switch
        {
            "description" => [.. "{\"type\":\"CREDIT\",\"amount\":\"1.00\",\"description\":\"a"u8, .. invalid, .. "\"}"u8],
            "type" => [.. "{\"type\":\"CR"u8, .. invalid, .. "\",\"amount\":\"1.00\"}"u8],
            _ => [.. "{\"type\":\"CREDIT\",\"amount\":\"1.00\",\""u8, .. invalid, .. "\":\"x\"}"u8],
        };

        var response = await ApiClient.PostRawAsync(_client, ApiClient.TransactionsUrl(merchant), body, ApiClient.NewKey(), Identity.For(merchant));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await CountAsync("transactions", merchant));
        Assert.Equal(0, await CountAsync("idempotency_keys", merchant));
    }

    [Fact]
    public async Task InvalidUtf8InReversalReason_Returns400AndDoesNotReverse()
    {
        var merchant = Guid.CreateVersion7();
        var (created, createdBody) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "10.00"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = createdBody["id"]!.GetValue<Guid>();
        byte[] body = [.. "{\"reason\":\"x"u8, 0xFF, .. "\"}"u8];

        var response = await ApiClient.PostRawAsync(
            _client, $"{ApiClient.TransactionsUrl(merchant)}/{id}/reversal", body, ApiClient.NewKey(), Identity.For(merchant));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, await CountAsync("transactions", merchant));
    }

    [Fact]
    public async Task MissingIdempotencyKey_Returns400()
    {
        var merchant = Guid.CreateVersion7();

        var response = await ApiClient.PostTransactionAsync(_client, merchant, ApiClient.CreateJson("CREDIT", "1.00"), key: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountAsync("transactions", merchant));
    }

    [Fact]
    public async Task OversizedBody_Returns413()
    {
        var merchant = Guid.CreateVersion7();
        var json = ApiClient.CreateJson("CREDIT", "1.00", $"\"description\":\"{new string('x', 9000)}\"");

        var response = await ApiClient.PostTransactionAsync(_client, merchant, json, ApiClient.NewKey());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Description_Over140_Returns422()
    {
        var merchant = Guid.CreateVersion7();

        var (response, body) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "1.00", $"\"description\":\"{new string('x', 141)}\""));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.NotNull(body["errors"]!["description"]);
    }

    [Theory]
    [InlineData("ok\\u0000fim")]
    [InlineData("\\u0000")]
    [InlineData("\\u0007")]
    [InlineData("\\u001f")]
    public async Task Description_WithControlCharacter_Returns422AndWritesNothing(string escapedDescription)
    {
        var merchant = Guid.CreateVersion7();

        var (response, body) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "1.00", $"\"description\":\"{escapedDescription}\""));

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(body["errors"]!["description"]);
        Assert.Equal(0, await CountAsync("transactions", merchant));
        Assert.Equal(0, await CountAsync("outbox_messages", merchant));
    }

    [Fact]
    public async Task Reversal_ReasonWithControlCharacter_Returns422AndCreatesNoReversal()
    {
        var merchant = Guid.CreateVersion7();
        var (_, original) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "1.00"));

        var response = await ApiClient.PostReversalAsync(
            _client, merchant, original["id"]!.GetValue<Guid>(), """{"reason":"ok\u0000fim"}""", ApiClient.NewKey());

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal(1, await CountAsync("transactions", merchant));
    }

    [Fact]
    public async Task Outbox_HasExactlyOneRow_WithTheFixedTransactionRegisteredV1Format()
    {
        var merchant = Guid.CreateVersion7();
        var (_, body) = await PostAsync(merchant, ApiClient.CreateJson("DEBIT", "35.20", "\"effectiveDate\":\"2026-01-15\""));
        var transactionId = body["id"]!.GetValue<Guid>();

        await using var connection = fixture.OpenTxApp();
        await using var command = new NpgsqlCommand(
            "SELECT id, merchant_id, aggregate_id, type, version, payload::text, headers::text, occurred_at, published_at, attempts FROM outbox_messages WHERE merchant_id = @m",
            connection);
        command.Parameters.AddWithValue("m", merchant);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(merchant, reader.GetGuid(1));
        Assert.Equal(transactionId, reader.GetGuid(2));
        Assert.Equal("TransactionRegistered", reader.GetString(3));
        Assert.Equal(1, reader.GetInt32(4));
        Assert.True(reader.IsDBNull(8));
        Assert.Equal(0, reader.GetInt32(9));

        var payload = JsonNode.Parse(reader.GetString(5))!;
        Assert.Equal(reader.GetGuid(0), payload["eventId"]!.GetValue<Guid>());
        Assert.Equal("TransactionRegistered", (string?)payload["type"]);
        Assert.Equal(1, (int)payload["version"]!);
        Assert.Equal(merchant, payload["merchantId"]!.GetValue<Guid>());
        Assert.Equal(transactionId, payload["transactionId"]!.GetValue<Guid>());
        Assert.Equal("DEBIT", (string?)payload["entryType"]);
        Assert.Equal("35.20", payload["amount"]!.GetValue<string>());
        Assert.Equal("BRL", (string?)payload["currency"]);
        Assert.Equal("2026-01-15", (string?)payload["effectiveDate"]);
        Assert.True(payload["reversalOf"] is null);
        Assert.Equal(body["occurredAt"]!.GetValue<DateTime>(), payload["occurredAt"]!.GetValue<DateTime>());

        var headers = JsonNode.Parse(reader.GetString(6))!;
        Assert.Equal(reader.GetGuid(0).ToString(), (string?)headers["message-id"]);
        Assert.Equal(merchant.ToString(), (string?)headers["merchant_id"]);
        Assert.Equal("application/json", (string?)headers["content-type"]);
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Reversal_CreatesOppositeEntry_WithSameAmountAndDate_AndASecondReversalConflicts()
    {
        var merchant = Guid.CreateVersion7();
        var (_, original) = await PostAsync(merchant, ApiClient.CreateJson("DEBIT", "35.20", "\"effectiveDate\":\"2026-03-10\""));
        var id = original["id"]!.GetValue<Guid>();

        var first = await ApiClient.PostReversalAsync(_client, merchant, id, """{"reason":"digitado errado"}""", ApiClient.NewKey());
        var firstBody = await ApiClient.JsonAsync(first);
        var second = await ApiClient.PostReversalAsync(_client, merchant, id, null, ApiClient.NewKey());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("CREDIT", (string?)firstBody["type"]);
        Assert.Equal("35.20", (string?)firstBody["amount"]);
        Assert.Equal("2026-03-10", (string?)firstBody["effectiveDate"]);
        Assert.Equal(id, firstBody["reversalOf"]!.GetValue<Guid>());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        // Distingue do 409 "em processamento" (Busy), que traz Retry-After e outro detail.
        var conflict = await ApiClient.JsonAsync(second);
        Assert.Equal("Conflito.", (string?)conflict["title"]);
        Assert.Contains("já foi estornado", (string?)conflict["detail"]);
        Assert.False(second.Headers.Contains("Retry-After"));

        var detail = await ApiClient.JsonAsync(await GetAsync(merchant, $"/{id}"));
        Assert.Equal(firstBody["id"]!.GetValue<Guid>(), detail["reversedBy"]!.GetValue<Guid>());
        Assert.Equal(2, await CountAsync("transactions", merchant));
        Assert.Equal(2, await CountAsync("outbox_messages", merchant));
    }

    [Fact]
    public async Task Reversal_WritesItsOwnOutboxEvent_WithOppositeTypeAndReversalOf()
    {
        var merchant = Guid.CreateVersion7();
        var (_, original) = await PostAsync(merchant, ApiClient.CreateJson("DEBIT", "35.20", "\"effectiveDate\":\"2026-03-10\""));
        var originalId = original["id"]!.GetValue<Guid>();
        var reversal = await ApiClient.JsonAsync(await ApiClient.PostReversalAsync(_client, merchant, originalId, null, ApiClient.NewKey()));
        var reversalId = reversal["id"]!.GetValue<Guid>();

        await using var connection = fixture.OpenTxApp();
        await using var command = new NpgsqlCommand(
            "SELECT payload::text, aggregate_id, type, version FROM outbox_messages WHERE merchant_id = @m AND aggregate_id = @a", connection);
        command.Parameters.AddWithValue("m", merchant);
        command.Parameters.AddWithValue("a", reversalId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var payload = JsonNode.Parse(reader.GetString(0))!;

        Assert.Equal("TransactionRegistered", reader.GetString(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(reversalId, payload["transactionId"]!.GetValue<Guid>());
        Assert.Equal(originalId, payload["reversalOf"]!.GetValue<Guid>());
        Assert.Equal("CREDIT", (string?)payload["entryType"]);
        Assert.Equal("35.20", payload["amount"]!.GetValue<string>());
        Assert.Equal("2026-03-10", (string?)payload["effectiveDate"]);
        Assert.Equal("BRL", (string?)payload["currency"]);
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task Reversal_Replay_WithSameKey_DoesNotBecomeConflict()
    {
        var merchant = Guid.CreateVersion7();
        var (_, original) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "1.00"));
        var id = original["id"]!.GetValue<Guid>();
        var key = ApiClient.NewKey();

        var first = await ApiClient.PostReversalAsync(_client, merchant, id, null, key);
        var replay = await ApiClient.PostReversalAsync(_client, merchant, id, null, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.True(ApiClient.SameJson(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Reversal_OfAReversal_Returns422()
    {
        var merchant = Guid.CreateVersion7();
        var (_, original) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "1.00"));
        var reversal = await ApiClient.JsonAsync(
            await ApiClient.PostReversalAsync(_client, merchant, original["id"]!.GetValue<Guid>(), null, ApiClient.NewKey()));

        var response = await ApiClient.PostReversalAsync(_client, merchant, reversal["id"]!.GetValue<Guid>(), null, ApiClient.NewKey());

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task Reversal_OfUnknownTransaction_Returns404()
    {
        var merchant = Guid.CreateVersion7();

        var response = await ApiClient.PostReversalAsync(_client, merchant, Guid.CreateVersion7(), null, ApiClient.NewKey());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reversal_ReasonOver140_Returns422()
    {
        var merchant = Guid.CreateVersion7();
        var (_, original) = await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "1.00"));

        var response = await ApiClient.PostReversalAsync(
            _client, merchant, original["id"]!.GetValue<Guid>(), $$"""{"reason":"{{new string('x', 141)}}"}""", ApiClient.NewKey());

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var merchant = Guid.CreateVersion7();

        var response = await GetAsync(merchant, $"/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_FiltersByDate_AndPaginates_AndRejectsPageSizeOver200()
    {
        var merchant = Guid.CreateVersion7();
        foreach (var day in new[] { "2025-05-01", "2025-05-02", "2025-05-03" })
        {
            await PostAsync(merchant, ApiClient.CreateJson("CREDIT", "1.00", $"\"effectiveDate\":\"{day}\""));
        }

        var range = await ApiClient.JsonAsync(await GetAsync(merchant, "?from=2025-05-02&to=2025-05-03"));
        var page1 = await ApiClient.JsonAsync(await GetAsync(merchant, "?pageSize=2&page=1"));
        var page2 = await ApiClient.JsonAsync(await GetAsync(merchant, "?pageSize=2&page=2"));
        var tooBig = await GetAsync(merchant, "?pageSize=201");
        var max = await GetAsync(merchant, "?pageSize=200");
        var badDate = await GetAsync(merchant, "?from=ontem");
        var inverted = await GetAsync(merchant, "?from=2025-05-03&to=2025-05-01");

        Assert.Equal(2, (int)range["total"]!);
        Assert.Equal(3, (int)page1["total"]!);
        Assert.Equal(2, page1["items"]!.AsArray().Count);
        Assert.Single(page2["items"]!.AsArray());
        Assert.Equal((HttpStatusCode)422, tooBig.StatusCode);
        Assert.Equal(HttpStatusCode.OK, max.StatusCode);
        Assert.Equal((HttpStatusCode)422, badDate.StatusCode);
        Assert.Equal((HttpStatusCode)422, inverted.StatusCode);
    }

    private async Task<long> CountAsync(string table, Guid merchant)
    {
        await using var connection = fixture.OpenTxApp();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE merchant_id = @m", connection);
        command.Parameters.AddWithValue("m", merchant);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
