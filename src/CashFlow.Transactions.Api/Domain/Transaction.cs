namespace CashFlow.Transactions.Api.Domain;

/// <summary>
/// Lançamento financeiro imutável (P-06): nunca é editado nem excluído; correção é outro lançamento
/// (<see cref="Reverse"/>). Criado só pelas fábricas, que validam as invariantes. O id nasce no servidor
/// (UUID v7) antes do commit, para que a resposta possa ser gravada junto da chave de idempotência.
/// </summary>
public sealed class Transaction
{
    public const int MaxDescriptionLength = 140;

    // Para o EF Core materializar linhas já validadas.
    private Transaction()
    {
    }

    public Guid Id { get; private set; }

    public Guid MerchantId { get; private set; }

    public EntryType EntryType { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = Money.Brl;

    public DateOnly EffectiveDate { get; private set; }

    public DateTime OccurredAt { get; private set; }

    public string? Description { get; private set; }

    public Guid? ReversalOf { get; private set; }

    public string CreatedBySub { get; private set; } = string.Empty;

    public string CreatedByClientId { get; private set; } = string.Empty;

    public DateTime CreatedAt { get; private set; }

    /// <param name="requestedEffectiveDate">null: derivada de <paramref name="nowUtc"/> em America/Sao_Paulo.
    /// Retroativa é aceita sem janela (ADR-0007); futura é rejeitada.</param>
    public static Result<Transaction> Register(
        Guid merchantId,
        EntryType entryType,
        Money money,
        DateOnly? requestedEffectiveDate,
        string? description,
        Author author,
        DateTime nowUtc)
    {
        var occurredAt = TruncateToMicroseconds(nowUtc);
        var today = BusinessCalendar.DateOf(occurredAt);

        var errors = new ValidationErrors();
        if (requestedEffectiveDate is { } requested && requested > today)
        {
            errors.Add("effectiveDate", "A data de competência não pode ser futura.");
        }

        var normalizedDescription = NormalizeDescription(description, "description", errors);
        if (errors.Any)
        {
            return Result<Transaction>.Fail(errors);
        }

        return Result<Transaction>.Ok(Create(
            merchantId, entryType, money.Amount, requestedEffectiveDate ?? today, occurredAt, normalizedDescription, null, author));
    }

    /// <summary>
    /// Estorno: tipo inverso, mesmo valor, mesma data de competência (P-09 recomendada), reversal_of = id.
    /// Estorno de estorno é negado. A unicidade (um estorno por original) é garantida pelo índice único
    /// parcial do banco, não por leitura prévia.
    /// </summary>
    public Result<Transaction> Reverse(string? reason, Author author, DateTime nowUtc)
    {
        var errors = new ValidationErrors();
        if (ReversalOf is not null)
        {
            errors.Add("id", "Um estorno não pode ser estornado.");
        }

        var normalizedReason = NormalizeDescription(reason, "reason", errors);
        if (errors.Any)
        {
            return Result<Transaction>.Fail(errors);
        }

        return Result<Transaction>.Ok(Create(
            MerchantId, EntryType.Opposite(), Amount, EffectiveDate, TruncateToMicroseconds(nowUtc), normalizedReason, Id, author));
    }

    private static Transaction Create(
        Guid merchantId, EntryType entryType, decimal amount, DateOnly effectiveDate,
        DateTime occurredAt, string? description, Guid? reversalOf, Author author) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            EntryType = entryType,
            Amount = amount,
            Currency = Money.Brl,
            EffectiveDate = effectiveDate,
            OccurredAt = occurredAt,
            Description = description,
            ReversalOf = reversalOf,
            CreatedBySub = author.Sub,
            CreatedByClientId = author.ClientId,
            CreatedAt = occurredAt,
        };

    private static string? NormalizeDescription(string? text, string field, ValidationErrors errors)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.Length > MaxDescriptionLength)
        {
            errors.Add(field, $"No máximo {MaxDescriptionLength} caracteres.");
        }

        // Cc (U+0000-U+001F, U+007F-U+009F). O PostgreSQL rejeita NUL em text; sem isto o INSERT falharia com 500.
        if (text.Any(char.IsControl))
        {
            errors.Add(field, "Não pode conter caracteres de controle.");
        }

        return text;
    }

    // timestamptz guarda microssegundos; truncar aqui faz o valor devolvido no 201 ser igual ao lido depois.
    private static DateTime TruncateToMicroseconds(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % 10), DateTimeKind.Utc);
}
