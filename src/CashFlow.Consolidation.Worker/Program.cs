using CashFlow.Consolidation.Worker;
using CashFlow.Hosting;
using CashFlow.Messaging;
using Npgsql;
using OpenTelemetry.Metrics;

if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Só o listener de saúde. /health/live não depende de banco nem de broker: o worker sobrevive à queda de ambos
// (pausa ou reconecta com backoff) e reiniciá-lo não ajudaria.
builder.AddCashFlowDefaults("consolidation-worker", withBusinessListener: false);

// cons_worker, SQL explícito (sem EF em tempo de execução). Falha na partida se ConnectionStrings:Default faltar.
builder.Services.AddCashFlowKeyedDataSource(builder.Configuration, "Default");
builder.Services.AddSingleton(sp => new ConsolidationStore(sp.GetRequiredKeyedService<NpgsqlDataSource>("Default")));
builder.Services.AddSingleton(RabbitMqOptions.From(key => builder.Configuration[key]));
builder.Services.AddSingleton(builder.Configuration.GetSection(ConsumerOptions.Section).Get<ConsumerOptions>() ?? new());
builder.Services.AddSingleton<WorkerMetrics>();
builder.Services.AddSingleton<ConsolidationConsumer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsolidationConsumer>());
builder.Services.AddHealthChecks().AddCheck<ConsumerHealthCheck>(ConsumerHealthCheck.Name, tags: ConsumerHealthCheck.Tags);
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(WorkerMetrics.MeterName));

var app = builder.Build();
app.UseCashFlowListeners();

await app.RunAsync();
return 0;

public partial class Program;
