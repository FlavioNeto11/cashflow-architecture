extern alias consapi;
extern alias gateway;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using CashFlow.Consolidation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ConsolidationApiProgram = consapi::Program;
using GatewayProgram = gateway::Program;
using TransactionsApiProgram = Program;

namespace CashFlow.Security.Tests.Support;

/// <summary>Configuração comum dos três hosts: ambiente Production (DevelopmentHeaders seria recusado) e JwtBearer real.</summary>
public static class HostSettings
{
    public const string Environment = "Production";

    public static void ApplyJwtBearer(IWebHostBuilder builder, KeycloakFixture keycloak, string? validIssuer = null) =>
        ApplyJwtBearer(builder, keycloak.MetadataAddress, validIssuer ?? keycloak.Issuer, clockSkewSeconds: 0); // skew 0: o teste de expiração não passa por tolerância

    /// <summary>JwtBearer apontado para qualquer emissor (Keycloak real ou o StubIssuer dos testes de relógio/rotação).</summary>
    public static void ApplyJwtBearer(IWebHostBuilder builder, string metadataAddress, string issuer, int clockSkewSeconds)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("Authentication:Mode", "JwtBearer");
        builder.UseSetting("Authentication:JwtBearer:MetadataAddress", metadataAddress);
        builder.UseSetting("Authentication:JwtBearer:ValidIssuer", issuer);
        builder.UseSetting("Authentication:JwtBearer:ValidAudience", KeycloakFixture.Audience);
        builder.UseSetting("Authentication:JwtBearer:AllowHttpMetadata", "true"); // IdP de teste em http (mesmo GAP do Compose)
        builder.UseSetting("Authentication:JwtBearer:ClockSkewSeconds", Port(clockSkewSeconds));
    }

    public static void ApplyMutualTls(IWebHostBuilder builder, TestPki pki, LeafCertificate server)
    {
        builder.UseSetting("Hosting:Tls:ServerCertificate", server.CertificatePath);
        builder.UseSetting("Hosting:Tls:ServerKey", server.KeyPath);
        builder.UseSetting("Hosting:Tls:ClientCa", pki.CaPath);
        builder.UseSetting("Hosting:Tls:AllowedClientNames", TestPki.GatewayName);
        builder.UseSetting("Hosting:Tls:BlockedThumbprintsFile", pki.BlocklistPath);
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Server.Kestrel.Https", "Debug"); // motivo de cada handshake recusado no log do teste
    }

    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static string Port(int port) => port.ToString(CultureInfo.InvariantCulture);
}

/// <summary>transactions-api REAL: Kestrel com mTLS no listener de negócio, JwtBearer, sem broker (publisher desligado).</summary>
public sealed class TransactionsApiHost(DatabasesFixture databases, KeycloakFixture keycloak, TestPki pki) : WebApplicationFactory<TransactionsApiProgram>
{
    public int BusinessPort { get; } = HostSettings.FreePort();

    public int HealthPort { get; } = HostSettings.FreePort();

    public Uri BusinessUri => new($"https://127.0.0.1:{BusinessPort}");

    public HttpClient Health { get; private set; } = null!;

    public TransactionsApiHost Start()
    {
        UseKestrel();
        StartServer();
        Health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HealthPort}") };
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        HostSettings.ApplyJwtBearer(builder, keycloak);
        HostSettings.ApplyMutualTls(builder, pki, pki.TransactionsServer);
        builder.UseSetting("Hosting:BusinessPort", HostSettings.Port(BusinessPort));
        builder.UseSetting("Hosting:HealthPort", HostSettings.Port(HealthPort));
        builder.UseSetting("ConnectionStrings:Default", databases.Pooled("tx_app", DatabasesFixture.TransactionsDb, 10));
        builder.UseSetting("ConnectionStrings:Publisher", databases.Pooled("tx_app", DatabasesFixture.TransactionsDb, 2));
        builder.UseSetting("Outbox:Publisher:Enabled", "false");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Health?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>consolidation-api REAL (cons_ro) com mTLS e JwtBearer.</summary>
public sealed class ConsolidationApiHost(DatabasesFixture databases, KeycloakFixture keycloak, TestPki pki) : WebApplicationFactory<ConsolidationApiProgram>
{
    public int BusinessPort { get; } = HostSettings.FreePort();

    public int HealthPort { get; } = HostSettings.FreePort();

    public Uri BusinessUri => new($"https://127.0.0.1:{BusinessPort}");

    public HttpClient Health { get; private set; } = null!;

