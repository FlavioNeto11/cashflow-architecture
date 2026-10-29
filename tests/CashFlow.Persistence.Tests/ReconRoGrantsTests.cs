using CashFlow.Hosting;
using CashFlow.Transactions.Api;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CashFlow.Persistence.Tests;

/// <summary>
/// F6 P-DB-1 (migração ReconRoLeastPrivilege): no transactions_db o recon_ro (fonte somente leitura do recon-tool)
/// lê SOMENTE "transactions"; nada em outbox_messages, idempotency_keys ou qualquer outra tabela de public.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReconRoGrantsTests(PostgresFixture postgres)
{
    private static readonly string[] Privileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE"];

    [Fact]
    public async Task ReconRo_HasSelectOnlyOnTransactions_AndNothingElse()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Migrator"] = postgres.ConnectionString("migrator", "transactions_db"),
            })
            .Build();
        Assert.Equal(0, await MigrationCommand.RunAsync<TransactionsDbContext>(configuration, o => new TransactionsDbContext(o)));

        await using var connection = new NpgsqlConnection(postgres.SuperuserConnectionString("transactions_db"));
        await connection.OpenAsync();

        var tables = new List<string>();
        await using (var list = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'public'", connection))
        await using (var reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.Contains("transactions", tables);
        Assert.Contains("outbox_messages", tables);

        foreach (var table in tables)
        {
            var granted = new List<string>();
            foreach (var privilege in Privileges)
            {
                await using var command = new NpgsqlCommand("SELECT has_table_privilege('recon_ro', @table, @privilege)", connection);
                command.Parameters.AddWithValue("table", "public." + table);
                command.Parameters.AddWithValue("privilege", privilege);
                if ((bool)(await command.ExecuteScalarAsync())!)
                {
                    granted.Add(privilege);
                }
            }

            Assert.Equal(table == "transactions" ? "SELECT" : string.Empty, string.Join(',', granted));
        }
    }
}
