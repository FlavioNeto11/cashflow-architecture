using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CashFlow.Security.Tests.Support;

/// <summary>
/// Emissor OIDC de TESTE (não é um IdP e nunca sai dos testes): serve o documento de discovery e um JWKS em http e assina
/// JWTs RS256 com claims escolhidas pelo teste — o que o Keycloak real não permite (nbf no futuro, exp no passado,
/// typ arbitrário, kid ainda não publicado). Com ele se exercitam os limites de tolerância de relógio, a rotação de
/// chave (kid novo → refresh do JWKS) e a recusa de typ inesperado (auditoria F5, P2-13).
/// </summary>
public sealed class StubIssuer : IAsyncDisposable
{
    private readonly List<RsaSecurityKey> _published = [];
    private readonly WebApplication _app;
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public StubIssuer()
    {
        Port = HostSettings.FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, Port));
        _app = builder.Build();
        _app.MapGet("/realms/stub/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = Issuer,
            jwks_uri = $"{Issuer}/protocol/openid-connect/certs",
            token_endpoint = $"{Issuer}/protocol/openid-connect/token",
            id_token_signing_alg_values_supported = new[] { "RS256" },
        }));
        _app.MapGet("/realms/stub/protocol/openid-connect/certs", () =>
        {
            lock (_published)
            {
                return Results.Json(new { keys = _published.Select(JsonWebKeyConverter.ConvertFromRSASecurityKey).Select(PublicOnly).ToArray() });
            }
        });

        Publish(NewKey("stub-key-1"));
    }

    public int Port { get; }

    public string Issuer => $"http://127.0.0.1:{Port}/realms/stub";

    public string MetadataAddress => $"{Issuer}/.well-known/openid-configuration";

    /// <summary>Chave publicada no JWKS desde o início (a que os hosts conhecem ao subir).</summary>
    public RsaSecurityKey CurrentKey
    {
        get
        {
            lock (_published)
            {
                return _published[0];
            }
        }
    }

    public Task StartAsync() => _app.StartAsync();

    public static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    /// <summary>Acrescenta a chave ao JWKS (rotação: a antiga continua publicada, tokens antigos seguem válidos).</summary>
    public void Publish(RsaSecurityKey key)
    {
        lock (_published)
        {
            _published.Add(key);
        }
    }

    /// <summary>
    /// Access token assinado com a chave dada. Claims mínimas que Caller exige (sub, azp, merchant_id, scope) + as de
    /// tempo escolhidas pelo teste; typ (cabeçalho JOSE) padrão "JWT", o que o Keycloak escreve.
    /// </summary>
    public string Token(
        RsaSecurityKey key, DateTimeOffset notBefore, DateTimeOffset expires, string type = "JWT", Guid? merchant = null, string scope = "transactions.write transactions.read consolidation.read")
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = KeycloakFixture.Audience,
            NotBefore = notBefore.UtcDateTime,
            Expires = expires.UtcDateTime,
            IssuedAt = DateTime.UtcNow,
            TokenType = type,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            // Dicionário (não ClaimsIdentity): os nomes vão ao payload como estão, sem mapeamento de tipos de claim.
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "stub-service-account",
                ["azp"] = "stub-client",
                ["merchant_id"] = (merchant ?? KeycloakFixture.MerchantA).ToString(),
                ["scope"] = scope,
            },
        };
        return _handler.CreateToken(descriptor);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static object PublicOnly(JsonWebKey key) => new { kty = key.Kty, use = "sig", alg = "RS256", kid = key.Kid, n = key.N, e = key.E };
}
