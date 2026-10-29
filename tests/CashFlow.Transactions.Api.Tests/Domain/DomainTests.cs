using CashFlow.Transactions.Api.Domain;

namespace CashFlow.Transactions.Api.Tests.Domain;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("100.00", 100.00)]
    [InlineData("35.20", 35.20)]
    [InlineData("0.01", 0.01)]
    [InlineData("5", 5)]
    [InlineData("100.5", 100.5)]
    public void Create_ValidAmount_Succeeds(string text, double expected)
    {
        Assert.True(Money.TryParseAmount(text, out var amount));

        var result = Money.Create(amount, currency: null);

        Assert.True(result.IsSuccess);
        Assert.Equal((decimal)expected, result.Value.Amount);
        Assert.Equal("BRL", result.Value.Currency);
    }

    [Theory]
    [InlineData("100.001")]
    [InlineData("100.000")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-1")]
    [InlineData("-0.01")]
    [InlineData("10000000000000000.00")]
    public void Create_InvalidAmount_FailsOnAmount(string text)
    {
        Assert.True(Money.TryParseAmount(text, out var amount));

        var result = Money.Create(amount, "BRL");

        Assert.False(result.IsSuccess);
        Assert.Contains("amount", result.Errors!.Keys);
    }

    [Theory]
    [InlineData("1e3")]
    [InlineData("1,5")]
    [InlineData(" 1.00")]
    [InlineData("1_000")]
    [InlineData("abc")]
    [InlineData("")]
    public void TryParseAmount_NonDecimalText_IsRejected(string text) =>
        Assert.False(Money.TryParseAmount(text, out _));

    [Fact]
    public void Create_NonBrlCurrency_FailsOnCurrency()
    {
        var result = Money.Create(10m, "USD");

        Assert.False(result.IsSuccess);
        Assert.Contains("currency", result.Errors!.Keys);
    }

    [Theory]
    [InlineData(100, "100.00")]
    [InlineData(35.2, "35.20")]
    [InlineData(0.5, "0.50")]
    public void FormatAmount_AlwaysTwoDecimals_InvariantCulture(double value, string expected) =>
        Assert.Equal(expected, Money.FormatAmount((decimal)value));
}

public sealed class TransactionTests
{
    private static readonly Guid Merchant = Guid.CreateVersion7();
    private static readonly Author Author = new("sub-1", "client-1");

    // 2026-10-29T02:30Z == 2026-10-28 23:30 em America/Sao_Paulo (UTC-3): a data de competência é a de Brasília.
    private static readonly DateTime NightUtc = new(2026, 10, 29, 2, 30, 0, DateTimeKind.Utc);

    private static Money Brl(decimal amount) => Money.Create(amount, "BRL").Value;

    [Fact]
    public void SaoPauloZone_ResolvesOnThisHost() =>
        Assert.Equal(new DateOnly(2026, 10, 28), BusinessCalendar.DateOf(NightUtc));

    [Fact]
    public void Register_WithoutEffectiveDate_DerivesFromSaoPauloNotUtc()
    {
        var result = Transaction.Register(Merchant, EntryType.Credit, Brl(100m), null, null, Author, NightUtc);

        Assert.True(result.IsSuccess);
        Assert.Equal(new DateOnly(2026, 10, 28), result.Value.EffectiveDate);
        Assert.Equal(NightUtc, result.Value.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, result.Value.OccurredAt.Kind);
    }

    [Fact]
    public void Register_FutureEffectiveDate_IsRejected()
    {
        // Em UTC já é 29/10, mas em São Paulo ainda é 28/10: 29/10 é futura.
        var result = Transaction.Register(Merchant, EntryType.Credit, Brl(1m), new DateOnly(2026, 10, 29), null, Author, NightUtc);

        Assert.False(result.IsSuccess);
        Assert.Contains("effectiveDate", result.Errors!.Keys);
    }

    [Fact]
    public void Register_TodayAndPastEffectiveDates_AreAccepted()
    {
        var today = Transaction.Register(Merchant, EntryType.Debit, Brl(1m), new DateOnly(2026, 10, 28), null, Author, NightUtc);
        var past = Transaction.Register(Merchant, EntryType.Credit, Brl(5m), new DateOnly(2020, 1, 1), null, Author, NightUtc);

        Assert.True(today.IsSuccess);
        Assert.True(past.IsSuccess);
        Assert.Equal(new DateOnly(2020, 1, 1), past.Value.EffectiveDate);
    }

