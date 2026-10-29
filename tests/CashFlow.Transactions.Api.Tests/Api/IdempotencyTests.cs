using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CashFlow.Transactions.Api.Http;
using CashFlow.Transactions.Api.Tests.Support;
using Npgsql;

namespace CashFlow.Transactions.Api.Tests.Api;

/// <summary>Idempotência, concorrência e atomicidade (zero perda e zero dupla contabilização).</summary>
[Collection(ApiCollection.Name)]
public sealed class IdempotencyTests(ApiFixture fixture)
{
    private readonly HttpClient _client = fixture.Factory.CreateClient();

    private const string Credit100 = """{"type":"CREDIT","amount":"100.00"}""";

    [Fact]
    public async Task SameKeySamePayload_Three_Times_CreatesOneRow_AndReplaysTheSameResponse()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();

        var first = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);
        var second = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);
        var third = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.False(first.Headers.Contains("Idempotency-Replayed"));
        foreach (var replay in new[] { second, third })
        {
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
            Assert.Equal(first.Headers.Location, replay.Headers.Location);
            // jsonb normaliza ordem/espaços: o replay é igual semanticamente, não byte a byte.
            Assert.True(ApiClient.SameJson(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync()));
        }

        Assert.Equal(1, await CountAsync("transactions", merchant));
        Assert.Equal(1, await CountAsync("outbox_messages", merchant));
        Assert.Equal(1, await CountAsync("idempotency_keys", merchant));
    }

    [Fact]
    public async Task SameKey_WithReorderedKeysAndWhitespace_IsStillTheSameRequest()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();

        var first = await ApiClient.PostTransactionAsync(_client, merchant, """{"type":"CREDIT","amount":"100.00"}""", key);
        var replay = await ApiClient.PostTransactionAsync(_client, merchant, """{ "amount" : "100.00",  "type":"CREDIT" }""", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal("true", replay.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.Equal(1, await CountAsync("transactions", merchant));
    }

    [Fact]
    public async Task SameKey_WithDifferentPayload_Returns422_AndCreatesNothingNew()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();
        await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);

        var conflicting = await ApiClient.PostTransactionAsync(_client, merchant, """{"type":"CREDIT","amount":"100.01"}""", key);

        Assert.Equal((HttpStatusCode)422, conflicting.StatusCode);
        Assert.Equal(1, await CountAsync("transactions", merchant));
        Assert.Equal(1, await CountAsync("outbox_messages", merchant));
    }

    [Fact]
    public async Task SameKey_OnADifferentRoute_IsADifferentRequest()
    {
        // O hash inclui método e rota: reutilizar a chave de um registro num estorno é uso indevido (422).
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();
        var created = await ApiClient.JsonAsync(await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key));

        var reversal = await ApiClient.PostReversalAsync(_client, merchant, created["id"]!.GetValue<Guid>(), null, key);

        Assert.Equal((HttpStatusCode)422, reversal.StatusCode);
        Assert.Equal(1, await CountAsync("transactions", merchant));
    }

    [Fact]
    public async Task SameKey_ForDifferentMerchants_CreatesOneRowEach()
    {
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        var key = ApiClient.NewKey();

        var a = await ApiClient.PostTransactionAsync(_client, merchantA, Credit100, key);
        var b = await ApiClient.PostTransactionAsync(_client, merchantB, Credit100, key);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(1, await CountAsync("transactions", merchantA));
        Assert.Equal(1, await CountAsync("transactions", merchantB));
    }

    [Fact]
    public async Task TwentyParallelPosts_WithTheSameKey_CreateExactlyOneRow()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => ApiClient.PostTransactionAsync(_client, merchant, Credit100, key))));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.All(statuses, s => Assert.True(s is HttpStatusCode.Created or HttpStatusCode.Conflict, $"status inesperado: {s}"));
        var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        Assert.Equal(1, created.Count(r => !r.Headers.Contains("Idempotency-Replayed")));
        Assert.Single(created.Select(r => r.Headers.Location!.OriginalString).Distinct());
        Assert.All(responses.Where(r => r.StatusCode == HttpStatusCode.Conflict), r => Assert.True(r.Headers.Contains("Retry-After")));

        var bodies = await Task.WhenAll(created.Select(r => r.Content.ReadAsStringAsync()));
        Assert.All(bodies, b => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(bodies[0]), JsonNode.Parse(b))));

        Assert.Equal(1, await CountAsync("transactions", merchant));
        Assert.Equal(1, await CountAsync("outbox_messages", merchant));
        Assert.Equal(1, await CountAsync("idempotency_keys", merchant));
    }

    [Fact]
    public async Task TwentyParallelReversals_WithDifferentKeys_CreateExactlyOneReversal()
    {
        var merchant = Guid.CreateVersion7();
        var original = await ApiClient.JsonAsync(await ApiClient.PostTransactionAsync(_client, merchant, Credit100, ApiClient.NewKey()));
        var id = original["id"]!.GetValue<Guid>();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => ApiClient.PostReversalAsync(_client, merchant, id, null, ApiClient.NewKey()))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, await CountAsync("transactions", merchant));
        Assert.Equal(2, await CountAsync("outbox_messages", merchant));
        // Os 19 que perderam desfizeram tudo, inclusive a chave de idempotência.
        Assert.Equal(2, await CountAsync("idempotency_keys", merchant));
    }

    [Fact]
    public async Task KeyBeingProcessedByAnotherTransaction_Returns409WithRetryAfter_AndDoesNotWriteAnything()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();

        // Simula uma requisição "em voo": a chave está inserida, mas a transação ainda não confirmou.
        await using var holder = fixture.OpenTxApp();
        await using var holding = await holder.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO idempotency_keys (merchant_id, idempotency_key, request_hash, transaction_id, response_status, response_body, created_at, expires_at)
            VALUES (@m, @k, repeat('a', 64), gen_random_uuid(), 201, '{}'::jsonb, now(), now() + interval '7 days')
            """, holder, holding))
        {
            insert.Parameters.AddWithValue("m", merchant);
            insert.Parameters.AddWithValue("k", key);
            await insert.ExecuteNonQueryAsync();
        }

        var started = DateTime.UtcNow;
        var response = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);
        var waited = DateTime.UtcNow - started;
        await holding.RollbackAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("1", response.Headers.GetValues("Retry-After").Single());
        Assert.InRange(waited, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));
        Assert.Equal(0, await CountAsync("transactions", merchant));

        // Com a chave liberada, o retry funciona: nada ficou "envenenado".
        var retry = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task SecondRequestBlockedOnTheKey_WhoseOwnerCommits_ReturnsTheStoredReplay()
    {
        // Caminho 23505 em pk_idempotency_keys: o segundo pedido espera a PK, o dono confirma, e o segundo
        // devolve a resposta armazenada (sem criar nada). Determinístico: o COMMIT só ocorre depois de
        // observarmos o backend do segundo pedido esperando por um Lock.
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();
        var storedId = Guid.CreateVersion7();
        var stored = StoredTransactionJson(merchant, storedId);

        await using var owner = fixture.OpenTxApp();
        await using var ownerTransaction = await owner.BeginTransactionAsync();
        await InsertIdempotency(owner, ownerTransaction, merchant, key, HashOfRegister(merchant, Credit100), storedId, stored);

        var request = ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);
        await WaitUntilABackendWaitsOnALockAsync();
        await ownerTransaction.CommitAsync();
        var response = await request;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.EndsWith($"/transactions/{storedId}", response.Headers.Location!.OriginalString);
        Assert.True(ApiClient.SameJson(stored, await response.Content.ReadAsStringAsync()));
        Assert.Equal(0, await CountAsync("transactions", merchant));
        Assert.Equal(0, await CountAsync("outbox_messages", merchant));
        Assert.Equal(1, await CountAsync("idempotency_keys", merchant));
    }

    [Fact]
    public async Task SecondRequestBlockedOnTheKey_WhoseOwnerCommitsADifferentPayload_Returns422()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();
        var storedId = Guid.CreateVersion7();

        await using var owner = fixture.OpenTxApp();
        await using var ownerTransaction = await owner.BeginTransactionAsync();
        await InsertIdempotency(owner, ownerTransaction, merchant, key, new string('a', 64), storedId, StoredTransactionJson(merchant, storedId));

        var request = ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);
        await WaitUntilABackendWaitsOnALockAsync();
        await ownerTransaction.CommitAsync();
        var response = await request;

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(0, await CountAsync("transactions", merchant));
        Assert.Equal(0, await CountAsync("outbox_messages", merchant));
    }

    [Theory]
    [InlineData("""{"type":"CREDIT","amount":"100.00","effectiveDate":"2999-01-01"}""")]
    [InlineData("""{"type":"CREDIT","amount":"100.001"}""")]
    [InlineData("""{"type":"CREDIT","amount":"100.00","campoNovoQueNaoExistiaAntes":1}""")]
    public async Task Retry_OfAnAlreadyStoredRequest_IsReplayedEvenIfTheBodyWouldNowBeRejected(string body)
    {
        // Simula "regra de validação mais estrita publicada depois": a linha de idempotência já existe para este
        // corpo, e o retry legítimo deve receber o replay (201), não 400/422.
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();
        var storedId = Guid.CreateVersion7();
        var stored = StoredTransactionJson(merchant, storedId);
        await using (var connection = fixture.OpenTxApp())
        {
            await InsertIdempotency(connection, null, merchant, key, HashOfRegister(merchant, body), storedId, stored);
        }

        var firstWithNewKey = await ApiClient.PostTransactionAsync(_client, merchant, body, ApiClient.NewKey());
        var retry = await ApiClient.PostTransactionAsync(_client, merchant, body, key);

        Assert.NotEqual(HttpStatusCode.Created, firstWithNewKey.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
        Assert.True(ApiClient.SameJson(stored, await retry.Content.ReadAsStringAsync()));
        Assert.Equal(0, await CountAsync("transactions", merchant));
    }

    [Fact]
    public async Task Retry_OfAStoredReversalWithAReasonThatWouldNowBeRejected_IsReplayed()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();
        var originalId = Guid.CreateVersion7();
        var storedId = Guid.CreateVersion7();
        var body = $$"""{"reason":"{{new string('x', 500)}}"}""";
        var route = $"/api/v1/merchants/{merchant:D}/transactions/{originalId:D}/reversal";
        await using (var connection = fixture.OpenTxApp())
        {
            await InsertIdempotency(
                connection, null, merchant, key, RequestHasher.Compute("POST", route, Encoding.UTF8.GetBytes(body)), storedId, StoredTransactionJson(merchant, storedId));
        }

        var retry = await ApiClient.PostReversalAsync(_client, merchant, originalId, body, key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotency-Replayed").Single());
    }

    private static string HashOfRegister(Guid merchant, string body) =>
        RequestHasher.Compute("POST", $"/api/v1/merchants/{merchant:D}/transactions", Encoding.UTF8.GetBytes(body));

    private static string StoredTransactionJson(Guid merchant, Guid id) => new JsonObject
    {
        ["id"] = id,
        ["merchantId"] = merchant,
        ["type"] = "CREDIT",
        ["amount"] = "100.00",
        ["currency"] = "BRL",
        ["effectiveDate"] = "2026-10-28",
        ["occurredAt"] = "2026-10-29T02:30:00Z",
        ["reversalOf"] = null,
        ["createdAt"] = "2026-10-29T02:30:00Z",
    }.ToJsonString();

    private static async Task InsertIdempotency(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid merchant, string key, string requestHash, Guid transactionId, string responseBody)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO idempotency_keys (merchant_id, idempotency_key, request_hash, transaction_id, response_status, response_body, created_at, expires_at)
            VALUES (@m, @k, @h, @t, 201, @b::jsonb, now(), now() + interval '7 days')
            """, connection, transaction);
        command.Parameters.AddWithValue("m", merchant);
        command.Parameters.AddWithValue("k", key);
        command.Parameters.AddWithValue("h", requestHash);
        command.Parameters.AddWithValue("t", transactionId);
        command.Parameters.AddWithValue("b", responseBody);
        await command.ExecuteNonQueryAsync();
    }

    // Espera (até 1,5 s, abaixo do lock_timeout de 2 s) um backend do tx_app bloqueado em lock. O candidato é o
    // POST em voo; a própria conexão de consulta é excluída por pid.
    private async Task WaitUntilABackendWaitsOnALockAsync()
    {
        await using var probe = fixture.OpenTxApp();
        var deadline = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE usename = 'tx_app' AND datname = 'transactions_db' AND wait_event_type = 'Lock' AND pid <> pg_backend_pid()
                """, probe);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Nenhum backend do tx_app ficou esperando por lock em 1,5 s: o POST não bloqueou na chave de idempotência.");
    }

    [Fact]
    public async Task FailureWhileWritingTheOutbox_RollsBackEverything_AndTheKeyCanBeRetried()
    {
        var merchant = Guid.CreateVersion7();
        var key = ApiClient.NewKey();
        var suffix = merchant.ToString("N");
        await using (var migrator = fixture.OpenMigrator())
        {
            await Exec(migrator, $"""
                CREATE FUNCTION fail_outbox_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'falha injetada no outbox'; END $$;
                CREATE TRIGGER trg_fail_outbox_{suffix} BEFORE INSERT ON outbox_messages
                    FOR EACH ROW WHEN (NEW.merchant_id = '{merchant}') EXECUTE FUNCTION fail_outbox_{suffix}();
                """);
        }

        try
        {
            var failed = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);

            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.Equal(0, await CountAsync("transactions", merchant));
            Assert.Equal(0, await CountAsync("idempotency_keys", merchant));
            Assert.Equal(0, await CountAsync("outbox_messages", merchant));
        }
        finally
        {
            await using var migrator = fixture.OpenMigrator();
            await Exec(migrator, $"DROP TRIGGER trg_fail_outbox_{suffix} ON outbox_messages; DROP FUNCTION fail_outbox_{suffix}();");
        }

        var retry = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(retry.Headers.Contains("Idempotency-Replayed"));
        Assert.Equal(1, await CountAsync("transactions", merchant));
        Assert.Equal(1, await CountAsync("outbox_messages", merchant));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankIdempotencyKey_Returns400(string key)
    {
        var merchant = Guid.CreateVersion7();

        var response = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task IdempotencyKey_Over255Chars_Returns400_AndExactly255IsAccepted()
    {
        var merchant = Guid.CreateVersion7();

        var tooLong = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, new string('k', 256));
        var limit = await ApiClient.PostTransactionAsync(_client, merchant, Credit100, new string('k', 255));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.Created, limit.StatusCode);
    }

    private static async Task Exec(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string table, Guid merchant)
    {
        await using var connection = fixture.OpenTxApp();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE merchant_id = @m", connection);
        command.Parameters.AddWithValue("m", merchant);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
