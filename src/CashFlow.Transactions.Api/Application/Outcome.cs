namespace CashFlow.Transactions.Api.Application;

/// <summary>Resultado de um caso de uso de escrita; o endpoint traduz cada caso em HTTP.</summary>
public abstract record Outcome
{
    /// <summary>Resposta armazenada: 201 recém-criado ou replay (mesma chave e mesmo payload).</summary>
    public sealed record Stored(int Status, string Body, Guid TransactionId, bool Replayed) : Outcome;

    /// <summary>Validação de domínio falhou (422 com erros por campo).</summary>
    public sealed record Invalid(IDictionary<string, string[]> Errors) : Outcome;

    /// <summary>Mesma Idempotency-Key com payload diferente (422, sem efeito).</summary>
    public sealed record KeyReusedWithDifferentPayload : Outcome;

    /// <summary>Lançamento original inexistente ou de outro merchant (404).</summary>
    public sealed record NotFound : Outcome;

    /// <summary>Original já estornado (409, índice único parcial de reversal_of).</summary>
    public sealed record AlreadyReversed : Outcome;

    /// <summary>Mesma chave em processamento concorrente além do lock_timeout (409 + Retry-After).</summary>
    public sealed record Busy : Outcome;
}
