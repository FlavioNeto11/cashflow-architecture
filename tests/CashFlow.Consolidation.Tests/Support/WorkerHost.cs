extern alias worker;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using WorkerProgram = worker::Program;

namespace CashFlow.Consolidation.Tests.Support;

/// <summary>consolidation-worker REAL (Program) como cons_worker, contra o Postgres e o RabbitMQ dos fixtures.</summary>
public sealed class WorkerHost(
    DatabasesFixture databases, RabbitMqFixture broker, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<WorkerProgram>
{
    private int _healthPort;

    public HttpClient Health { get; private set; } = null!;

    public worker::CashFlow.Consolidation.Worker.ConsolidationConsumer Consumer =>
        Services.GetRequiredService<worker::CashFlow.Consolidation.Worker.ConsolidationConsumer>();

    public WorkerHost Start(Func<IChannel, Task>? afterCommitBeforeAck = null)
    {
        _healthPort = FreePort();
        UseKestrel();
        StartServer();
        Consumer.AfterCommitBeforeAck = afterCommitBeforeAck;
        Health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_healthPort}") };
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Hosting:HealthPort", _healthPort.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("ConnectionStrings:Default", databases.Pooled("cons_worker", DatabasesFixture.ConsolidationDb, 6));
        builder.UseSetting("RabbitMq:Host", broker.Options.Host);
        builder.UseSetting("RabbitMq:Port", broker.Options.Port.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("RabbitMq:User", broker.Options.User);
        builder.UseSetting("RabbitMq:Password", broker.Options.Password);
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
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
