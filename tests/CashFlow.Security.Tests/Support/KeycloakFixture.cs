using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using CashFlow.Persistence.Tests;
using Testcontainers.Keycloak;

namespace CashFlow.Security.Tests.Support;

/// <summary>
/// Keycloak 26 REAL (Testcontainers) importando o MESMO infra/keycloak/realm-export.json do Compose. Como no Compose, o
/// realm não traz segredos: os secrets dos clientes confidenciais entram depois do import pela Admin REST (o papel do
/// serviço keycloak-init). Porta do host fixa para que o issuer não mude quando o container é parado e religado (T-07).
/// Sem KC_HOSTNAME: o issuer segue o cabeçalho Host, o que dá um token REAL com iss diferente (via o outro nome do
/// loopback) para o negativo de issuer.
/// </summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = "cashflow";
    public const string MerchantAClient = "merchant-a-system";
    public const string MerchantBClient = "merchant-b-system";
    public const string NoAudienceClient = "probe-no-audience";
    public const string ShortLivedClient = "probe-short-lived";
    public const string Audience = "cashflow-api";

    // Segredos de teste, descartáveis (o container some ao fim da execução).
    public const string MerchantASecret = "test_merchant_a_secret";
    public const string MerchantBSecret = "test_merchant_b_secret";
    public const string ProbeSecret = "test_probe_secret";

    public static readonly Guid MerchantA = Guid.Parse("33333333-3333-4333-8333-333333333333");
    public static readonly Guid MerchantB = Guid.Parse("44444444-4444-4444-8444-444444444444");

    private const string AdminUser = "test_kc_admin";
    private const string AdminPassword = "test_kc_admin_pw";
    private const string MerchantAClientUuid = "a1a1a1a1-0000-4000-8000-00000000000a";
    private const string MerchantBClientUuid = "b2b2b2b2-0000-4000-8000-00000000000b";

    private readonly int _port = FreePort();
    private readonly KeycloakContainer _container;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public KeycloakFixture()
    {
        _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.8.1")
            .WithUsername(AdminUser)
            .WithPassword(AdminPassword)
            .WithRealm(Path.Combine(RepositoryPaths.Root, "infra", "keycloak", "realm-export.json"))
            .WithPortBinding(_port, 8080)
            // Testcontainers publica em 0.0.0.0 por padrão (IdP de teste com senha fixa alcançável pela rede local);
            // restringe todas as publicações ao loopback (auditoria F5).
            .WithCreateParameterModifier(parameters =>
            {
                foreach (var binding in (parameters.HostConfig?.PortBindings?.Values ?? []).SelectMany(b => b))
                {
                    binding.HostIP = "127.0.0.1";
                }
            })
            .Build();
    }

    /// <summary>Nome do host usado nos endereços "oficiais" (issuer, metadados).</summary>
    public string Host => _container.Hostname;

    /// <summary>Outro nome do mesmo loopback: um token pedido por aqui tem iss diferente (negativo de issuer).</summary>
    public string AlternateHost => Host is "localhost" ? "127.0.0.1" : "localhost";

    public string BaseAddress => $"http://{Host}:{_port}";

    public string Issuer => $"{BaseAddress}/realms/{Realm}";

    public string MetadataAddress => $"{Issuer}/.well-known/openid-configuration";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var admin = await AdminTokenAsync();

        // Mesmo efeito do keycloak-init do Compose: secret nos clientes de id fixo (PUT parcial, como o kcadm faz).
        await AdminAsync(admin, HttpMethod.Put, $"clients/{MerchantAClientUuid}", new JsonObject { ["secret"] = MerchantASecret });
        await AdminAsync(admin, HttpMethod.Put, $"clients/{MerchantBClientUuid}", new JsonObject { ["secret"] = MerchantBSecret });

        // Clientes-sonda só do teste: token sem aud=cashflow-api e token que expira em 1 s.
        await AdminAsync(admin, HttpMethod.Post, "clients", ProbeClient(NoAudienceClient, ["basic", "transactions.write"], lifespanSeconds: null));
        await AdminAsync(admin, HttpMethod.Post, "clients", ProbeClient(ShortLivedClient, ["basic", Audience, "transactions.write"], lifespanSeconds: 1));
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Para o IdP (docker stop): conexões passam a ser recusadas, como numa indisponibilidade real.</summary>
    public Task StopAsync() => DockerAsync("stop", "--time", "10", _container.Id);

    /// <summary>
    /// Religa o MESMO container (docker start: a porta fixa 8080 e o issuer não mudam) e espera o discovery responder.
    /// Não usa IContainer.StartAsync: depois de um stop ele reexecuta a estratégia de espera do módulo contra a porta de
    /// gestão (9000), cujo mapeamento aleatório muda no restart, e fica esperando sem limite (observado: teste travado
    /// 11 min, docs/testing/evidence/f5/dotnet-test-security-hang.log).
    /// </summary>
    public async Task StartAsync()
    {
        await DockerAsync("start", _container.Id);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (true)
        {
            try
            {
                using var response = await _http.GetAsync(MetadataAddress);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // ainda subindo
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("Keycloak não voltou a responder o discovery em 120 s após docker start.");
            }

            await Task.Delay(500);
        }
    }

    /// <summary>docker CLI (mesmo daemon que o Testcontainers usa); falha alto se o comando não terminar em 60 s ou sair != 0.</summary>
    private static async Task DockerAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("não foi possível iniciar o docker CLI");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', arguments)} saiu com {process.ExitCode}: {await stderr}");
        }
    }

    /// <summary>
    /// Client Credentials. scope: escopos opcionais pedidos (separados por espaço; vazio = só os default do cliente, pois o
    /// Keycloak recusa "scope=" vazio com "Invalid scopes"); host: nome alternativo do loopback.
    /// </summary>
    public async Task<string> TokenAsync(string clientId, string secret, string scope, string? host = null)
    {
        var tokenEndpoint = $"http://{host ?? Host}:{_port}/realms/{Realm}/protocol/openid-connect/token";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        };
        if (!string.IsNullOrWhiteSpace(scope))
        {
            form["scope"] = scope;
        }

        using var response = await _http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form));
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"token de {clientId}: {response.StatusCode} {body?["error_description"]}");
        return (string)body!["access_token"]!;
    }

    public Task<string> MerchantATokenAsync(string scope = "transactions.write transactions.read consolidation.read") =>
        TokenAsync(MerchantAClient, MerchantASecret, scope);

    public Task<string> MerchantBTokenAsync(string scope = "transactions.write transactions.read consolidation.read") =>
        TokenAsync(MerchantBClient, MerchantBSecret, scope);

    private static JsonObject ProbeClient(string clientId, string[] defaultScopes, int? lifespanSeconds)
    {
        var attributes = new JsonObject { ["use.refresh.tokens"] = "false", ["client_credentials.use_refresh_token"] = "false" };
        if (lifespanSeconds is not null)
        {
            attributes["access.token.lifespan"] = lifespanSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new JsonObject
        {
            ["clientId"] = clientId,
            ["enabled"] = true,
            ["protocol"] = "openid-connect",
            ["publicClient"] = false,
            ["secret"] = ProbeSecret,
            ["serviceAccountsEnabled"] = true,
            ["standardFlowEnabled"] = false,
            ["directAccessGrantsEnabled"] = false,
            ["fullScopeAllowed"] = false,
            ["attributes"] = attributes,
            ["protocolMappers"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "merchant_id (fixo: merchant A)",
                    ["protocol"] = "openid-connect",
                    ["protocolMapper"] = "oidc-hardcoded-claim-mapper",
                    ["config"] = new JsonObject
                    {
                        ["claim.name"] = "merchant_id",
                        ["claim.value"] = MerchantA.ToString(),
                        ["jsonType.label"] = "String",
                        ["access.token.claim"] = "true",
                        ["id.token.claim"] = "false",
                    },
                }),
            ["defaultClientScopes"] = new JsonArray(defaultScopes.Select(s => (JsonNode)s).ToArray()),
            ["optionalClientScopes"] = new JsonArray(),
        };
    }

    private async Task<string> AdminTokenAsync()
    {
        using var response = await _http.PostAsync($"{BaseAddress}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUser,
            ["password"] = AdminPassword,
        }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        return (string)body!["access_token"]!;
    }

    private async Task AdminAsync(string adminToken, HttpMethod method, string path, JsonObject body)
    {
        using var request = new HttpRequestMessage(method, $"{BaseAddress}/admin/realms/{Realm}/{path}")
        {
            Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await _http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"Admin REST {method} {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
