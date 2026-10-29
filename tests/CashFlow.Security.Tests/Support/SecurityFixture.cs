using System.Net;
using CashFlow.Consolidation.Tests.Support;

namespace CashFlow.Security.Tests.Support;

/// <summary>
/// Ambiente completo de F5, compartilhado pela coleção: Postgres (migrações reais), Keycloak (realm real), PKI de teste,
/// as duas APIs com mTLS + JwtBearer e o gateway na frente delas. Tudo processo real; nada simulado.
/// </summary>
public sealed class SecurityFixture : IAsyncLifetime
{
    public DatabasesFixture Databases { get; } = new();

    public KeycloakFixture Keycloak { get; } = new();

    public TestPki Pki { get; } = new();

    public TransactionsApiHost Transactions { get; private set; } = null!;

    public ConsolidationApiHost Consolidation { get; private set; } = null!;

    public GatewayHost Gateway { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Databases.InitializeAsync(), Keycloak.InitializeAsync());

        Transactions = new TransactionsApiHost(Databases, Keycloak, Pki).Start();
        Consolidation = new ConsolidationApiHost(Databases, Keycloak, Pki).Start();
        Gateway = new GatewayHost(Keycloak, Pki, Transactions.BusinessPort, Consolidation.BusinessPort).Start();

        await WaitReadyAsync(Transactions.Health, "transactions-api");
        await WaitReadyAsync(Consolidation.Health, "consolidation-api");
        await WaitReadyAsync(Gateway.Health, "gateway");
    }

    public async Task DisposeAsync()
    {
        Gateway?.Dispose();
        Consolidation?.Dispose();
        Transactions?.Dispose();
        await Keycloak.DisposeAsync();
        await Databases.DisposeAsync();
        Pki.Dispose();
    }

    /// <summary>Cliente TLS direto para a API (como o gateway faria), com o certificado dado (ou nenhum).</summary>
    public HttpClient DirectTransactions(System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate) =>
        Pki.CreateTlsClient(Transactions.BusinessUri, clientCertificate);

    public HttpClient DirectConsolidation(System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate) =>
        Pki.CreateTlsClient(Consolidation.BusinessUri, clientCertificate);

    public static async Task WaitReadyAsync(HttpClient health, string name, int seconds = 60) =>
        await Eventually.WaitAsync(
            async () =>
            {
                try
                {
                    using var response = await health.GetAsync("/health/ready");
                    return response.StatusCode;
                }
                catch (HttpRequestException)
                {
                    return HttpStatusCode.ServiceUnavailable;
                }
            },
            status => status == HttpStatusCode.OK,
            TimeSpan.FromSeconds(seconds),
            $"/health/ready de {name} responder 200 (banco e metadados OIDC em cache)");
}

[CollectionDefinition(Name)]
public sealed class SecurityCollection : ICollectionFixture<SecurityFixture>
{
    public const string Name = "security";
}
