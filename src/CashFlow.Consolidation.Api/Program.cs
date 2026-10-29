using CashFlow.Consolidation.Api;
using CashFlow.Consolidation.Api.Http;
using CashFlow.Consolidation.Persistence;
using CashFlow.Hosting;
using CashFlow.Hosting.Security;
using OpenTelemetry.Metrics;

if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args);
}

if (MigrationCommand.IsRequested(args))
{
    return await MigrationCommand.RunAsync<ConsolidationDbContext>(args, options => new(options));
}

var builder = WebApplication.CreateBuilder(args);
builder.AddCashFlowDefaults("consolidation-api");

// cons_ro (somente SELECT). O readiness depende só do Postgres: queda do worker ou do broker não tira a API do ar,
// apenas congela asOf (target.md, tabela de falhas).
builder.Services.AddCashFlowPostgres<ConsolidationDbContext>(builder.Configuration);
builder.Services.AddSingleton<ConsolidationQueries>();

// Fail-closed: sem Authentication:Mode válido a API não sobe (mesmo AuthenticationSetup da transactions-api).
builder.AddCashFlowAuthentication(ConsolidationMetrics.MeterName, ConsolidationScopes.Read);

builder.Services.AddProblemDetails();
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(ConsolidationMetrics.MeterName));

var app = builder.Build();
app.UseCashFlowListeners();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Rota anônima do scaffold F2, mantida: identifica o serviço na porta de negócio.
app.MapGet("/api/v1", () => Results.Ok(new { service = "consolidation-api" }));
app.MapDailyConsolidationEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
