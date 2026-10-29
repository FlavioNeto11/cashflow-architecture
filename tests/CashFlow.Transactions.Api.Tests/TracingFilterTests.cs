using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

namespace CashFlow.Transactions.Api.Tests;

/// <summary>
/// Os probes de /health/* não devem gerar spans (ruído a cada poucos segundos), mas rotas de negócio sim.
/// Usa um processor do OpenTelemetry registrado no teste, sobre o Kestrel real.
/// </summary>
public sealed class TracingFilterTests(TracingFilterTests.ApiFactory factory) : IClassFixture<TracingFilterTests.ApiFactory>
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

        public ConcurrentQueue<string?> ExportedPaths { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Hosting:BusinessPort", BusinessPort.ToString());
            builder.UseSetting("Hosting:HealthPort", HealthPort.ToString());
            builder.UseSetting("ConnectionStrings:Default", "Host=127.0.0.1;Port=1;Database=transactions_db;Username=tx_app;Password=x;Timeout=2");
            builder.UseSetting("ConnectionStrings:Publisher", "Host=127.0.0.1;Port=1;Database=transactions_db;Username=tx_app;Password=x;Timeout=2");
            builder.UseSetting("Authentication:Mode", "DevelopmentHeaders");
            builder.UseSetting("Outbox:Publisher:Enabled", "false");

            // Processor de teste que só guarda o caminho de cada span HTTP de servidor concluído.
            var paths = ExportedPaths;
            builder.ConfigureServices(services => services.ConfigureOpenTelemetryTracerProvider((_, tracing) =>
                tracing.AddProcessor(new PathRecordingProcessor(paths))));
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class PathRecordingProcessor(ConcurrentQueue<string?> paths) : OpenTelemetry.BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            if (data.Kind == ActivityKind.Server)
            {
                paths.Enqueue(data.GetTagItem("url.path") as string);
            }
        }
    }

    [Fact]
    public async Task HealthProbes_AreNotTraced_ButBusinessRequestsAre()
    {
        factory.StartServer();
        using var health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{factory.HealthPort}") };
        using var business = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{factory.BusinessPort}") };

        await health.GetAsync("/health/live");
        await health.GetAsync("/health/ready");
        await business.GetAsync("/api/v1");

        // O span de servidor termina logo após a resposta; espera até 5 s o da rota de negócio aparecer.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!factory.ExportedPaths.Contains("/api/v1") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Contains("/api/v1", factory.ExportedPaths);
        Assert.DoesNotContain(factory.ExportedPaths, path => path is not null && path.StartsWith("/health", StringComparison.Ordinal));
    }
}
