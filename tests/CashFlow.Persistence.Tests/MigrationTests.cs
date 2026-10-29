using CashFlow.Consolidation.Persistence;
using CashFlow.Hosting;
using CashFlow.Transactions.Api;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CashFlow.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresFixture postgres)
{
    private static IConfiguration MigratorConfiguration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Migrator"] = connectionString })
            .Build();

    [Fact]
    public async Task Migrate_TransactionsContext_AppliesInitialAsMigratorRole()
    {
        var connectionString = postgres.ConnectionString("migrator", "transactions_db");

        var exitCode = await MigrationCommand.RunAsync<TransactionsDbContext>(
            MigratorConfiguration(connectionString), options => new TransactionsDbContext(options));

        Assert.Equal(0, exitCode);
        Assert.Contains(await MigrationHistory(connectionString), id => id.EndsWith("_Initial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_ConsolidationContext_AppliesInitialAsMigratorRole()
    {
        var connectionString = postgres.ConnectionString("migrator", "consolidation_db");

        var exitCode = await MigrationCommand.RunAsync<ConsolidationDbContext>(
            MigratorConfiguration(connectionString), options => new ConsolidationDbContext(options));

        Assert.Equal(0, exitCode);
        Assert.Contains(await MigrationHistory(connectionString), id => id.EndsWith("_Initial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_CalledTwice_IsIdempotent()
    {
        var configuration = MigratorConfiguration(postgres.ConnectionString("migrator", "transactions_db"));

        var first = await MigrationCommand.RunAsync<TransactionsDbContext>(configuration, o => new TransactionsDbContext(o));
        var second = await MigrationCommand.RunAsync<TransactionsDbContext>(configuration, o => new TransactionsDbContext(o));

        Assert.Equal(0, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Migrate_WithoutMigratorConnectionString_ExitsWith1()
    {
        var exitCode = await MigrationCommand.RunAsync<TransactionsDbContext>(
            new ConfigurationBuilder().Build(), options => new TransactionsDbContext(options));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Migrate_WithWrongPassword_ExitsWith1()
    {
        var connectionString = postgres.ConnectionString("migrator", "transactions_db", password: "senha_errada");

        var exitCode = await MigrationCommand.RunAsync<TransactionsDbContext>(
            MigratorConfiguration(connectionString), options => new TransactionsDbContext(options));

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ApplicationRole_CannotCreateTables()
    {
        // tx_app não é dono do banco nem do schema public: sem DDL (separação migrator x aplicação).
        await using var connection = new NpgsqlConnection(postgres.ConnectionString("tx_app", "transactions_db"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("CREATE TABLE public.tx_app_nao_deveria_criar (id int)", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task ApplicationRole_CannotTamperWithMigrationHistory()
    {
        var migratorConnection = postgres.ConnectionString("migrator", "transactions_db");
        var exitCode = await MigrationCommand.RunAsync<TransactionsDbContext>(
            MigratorConfiguration(migratorConnection), options => new TransactionsDbContext(options));
        Assert.Equal(0, exitCode);

        await using var connection = new NpgsqlConnection(postgres.ConnectionString("tx_app", "transactions_db"));
        await connection.OpenAsync();

        foreach (var sql in new[]
        {
            "UPDATE ef.\"__EFMigrationsHistory\" SET \"ProductVersion\" = 'adulterado'",
            "SELECT \"MigrationId\" FROM ef.\"__EFMigrationsHistory\"",
            "DELETE FROM ef.\"__EFMigrationsHistory\"",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Theory]
    [InlineData("transactions_db", "tx_app")]
    [InlineData("transactions_db", "recon_ro")]
    [InlineData("consolidation_db", "cons_ro")]
    [InlineData("consolidation_db", "cons_worker")]
    [InlineData("consolidation_db", "recon_writer")]
    public async Task EfSchema_HasNoUsageForApplicationRoles(string database, string role)
    {
        var configuration = MigratorConfiguration(postgres.ConnectionString("migrator", database));
        var exitCode = database == "transactions_db"
            ? await MigrationCommand.RunAsync<TransactionsDbContext>(configuration, o => new TransactionsDbContext(o))
            : await MigrationCommand.RunAsync<ConsolidationDbContext>(configuration, o => new ConsolidationDbContext(o));
        Assert.Equal(0, exitCode);

        await using var connection = new NpgsqlConnection(postgres.SuperuserConnectionString(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT has_schema_privilege(@role, 'ef', 'USAGE') OR has_schema_privilege(@role, 'ef', 'CREATE')", connection);
        command.Parameters.AddWithValue("role", role);

        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    private static async Task<List<string>> MigrationHistory(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT \"MigrationId\" FROM ef.\"__EFMigrationsHistory\"", connection);
        await using var reader = await command.ExecuteReaderAsync();

        var ids = new List<string>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
