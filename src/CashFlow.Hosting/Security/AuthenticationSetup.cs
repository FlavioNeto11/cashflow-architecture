using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CashFlow.Hosting.Security;

public static class AuthenticationSetup
{
    public const string ModeKey = "Authentication:Mode";
    public const string DevelopmentHeadersMode = "DevelopmentHeaders";
    public const string JwtBearerMode = "JwtBearer";

    /// <summary>
    /// Fail-closed: o modo precisa estar configurado explicitamente. DevelopmentHeaders (identidade por
    /// cabeçalhos, sem validação) só é aceito em Development; JwtBearer (F5) valida assinatura via JWKS, iss exato,
    /// aud, exp e algoritmo (<see cref="JwtBearerSettings"/>). Qualquer outra combinação derruba a partida, para que
    /// um ambiente mal configurado nunca suba sem autenticação real. Autorização (escopo e tenant) é a mesma para
    /// qualquer esquema.
    /// </summary>
    /// <param name="meterName">Meter do serviço onde authz_cross_tenant_denied_total é contado (TenantFilter).</param>
    /// <param name="scopes">Uma política por escopo, com o mesmo nome; exige principal autenticado com o escopo.</param>
    public static WebApplicationBuilder AddCashFlowAuthentication(
        this WebApplicationBuilder builder, string meterName, params string[] scopes) =>
        builder.AddCashFlowAuthentication(meterName, allowDevelopmentHeaders: true, scopes);

    /// <param name="allowDevelopmentHeaders">
    /// false para hosts que nunca podem aceitar identidade por cabeçalhos, mesmo em Development (o gateway, que
    /// é a borda e remove X-Dev-* das requisições que encaminha).
    /// </param>
    public static WebApplicationBuilder AddCashFlowAuthentication(
        this WebApplicationBuilder builder, string meterName, bool allowDevelopmentHeaders, params string[] scopes)
    {
        var mode = builder.Configuration[ModeKey];

        switch (mode)
        {
            case DevelopmentHeadersMode when allowDevelopmentHeaders && builder.Environment.IsDevelopment():
                builder.Services.AddAuthentication(DevelopmentHeadersHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, DevelopmentHeadersHandler>(
                    DevelopmentHeadersHandler.SchemeName, configureOptions: null);
                break;
            case DevelopmentHeadersMode when !allowDevelopmentHeaders:
                throw new InvalidOperationException(
                    $"{ModeKey}={DevelopmentHeadersMode} não é aceito neste host; use {JwtBearerMode}.");
            case DevelopmentHeadersMode:
                throw new InvalidOperationException(
                    $"{ModeKey}={DevelopmentHeadersMode} só é permitido no ambiente Development (ambiente atual: {builder.Environment.EnvironmentName}).");
            case JwtBearerMode:
                JwtBearerSetup.Add(builder, JwtBearerSettings.Load(builder.Configuration));
                break;
            case null or "":
                throw new InvalidOperationException(
                    $"{ModeKey} não configurado. Valores: {DevelopmentHeadersMode} (somente Development) ou {JwtBearerMode}.");
            default:
                throw new InvalidOperationException($"{ModeKey} desconhecido: '{mode}'.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        if (scopes.Length == 0)
        {
            throw new ArgumentException("Informe ao menos um escopo.", nameof(scopes));
        }

        builder.Services.AddSingleton(sp => new TenantAccessMetrics(sp.GetRequiredService<IMeterFactory>(), meterName));
        var authorization = builder.Services.AddAuthorizationBuilder();
        foreach (var scope in scopes)
        {
            authorization.AddPolicy(scope, policy => RequireScope(policy, scope));
        }

        return builder;
    }

    // Principal não autenticado falha em RequireAuthenticatedUser (401, challenge); autenticado sem escopo
    // ou sem claims obrigatórias falha na asserção (403, forbid).
    private static void RequireScope(AuthorizationPolicyBuilder policy, string scope) =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            Caller.TryFrom(context.User, out var caller) && caller.Scopes.Contains(scope));
}
