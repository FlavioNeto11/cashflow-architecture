using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace CashFlow.Persistence.Tests;

/// <summary>
/// Guarda o orçamento de conexões (target.md 7.2a) lendo o que de fato roda: todas as connection strings
/// (ConnectionStrings__*) de infra/compose/docker-compose.yml e os CONNECTION LIMIT de infra/postgres/init/01-roles.sql.
/// Para cada papel: soma dos "Maximum Pool Size" x instâncias &lt;= CONNECTION LIMIT do papel.
/// Erros de grafia (Npgsql rejeita "Max Pool Size") só apareceriam em runtime; aqui o parse do builder os pega.
/// </summary>
public sealed partial class ConnectionBudgetTests
{
    // Instâncias do orçamento (target.md 7.2a): o Compose roda 1 consolidation-api, mas o orçamento
    // precisa caber em 2 réplicas (demonstração de escala horizontal, T-08).
    private static readonly Dictionary<string, int> Instances = new() { ["consolidation-api"] = 2 };

    private sealed record Pool(string Service, string Name, string Role, int MaxPoolSize, int Instances);

    [Fact]
    public void EveryComposeConnectionString_ParsesAndDeclaresExplicitPoolSize()
    {
        var entries = ComposeConnectionStrings();

        Assert.NotEmpty(entries);
        foreach (var (service, name, connectionString) in entries)
        {
            // O default do Npgsql é 100: sem tamanho explícito o orçamento não vale.
            Assert.True(
                ExplicitPoolSize().IsMatch(connectionString),
                $"{service}: ConnectionStrings__{name} sem 'Maximum Pool Size' explícito.");
            _ = new NpgsqlConnectionStringBuilder(connectionString); // lança ArgumentException em chave inválida
        }
    }

    [Fact]
    public void ComposeDeclaresPoolsForEveryRoleThatConnects()
    {
        var roles = Pools().Select(p => p.Role).Distinct().Order().ToArray();

        Assert.Equal(["cons_ro", "cons_worker", "migrator", "recon_ro", "recon_writer", "tx_app"], roles);
    }

    [Fact]
    public void TransactionsApi_DefaultAndPublisherPools_FitInTxAppLimit()
    {
        var pools = Pools().Where(p => p.Role == "tx_app").ToList();
        var demand = pools.Sum(p => p.MaxPoolSize * p.Instances);

        Assert.Contains(pools, p => p is { Name: "Default", MaxPoolSize: 30 });
        Assert.Contains(pools, p => p is { Name: "Publisher", MaxPoolSize: 2 });
        Assert.Equal(32, demand);
        Assert.True(demand <= RoleLimits()["tx_app"]);
    }

    [Theory]
    [InlineData("tx_app")]
    [InlineData("cons_ro")]
    [InlineData("cons_worker")]
    [InlineData("migrator")]
    [InlineData("recon_ro")]
    [InlineData("recon_writer")]
    public void SumOfPoolsTimesInstances_FitsRoleConnectionLimit(string role)
    {
        var demand = Pools().Where(p => p.Role == role).Sum(p => p.MaxPoolSize * p.Instances);

        Assert.InRange(demand, 1, RoleLimits()[role]);
    }

    [Fact]
    public void ConsolidationApi_TwoInstancesOf12_FitIn25()
    {
        var consRo = Pools().Where(p => p.Role == "cons_ro").ToList();

        Assert.Equal(24, consRo.Sum(p => p.MaxPoolSize * p.Instances));
        Assert.Equal(25, RoleLimits()["cons_ro"]);
    }

    [Theory]
    [InlineData("CashFlow.Transactions.Api")]
    [InlineData("CashFlow.Consolidation.Api")]
    public void AppSettings_HasNoConnectionStrings_SoMissingEnvironmentFailsAtStartup(string project)
    {
        var path = Path.Combine(RepositoryPaths.Root, "src", project, "appsettings.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        Assert.False(document.RootElement.TryGetProperty("ConnectionStrings", out _));
    }

    private static List<Pool> Pools() =>
        ComposeConnectionStrings()
            .Select(entry =>
            {
                var builder = new NpgsqlConnectionStringBuilder(entry.ConnectionString);
                return new Pool(entry.Service, entry.Name, builder.Username!, builder.MaxPoolSize, Instances.GetValueOrDefault(entry.Service, 1));
            })
            .ToList();

    private static Dictionary<string, int> RoleLimits() =>
        RoleLimit().Matches(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "infra", "postgres", "init", "01-roles.sql")))
            .ToDictionary(m => m.Groups["role"].Value, m => int.Parse(m.Groups["limit"].Value, CultureInfo.InvariantCulture));

    // Varredura de linhas em vez de um parser YAML: o arquivo tem formato controlado (serviços com 2 espaços,
    // variáveis ConnectionStrings__X entre aspas) e evita uma dependência só para o teste.
    private static List<(string Service, string Name, string ConnectionString)> ComposeConnectionStrings()
    {
        var path = Path.Combine(RepositoryPaths.Root, "infra", "compose", "docker-compose.yml");
        var result = new List<(string, string, string)>();
        var service = string.Empty;
        foreach (var line in File.ReadLines(path))
        {
            if (ServiceHeader().Match(line) is { Success: true } header)
            {
                service = header.Groups["name"].Value;
            }
            else if (ConnectionStringVariable().Match(line) is { Success: true } variable)
            {
                result.Add((service, variable.Groups["name"].Value, variable.Groups["value"].Value));
            }
        }

        return result;
    }

    [GeneratedRegex(@"^  (?<name>[a-z0-9-]+):\s*$")]
    private static partial Regex ServiceHeader();

    [GeneratedRegex(@"^\s+ConnectionStrings__(?<name>\w+):\s*""(?<value>.*)""\s*$")]
    private static partial Regex ConnectionStringVariable();

    [GeneratedRegex(@"CREATE ROLE (?<role>\w+)\s+LOGIN PASSWORD :'\w+'\s+CONNECTION LIMIT (?<limit>\d+)")]
    private static partial Regex RoleLimit();

    [GeneratedRegex(@"(^|;)\s*Maximum Pool Size\s*=\s*\d+", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitPoolSize();
}
