using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace CashFlow.Hosting;

public static class PostgresExtensions
{
    public const string ReadyTag = "ready";

    /// <summary>
    /// Chave do data source do publisher do outbox (F4): pool próprio e pequeno, para que a publicação
    /// não dispute conexões com as requisições (ADR-0002; target.md 7.2a).
    /// </summary>
    public const string PublisherKey = "Publisher";

    /// <summary>
    /// Registra o <see cref="NpgsqlDataSource"/> "Default", compartilhado pelo DbContext e pelo health check,
    /// para que "Maximum Pool Size" valha para todo o uso do processo naquele pool. Há um data source por
    /// finalidade, registrado com chave quando houver mais de um (Default, Publisher; ver
    /// <see cref="AddCashFlowKeyedDataSource"/>); o orçamento de conexões do papel soma todos os pools do
    /// processo (target.md 7.2a). Falha na partida se ConnectionStrings:Default faltar.
    /// <paramref name="isExpectedError"/> marca os erros do Postgres que a aplicação trata (logados em Debug em vez
    /// de Error; ver <see cref="PostgresErrorLogInterceptor"/>). Sem predicado, toda falha de banco é Error.
    /// </summary>
    public static IServiceCollection AddCashFlowPostgres<TContext>(
        this IServiceCollection services, IConfiguration configuration, Func<PostgresException, bool>? isExpectedError = null)
        where TContext : DbContext
    {
        var connectionString = RequiredConnectionString(configuration, "Default");
        var expected = isExpectedError ?? (static _ => false);

        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(provider => new PostgresErrorLogInterceptor(
            expected, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PostgresErrorLogInterceptor>>()));
        services.AddDbContext<TContext>((provider, options) =>
        {
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>());
            PostgresErrorLogInterceptor.Configure(options, provider.GetRequiredService<PostgresErrorLogInterceptor>());
        });

        services.AddHealthChecks().AddNpgSql(
            provider => provider.GetRequiredService<NpgsqlDataSource>(),
            name: "postgres",
            failureStatus: HealthStatus.Unhealthy,
            tags: [ReadyTag],
            timeout: TimeSpan.FromSeconds(2));

        return services;
    }

    /// <summary>
    /// Registra um <see cref="NpgsqlDataSource"/> adicional, com chave, a partir de ConnectionStrings:{name}
    /// (ex.: "Publisher", resolvido com [FromKeyedServices]). Falha na partida se a connection string faltar.
    /// </summary>
    public static IServiceCollection AddCashFlowKeyedDataSource(
        this IServiceCollection services, IConfiguration configuration, string name)
    {
        var connectionString = RequiredConnectionString(configuration, name);

        services.AddKeyedSingleton(name, (_, _) => NpgsqlDataSource.Create(connectionString));
        return services;
    }

    // Sem valor padrão em appsettings.json (sem host nem usuário): variável de ambiente ausente derruba a partida.
    private static string RequiredConnectionString(IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(connectionString)
            ? throw new InvalidOperationException($"ConnectionStrings:{name} não configurada (defina ConnectionStrings__{name}).")
            : connectionString;
    }
}
