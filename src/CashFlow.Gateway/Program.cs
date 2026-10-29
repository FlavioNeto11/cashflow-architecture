using CashFlow.Gateway;
using CashFlow.Hosting;
using CashFlow.Hosting.Security;
using Yarp.ReverseProxy.Forwarder;

if (HealthProbe.IsRequested(args))
{
    return await HealthProbe.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// Listeners 8080 (negócio; TLS de borda opcional via Hosting:Tls com RequireClientCertificate=false) e 8081 (saúde).
builder.AddCashFlowDefaults("gateway");
var settings = GatewaySettings.Load(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Upstream);
builder.Services.AddSingleton(settings.Limits);
builder.Services.AddSingleton(settings.RateLimits);

// Limite duro do Kestrel além da checagem por Content-Length do EdgeMiddleware; sem "Server: Kestrel" (o Kestrel o
// acrescenta DEPOIS dos callbacks OnStarting, então removê-lo no middleware não basta — visto no teste de borda).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = settings.Limits.MaxBodyBytes;
    kestrel.AddServerHeader = false;
});

// Só JwtBearer: o gateway é a borda e nunca aceita identidade por cabeçalhos, nem em Development.
builder.AddCashFlowAuthentication(
    "CashFlow.Gateway", allowDevelopmentHeaders: false,
    ProxyRoutes.TransactionsWriteScope, ProxyRoutes.TransactionsReadScope, ProxyRoutes.ConsolidationReadScope);

builder.Services.AddGatewayRateLimiting(settings.RateLimits);
builder.Services.AddRequestTimeouts();
builder.Services.AddProblemDetails();

// mTLS de cliente para as APIs (registrado antes do AddReverseProxy, que só adiciona o padrão se não houver um).
builder.Services.AddSingleton<IForwarderHttpClientFactory, UpstreamHttpClientFactory>();
builder.Services.AddReverseProxy().LoadFromMemory(ProxyRoutes.Routes(settings.Limits), ProxyRoutes.Clusters(settings.Upstream));

var app = builder.Build();

// EdgeMiddleware antes de tudo: o 404 do isolamento de listener (/health/* na borda) e qualquer resposta curta do
// pipeline também saem com os cabeçalhos de segurança (auditoria F5, P2-2).
app.UseMiddleware<EdgeMiddleware>();
app.UseCashFlowListeners();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseRequestTimeouts();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Sem retentativa em nenhum método: o YARP não repete requisições (um POST repetido só é seguro com Idempotency-Key, e
// isso é decisão do cliente, não do gateway).
app.MapReverseProxy(proxy =>
{
    proxy.Use(async (context, next) =>
    {
        await next(context);
        await ForwarderErrorResponse.WriteAsync(context);
    });
});

await app.RunAsync();
return 0;

public partial class Program;
