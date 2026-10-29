using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using CashFlow.Consolidation.Tests.Support;
using CashFlow.Hosting.Security;
using CashFlow.Security.Tests.Support;
using Xunit.Abstractions;

namespace CashFlow.Security.Tests;

/// <summary>
/// P1-1 da auditoria de segurança de F7: o GlobalLimiter da rota de consolidado (teto por instância) não pode ser
/// esgotado por requisições ANÔNIMAS. Sem token válido o caller já é limitado por IP (política do endpoint) e nunca
/// chega ao upstream (toda rota exige escopo: 401); se o anônimo consumisse o balde global, um único IP sem credencial
/// acima do teto global derrubaria para 429 todos os merchants autenticados — justamente o caminho dos 50 RPS
/// (RNF-02/03). Gateway próprio na frente de um eco TLS, com limites pequenos (global 5/s, anônimo 3/s por IP) e o
/// limite por merchant alto (50/s) para que só o teto global possa recusar a sonda autenticada.
/// </summary>
[Collection(SecurityCollection.Name)]
public sealed class GlobalLimiterAnonymousTests(SecurityFixture fx, ITestOutputHelper output) : IAsyncLifetime
{
    private const int GlobalPerSecond = 5;
    private const int AnonymousPerIpPerSecond = 3;

    private EchoUpstream _echo = null!;
    private GatewayHost _gateway = null!;

    public async Task InitializeAsync()
    {
        _echo = new EchoUpstream(fx.Pki);
        await _echo.StartAsync();
        _gateway = new GatewayHost(fx.Keycloak, fx.Pki, _echo.Port, _echo.Port)
        {
            ConsolidationReadGlobalPerSecond = GlobalPerSecond,
            AnonymousPerIpPerSecond = AnonymousPerIpPerSecond,
            ConsolidationReadPerMerchantPerSecond = 50,
        }.Start();
        await SecurityFixture.WaitReadyAsync(_gateway.Health, "gateway (teto global baixo)");
    }

    public async Task DisposeAsync()
    {
        _gateway.Dispose();
        await _echo.DisposeAsync();
    }

    [Fact]
    public async Task AnonymousFlood_OnConsolidationRoute_DoesNotExhaustGlobalLimiterForAuthenticatedMerchant()
    {
        // Token obtido ANTES da rajada: a chamada ao Keycloak não pode cair dentro da janela de 1 s do balde.
        var token = await fx.Keycloak.MerchantATokenAsync();
        var url = Requests.DailyUrl(KeycloakFixture.MerchantA, "2026-01-01");
        using var metrics = new MetricCapture(SecurityMetrics.MeterName);

        // Fase A: rajada anônima acima do teto global, no mesmo segundo e do mesmo IP (127.0.0.1). Dentro do balde por
        // IP: 401 (a autorização recusa); acima dele: 429. Até 2x o limite por IP tolera uma reposição do balde no meio.
        var burst = await Task.WhenAll(Enumerable.Range(0, 4 * AnonymousPerIpPerSecond).Select(_ => Requests.GetAsync(_gateway.Edge, url, token: null)));
        try
        {
            var statuses = burst.Select(r => (int)r.StatusCode).ToList();
            output.WriteLine($"fase A (anônimos, {burst.Length} em paralelo): {string.Join(',', statuses)}");
            Assert.All(statuses, status => Assert.Contains(status, new[] { 401, 429 }));
            var unauthorized = burst.Where(r => r.StatusCode == HttpStatusCode.Unauthorized).ToList();
            Assert.InRange(unauthorized.Count, 1, 2 * AnonymousPerIpPerSecond);
            foreach (var response in unauthorized)
            {
                await Requests.AssertProblemAsync(response, 401);
                Requests.AssertSecurityHeaders(response);
            }

            var throttled = burst.First(r => r.StatusCode == HttpStatusCode.TooManyRequests);
            Assert.NotNull(throttled.Headers.RetryAfter);
            await Requests.AssertProblemAsync(throttled, 429);
            Requests.AssertSecurityHeaders(throttled);
        }
        finally
        {
            foreach (var response in burst)
            {
                response.Dispose();
            }
        }

        // Fase B: inundação anônima sustentada (4 laços) e, com ela em curso e já acima do teto global, UMA requisição
        // autenticada. Se o anônimo consumisse o balde global, cada reposição (5 fichas/s) seria engolida pela
        // inundação em milissegundos e a sonda veria 429; com o anônimo fora do teto global, ela tem de chegar ao eco.
        var floodStatuses = new ConcurrentQueue<int>();
        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var response = await Requests.GetAsync(_gateway.Edge, url, token: null);
                floodStatuses.Enqueue((int)response.StatusCode);
            }
        })).ToArray();

        HttpResponseMessage probe;
        var clock = Stopwatch.StartNew();
        try
        {
            while (floodStatuses.Count < 4 * GlobalPerSecond)
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), "a inundação anônima não produziu respostas em 10 s");
                await Task.Delay(10);
            }

            output.WriteLine($"fase B: {floodStatuses.Count} respostas anônimas em {clock.ElapsedMilliseconds} ms antes da sonda");
            probe = await Requests.GetAsync(_gateway.Edge, url, token);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(workers);
        }

        using (probe)
        {
            var flood = floodStatuses.ToList();
            output.WriteLine($"fase B: sonda autenticada = {(int)probe.StatusCode}; inundação: {flood.Count} respostas, {flood.Count(s => s == 401)}x401, {flood.Count(s => s == 429)}x429");
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
            var echoed = await Requests.JsonAsync(probe);
            Assert.Equal($"/api/v1/merchants/{KeycloakFixture.MerchantA}/consolidations/daily", (string?)echoed["path"]);
            Assert.All(flood, status => Assert.Contains(status, new[] { 401, 429 }));
            Assert.Contains(429, flood);
        }

        Assert.True(metrics.Total("rate_limited_total", "route", "consolidation-read") >= 1);
    }
}
