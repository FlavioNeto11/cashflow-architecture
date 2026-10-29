namespace CashFlow.Transactions.Api.Persistence;

/// <summary>
/// Nomes de constraints e índices. São parte do contrato: o código distingue os 23505 pelo
/// <c>PostgresException.ConstraintName</c> (idempotência → replay/422; estorno duplicado → 409).
/// </summary>
public static class DbNames
{
    public const string IdempotencyPk = "pk_idempotency_keys";
    public const string ReversalOfUnique = "ux_transactions_reversal_of";
    public const string OutboxEventUnique = "ux_outbox_messages_aggregate_type_version";
}
