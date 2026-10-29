using Yarp.ReverseProxy.Configuration;

namespace CashFlow.Gateway;

/// <summary>
/// Rotas e clusters do YARP, em código (não em appsettings) para que a política de autorização, o rate limit, o tempo
/// limite e a allowlist de cabeçalhos de cada rota fiquem explícitos e revisáveis (security.md 5). Só o que está aqui
/// é encaminhado: /health/* das APIs e qualquer outro caminho respondem 404 no gateway.
/// </summary>
internal static class ProxyRoutes
{
    public const string TransactionsCluster = "transactions-api";
    public const string ConsolidationCluster = "consolidation-api";

    public const string TransactionsWriteRoute = "transactions-write";
    public const string TransactionsReadRoute = "transactions-read";
    public const string ConsolidationReadRoute = "consolidation-read";

    public const string TransactionsWriteScope = "transactions.write";
    public const string TransactionsReadScope = "transactions.read";
    public const string ConsolidationReadScope = "consolidation.read";

    /// <summary>
    /// Cabeçalhos de requisição encaminhados às APIs; tudo o mais (inclusive X-Dev-*, Cookie, X-Forwarded-* do cliente)
    /// é descartado. traceparent/tracestate/baggage: correlação W3C (ADR-0010). Content-Length na lista: sem ele o YARP
    /// reenvia o corpo em chunked e a API perde o comprimento declarado (verificado por HeaderAllowlistTests).
    /// </summary>
    public static readonly string[] ForwardedRequestHeaders =
    [
        "Authorization", "Idempotency-Key", "Content-Type", "Content-Length", "Accept", "traceparent", "tracestate", "baggage",
    ];

    private const string MerchantPrefix = "/api/v1/merchants/{merchantId:guid}";

    public static IReadOnlyList<RouteConfig> Routes(LimitSettings limits)
    {
        var get = TimeSpan.FromSeconds(limits.GetTimeoutSeconds);
        var post = TimeSpan.FromSeconds(limits.PostTimeoutSeconds);
        return
        [
            Route(TransactionsWriteRoute, TransactionsCluster, $"{MerchantPrefix}/transactions", ["POST"], TransactionsWriteScope, post, order: 1),
            Route($"{TransactionsWriteRoute}-reversal", TransactionsCluster, $"{MerchantPrefix}/transactions/{{id:guid}}/reversal", ["POST"], TransactionsWriteScope, post, order: 1, rateLimiterPolicy: TransactionsWriteRoute),
            Route(TransactionsReadRoute, TransactionsCluster, $"{MerchantPrefix}/transactions", ["GET"], TransactionsReadScope, get, order: 2),
            Route($"{TransactionsReadRoute}-single", TransactionsCluster, $"{MerchantPrefix}/transactions/{{id:guid}}", ["GET"], TransactionsReadScope, get, order: 2, rateLimiterPolicy: TransactionsReadRoute),
            Route(ConsolidationReadRoute, ConsolidationCluster, $"{MerchantPrefix}/consolidations/daily", ["GET"], ConsolidationReadScope, get, order: 3),
        ];
    }

    public static IReadOnlyList<ClusterConfig> Clusters(UpstreamSettings upstream) =>
    [
        Cluster(TransactionsCluster, upstream.TransactionsApi, upstream.MaxConnectionsPerServer),
        Cluster(ConsolidationCluster, upstream.ConsolidationApi, upstream.MaxConnectionsPerServer),
    ];

    private static RouteConfig Route(
        string id, string cluster, string path, string[] methods, string scope, TimeSpan timeout, int order, string? rateLimiterPolicy = null)
    {
        // Nenhum cabeçalho do cliente passa por padrão (RequestHeadersCopy=false); só a allowlist. O Host passa a ser o
        // do destino e o gateway não acrescenta X-Forwarded-* (as APIs não os usam e não devem confiar neles).
        return new RouteConfig
        {
            RouteId = id,
            ClusterId = cluster,
            Order = order,
            Match = new RouteMatch { Path = path, Methods = methods },
            AuthorizationPolicy = scope,
            RateLimiterPolicy = rateLimiterPolicy ?? id,
            Timeout = timeout,
            Transforms =
            [
                new Dictionary<string, string> { ["RequestHeadersCopy"] = "false" },
                new Dictionary<string, string> { ["RequestHeadersAllowed"] = string.Join(';', ForwardedRequestHeaders) },
                new Dictionary<string, string> { ["RequestHeaderOriginalHost"] = "false" },
                new Dictionary<string, string> { ["X-Forwarded"] = "Off" },
                // A API interna não se identifica para fora. When=Always: o padrão do YARP (Success) só remove em respostas
                // < 400, e "Server: Kestrel" vazava nos 404 proxiados (auditoria F5, P2-1).
                new Dictionary<string, string> { ["ResponseHeaderRemove"] = "Server", ["When"] = "Always" },
            ],
        };
    }

    private static ClusterConfig Cluster(string id, string address, int maxConnectionsPerServer) => new()
    {
        ClusterId = id,
        Destinations = new Dictionary<string, DestinationConfig> { ["primary"] = new() { Address = address } },
        HttpClient = new HttpClientConfig
        {
            MaxConnectionsPerServer = maxConnectionsPerServer,
            DangerousAcceptAnyServerCertificate = false,
        },
    };
}
