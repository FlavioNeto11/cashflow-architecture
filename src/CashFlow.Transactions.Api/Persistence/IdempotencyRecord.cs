namespace CashFlow.Transactions.Api.Persistence;

/// <summary>
/// Uma operação idempotente já confirmada: (merchant, chave) → hash do pedido + resposta armazenada (ADR-0005).
/// Sem FK para transactions: é inserida ANTES do lançamento (a PK é a barreira de concorrência).
/// </summary>
public sealed class IdempotencyRecord
{
    // Retenção de 7 dias (HIPÓTESE, target.md 9.1). F3 não limpa; a expiração só é gravada.
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public Guid MerchantId { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;

    public Guid TransactionId { get; set; }

    public short ResponseStatus { get; set; }

    public string ResponseBody { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
