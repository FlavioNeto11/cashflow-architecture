using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CashFlow.Consolidation.Persistence;

/// <summary>
/// Usada só pelo `dotnet ef` (design time). A connection string é um marcador: nenhuma conexão é aberta
/// para gerar migrações, e nenhum segredo é versionado. O histórico vai para o schema "ef", como em
/// CashFlow.Hosting.MigrationCommand (este projeto não referencia o Hosting, então o valor é repetido aqui).
/// </summary>
internal sealed class ConsolidationDbContextFactory : IDesignTimeDbContextFactory<ConsolidationDbContext>
{
    public ConsolidationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ConsolidationDbContext>()
            .UseNpgsql("Host=design-time;Database=consolidation_db", npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "ef"))
            .Options);
}
