extern alias gateway;

using System.Diagnostics.Metrics;
using System.Net.Security;
using CashFlow.Hosting.Security;
using CashFlow.Hosting.Tls;
using CashFlow.Security.Tests.Support;
using gateway::CashFlow.Gateway;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashFlow.Security.Tests;

/// <summary>Regras sem rede: validador de certificado de cliente, validador de certificado de servidor, configurações fail-closed, allowlist do gateway.</summary>
public sealed class ClientCertificateValidatorTests : IDisposable
{
    private readonly TestPki _pki = new();

    [Fact]
    public void Evaluate_ReturnsTheReason_ForEachNegative()
    {
        var validator = new ClientCertificateValidator(_pki.TrustedRoots, [TestPki.GatewayName], new ThumbprintBlocklist(_pki.BlocklistPath));

        Assert.Null(validator.Evaluate(_pki.GatewayClient.Certificate, SslPolicyErrors.None));
        Assert.Equal("missing", validator.Evaluate(null, SslPolicyErrors.None));
        Assert.Equal("missing", validator.Evaluate(_pki.GatewayClient.Certificate, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.Equal("untrusted_ca", validator.Evaluate(_pki.ForeignClient.Certificate, SslPolicyErrors.None));
        Assert.Equal("expired", validator.Evaluate(_pki.ExpiredClient.Certificate, SslPolicyErrors.None));
        Assert.Equal("name_mismatch", validator.Evaluate(_pki.WrongSanClient.Certificate, SslPolicyErrors.None));
        Assert.Equal("eku", validator.Evaluate(_pki.WrongEkuClient.Certificate, SslPolicyErrors.None));
        Assert.Equal("blocked_thumbprint", validator.Evaluate(_pki.BlockedClient.Certificate, SslPolicyErrors.None));
    }

    [Fact]
    public void Blocklist_IsReloaded_WhenTheFileChanges()
    {
        var path = Path.Combine(_pki.Directory, "reload.txt");
        File.WriteAllText(path, "# vazio\n");
        var blocklist = new ThumbprintBlocklist(path);
        Assert.Equal(0, blocklist.Count);
        Assert.False(blocklist.Contains(_pki.GatewayClient.Certificate.Thumbprint));

        File.WriteAllText(path, $"{_pki.GatewayClient.Certificate.Thumbprint.ToLowerInvariant()} # revogado\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.True(blocklist.Contains(_pki.GatewayClient.Certificate.Thumbprint));
    }

    [Fact]
    public void Validator_RequiresRootsAndNames()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientCertificateValidator([], [TestPki.GatewayName]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientCertificateValidator(_pki.TrustedRoots, []));
    }

    public void Dispose() => _pki.Dispose();
}

public sealed class UpstreamServerCertificateTests : IDisposable
{
    private readonly TestPki _pki = new();
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();

    [Fact]
    public void EvaluateServerCertificate_AcceptsOnlyLocalCa_AndMatchingName()
    {
        var factory = Factory(_pki.CaPath);
        var server = _pki.TransactionsServer.Certificate;

        Assert.Null(factory.EvaluateServerCertificate(server, "127.0.0.1", SslPolicyErrors.None));
        Assert.Null(factory.EvaluateServerCertificate(server, "localhost", SslPolicyErrors.None));
        Assert.Equal("name_mismatch", factory.EvaluateServerCertificate(server, "transactions-api", SslPolicyErrors.None));
        Assert.Equal("name_mismatch", factory.EvaluateServerCertificate(server, string.Empty, SslPolicyErrors.None));
        Assert.Equal("missing", factory.EvaluateServerCertificate(null, "localhost", SslPolicyErrors.None));
        Assert.NotNull(factory.EvaluateServerCertificate(_pki.GatewayClient.Certificate, "localhost", SslPolicyErrors.None)); // EKU clientAuth não serve como servidor
        Assert.Equal("untrusted_ca", Factory(_pki.ForeignCaPath).EvaluateServerCertificate(server, "localhost", SslPolicyErrors.None));
    }

    public void Dispose()
    {
        _services.Dispose();
        _pki.Dispose();
    }

    private UpstreamHttpClientFactory Factory(string serverCa) => new(
        new UpstreamSettings
        {
            TransactionsApi = "https://127.0.0.1:1",
            ConsolidationApi = "https://127.0.0.1:2",
            ClientCertificate = _pki.GatewayClient.CertificatePath,
            ClientKey = _pki.GatewayClient.KeyPath,
            ServerCa = serverCa,
        },
        new SecurityMetrics(_services.GetRequiredService<IMeterFactory>()),
        NullLogger<UpstreamHttpClientFactory>.Instance);
}

public sealed class SettingsTests
{
    [Fact]
    public void JwtBearerSettings_FailClosed()
    {
        Valid().Validate();

        Assert.Throws<InvalidOperationException>(() => With(s => s.AllowHttpMetadata = false).Validate()); // http sem opt-in
        Assert.Throws<InvalidOperationException>(() => With(s => s.MetadataAddress = null).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.MetadataAddress = "ftp://idp/x").Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ValidIssuer = " ").Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ValidAudience = string.Empty).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ClockSkewSeconds = 301).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ClockSkewSeconds = -1).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ValidAlgorithms = ["RS256", "HS256"]).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ValidAlgorithms = ["none"]).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ValidAlgorithms = []).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.MetadataRefreshMinutes = 0).Validate());
        Assert.Throws<InvalidOperationException>(() => With(s => s.ValidTokenTypes = []).Validate()); // typ aberto não é permitido
        Assert.Throws<InvalidOperationException>(() => With(s => s.ValidTokenTypes = [" "]).Validate());
        Assert.Equal(["JWT", "at+jwt"], Valid().ValidTokenTypes); // cabeçalho typ do Keycloak é "JWT"; "Bearer" é claim do payload

        static JwtBearerSettings Valid() => new()
        {
            MetadataAddress = "http://keycloak:8080/realms/cashflow/.well-known/openid-configuration",
            ValidIssuer = "http://keycloak:8080/realms/cashflow",
            AllowHttpMetadata = true,
        };

        static JwtBearerSettings With(Action<JwtBearerSettings> change)
        {
            var settings = Valid();
            change(settings);
            return settings;
        }
    }

    [Fact]
    public void UpstreamSettings_RequireMutualTlsMaterial_OrExplicitPlainHttp()
    {
        var https = new UpstreamSettings();
        Assert.Throws<InvalidOperationException>(https.Validate); // https sem certificados

        var http = new UpstreamSettings { TransactionsApi = "http://a:8080", ConsolidationApi = "http://b:8080" };
        Assert.Throws<InvalidOperationException>(http.Validate); // http sem AllowPlainHttp

        http.AllowPlainHttp = true;
        http.Validate();

        http.ServerCa = "/certs/ca.crt";
        Assert.Throws<InvalidOperationException>(http.Validate); // certificados com http: incoerente

        var mixed = new UpstreamSettings { TransactionsApi = "https://a:8080", ConsolidationApi = "http://b:8080" };
        Assert.Throws<InvalidOperationException>(mixed.Validate);
    }

    [Fact]
    public void LimitsAndRateLimits_RejectOutOfRange()
    {
        Assert.Throws<InvalidOperationException>(() => new LimitSettings { MaxBodyBytes = 512 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new LimitSettings { GetTimeoutSeconds = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new RateLimitSettings { TransactionsWriteBurst = 1, TransactionsWritePerSecond = 2 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new RateLimitSettings { AnonymousPerIpPerSecond = 0 }.Validate());
        new LimitSettings().Validate();
        new RateLimitSettings().Validate();
    }
}

public sealed class ProxyRoutesTests
{
    [Fact]
    public void ForwardedHeaders_AreAnAllowlist_WithoutIdentityOrProxyHeaders()
    {
        var allowed = ProxyRoutes.ForwardedRequestHeaders;

        Assert.Contains("Authorization", allowed);
        Assert.Contains("Idempotency-Key", allowed);
        Assert.Contains("Content-Type", allowed);
        Assert.Contains("Content-Length", allowed);
        Assert.Contains("traceparent", allowed);
        Assert.DoesNotContain(allowed, h => h.StartsWith("X-Dev-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allowed, h => h.StartsWith("X-Forwarded", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Cookie", allowed);
        Assert.DoesNotContain("Host", allowed);
    }

    [Fact]
    public void EveryRoute_HasScopePolicy_RateLimit_Timeout_AndHeaderAllowlist()
    {
        var limits = new LimitSettings();
        var routes = ProxyRoutes.Routes(limits);

        Assert.Equal(5, routes.Count);
        foreach (var route in routes)
        {
            Assert.Contains(route.AuthorizationPolicy, new[] { "transactions.write", "transactions.read", "consolidation.read" });
            Assert.False(string.IsNullOrEmpty(route.RateLimiterPolicy));
            Assert.NotNull(route.Timeout);
            Assert.NotNull(route.Transforms);
            Assert.Contains(route.Transforms!, t => t.TryGetValue("RequestHeadersCopy", out var v) && v == "false");
            Assert.Contains(route.Transforms!, t => t.ContainsKey("RequestHeadersAllowed"));
            Assert.Contains(route.Transforms!, t => t.TryGetValue("X-Forwarded", out var v) && v == "Off");
            Assert.StartsWith("/api/v1/merchants/{merchantId:guid}", route.Match.Path);
        }

        Assert.All(routes.Where(r => r.Match.Methods!.Contains("GET")), r => Assert.Equal(TimeSpan.FromSeconds(limits.GetTimeoutSeconds), r.Timeout));
        Assert.All(routes.Where(r => r.Match.Methods!.Contains("POST")), r => Assert.Equal(TimeSpan.FromSeconds(limits.PostTimeoutSeconds), r.Timeout));
        Assert.Equal("consolidation.read", routes.Single(r => r.RouteId == ProxyRoutes.ConsolidationReadRoute).AuthorizationPolicy);
    }

    [Fact]
    public void Clusters_NeverAcceptAnyServerCertificate()
    {
        var clusters = ProxyRoutes.Clusters(new UpstreamSettings());

        Assert.All(clusters, c => Assert.False(c.HttpClient!.DangerousAcceptAnyServerCertificate));
        Assert.All(clusters, c => Assert.StartsWith("https://", c.Destinations!.Single().Value.Address));
    }
}
