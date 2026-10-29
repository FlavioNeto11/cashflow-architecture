using CashFlow.Consolidation.Persistence;
using CashFlow.Hosting;
using CashFlow.Persistence.Tests;
using CashFlow.Transactions.Api;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CashFlow.Consolidation.Tests.Support;

/// <summary>
/// Um postgres:17 real (scripts de infra/postgres/init) com as migrações REAIS dos dois bancos aplicadas pelo
/// "migrator". Compartilhado pela coleção; cada teste usa merchants novos para não depender de limpeza.
/// </summary>
public sealed class DatabasesFixture : IAsyncLifetime
{
    public const string TransactionsDb = "transactions_db";
    public const string ConsolidationDb = "consolidation_db";

    public PostgresFixture Postgres { get; } = new();

    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();
        Assert.Equal(0, await MigrationCommand.RunAsync<TransactionsDbContext>(Migrator(TransactionsDb), o => new TransactionsDbContext(o)));
        Assert.Equal(0, await MigrationCommand.RunAsync<ConsolidationDbContext>(Migrator(ConsolidationDb), o => new ConsolidationDbContext(o)));
    }

    public Task DisposeAsync() => Postgres.DisposeAsync();

    public string Pooled(string role, string database, int maxPoolSize) =>
        new NpgsqlConnectionStringBuilder(Postgres.ConnectionString(role, database)) { Pooling = true, MaxPoolSize = maxPoolSize }.ConnectionString;

    public async Task<NpgsqlConnection> OpenSuperuserAsync(string database)
    {
        var connection = new NpgsqlConnection(Postgres.SuperuserConnectionString(database));
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>Consulta escalar como superusuário (só para asserções; a aplicação nunca usa este papel).</summary>
    public async Task<T?> ScalarAsync<T>(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenSuperuserAsync(database);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private IConfiguration Migrator(string database) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Migrator"] = Postgres.ConnectionString("migrator", database),
            })
            .Build();
}
