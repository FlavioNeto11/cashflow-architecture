using CashFlow.Consolidation.Persistence;
using CashFlow.Hosting;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CashFlow.Persistence.Tests;

/// <summary>
/// Concessões por tabela do consolidation_db (migração ConsolidationSchema; security.md 4.3; ADR-0002 itens 4 e 7).
/// F6 P-DB-1 (migração ReadRoleLeastPrivilege): cons_ro só lê daily_consolidations; cons_worker não toca em
/// reconciliation_runs. A matriz é verificada com has_table_privilege e, para os casos que protegem a integridade, por execução real.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConsolidationGrantsTests(PostgresFixture postgres)
{
    private static readonly string[] Privileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE"];

    public static TheoryData<string, string, string> Expected => new()
    {
        { "cons_worker", ConsolidationSchema.DailyConsolidations, "SELECT,INSERT,UPDATE" },
        { "cons_worker", ConsolidationSchema.ProcessedMessages, "SELECT,INSERT" },
        { "cons_worker", ConsolidationSchema.ReconciliationRuns, "" },
        { "recon_writer", ConsolidationSchema.DailyConsolidations, "SELECT,INSERT,UPDATE" },
        { "recon_writer", ConsolidationSchema.ProcessedMessages, "SELECT,INSERT,DELETE" },
        { "recon_writer", ConsolidationSchema.ReconciliationRuns, "SELECT,INSERT" },
        { "cons_ro", ConsolidationSchema.DailyConsolidations, "SELECT" },
        { "cons_ro", ConsolidationSchema.ProcessedMessages, "" },
        { "cons_ro", ConsolidationSchema.ReconciliationRuns, "" },
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public async Task Role_HasExactlyTheExpectedTablePrivileges(string role, string table, string expected)
    {
        await MigrateAsync();
        await using var connection = new NpgsqlConnection(postgres.SuperuserConnectionString("consolidation_db"));
        await connection.OpenAsync();

        var granted = new List<string>();
        foreach (var privilege in Privileges)
        {
            await using var command = new NpgsqlCommand("SELECT has_table_privilege(@role, @table, @privilege)", connection);
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("table", "public." + table);
            command.Parameters.AddWithValue("privilege", privilege);
            if ((bool)(await command.ExecuteScalarAsync())!)
            {
                granted.Add(privilege);
            }
        }

        Assert.Equal(expected, string.Join(',', granted));
    }

    [Fact]
    public async Task ConsWorker_CannotDeleteFromInbox_ButReconWriterCan()
    {
        await MigrateAsync();
        var transactionId = Guid.CreateVersion7();
        const string insert = """
            INSERT INTO processed_messages (transaction_id, event_id, merchant_id, effective_date, consumer, processed_at)
            VALUES (@id, NULL, gen_random_uuid(), DATE '2026-01-01', 'worker', now())
            """;
        const string delete = "DELETE FROM processed_messages WHERE transaction_id = @id";

        await using (var worker = new NpgsqlConnection(postgres.ConnectionString("cons_worker", "consolidation_db")))
        {
            await worker.OpenAsync();
            await Execute(worker, insert, transactionId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(worker, delete, transactionId));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await using var recon = new NpgsqlConnection(postgres.ConnectionString("recon_writer", "consolidation_db"));
        await recon.OpenAsync();
        Assert.Equal(1, await Execute(recon, delete, transactionId));
    }

    [Fact]
    public async Task ConsRo_CannotWriteTheProjection()
    {
        await MigrateAsync();
        await using var connection = new NpgsqlConnection(postgres.ConnectionString("cons_ro", "consolidation_db"));
        await connection.OpenAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, """
            INSERT INTO daily_consolidations (merchant_id, effective_date, credits, debits, entries_count, last_applied_at, updated_at)
            VALUES (@id, DATE '2026-01-01', 0, 0, 0, NULL, now())
            """, Guid.CreateVersion7()));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    private async Task MigrateAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Migrator"] = postgres.ConnectionString("migrator", "consolidation_db"),
            })
            .Build();
        Assert.Equal(0, await MigrationCommand.RunAsync<ConsolidationDbContext>(configuration, o => new ConsolidationDbContext(o)));
    }

    private static async Task<int> Execute(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync();
    }
}
