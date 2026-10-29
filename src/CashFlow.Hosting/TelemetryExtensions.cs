using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CashFlow.Hosting;

public static class TelemetryExtensions
{
    /// <summary>
    /// Logs estruturados (JSON no console, com escopo de TraceId/SpanId) e OpenTelemetry com
    /// instrumentação de ASP.NET Core, HttpClient e Npgsql. O exportador OTLP só é ligado quando
    /// OTEL_EXPORTER_OTLP_ENDPOINT está definido; sem ele, nada sai do processo.
    /// </summary>
    public static WebApplicationBuilder AddCashFlowTelemetry(this WebApplicationBuilder builder, string serviceName)
    {
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
        });
        builder.Logging.Configure(options =>
            options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                // Probes de saúde (a cada poucos segundos) só geram ruído nos traces.
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsqlInstrumentation()
                .AddMeter(Security.SecurityMetrics.MeterName))
            .WithLogging();

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
