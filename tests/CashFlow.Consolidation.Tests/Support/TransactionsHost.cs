using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CashFlow.Consolidation.Tests.Support;

/// <summary>
/// transactions-api REAL (Program, Kestrel em duas portas) contra o Postgres e o RabbitMQ dos fixtures, com o
/// publisher do outbox LIGADO (configuração padrão). Intervalos encurtados só para o teste não esperar à toa.
/// </summary>
public sealed class TransactionsHost(DatabasesFixture databases, RabbitMqFixture broker) : WebApplicationFactory<Program>
{
    private int _businessPort;
    private int _healthPort;

    public HttpClient Business { get; private set; } = null!;

    public HttpClient Health { get; private set; } = null!;

    public TransactionsHost Start()
    {
        _businessPort = FreePort();
        _healthPort = FreePort();
        UseKestrel();
        StartServer();
        Business = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_businessPort}") };
        Health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_healthPort}") };
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Hosting:BusinessPort", _businessPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Hosting:HealthPort", _healthPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("ConnectionStrings:Default", databases.Pooled("tx_app", DatabasesFixture.TransactionsDb, 10));
        builder.UseSetting("ConnectionStrings:Publisher", databases.Pooled("tx_app", DatabasesFixture.TransactionsDb, 2));
        builder.UseSetting("Authentication:Mode", "DevelopmentHeaders");
        builder.UseSetting("RabbitMq:Host", broker.Options.Host);
        builder.UseSetting("RabbitMq:Port", broker.Options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("RabbitMq:User", broker.Options.User);
        builder.UseSetting("RabbitMq:Password", broker.Options.Password);
        builder.UseSetting("Outbox:Publisher:IdlePollMilliseconds", "100");
        builder.UseSetting("Outbox:Publisher:BacklogRefreshSeconds", "1");
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
