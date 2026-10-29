extern alias consapi;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ConsolidationApiProgram = consapi::Program;

namespace CashFlow.Consolidation.Tests.Support;

/// <summary>
/// consolidation-api REAL (Program, Kestrel em duas portas) como cons_ro (pool 12, o do Compose) contra o
/// consolidation_db do fixture. Ambiente e modo de autenticação configuráveis para os testes fail-closed.
/// </summary>
public sealed class ConsolidationApiHost(DatabasesFixture databases) : WebApplicationFactory<ConsolidationApiProgram>
{
    private int _businessPort;
    private int _healthPort;

    public string Environment { get; init; } = "Development";

    public string? AuthenticationMode { get; init; } = "DevelopmentHeaders";

    public HttpClient Business { get; private set; } = null!;

    public HttpClient Health { get; private set; } = null!;

    public ConsolidationApiHost Start()
    {
        _businessPort = FreePort();
        _healthPort = FreePort();
        UseKestrel();
        StartServer();
        Business = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_businessPort}") };
        Health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_healthPort}") };
        return this;
    }

    public static string DailyUrl(Guid merchantId, string query) => $"/api/v1/merchants/{merchantId}/consolidations/daily?{query}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("Hosting:BusinessPort", _businessPort.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("Hosting:HealthPort", _healthPort.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("ConnectionStrings:Default", databases.Pooled("cons_ro", DatabasesFixture.ConsolidationDb, 12));
        builder.UseSetting("Authentication:Mode", AuthenticationMode);
        builder.UseSetting("Hosting:Tls:AllowPlainHttp", "true"); // Kestrel em HTTP; mTLS coberto em CashFlow.Security.Tests (C-F5-09)
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Business?.Dispose();
            Health?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
