using DotNet.Testcontainers.Builders;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CashFlow.Persistence.Tests;

/// <summary>
/// Sobe um postgres:17 real executando os MESMOS scripts de infra/postgres/init usados pelo Compose
/// (papéis, bancos, limites, timeouts). Nada de banco simulado.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string SuperuserPassword = "test_superuser_pw";

    // Senhas de teste, descartáveis; o container some ao fim da execução.
    private static readonly Dictionary<string, string> RolePasswords = new()
    {
        ["MIGRATOR_PASSWORD"] = "test_migrator_pw",
        ["TX_APP_PASSWORD"] = "test_tx_app_pw",
        ["CONS_RO_PASSWORD"] = "test_cons_ro_pw",
        ["CONS_WORKER_PASSWORD"] = "test_cons_worker_pw",
        ["RECON_RO_PASSWORD"] = "test_recon_ro_pw",
        ["RECON_WRITER_PASSWORD"] = "test_recon_writer_pw",
    };

    private readonly PostgreSqlContainer _container;

    public PostgresFixture()
    {
        var builder = new PostgreSqlBuilder("postgres:17")
            .WithPassword(SuperuserPassword)
            .WithResourceMapping(new DirectoryInfo(Path.Combine(RepositoryPaths.Root, "infra", "postgres", "init")), "/docker-entrypoint-initdb.d/")
            // Checa por TCP: o servidor temporário do initdb só escuta em socket Unix, então isto só
            // passa depois que os scripts de init terminaram (mesma técnica do healthcheck do Compose).
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-U", "postgres"))
            // Testcontainers publica em 0.0.0.0 por padrão (banco/broker de teste com senha fixa alcançável pela rede local);
            // restringe todas as publicações ao loopback (GAP-F5-10; mesma técnica do KeycloakFixture).
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var binding in (parameters.HostConfig?.PortBindings?.Values ?? []).SelectMany(b => b))
                {
                    binding.HostIP = "127.0.0.1";
                }
            });

        foreach (var (name, value) in RolePasswords)
        {
            builder = builder.WithEnvironment(name, value);
        }

        _container = builder.Build();
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public string ConnectionString(string role, string database, string? password = null) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(5432),
            Username = role,
            Password = password ?? RolePasswords[role.ToUpperInvariant() + "_PASSWORD"],
            Database = database,
            Pooling = false,
        }.ConnectionString;

    public string SuperuserConnectionString(string database) =>
        ConnectionString("postgres", database, SuperuserPassword);
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
