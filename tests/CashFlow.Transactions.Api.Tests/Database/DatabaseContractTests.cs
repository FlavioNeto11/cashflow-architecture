using CashFlow.Transactions.Api.Application;
using CashFlow.Transactions.Api.Tests.Support;
using Npgsql;

namespace CashFlow.Transactions.Api.Tests.Database;

/// <summary>
/// Contrato do banco, sem passar pela API: privilégios do tx_app (C-05), constraints (target.md 9.1) e o SQLSTATE
/// que o Postgres devolve a quem espera uma chave de idempotência em processamento.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DatabaseContractTests(ApiFixture fixture)
{
    private const string InsertTransaction = """
        INSERT INTO transactions (id, merchant_id, entry_type, amount, currency, effective_date, occurred_at, reversal_of,
                                  created_by_sub, created_by_client_id, created_at)
        VALUES (@id, @merchant, @type, @amount, @currency, DATE '2026-01-01', now(), @reversalOf, 's', 'c', now())
        """;

    private static async Task<PostgresException> SqlStateOf(NpgsqlConnection connection, string sql, params (string, object?)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params (string, object?)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static (string, object?)[] Tx(Guid id, Guid merchant, short type = 1, decimal amount = 10m, string currency = "BRL", Guid? reversalOf = null) =>
        [("id", id), ("merchant", merchant), ("type", type), ("amount", amount), ("currency", currency), ("reversalOf", reversalOf)];

    [Fact]
    public async Task TxApp_CanInsertAndSelectTransactions_ButNotUpdateOrDelete()
    {
        await using var connection = fixture.OpenTxApp();
        var id = Guid.CreateVersion7();
        var merchant = Guid.CreateVersion7();
        await ExecuteAsync(connection, InsertTransaction, Tx(id, merchant));

        await using (var select = new NpgsqlCommand("SELECT amount FROM transactions WHERE id = @id", connection))
        {
            select.Parameters.AddWithValue("id", id);
            Assert.Equal(10m, (decimal)(await select.ExecuteScalarAsync())!);
        }

        var update = await SqlStateOf(connection, "UPDATE transactions SET amount = 1 WHERE id = @id", ("id", id));
        var delete = await SqlStateOf(connection, "DELETE FROM transactions WHERE id = @id", ("id", id));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, delete.SqlState);
    }

    [Fact]
    public async Task TxApp_CanUpdateOutboxAndIdempotencyTables()
    {
        // O publisher de F4 atualiza só as colunas de estado de entrega (migração OutboxColumnGrants).
        await using var connection = fixture.OpenTxApp();
        var aggregate = Guid.CreateVersion7();
        await ExecuteAsync(connection, InsertOutbox, ("id", Guid.CreateVersion7()), ("aggregate", aggregate), ("type", "TransactionRegistered"));

        await using var update = new NpgsqlCommand(
            "UPDATE outbox_messages SET published_at = now(), attempts = attempts + 1, locked_by = 'x' WHERE aggregate_id = @aggregate", connection);
        update.Parameters.AddWithValue("aggregate", aggregate);

        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task TxApp_OnOutbox_UpdatesOnlyDeliveryColumns_AndCannotDelete()
    {
        // ADR-0002 item 1: o evento gravado é imutável para a aplicação; só o estado de entrega muda.
        await using var connection = fixture.OpenTxApp();
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connection, InsertOutbox, ("id", id), ("aggregate", Guid.CreateVersion7()), ("type", "TransactionRegistered"));

        await using (var delivery = new NpgsqlCommand(
            """
            UPDATE outbox_messages
               SET published_at = now(), locked_until = NULL, locked_by = NULL, attempts = attempts + 1,
                   next_attempt_at = now(), last_error = 'x'
             WHERE id = @id
            """, connection))
        {
            delivery.Parameters.AddWithValue("id", id);
            Assert.Equal(1, await delivery.ExecuteNonQueryAsync());
        }

        var payload = await SqlStateOf(connection, "UPDATE outbox_messages SET payload = '{}'::jsonb WHERE id = @id", ("id", id));
        var headers = await SqlStateOf(connection, "UPDATE outbox_messages SET headers = '{}'::jsonb WHERE id = @id", ("id", id));
        var occurred = await SqlStateOf(connection, "UPDATE outbox_messages SET occurred_at = now() WHERE id = @id", ("id", id));
        var delete = await SqlStateOf(connection, "DELETE FROM outbox_messages WHERE id = @id", ("id", id));

        Assert.All([payload, headers, occurred, delete], e => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, e.SqlState));
    }

    [Fact]
    public async Task Migrator_Constraints_RejectInvalidRows()
    {
        await using var connection = fixture.OpenMigrator();
        var merchant = Guid.CreateVersion7();

        var zero = await SqlStateOf(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant, amount: 0m));
        var negative = await SqlStateOf(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant, amount: -1m));
        var usd = await SqlStateOf(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant, currency: "USD"));
        var badType = await SqlStateOf(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant, type: 3));

        Assert.All([zero, negative, usd, badType], e => Assert.Equal(PostgresErrorCodes.CheckViolation, e.SqlState));
        Assert.Equal("ck_transactions_amount_positive", zero.ConstraintName);
        Assert.Equal("ck_transactions_amount_positive", negative.ConstraintName);
        Assert.Equal("ck_transactions_currency_brl", usd.ConstraintName);
        Assert.Equal("ck_transactions_entry_type", badType.ConstraintName);
    }

    [Fact]
    public async Task Migrator_NumericScale_IsTwoAndLargeValuesAreExact()
    {
        await using var connection = fixture.OpenMigrator();
        var id = Guid.CreateVersion7();
        await ExecuteAsync(connection, InsertTransaction, Tx(id, Guid.CreateVersion7(), amount: 9_999_999_999_999_999.99m));

        await using var select = new NpgsqlCommand("SELECT amount::text FROM transactions WHERE id = @id", connection);
        select.Parameters.AddWithValue("id", id);

        Assert.Equal("9999999999999999.99", (string)(await select.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Migrator_SecondReversalOfTheSameOriginal_ViolatesPartialUniqueIndex()
    {
        await using var connection = fixture.OpenMigrator();
        var merchant = Guid.CreateVersion7();
        var original = Guid.CreateVersion7();
        await ExecuteAsync(connection, InsertTransaction, Tx(original, merchant));
        await ExecuteAsync(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant, type: 2, reversalOf: original));

        var second = await SqlStateOf(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant, type: 2, reversalOf: original));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, second.SqlState);
        Assert.Equal("ux_transactions_reversal_of", second.ConstraintName);
    }

    [Fact]
    public async Task Migrator_ManyRowsWithNullReversalOf_AreAllowedByThePartialIndex()
    {
        await using var connection = fixture.OpenMigrator();
        var merchant = Guid.CreateVersion7();

        await ExecuteAsync(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant));
        await ExecuteAsync(connection, InsertTransaction, Tx(Guid.CreateVersion7(), merchant));
    }

    [Fact]
    public async Task Migrator_ReversalOfUnknownTransaction_ViolatesForeignKey()
    {
        await using var connection = fixture.OpenMigrator();

        var error = await SqlStateOf(connection, InsertTransaction, Tx(Guid.CreateVersion7(), Guid.CreateVersion7(), reversalOf: Guid.CreateVersion7()));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }

    [Fact]
    public async Task Migrator_DuplicateOutboxEventPerAggregate_ViolatesUniqueIndex()
    {
        await using var connection = fixture.OpenMigrator();
        var aggregate = Guid.CreateVersion7();
        await ExecuteAsync(connection, InsertOutbox, ("id", Guid.CreateVersion7()), ("aggregate", aggregate), ("type", "TransactionRegistered"));

        var duplicate = await SqlStateOf(connection, InsertOutbox, ("id", Guid.CreateVersion7()), ("aggregate", aggregate), ("type", "TransactionRegistered"));
        await ExecuteAsync(connection, InsertOutbox, ("id", Guid.CreateVersion7()), ("aggregate", aggregate), ("type", "OtherEvent"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal(CashFlow.Transactions.Api.Persistence.DbNames.OutboxEventUnique, duplicate.ConstraintName);
    }

    [Fact]
    public async Task Migrator_IdempotencyKeys_ArePerMerchant()
    {
        await using var connection = fixture.OpenMigrator();
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        var parameters = (Guid merchant) => new (string, object?)[] { ("merchant", merchant), ("key", "k-1") };

        await ExecuteAsync(connection, InsertIdempotency, parameters(merchantA));
        await ExecuteAsync(connection, InsertIdempotency, parameters(merchantB));
        var duplicate = await SqlStateOf(connection, InsertIdempotency, parameters(merchantA));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal(CashFlow.Transactions.Api.Persistence.DbNames.IdempotencyPk, duplicate.ConstraintName);
    }

    [Fact]
    public async Task WaitingOnAnUncommittedIdempotencyKey_FailsWithLockNotAvailable_AfterLockTimeout()
    {
        // Prova (não presume) o SQLSTATE que o serviço mapeia para 409 + Retry-After.
        var merchant = Guid.CreateVersion7();
        (string, object?)[] parameters = [("merchant", merchant), ("key", "em-processamento")];

        await using var first = fixture.OpenTxApp();
        await using var firstTransaction = await first.BeginTransactionAsync();
        await ExecuteAsync(first, InsertIdempotency, parameters);

        await using var second = fixture.OpenTxApp();
        await using var secondTransaction = await second.BeginTransactionAsync();
        await ExecuteAsync(second, $"SET LOCAL lock_timeout = '{TransactionService.LockTimeout}'");
        var started = DateTime.UtcNow;
        var blocked = await SqlStateOf(second, InsertIdempotency, parameters);
        var waited = DateTime.UtcNow - started;

        Assert.Equal(TransactionService.LockNotAvailable, blocked.SqlState);
        Assert.Equal("55P03", blocked.SqlState);
        Assert.InRange(waited, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10));

        await firstTransaction.RollbackAsync();
    }

    [Fact]
    public async Task WaitingOnAKeyThatThenCommits_FailsWithUniqueViolationOnThePrimaryKey()
    {
        var merchant = Guid.CreateVersion7();
        (string, object?)[] parameters = [("merchant", merchant), ("key", "vai-commitar")];

        await using var first = fixture.OpenTxApp();
        await using var firstTransaction = await first.BeginTransactionAsync();
        await ExecuteAsync(first, InsertIdempotency, parameters);

        await using var second = fixture.OpenTxApp();
        await using var secondTransaction = await second.BeginTransactionAsync();
        var blockedInsert = SqlStateOf(second, InsertIdempotency, parameters);
        await Task.Delay(300);
        await firstTransaction.CommitAsync();
        var error = await blockedInsert;

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(CashFlow.Transactions.Api.Persistence.DbNames.IdempotencyPk, error.ConstraintName);
    }

    private const string InsertOutbox = """
        INSERT INTO outbox_messages (id, merchant_id, aggregate_id, type, version, payload, headers, occurred_at, next_attempt_at)
        VALUES (@id, gen_random_uuid(), @aggregate, @type, 1, '{}'::jsonb, '{}'::jsonb, now(), now())
        """;

    private const string InsertIdempotency = """
        INSERT INTO idempotency_keys (merchant_id, idempotency_key, request_hash, transaction_id, response_status, response_body, created_at, expires_at)
        VALUES (@merchant, @key, repeat('a', 64), gen_random_uuid(), 201, '{}'::jsonb, now(), now() + interval '7 days')
        """;
}
