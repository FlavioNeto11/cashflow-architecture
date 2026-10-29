using System.Security.Claims;

namespace CashFlow.Hosting.Security;

/// <summary>
/// Chamador resolvido de HttpContext.User (decisão C-08). A API só conhece estas claims, venham elas de
/// cabeçalhos de desenvolvimento ou do JWT validado (F5): sub, azp/client_id, merchant_id e scope.
/// Com JwtBearer, F5 precisa desligar o mapeamento de claims de entrada (MapInboundClaims = false)
/// para que "sub" continue com esse nome.
/// </summary>
public sealed record Caller(Guid MerchantId, string Sub, string ClientId, IReadOnlySet<string> Scopes)
{
    public const string SubClaim = "sub";
    public const string AuthorizedPartyClaim = "azp";
    public const string ClientIdClaim = "client_id";
    public const string MerchantClaim = "merchant_id";
    public const string ScopeClaim = "scope";

    private const int MaxIdentityLength = 255;

    /// <summary>False quando falta alguma claim obrigatória: o autor é NOT NULL e o tenant é a base da autorização.</summary>
    public static bool TryFrom(ClaimsPrincipal user, out Caller caller)
    {
        caller = null!;

        var sub = user.FindFirstValue(SubClaim);
        var clientId = user.FindFirstValue(AuthorizedPartyClaim) ?? user.FindFirstValue(ClientIdClaim);
        if (!IsValidIdentity(sub) || !IsValidIdentity(clientId)
            || !Guid.TryParse(user.FindFirstValue(MerchantClaim), out var merchantId))
        {
            return false;
        }

        var scopes = (user.FindFirstValue(ScopeClaim) ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        caller = new Caller(merchantId, sub!, clientId!, scopes);
        return true;
    }

    private static bool IsValidIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaxIdentityLength;
}
