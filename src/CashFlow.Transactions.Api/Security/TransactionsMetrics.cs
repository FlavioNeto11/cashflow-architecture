namespace CashFlow.Transactions.Api.Security;

/// <summary>Meter do serviço (publisher do outbox e negações cross-tenant do TenantFilter).</summary>
public static class TransactionsMetrics
{
    public const string MeterName = "CashFlow.Transactions";
}
