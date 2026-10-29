using System.Net;
using CashFlow.Hosting.Security;
using CashFlow.Hosting.Tls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CashFlow.Hosting;

public static class HostingExtensions
{
    /// <summary>
    /// Configura logs JSON com escopo de TraceId, OpenTelemetry e o par de listeners do Kestrel.
    /// Chaves de configuração: Hosting:BindAddress (padrão 127.0.0.1; a imagem Docker define 0.0.0.0),
    /// Hosting:BusinessPort (8080) e Hosting:HealthPort (8081). Com Hosting:Tls:ServerCertificate definido
    /// (F5, security.md 6) o listener de negócio passa a exigir mTLS (<see cref="TlsSettings"/>); sem ele, HTTP
    /// puro. O listener de saúde é sempre HTTP puro.
    /// </summary>
    /// <param name="withBusinessListener">false para hosts sem API (worker): só a porta de saúde abre.</param>
    public static WebApplicationBuilder AddCashFlowDefaults(
        this WebApplicationBuilder builder, string serviceName, bool withBusinessListener = true)
    {
        builder.AddCashFlowTelemetry(serviceName);

        var section = builder.Configuration.GetSection("Hosting");
        var bindAddress = IPAddress.Parse(section["BindAddress"] ?? "127.0.0.1");
        var ports = new ListenerPorts(
            withBusinessListener ? section.GetValue("BusinessPort", ListenerPorts.DefaultBusiness) : 0,
            section.GetValue("HealthPort", ListenerPorts.DefaultHealth));
        var tls = TlsSettings.Load(builder.Configuration); // valida (e falha) na partida, antes de abrir portas
        if (ports.Business != 0 && !tls.Enabled && !builder.Environment.IsDevelopment())
        {
            // GAP-F5-03 / C-F5-09 (security.md 11): fora de Development, listener de negócio em HTTP puro só com opt-in
            // explícito (Hosting:Tls:AllowPlainHttp=true), como já exige Gateway:Upstream:AllowPlainHttp; sem ele a
            // partida falha. Com o opt-in fica o aviso ruidoso.
            if (!tls.AllowPlainHttp)
            {
                throw new InvalidOperationException(
                    $"{TlsSettings.Section}:ServerCertificate ausente no ambiente {builder.Environment.EnvironmentName}: o listener de negócio subiria sem TLS. " +
                    $"Configure o certificado ou, só em teste/desenvolvimento, {TlsSettings.Section}:AllowPlainHttp=true.");
            }

            LoggerFactory.Create(logging => logging.AddJsonConsole()).CreateLogger("CashFlow.Hosting.Tls").LogWarning(
                "Listener de negócio ({Port}) sem TLS no ambiente {Environment}: Hosting:Tls:ServerCertificate não configurado. Aceitável só em teste/desenvolvimento (GAP declarado).",
                ports.Business, builder.Environment.EnvironmentName);
        }

        builder.Services.AddSingleton(ports);
        builder.Services.AddSingleton(tls);
        builder.Services.AddSingleton<SecurityMetrics>();
        builder.Services.AddHealthChecks();

        // Listen explícito desativa ASPNETCORE_URLS/HTTP_PORTS: as portas vêm só daqui.
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(bindAddress, ports.Health);
            if (ports.Business == 0)
            {
                return;
            }

            if (tls.Enabled)
            {
                kestrel.Listen(bindAddress, ports.Business, listener => BusinessListenerTls.Configure(listener, tls, kestrel.ApplicationServices));
            }
            else
            {
                kestrel.Listen(bindAddress, ports.Business);
            }
        });

        return builder;
    }

    /// <summary>
    /// Aplica o isolamento por listener e mapeia /health/live (processo vivo, sem dependências) e
    /// /health/ready (checks marcados com a tag "ready", p.ex. o banco e os metadados OIDC).
    /// </summary>
    public static WebApplication UseCashFlowListeners(this WebApplication app)
    {
        app.UseMiddleware<ListenerIsolationMiddleware>();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(PostgresExtensions.ReadyTag),
        });

        return app;
    }
}
