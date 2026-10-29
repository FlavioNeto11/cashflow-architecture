using System.Net;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Hosting.Security;
using CashFlow.Security.Tests.Support;
using Xunit.Abstractions;

namespace CashFlow.Security.Tests;

/// <summary>
/// PF-18 (F7, passo B): a rajada de UM merchant acima do próprio limite não pode gastar o teto global da rota de
/// consolidado e gerar 429 para OUTRO merchant. O GlobalLimiter roda antes da política do endpoint e a ficha de token
/// bucket não volta quando a política recusa; por isso o limite por merchant do consolidado passou a ser o primeiro elo
/// de um limitador encadeado (por merchant e depois global): o excesso de A é recusado sem tocar no balde global.
/// Limites pequenos (por merchant 3/s, global 10/s) tornam o caso determinístico: A consome no máximo 6 fichas globais
/// (3 + uma reposição), sobram pelo menos 4 para as 3 requisições de B.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class MerchantBurstGlobalLimiterTests(SecurityFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const int PerMerchantPerSecond = 3;
    private const int GlobalPerSecond = 10;

    private EchoUpstream _echo = null!;
    private GatewayHost _gateway = null!;

    public async Task InitializeAsync()
    {
        _echo = new EchoUpstream(fx.Pki);
        await _echo.StartAsync();
        _gateway = new GatewayHost(fx.Keycloak, fx.Pki, _echo.Port, _echo.Port)
        {
            ConsolidationReadGlobalPerSecond = GlobalPerSecond,
            ConsolidationReadPerMerchantPerSecond = PerMerchantPerSecond,
        }.Start();
        await SecurityFixture.WaitReadyAsync(_gateway.Health, "gateway (limitador encadeado)");
    }

    public async Task DisposeAsync()
    {
        _gateway.Dispose();
        await _echo.DisposeAsync();
    }

    [Fact]
    public async Task BurstOfOneMerchant_AbovePerMerchantLimit_DoesNotThrottleAnotherMerchant()
    {
        // Tokens obtidos ANTES da rajada: a chamada ao Keycloak não pode cair dentro da janela de 1 s do balde.
        var tokenA = await fx.Keycloak.MerchantATokenAsync();
        var tokenB = await fx.Keycloak.MerchantBTokenAsync();
        var urlA = Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01");
        var urlB = Requests.DailyUrl(KeycloakFixture.MerchantB, "2026-01-01");
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        var burst = await Task.WhenAll(Enumerable.Range(0, 10 * PerMerchantPerSecond).Select(_ => Requests.GetAsync(_gateway.Edge, urlA, tokenA)));
        var probes = await Task.WhenAll(Enumerable.Range(0, PerMerchantPerSecond).Select(_ => Requests.GetAsync(_gateway.Edge, urlB, tokenB)));
        try
        {
            var burstStatuses = burst.Select(r => (int)r.StatusCode).ToList();
            var probeStatuses = probes.Select(r => (int)r.StatusCode).ToList();
            output.WriteLine($"rajada de A ({burst.Length} em paralelo): {burstStatuses.Count(s => s == 200)}x200, {burstStatuses.Count(s => s == 429)}x429");
            output.WriteLine($"merchant B logo depois ({probes.Length}): {string.Join(',', probeStatuses)}");

            Assert.All(burstStatuses, status => Assert.Contains(status, new[] { 200, 429 }));
            Assert.InRange(burstStatuses.Count(s => s == 200), 1, 2 * PerMerchantPerSecond);
            Assert.All(probeStatuses, status => Assert.Equal(200, status));

            var throttled = burst.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.NotNull(throttled.Headers.RetryAfter);
            await Requests.AssertProblemAsync(throttled, 429);
        }
        finally
        {
            foreach (var response in burst.Concat(probes))
            {
                response.Dispose();
            }
        }

        Assert.True(metrics.Total("rate_limited_total", "route", "consolidation-read") >= 1);
    }
}
