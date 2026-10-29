using CashFlow.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CashFlow.Transactions.Api;

/// <summary>
/// Usada só pelo `dotnet ef` (design time). A connection string é um marcador: nenhuma conexão é aberta
/// para gerar migrações, e nenhum segredo é versionado.
/// </summary>
internal sealed class TransactionsDbContextFactory : IDesignTimeDbContextFactory<TransactionsDbContext>
{
    public TransactionsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TransactionsDbContext>()
            .UseNpgsql("Host=design-time;Database=transactions_db", npgsql => npgsql.MigrationsHistoryTable(MigrationCommand.HistoryTable, MigrationCommand.HistorySchema))
            .Options);
}
