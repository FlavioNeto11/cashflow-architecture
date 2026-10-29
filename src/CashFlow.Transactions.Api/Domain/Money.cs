using System.Globalization;

namespace CashFlow.Transactions.Api.Domain;

/// <summary>
/// Valor monetário: decimal exato, escala ≤ 2, &gt; 0, moeda BRL (P-01, P-02, P-04).
/// A escala é validada aqui, ANTES do insert: o numeric(18,2) do Postgres arredondaria em silêncio.
/// </summary>
public readonly record struct Money
{
    public const string Brl = "BRL";

    // numeric(18,2): 16 dígitos inteiros.
    public const decimal MaxAmount = 9_999_999_999_999_999.99m;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    /// <summary>Formato de fio e de evento: string decimal com 2 casas, cultura invariante ("100.00").</summary>
    public string AmountText => Amount.ToString("F2", CultureInfo.InvariantCulture);

    public static string FormatAmount(decimal amount) => amount.ToString("F2", CultureInfo.InvariantCulture);

    public static Result<Money> Create(decimal amount, string? currency)
    {
        var errors = new ValidationErrors();
        ValidateAmount(amount, errors);
        var resolvedCurrency = currency ?? Brl;
        if (!string.Equals(resolvedCurrency, Brl, StringComparison.Ordinal))
        {
            errors.Add("currency", "Somente BRL é aceito.");
        }

        return errors.Any ? Result<Money>.Fail(errors) : Result<Money>.Ok(new Money(amount, Brl));
    }

    /// <summary>
    /// Interpreta o texto do valor: apenas dígitos, sinal opcional e ponto decimal, cultura invariante.
    /// Sem separador de milhar, notação científica nem espaços.
    /// </summary>
    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0;
        return text is not null
            && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount);
    }

    private static void ValidateAmount(decimal amount, ValidationErrors errors)
    {
        if (amount <= 0)
        {
            errors.Add("amount", "O valor deve ser maior que zero.");
        }
        else if (amount > MaxAmount)
        {
            errors.Add("amount", "O valor excede o máximo suportado.");
        }

        // Escala pela representação (rejeita "100.000" mesmo valendo 100): contrato é no máximo 2 casas informadas.
        if (Scale(amount) > 2)
        {
            errors.Add("amount", "O valor aceita no máximo 2 casas decimais.");
        }
    }

    private static int Scale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;
}
