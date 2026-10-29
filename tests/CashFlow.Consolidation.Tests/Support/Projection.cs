using Npgsql;

namespace CashFlow.Consolidation.Tests.Support;

public sealed record DailyRow(decimal Credits, decimal Debits, int Entries)
{
    public decimal Net => Credits - Debits;
}

/// <summary>Leituras de asserção em consolidation_db (superusuário; a aplicação nunca usa este papel).</summary>
public static class Projection
{
    public static async Task<DailyRow?> DailyAsync(DatabasesFixture databases, Guid merchant, DateOnly date)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb);
        await using var command = new NpgsqlCommand(
            "SELECT credits, debits, entries_count FROM daily_consolidations WHERE merchant_id = @m AND effective_date = @d", connection);
        command.Parameters.AddWithValue("m", merchant);
        command.Parameters.AddWithValue("d", date);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? new DailyRow(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetInt32(2)) : null;
    }

    public static async Task<long> InboxCountAsync(DatabasesFixture databases, Guid merchant) =>
        await databases.ScalarAsync<long>(
            DatabasesFixture.ConsolidationDb, "SELECT count(*) FROM processed_messages WHERE merchant_id = @m", ("m", merchant));

    /// <summary>Teto de um lançamento (Money.MaxAmount) no formato do contrato (string decimal).</summary>
    public static string MoneyCeiling => CashFlow.Transactions.Api.Domain.Money.MaxAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Maior valor que cabe HOJE em daily_consolidations.credits (10^(p-s) - 10^-s), lido do catálogo: o teste do caminho
    /// 22003 não depende da precisão fixada na migração.
    /// </summary>
    public static async Task<decimal> CreditsColumnCeilingAsync(DatabasesFixture databases)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb);
        await using var command = new NpgsqlCommand(
            """
            SELECT numeric_precision, numeric_scale FROM information_schema.columns
            WHERE table_name = 'daily_consolidations' AND column_name = 'credits'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var (precision, scale) = (reader.GetInt32(0), reader.GetInt32(1));
        var integer = 1m;
        for (var i = 0; i < precision - scale; i++)
        {
            integer *= 10m;
        }

        var unit = 1m;
        for (var i = 0; i < scale; i++)
        {
            unit /= 10m;
        }

        return integer - unit;
    }

    /// <summary>Linha da projeção gravada direto (superusuário): prepara o estado à beira do teto da coluna.</summary>
    public static async Task SeedDailyAsync(DatabasesFixture databases, Guid merchant, DateOnly date, decimal credits, int entries)
    {
        await using var connection = await databases.OpenSuperuserAsync(DatabasesFixture.ConsolidationDb);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO daily_consolidations (merchant_id, effective_date, credits, debits, entries_count, last_applied_at, updated_at)
            VALUES (@m, @d, @c, 0, @n, now(), now())
            """, connection);
        command.Parameters.AddWithValue("m", merchant);
        command.Parameters.AddWithValue("d", date);
        command.Parameters.AddWithValue("c", credits);
        command.Parameters.AddWithValue("n", entries);
        await command.ExecuteNonQueryAsync();
    }
}