    [Fact]
    public void Register_DescriptionOver140_IsRejected()
    {
        var result = Transaction.Register(Merchant, EntryType.Credit, Brl(1m), null, new string('x', 141), Author, NightUtc);

        Assert.False(result.IsSuccess);
        Assert.Contains("description", result.Errors!.Keys);
    }

    // Categoria Unicode Cc: U+0000-U+001F e U+007F-U+009F. NUL é rejeitado pelo PostgreSQL em text (viraria 500).
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0007)]
    [InlineData(0x001F)]
    [InlineData(0x007F)]
    [InlineData(0x0080)]
    [InlineData(0x009F)]
    public void Register_DescriptionWithControlCharacter_IsRejected(int codePoint)
    {
        var text = $"ok{(char)codePoint}fim";

        var result = Transaction.Register(Merchant, EntryType.Credit, Brl(1m), null, text, Author, NightUtc);

        Assert.False(result.IsSuccess);
        Assert.Contains("description", result.Errors!.Keys);
    }

    [Fact]
    public void Reverse_ReasonWithControlCharacter_IsRejected()
    {
        var tx = Transaction.Register(Merchant, EntryType.Debit, Brl(1m), null, null, Author, NightUtc).Value;

        var result = tx.Reverse("ok\0fim", Author, NightUtc);

        Assert.False(result.IsSuccess);
        Assert.Contains("reason", result.Errors!.Keys);
    }

    [Fact]
    public void Register_AssignsServerSideV7IdAndAuthor()
    {
        var tx = Transaction.Register(Merchant, EntryType.Credit, Brl(1m), null, null, Author, NightUtc).Value;

        Assert.Equal(7, tx.Id.Version);
        Assert.Equal("sub-1", tx.CreatedBySub);
        Assert.Equal("client-1", tx.CreatedByClientId);
        Assert.Null(tx.ReversalOf);
    }

    [Fact]
    public void Register_TruncatesToMicroseconds_SoTheStoredValueEqualsTheReturnedOne()
    {
        var withTicks = NightUtc.AddTicks(9);

        var tx = Transaction.Register(Merchant, EntryType.Credit, Brl(1m), null, null, Author, withTicks).Value;

        Assert.Equal(NightUtc, tx.OccurredAt);
    }

    [Theory]
    [InlineData(EntryType.Debit, EntryType.Credit)]
    [InlineData(EntryType.Credit, EntryType.Debit)]
    public void Reverse_CreatesOppositeTypeSameAmountAndEffectiveDate(EntryType original, EntryType expected)
    {
        var tx = Transaction.Register(Merchant, original, Brl(35.20m), new DateOnly(2026, 10, 20), null, Author, NightUtc).Value;

        var reversal = tx.Reverse("erro de digitação", new Author("sub-2", "client-2"), NightUtc.AddHours(5));

        Assert.True(reversal.IsSuccess);
        var r = reversal.Value;
        Assert.Equal(expected, r.EntryType);
        Assert.Equal(35.20m, r.Amount);
        Assert.Equal(new DateOnly(2026, 10, 20), r.EffectiveDate);
        Assert.Equal(tx.Id, r.ReversalOf);
        Assert.Equal("erro de digitação", r.Description);
        Assert.Equal("sub-2", r.CreatedBySub);
        Assert.NotEqual(tx.Id, r.Id);
    }

    [Fact]
    public void Reverse_OfAReversal_IsDenied()
    {
        var tx = Transaction.Register(Merchant, EntryType.Debit, Brl(1m), null, null, Author, NightUtc).Value;
        var reversal = tx.Reverse(null, Author, NightUtc).Value;

        var again = reversal.Reverse(null, Author, NightUtc);

        Assert.False(again.IsSuccess);
    }

    [Fact]
    public void Reverse_ReasonOver140_IsRejected()
    {
        var tx = Transaction.Register(Merchant, EntryType.Debit, Brl(1m), null, null, Author, NightUtc).Value;

        var result = tx.Reverse(new string('x', 141), Author, NightUtc);

        Assert.False(result.IsSuccess);
        Assert.Contains("reason", result.Errors!.Keys);
    }
}
