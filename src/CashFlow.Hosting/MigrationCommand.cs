using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CashFlow.Hosting;

/// <summary>
/// Modo "--migrate": aplica as migrações do DbContext com a connection string do papel "migrator"
/// (ConnectionStrings:Migrator, via variável ConnectionStrings__Migrator) e sai com 0 ou 1, sem subir o Kestrel.
/// Usado pelos jobs one-shot migrations-tx e migrations-cons (target.md 6).
/// </summary>
public static class MigrationCommand
{
    public const string Switch = "--migrate";

    /// <summary>
    /// A tabela de histórico do EF fica no schema "ef", fora de "public": os papéis de aplicação recebem
    /// privilégios só em "public" (infra/postgres/init/02-databases.sql) e, sem USAGE em "ef", não leem nem
    /// adulteram o histórico de migrações (security.md 4.3). Os design-time factories usam o mesmo valor.
    /// </summary>
    public const string HistoryTable = "__EFMigrationsHistory";

    public const string HistorySchema = "ef";

    public static bool IsRequested(string[] args) => args.Contains(Switch, StringComparer.Ordinal);

    public static Task<int> RunAsync<TContext>(
        string[] args, Func<DbContextOptions<TContext>, TContext> create, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args).Build();
        return RunAsync(configuration, create, cancellationToken);
    }

    public static async Task<int> RunAsync<TContext>(
        IConfiguration configuration, Func<DbContextOptions<TContext>, TContext> create, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddJsonConsole());
        var logger = loggerFactory.CreateLogger(typeof(MigrationCommand));

        var connectionString = configuration.GetConnectionString("Migrator");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError("ConnectionStrings__Migrator não definida; migração de {Context} abortada.", typeof(TContext).Name);
            return 1;
        }

        try
        {
            var options = new DbContextOptionsBuilder<TContext>()
                .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(HistoryTable, HistorySchema))
                .Options;
            await using var context = create(options);
            await context.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Migrações de {Context} aplicadas.", typeof(TContext).Name);
            return 0;
        }
        catch (Exception ex)
        {
            // Fronteira do processo: o job precisa terminar com código 1 e registrar a causa.
            logger.LogError(ex, "Falha ao migrar {Context}.", typeof(TContext).Name);
            return 1;
        }
    }
}
