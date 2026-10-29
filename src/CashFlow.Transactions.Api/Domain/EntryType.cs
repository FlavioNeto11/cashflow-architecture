namespace CashFlow.Transactions.Api.Domain;

/// <summary>
/// Débito e crédito sempre carregam valor positivo; o sinal vem do tipo (P-04). Os números são o contrato
/// de persistência (smallint em transactions.entry_type, CHECK IN (1,2)).
/// </summary>
public enum EntryType : short
{
    Credit = 1,
    Debit = 2,
}

public static class EntryTypeExtensions
{
    public static string ToWire(this EntryType type) => type switch
    {
        EntryType.Credit => "CREDIT",
        EntryType.Debit => "DEBIT",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParseWire(string? value, out EntryType type)
    {
        // Ordinal e exato: "credit" ou " CREDIT" não são aceitos (contrato fechado, sem normalização silenciosa).
        switch (value)
        {
            case "CREDIT":
                type = EntryType.Credit;
                return true;
            case "DEBIT":
                type = EntryType.Debit;
                return true;
            default:
                type = default;
                return false;
        }
    }

    public static EntryType Opposite(this EntryType type) => type == EntryType.Credit ? EntryType.Debit : EntryType.Credit;
}
