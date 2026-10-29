using System.Text.Json;
using System.Text.Json.Serialization;
using CashFlow.Transactions.Api.Domain;

namespace CashFlow.Transactions.Api.Http;

/// <summary>Opções de JSON compartilhadas: camelCase, datas ISO-8601, nomes de propriedade exatos na entrada.</summary>
public static class Json
{
    /// <summary>Saída (respostas e payload do outbox).</summary>
    public static readonly JsonSerializerOptions Output = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Entrada: membro desconhecido ou duplicado é erro (mass assignment: merchantId, id, occurredAt etc. no corpo
    /// nunca são aceitos) e nomes são case-sensitive, para que a forma canônica do hash seja a mesma que o parser lê.
    /// </summary>
    public static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };
}

/// <summary>
/// Corpo de POST /transactions. Os campos são JsonElement de propósito: o tipo do valor JSON é validado à mão,
/// porque número JSON em "amount" (ou tipo errado em qualquer campo) é erro de validação (422), não de
/// sintaxe (400). "amount" é STRING decimal ("100.00"): número JSON passa por double em muitos clientes e perderia precisão.
/// </summary>
public sealed record CreateTransactionRequest(
    JsonElement? Type, JsonElement? Amount, JsonElement? Currency, JsonElement? EffectiveDate, JsonElement? Description);

/// <summary>Corpo (opcional) de POST /transactions/{id}/reversal.</summary>
public sealed record ReverseTransactionRequest(JsonElement? Reason);

/// <summary>Representação do lançamento no 201 (campos de target.md 8.1; é o que fica gravado para replay).</summary>
public sealed record TransactionResponse(
    Guid Id,
    Guid MerchantId,
    string Type,
    string Amount,
    string Currency,
    DateOnly EffectiveDate,
    DateTime OccurredAt,
    Guid? ReversalOf,
    DateTime CreatedAt)
{
    public static TransactionResponse From(Transaction t) => new(
        t.Id, t.MerchantId, t.EntryType.ToWire(), Money.FormatAmount(t.Amount), t.Currency,
        t.EffectiveDate, t.OccurredAt, t.ReversalOf, t.CreatedAt);
}

/// <summary>
/// Representação nas consultas: os campos do 201 mais "description" e "reversedBy" (derivado na leitura, sem
/// coluna: id do estorno, se houver; ADR-0007). São acréscimos; nenhum campo de 8.1 muda.
/// </summary>
public sealed record TransactionDetail(
    Guid Id,
    Guid MerchantId,
    string Type,
    string Amount,
    string Currency,
    DateOnly EffectiveDate,
    DateTime OccurredAt,
    Guid? ReversalOf,
    DateTime CreatedAt,
    string? Description,
    Guid? ReversedBy)
{
    public static TransactionDetail From(Transaction t, Guid? reversedBy) => new(
        t.Id, t.MerchantId, t.EntryType.ToWire(), Money.FormatAmount(t.Amount), t.Currency,
        t.EffectiveDate, t.OccurredAt, t.ReversalOf, t.CreatedAt, t.Description, reversedBy);
}

public sealed record TransactionPage(IReadOnlyList<TransactionDetail> Items, int Page, int PageSize, int Total);
