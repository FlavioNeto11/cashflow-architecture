using CashFlow.Persistence.Tests;
using CashFlow.Transactions.Api.Tests.Support;

namespace CashFlow.Transactions.Api.Tests.Api;

/// <summary>
/// Fail-closed na partida: a identidade por cabeçalhos (sem validação) nunca sobe fora de Development e o modo
/// precisa ser explícito. Não precisa de banco: a configuração é rejeitada antes de qualquer conexão.
/// </summary>
public sealed class AuthenticationModeTests
{
    private const string AnyConnection = "Host=nao-conecta;Database=x;Username=x;Password=x";

    private static IEnumerable<string> Messages(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }

    private static Exception StartupFailure(string environment, string? mode)
    {
        using var factory = new TransactionsApiFactory(AnyConnection, AnyConnection) { EnvironmentName = environment, AuthenticationMode = mode };
        return Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void DevelopmentHeaders_OutsideDevelopment_FailsTheHostStartup(string environment)
    {
        var failure = StartupFailure(environment, "DevelopmentHeaders");

        Assert.Contains(Messages(failure), m => m.Contains("só é permitido no ambiente Development", StringComparison.Ordinal));
    }

    /// <summary>
    /// C-F5-09 / GAP-F5-03: fora de Development, listener de negócio sem certificado só sobe com
    /// Hosting:Tls:AllowPlainHttp=true; sem o opt-in a partida falha antes de abrir portas.
    /// </summary>
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void BusinessListenerWithoutTls_OutsideDevelopment_WithoutOptIn_FailsTheHostStartup(string environment)
    {
        using var factory = new TransactionsApiFactory(AnyConnection, AnyConnection)
        {
            EnvironmentName = environment,
            AuthenticationMode = "JwtBearer",
            ExtraSettings = { ["Hosting:Tls:AllowPlainHttp"] = "false" },
        };

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Messages(failure), m => m.Contains("Hosting:Tls:ServerCertificate ausente", StringComparison.Ordinal));
    }

    /// <summary>JwtBearer sem a seção Authentication:JwtBearer (MetadataAddress/ValidIssuer) não sobe: fail-closed (G1-14).</summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void JwtBearer_WithoutMetadataAddressAndIssuer_FailsTheHostStartup(string environment)
    {
        var failure = StartupFailure(environment, "JwtBearer");

        Assert.Contains(Messages(failure), m => m.Contains("Authentication:JwtBearer:MetadataAddress", StringComparison.Ordinal));
    }

    /// <summary>Metadados OIDC por http exigem AllowHttpMetadata=true explícito (GAP declarado em security.md 11).</summary>
    [Fact]
    public void JwtBearer_HttpMetadataWithoutExplicitAllow_FailsTheHostStartup()
    {
        using var factory = new TransactionsApiFactory(AnyConnection, AnyConnection)
        {
            EnvironmentName = "Production",
            AuthenticationMode = "JwtBearer",
            ExtraSettings =
            {
                ["Authentication:JwtBearer:MetadataAddress"] = "http://idp.local/realms/cashflow/.well-known/openid-configuration",
                ["Authentication:JwtBearer:ValidIssuer"] = "http://idp.local/realms/cashflow",
            },
        };

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(Messages(failure), m => m.Contains("AllowHttpMetadata", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Production", null)]
    [InlineData("Development", "")]
    [InlineData("Development", "developmentheaders")]
    [InlineData("Development", "Basic")]
    public void MissingOrUnknownMode_FailsTheHostStartup(string environment, string? mode)
    {
        var failure = StartupFailure(environment, mode);

        Assert.Contains(Messages(failure), m => m.Contains("Authentication:Mode", StringComparison.Ordinal));
    }

    [Fact]
    public void AppSettings_DoNotDefineTheAuthenticationMode()
    {
        var appsettings = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "CashFlow.Transactions.Api", "appsettings.json"));

        Assert.DoesNotContain("Authentication", appsettings, StringComparison.Ordinal);
        Assert.DoesNotContain("DevelopmentHeaders", appsettings, StringComparison.Ordinal);
    }
}
