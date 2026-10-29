using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CashFlow.Hosting.Security;

/// <summary>
/// Esquema SOMENTE de desenvolvimento: monta o principal a partir dos cabeçalhos X-Dev-Sub, X-Dev-Client-Id,
/// X-Dev-Merchant-Id e X-Dev-Scope. Não valida nada: quem controla os cabeçalhos controla a identidade.
/// Por isso <see cref="AuthenticationSetup"/> só o registra com Authentication:Mode=DevelopmentHeaders em
/// ambiente Development; em F5 o esquema JwtBearer o substitui sem tocar nas políticas.
/// </summary>
internal sealed class DevelopmentHeadersHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevelopmentHeaders";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sub = Request.Headers["X-Dev-Sub"].ToString();
        if (string.IsNullOrWhiteSpace(sub))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(Caller.SubClaim, sub) };
        AddIfPresent(claims, Caller.AuthorizedPartyClaim, "X-Dev-Client-Id");
        AddIfPresent(claims, Caller.MerchantClaim, "X-Dev-Merchant-Id");
        AddIfPresent(claims, Caller.ScopeClaim, "X-Dev-Scope");

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        SecurityProblems.Unauthorized().ExecuteAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        SecurityProblems.Forbidden().ExecuteAsync(Context);

    private void AddIfPresent(List<Claim> claims, string claimType, string header)
    {
        var value = Request.Headers[header].ToString();
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new Claim(claimType, value));
        }
    }
}
