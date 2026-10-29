using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CashFlow.Transactions.Api.Tests;

/// <summary>
/// Sobe a transactions-api com Kestrel REAL (não TestServer) em duas portas e verifica o isolamento
/// dos listeners (deployment.md 1.2; T-28 proposto): saúde só em /health/live e /health/ready na porta de
/// saúde; /health/* ausente na porta de negócio.
/// </summary>
public sealed class DualListenerTests(DualListenerTests.ApiFactory factory) : IClassFixture<DualListenerTests.ApiFactory>
{
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public ApiFactory()
        {
            BusinessPort = FreePort();
            HealthPort = FreePort();
            UseKestrel();
        }

        public int BusinessPort { get; }

        public int HealthPort { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Hosting:BusinessPort", BusinessPort.ToString());
            builder.UseSetting("Hosting:HealthPort", HealthPort.ToString());

            // Banco inalcançável de propósito: /health/ready deve ficar não saudável sem derrubar /health/live.
            builder.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Port=1;Database=transactions_db;Username=tx_app;Password=x;Timeout=2");
            builder.UseSetting("ConnectionStrings:Publisher", "Host=127.0.0.1;Port=1;Database=transactions_db;Username=tx_app;Password=x;Timeout=2");
            builder.UseSetting("Authentication:Mode", "DevelopmentHeaders");
            builder.UseSetting("Outbox:Publisher:Enabled", "false");
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private HttpClient Client(int port, string? hostHeader = null)
    {
        factory.StartServer();
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        if (hostHeader is not null)
        {
            client.DefaultRequestHeaders.Host = hostHeader;
        }

        return client;
    }

    [Fact]
    public async Task HealthListener_Live_Returns200()
    {
        using var client = Client(factory.HealthPort);

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthListener_ReadyWithoutDatabase_Returns503WhileLiveStays200()
    {
        using var client = Client(factory.HealthPort);

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1")]
    [InlineData("/")]
    [InlineData("/health")]
    [InlineData("/health/live/extra")]
    public async Task HealthListener_AnyOtherRoute_Returns404(string path)
    {
        using var client = Client(factory.HealthPort);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/HEALTH/LIVE")]
    [InlineData("/health/live/")]
    public async Task BusinessListener_HealthRoutes_Return404(string path)
    {
        using var client = Client(factory.BusinessPort);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BusinessListener_HealthRouteWithSpoofedHostHeader_StillReturns404()
    {
        // O isolamento usa a porta local da conexão, não o cabeçalho Host controlado pelo cliente.
        using var client = Client(factory.BusinessPort, hostHeader: $"127.0.0.1:{factory.HealthPort}");

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BusinessListener_BusinessRoute_Returns200()
    {
        using var client = Client(factory.BusinessPort);

        var response = await client.GetAsync("/api/v1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("transactions-api", await response.Content.ReadAsStringAsync());
    }
}
