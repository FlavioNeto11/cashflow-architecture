using CashFlow.Hosting;
using CashFlow.Transactions.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace CashFlow.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RoleAndHealthTests(PostgresFixture postgres)
{
    private static readonly string[] RoleNames = ["tx_app", "cons_ro", "cons_worker", "recon_ro", "recon_writer", "migrator"];

    [Fact]
    public async Task Roles_HaveConnectionLimitsFromBudget()
    {
        var limits = await QueryRoles("SELECT rolname, rolconnlimit::text FROM pg_roles WHERE rolname = ANY(@names)");

        Assert.Equal("40", limits["tx_app"]);
        Assert.Equal("25", limits["cons_ro"]);
        Assert.Equal("8", limits["cons_worker"]);
        Assert.Equal("3", limits["recon_ro"]);
        Assert.Equal("3", limits["recon_writer"]);
        Assert.Equal("2", limits["migrator"]);
    }

    [Fact]
    public async Task Roles_TimeoutsExistOnlyOnConsolidationAndReconRoles()
    {
        var settings = await QueryRoles(
            "SELECT rolname, COALESCE(array_to_string(rolconfig, ','), '') FROM pg_roles WHERE rolname = ANY(@names)");

        Assert.Contains("statement_timeout=2s", settings["cons_ro"]);
        Assert.Contains("statement_timeout=10s", settings["cons_worker"]);
        Assert.Contains("lock_timeout=5s", settings["cons_worker"]);
        Assert.Contains("statement_timeout=60s", settings["recon_writer"]);
        Assert.Contains("lock_timeout=2s", settings["recon_writer"]);
        Assert.Contains("statement_timeout=60s", settings["recon_ro"]);
        foreach (var role in new[] { "cons_ro", "cons_worker", "recon_ro", "recon_writer" })
        {
            Assert.Contains("idle_in_transaction_session_timeout=30s", settings[role]);
        }

        Assert.Equal(string.Empty, settings["tx_app"]);
        Assert.Equal(string.Empty, settings["migrator"]);
    }

    [Fact]
    public async Task ConnectPrivilege_IsRevokedFromPublicAndGrantedPerRole()
    {
        // tx_app só conecta em transactions_db; cons_ro só em consolidation_db.
        await using (var allowed = new NpgsqlConnection(postgres.ConnectionString("tx_app", "transactions_db")))
        {
            await allowed.OpenAsync();
        }

        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString("tx_app", "consolidation_db"));
            await connection.OpenAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

        var deniedReverse = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString("cons_ro", "transactions_db"));
            await connection.OpenAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deniedReverse.SqlState);
    }

    [Fact]
    public async Task ApplicationRoles_CannotConnectToAdministrativeDatabase()
    {
        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var connection = new NpgsqlConnection(postgres.ConnectionString("tx_app", "postgres"));
            await connection.OpenAsync();
        });

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Theory]
    [InlineData("transactions_db", "tx_app")]
    [InlineData("transactions_db", "recon_ro")]
    [InlineData("consolidation_db", "cons_ro")]
    [InlineData("consolidation_db", "cons_worker")]
    [InlineData("consolidation_db", "recon_writer")]
    public async Task ApplicationRoles_HaveNoTemporaryPrivilege(string database, string role)
    {
        await using var connection = new NpgsqlConnection(postgres.SuperuserConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT has_database_privilege(@role, @database, 'TEMPORARY')", connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("database", database);

        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public void KeyedDataSource_IsRegisteredSeparatelyFromDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=h;Database=d;Username=u;Maximum Pool Size=30",
                ["ConnectionStrings:Publisher"] = "Host=h;Database=d;Username=u;Maximum Pool Size=2",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddCashFlowPostgres<TransactionsDbContext>(configuration);
        services.AddCashFlowKeyedDataSource(configuration, PostgresExtensions.PublisherKey);

        using var provider = services.BuildServiceProvider();
        var defaultSource = provider.GetRequiredService<NpgsqlDataSource>();
        var publisherSource = provider.GetRequiredKeyedService<NpgsqlDataSource>(PostgresExtensions.PublisherKey);

        Assert.NotSame(defaultSource, publisherSource);
        Assert.Equal(30, new NpgsqlConnectionStringBuilder(defaultSource.ConnectionString).MaxPoolSize);
        Assert.Equal(2, new NpgsqlConnectionStringBuilder(publisherSource.ConnectionString).MaxPoolSize);
    }

    [Fact]
    public void MissingConnectionString_FailsEarly()
    {
        var empty = new ConfigurationBuilder().Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCashFlowPostgres<TransactionsDbContext>(empty));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCashFlowKeyedDataSource(empty, PostgresExtensions.PublisherKey));
    }

    [Fact]
    public async Task ReadyHealthCheck_WithReachableDatabase_IsHealthy()
    {
        var report = await RunReadyCheck(postgres.ConnectionString("tx_app", "transactions_db"));

        Assert.Equal(HealthStatus.Healthy, report.Status);
        Assert.Contains("postgres", report.Entries.Keys);
    }

    [Fact]
    public async Task ReadyHealthCheck_WithUnreachableDatabase_IsUnhealthy()
    {
        var unreachable = new NpgsqlConnectionStringBuilder(postgres.ConnectionString("tx_app", "transactions_db"))
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 2,
        }.ConnectionString;

        var report = await RunReadyCheck(unreachable);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }

    private static async Task<HealthReport> RunReadyCheck(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCashFlowPostgres<TransactionsDbContext>(configuration);

        await using var provider = services.BuildServiceProvider();
        var healthChecks = provider.GetRequiredService<HealthCheckService>();
        return await healthChecks.CheckHealthAsync(r => r.Tags.Contains(PostgresExtensions.ReadyTag));
    }

    private async Task<Dictionary<string, string>> QueryRoles(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.SuperuserConnectionString("postgres"));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("names", RoleNames);
        await using var reader = await command.ExecuteReaderAsync();

        var result = new Dictionary<string, string>();
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }
}
