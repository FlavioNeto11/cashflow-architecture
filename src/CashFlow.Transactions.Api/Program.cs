using CashFlow.Hosting;
using CashFlow.Hosting.Security;
using CashFlow.Messaging;
using CashFlow.Transactions.Api;
using CashFlow.Transactions.Api.Application;
using CashFlow.Transactions.Api.Http;
using CashFlow.Transactions.Api.Outbox;
using CashFlow.Transactions.Api.Security;
using OpenTelemetry.Metrics;

if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args);
}

if (MigrationCommand.IsRequested(args))
{
    return await MigrationCommand.RunAsync<TransactionsDbContext>(args, options => new(options));
}

var builder = WebApplication.CreateBuilder(args);
builder.AddCashFlowDefaults("transactions-api");
builder.Services.AddCashFlowPostgres<TransactionsDbContext>(builder.Configuration, TransactionService.IsHandled);

// Pool próprio do publisher do outbox (decisão C-06): a ausência de ConnectionStrings__Publisher derruba a partida.
builder.Services.AddCashFlowKeyedDataSource(builder.Configuration, PostgresExtensions.PublisherKey);

// Publisher do outbox (F4). O broker NÃO entra no readiness (RNF-01): broker fora => eventos ficam pendentes no
// outbox e a API segue aceitando lançamentos. Configuração RabbitMq__* obrigatória quando habilitado (fail-fast).
var outboxOptions = builder.Configuration.GetSection(OutboxPublisherOptions.Section).Get<OutboxPublisherOptions>() ?? new();
if (outboxOptions.Enabled)
{
    builder.Services.AddSingleton(outboxOptions);
    builder.Services.AddSingleton(RabbitMqOptions.From(key => builder.Configuration[key]));
    builder.Services.AddSingleton(sp => new OutboxStore(sp.GetRequiredKeyedService<Npgsql.NpgsqlDataSource>(PostgresExtensions.PublisherKey)));
    builder.Services.AddSingleton<OutboxMetrics>();
    builder.Services.AddHostedService<OutboxPublisher>();
}

// Fail-closed: sem Authentication:Mode válido a API não sobe (ver AuthenticationSetup).
builder.AddCashFlowAuthentication(TransactionsMetrics.MeterName, TransactionScopes.Write, TransactionScopes.Read);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<TransactionQueries>();
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(TransactionsMetrics.MeterName));

var app = builder.Build();
app.UseCashFlowListeners();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

// Rota anônima do scaffold F2, mantida: identifica o serviço na porta de negócio e é coberta por DualListenerTests.
app.MapGet("/api/v1", () => Results.Ok(new { service = "transactions-api" }));
app.MapTransactionEndpoints();

await app.RunAsync();
return 0;

public partial class Program;