    public ConsolidationApiHost Start()
    {
        UseKestrel();
        StartServer();
        Health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HealthPort}") };
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        HostSettings.ApplyJwtBearer(builder, keycloak);
        HostSettings.ApplyMutualTls(builder, pki, pki.ConsolidationServer);
        builder.UseSetting("Hosting:BusinessPort", HostSettings.Port(BusinessPort));
        builder.UseSetting("Hosting:HealthPort", HostSettings.Port(HealthPort));
        builder.UseSetting("ConnectionStrings:Default", databases.Pooled("cons_ro", DatabasesFixture.ConsolidationDb, 12));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Health?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Gateway REAL: borda em HTTP puro (o TLS de borda é do Compose; aqui o que se prova é JWT, escopo, rate limit, allowlist
/// e o mTLS de cliente para as APIs). Limites de taxa pequenos para os testes de 429; ServerCa trocável para o negativo
/// do lado cliente do mTLS.
/// </summary>
public sealed class GatewayHost(KeycloakFixture keycloak, TestPki pki, int transactionsPort, int consolidationPort) : WebApplicationFactory<GatewayProgram>
{
    public int BusinessPort { get; } = HostSettings.FreePort();

    public int HealthPort { get; } = HostSettings.FreePort();

    /// <summary>CA usada para validar os certificados de servidor das APIs (padrão: a CA de teste).</summary>
    public string? ServerCaPath { get; init; }

    /// <summary>Emissor alternativo (StubIssuer) e tolerância de relógio; null = Keycloak da fixture com skew 0.</summary>
    public (string MetadataAddress, string Issuer, int ClockSkewSeconds)? IssuerOverride { get; init; }

    /// <summary>Alto: os testes de 429 usam só a rota de consolidado; leituras de transação não podem ser estranguladas entre testes.</summary>
    public int TransactionsReadPerSecond { get; init; } = 50;

    /// <summary>Baixo de propósito: é a rota em que o teste de rate limit por merchant força 429.</summary>
    public int ConsolidationReadPerMerchantPerSecond { get; init; } = 3;

    public int ConsolidationReadGlobalPerSecond { get; init; } = 100;

    /// <summary>Padrão = o de produção (RateLimitSettings); baixo só no teste do GlobalLimiter com anônimos (P1-1 de F7).</summary>
    public int AnonymousPerIpPerSecond { get; init; } = 20;

    public HttpClient Edge { get; private set; } = null!;

    public HttpClient Health { get; private set; } = null!;

    public GatewayHost Start()
    {
        UseKestrel();
        StartServer();
        Edge = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{BusinessPort}"), Timeout = TimeSpan.FromSeconds(30) };
        Health = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{HealthPort}") };
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (IssuerOverride is { } issuer)
        {
            HostSettings.ApplyJwtBearer(builder, issuer.MetadataAddress, issuer.Issuer, issuer.ClockSkewSeconds);
        }
        else
        {
            HostSettings.ApplyJwtBearer(builder, keycloak);
        }

        builder.UseSetting("Hosting:BusinessPort", HostSettings.Port(BusinessPort));
        builder.UseSetting("Hosting:HealthPort", HostSettings.Port(HealthPort));
        builder.UseSetting("Hosting:Tls:AllowPlainHttp", "true"); // borda em HTTP nos testes; TLS de borda coberto em GatewayEdgeTests (C-F5-09)
        builder.UseSetting("Gateway:Upstream:TransactionsApi", $"https://127.0.0.1:{transactionsPort}");
        builder.UseSetting("Gateway:Upstream:ConsolidationApi", $"https://127.0.0.1:{consolidationPort}");
        builder.UseSetting("Gateway:Upstream:ClientCertificate", pki.GatewayClient.CertificatePath);
        builder.UseSetting("Gateway:Upstream:ClientKey", pki.GatewayClient.KeyPath);
        builder.UseSetting("Gateway:Upstream:ServerCa", ServerCaPath ?? pki.CaPath);
        builder.UseSetting("Gateway:RateLimits:TransactionsWritePerSecond", "50");
        builder.UseSetting("Gateway:RateLimits:TransactionsWriteBurst", "100");
        builder.UseSetting("Gateway:RateLimits:TransactionsReadPerSecond", HostSettings.Port(TransactionsReadPerSecond));
        builder.UseSetting("Gateway:RateLimits:ConsolidationReadPerMerchantPerSecond", HostSettings.Port(ConsolidationReadPerMerchantPerSecond));
        builder.UseSetting("Gateway:RateLimits:ConsolidationReadGlobalPerSecond", HostSettings.Port(ConsolidationReadGlobalPerSecond));
        builder.UseSetting("Gateway:RateLimits:AnonymousPerIpPerSecond", HostSettings.Port(AnonymousPerIpPerSecond));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Edge?.Dispose();
            Health?.Dispose();
        }

        base.Dispose(disposing);
    }
}
