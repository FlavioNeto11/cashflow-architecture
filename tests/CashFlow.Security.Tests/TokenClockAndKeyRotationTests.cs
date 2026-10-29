using System.Net;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Hosting.Security;
using CashFlow.Security.Tests.Support;

namespace CashFlow.Security.Tests;

/// <summary>
/// Limites de relógio, typ e rotação de kid (auditoria F5, P2-13), com o StubIssuer (emissor de teste em http) na frente
/// de um gateway real configurado com ClockSkewSeconds=30 e de um eco TLS como destino. O Keycloak real não emite tokens
/// com nbf no futuro, exp no passado nem typ arbitrário, por isso o emissor de teste. As asserções de motivo usam o
/// Meter CashFlow.Security do gateway (authn_failed_total{reason}).
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class TokenClockAndKeyRotationTests(SecurityFixture fx) : IAsyncLifetime
{
    private const int SkewSeconds = 30;

    private StubIssuer _issuer = null!;
    private EchoUpstream _echo = null!;
    private GatewayHost _gateway = null!;

    public async Task InitializeAsync()
    {
        _issuer = new StubIssuer();
        await _issuer.StartAsync();
        _echo = new EchoUpstream(fx.Pki);
        await _echo.StartAsync();
        _gateway = new GatewayHost(fx.Keycloak, fx.Pki, _echo.Port, _echo.Port)
        {
            IssuerOverride = (_issuer.MetadataAddress, _issuer.Issuer, SkewSeconds),
        }.Start();
        await SecurityFixture.WaitReadyAsync(_gateway.Health, "gateway (emissor de teste)");
    }

    public async Task DisposeAsync()
    {
        _gateway.Dispose();
        await _echo.DisposeAsync();
        await _issuer.DisposeAsync();
    }

    [Fact]
    public async Task StubIssuer_TokenInsideItsWindow_IsAccepted()
    {
        var now = DateTimeOffset.UtcNow;

        using var response = await GetAsync(_issuer.Token(_issuer.CurrentKey, now.AddSeconds(-5), now.AddMinutes(5)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(20, true)] // nbf 20 s no futuro: dentro da tolerância de 30 s
    [InlineData(45, false)] // nbf 45 s no futuro: além da tolerância
    public async Task NotBeforeInTheFuture_IsToleratedOnlyWithinSkew(int secondsAhead, bool accepted)
    {
        var now = DateTimeOffset.UtcNow;
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        using var response = await GetAsync(_issuer.Token(_issuer.CurrentKey, now.AddSeconds(secondsAhead), now.AddMinutes(5)));

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, metrics.Total("authn_failed_total"));
        }
        else
        {
            await Requests.AssertProblemAsync(response, 401);
            Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "not_yet_valid"));
        }
    }

    [Theory]
    [InlineData(20, true)] // exp 20 s no passado: dentro da tolerância de 30 s
    [InlineData(45, false)] // exp 45 s no passado: expirado
    public async Task ExpiredToken_IsToleratedOnlyWithinSkew(int secondsAgo, bool accepted)
    {
        var now = DateTimeOffset.UtcNow;
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        using var response = await GetAsync(_issuer.Token(_issuer.CurrentKey, now.AddMinutes(-5), now.AddSeconds(-secondsAgo)));

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(0, metrics.Total("authn_failed_total"));
        }
        else
        {
            await Requests.AssertProblemAsync(response, 401);
            Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "expired"));
        }
    }

    /// <summary>
    /// typ do CABEÇALHO JOSE (TokenValidationParameters.ValidTypes). O Keycloak 26 escreve "JWT" no cabeçalho dos access
    /// tokens ("Bearer" é a claim typ do payload, que o IdentityModel não valida) e "at+jwt" só com a opção RFC 9068 por
    /// cliente; qualquer outro valor é 401 invalid_type.
    /// </summary>
    [Theory]
    [InlineData("JWT", true)] // cabeçalho que o Keycloak emite por padrão
    [InlineData("at+jwt", true)] // RFC 9068
    [InlineData("Bearer", false)] // valor do payload, nunca do cabeçalho: não está na lista
    [InlineData("ID", false)] // id_token apresentado como access token
    [InlineData("Logout", false)]
    public async Task UnexpectedTokenType_IsRejected(string type, bool accepted)
    {
        var now = DateTimeOffset.UtcNow;
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        using var response = await GetAsync(_issuer.Token(_issuer.CurrentKey, now.AddSeconds(-5), now.AddMinutes(5), type));

        if (accepted)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
        {
            await Requests.AssertProblemAsync(response, 401);
            Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "invalid_type"));
        }
    }

    /// <summary>
    /// Rotação de chave no IdP: o primeiro token com o kid novo falha (401 unknown_signing_key) e dispara o refresh do JWKS
    /// (RefreshOnIssuerKeyNotFound; o handler não repete a validação na mesma requisição); o segundo passa. Depois disso,
    /// uma chave nunca publicada continua 401 (o refresh por kid desconhecido é limitado a um por RefreshInterval, então
    /// este passo vem por último para não consumir o refresh que a rotação precisa).
    /// </summary>
    [Fact]
    public async Task KeyRotation_NewKidIsAcceptedAfterJwksRefresh_UnknownKeyStaysRejected()
    {
        var now = DateTimeOffset.UtcNow;
        var rotated = StubIssuer.NewKey("stub-key-2");
        _issuer.Publish(rotated);
        var tokenWithNewKid = _issuer.Token(rotated, now.AddSeconds(-5), now.AddMinutes(5));
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        using var beforeRefresh = await GetAsync(tokenWithNewKid);
        using var afterRefresh = await GetAsync(tokenWithNewKid);
        using var oldKeyStillValid = await GetAsync(_issuer.Token(_issuer.CurrentKey, now.AddSeconds(-5), now.AddMinutes(5)));

        await Requests.AssertProblemAsync(beforeRefresh, 401);
        Assert.Equal(1, metrics.Total("authn_failed_total", "reason", "unknown_signing_key"));
        Assert.Equal(HttpStatusCode.OK, afterRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, oldKeyStillValid.StatusCode);

        var neverPublished = StubIssuer.NewKey("stub-key-rogue");
        using var rogue = await GetAsync(_issuer.Token(neverPublished, now.AddSeconds(-5), now.AddMinutes(5)));
        using var rogueAgain = await GetAsync(_issuer.Token(neverPublished, now.AddSeconds(-5), now.AddMinutes(5)));

        await Requests.AssertProblemAsync(rogue, 401);
        await Requests.AssertProblemAsync(rogueAgain, 401);
        Assert.Equal(3, metrics.Total("authn_failed_total", "reason", "unknown_signing_key"));
    }

    private Task<HttpResponseMessage> GetAsync(string token) =>
        Requests.GetAsync(_gateway.Edge, $"{Requests.TransactionsUrl(KeycloakFixture.MerchantA)}/{Guid.NewGuid()}", token);
}
