using Npgsql;

namespace CashFlow.Consolidation.Api;

/// <summary>Linha merchant/data lida da projeção; Closing só quando pedido (include=closingBalance).</summary>
public sealed record DailyReading(decimal Credits, decimal Debits, int EntriesCount, DateTime? AsOf, decimal? Closing);

/// <summary>
/// Leitura da projeção como cons_ro (somente SELECT; statement_timeout do papel = 2 s). Npgsql direto: é uma consulta
/// de uma linha mais a soma do fechamento, sem modelo a rastrear.
/// </summary>
public sealed class ConsolidationQueries(NpgsqlDataSource dataSource)
{
    // Um único comando = um único snapshot (READ COMMITTED): o dia e o fechamento nunca vêm de instantes diferentes.
    // LEFT JOIN a partir de uma linha fixa: data sem movimento devolve nulos (zeros no contrato), não "nenhuma linha".
    // closingBalance = Σ dailyNet(d <= date), calculado na leitura (ADR-0004), com abertura assumida 0 na primeira data.
    private const string Sql = """
        SELECT d.credits, d.debits, d.entries_count, d.last_applied_at,
               CASE WHEN @closing THEN (
                   SELECT COALESCE(sum(c.credits - c.debits), 0)
                   FROM daily_consolidations c
                   WHERE c.merchant_id = @merchant AND c.effective_date <= @date)
               END
        FROM (SELECT 1) AS one
        LEFT JOIN daily_consolidations d ON d.merchant_id = @merchant AND d.effective_date = @date
        """;

    public async Task<DailyReading> DailyAsync(Guid merchantId, DateOnly date, bool includeClosing, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(Sql);
        command.Parameters.AddWithValue("merchant", merchantId);
        command.Parameters.AddWithValue("date", date);
        command.Parameters.AddWithValue("closing", includeClosing);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        decimal? closing = includeClosing ? reader.GetDecimal(4) : null;
        return reader.IsDBNull(0)
            ? new DailyReading(0m, 0m, 0, null, closing)
            : new DailyReading(
                reader.GetDecimal(0),
                reader.GetDecimal(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTime>(3),
                closing);
    }
}
