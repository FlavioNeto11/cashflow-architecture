namespace CashFlow.Messaging;

/// <summary>
/// Nomes da topologia declarada em infra/rabbitmq/definitions.json. Os serviços só fazem declaração passiva:
/// quem cria exchanges, filas e bindings é o broker ao carregar as definições.
/// </summary>
public static class Topology
{
    public const string TransactionsExchange = "cashflow.transactions";
    public const string DeadLetterExchange = "cashflow.dlx";
    public const string ConsolidationQueue = "consolidation.transaction-registered";
    public const string DeadLetterQueue = "consolidation.dlq";
    public const string TransactionRegisteredRoutingKey = "transaction.registered.v1";
}

/// <summary>Cabeçalhos AMQP do contrato (target.md 8.3) e os de diagnóstico da DLQ (ADR-0002 item 5).</summary>
public static class HeaderNames
{
    public const string MessageId = "message-id";
    public const string Traceparent = "traceparent";
    public const string MerchantId = "merchant_id";
    public const string ContentType = "content-type";

    public const string Reason = "x-reason";
    public const string ReasonDetail = "x-reason-detail";
    public const string OriginalRoutingKey = "x-original-routing-key";
    public const string Worker = "x-worker";
    public const string FailedAt = "x-failed-at";

    /// <summary>Cabeçalhos que o redrive remove antes de republicar (o evento volta "limpo" à fila principal).</summary>
    public static readonly IReadOnlySet<string> StrippedOnRedrive = new HashSet<string>(StringComparer.Ordinal)
    {
        "x-death", "x-delivery-count", "x-first-death-exchange", "x-first-death-queue", "x-first-death-reason",
        "x-last-death-exchange", "x-last-death-queue", "x-last-death-reason",
        Reason, ReasonDetail, OriginalRoutingKey, Worker, FailedAt,
    };
}
