using CashFlow.Persistence.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CashFlow.Transactions.Api.Tests.Support;

/// <summary>
/// Postgres 17 real (scripts reais de infra/postgres/init) + migrações aplicadas como "migrator" + a API em memória
/// (TestServer) conectada como "tx_app", com pool habilitado e tamanho como o do Compose. O banco é compartilhado
/// por todos os testes da coleção; como tx_app não pode DELETE, cada teste usa um merchantId novo.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();

    public TransactionsApiFactory Factory { get; private set; } = null!;

    public string TxAppConnectionString => _postgres.ConnectionString("tx_app", "transactions_db");

    public string MigratorConnectionString => _postgres.ConnectionString("migrator", "transactions_db");

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();

        var exitCode = await CashFlow.Hosting.MigrationCommand.RunAsync<TransactionsDbContext>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Migrator"] = MigratorConnectionString })
                .Build(),
            options => new TransactionsDbContext(options));
        Assert.Equal(0, exitCode);

        Factory = new TransactionsApiFactory(PooledConnectionString(30), PooledConnectionString(2));
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await _postgres.DisposeAsync();
    }

    public NpgsqlConnection OpenTxApp() => Open(TxAppConnectionString);

    public NpgsqlConnection OpenMigrator() => Open(MigratorConnectionString);

    private static NpgsqlConnection Open(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    private string PooledConnectionString(int maxPoolSize) =>
        new NpgsqlConnectionStringBuilder(TxAppConnectionString) { Pooling = true, MaxPoolSize = maxPoolSize }.ConnectionString;
}

public sealed class TransactionsApiFactory(string defaultConnection, string publisherConnection) : WebApplicationFactory<Program>
{
    public string? EnvironmentName { get; init; }

    public string? AuthenticationMode { get; init; } = "DevelopmentHeaders";

    /// <summary>Provider de log adicional, para testes que verificam a trilha de auditoria.</summary>
    public Microsoft.Extensions.Logging.ILoggerProvider? LogProvider { get; init; }

    /// <summary>Configurações extras (p.ex. Authentication:JwtBearer:*), aplicadas por UseSetting.</summary>
    public Dictionary<string, string?> ExtraSettings { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", defaultConnection);
        builder.UseSetting("ConnectionStrings:Publisher", publisherConnection);

        // TestServer sem TLS (o mTLS é coberto em CashFlow.Security.Tests); fora de Development exige o opt-in (C-F5-09).
        // Antes de ExtraSettings para que um teste possa retirá-lo.
        builder.UseSetting("Hosting:Tls:AllowPlainHttp", "true");
        foreach (var (key, value) in ExtraSettings)
        {
            builder.UseSetting(key, value);
        }

        // Estes testes não têm broker; o publisher do outbox é coberto em CashFlow.Consolidation.Tests.
        builder.UseSetting("Outbox:Publisher:Enabled", "false");
        if (AuthenticationMode is not null)
        {
            builder.UseSetting("Authentication:Mode", AuthenticationMode);
        }

        if (EnvironmentName is not null)
        {
            builder.UseEnvironment(EnvironmentName);
        }

        if (LogProvider is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(LogProvider));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
