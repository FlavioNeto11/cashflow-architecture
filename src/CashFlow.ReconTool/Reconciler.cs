using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CashFlow.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace CashFlow.ReconTool;

/// <summary>Totais de uma linha merchant/data. Crédito = entry_type 1, débito = 2 (contrato de transactions.entry_type).</summary>
public readonly record struct Totals(decimal Credits, decimal Debits, int Entries)
{
    public static Totals Of(IEnumerable<SourceEntry> entries) =>
        entries.Aggregate(default(Totals), (sum, e) => e.IsCredit
            ? sum with { Credits = sum.Credits + e.Amount, Entries = sum.Entries + 1 }
            : sum with { Debits = sum.Debits + e.Amount, Entries = sum.Entries + 1 });

    public static Totals operator +(Totals a, Totals b) => new(a.Credits + b.Credits, a.Debits + b.Debits, a.Entries + b.Entries);

    public object ToJson() => new
    {
        credits = Credits.ToString("F2", CultureInfo.InvariantCulture),
        debits = Debits.ToString("F2", CultureInfo.InvariantCulture),
        entries = Entries,
    };
}

/// <summary>Lançamento confirmado na fonte (transactions_db), lido como recon_ro.</summary>
public sealed record SourceEntry(Guid TransactionId, short EntryType, decimal Amount, DateTime OccurredAt)
{
    public bool IsCredit => EntryType == 1;
}

public enum RowOutcome
{
    /// <summary>Linha já igual à fonte: nada gravado além do updated_at do lock.</summary>
    Clean,

    /// <summary>Correção (catch_up / corrected_corruption) ou reconstrução gravada e registrada na mesma transação.</summary>
    Corrected,

    /// <summary>ROLLBACK da linha (out_of_retention, anomaly, lock_timeout, source_timeout); registrada em transação própria.</summary>
    Aborted,
}

public sealed record RowResult(DateOnly Date, RowOutcome Outcome, int Divergences, IReadOnlyDictionary<string, int> Kinds, string? Note, TimeSpan LockHeld);

public sealed record ReconReport(Guid MerchantId, DateOnly From, DateOnly To, bool Rebuild, IReadOnlyList<RowResult> Rows)
{
    public int Divergences => Rows.Sum(r => r.Divergences);

    public int Aborted => Rows.Count(r => r.Outcome == RowOutcome.Aborted);

    public TimeSpan MaxLockHeld => Rows.Count == 0 ? TimeSpan.Zero : Rows.Max(r => r.LockHeld);
}

/// <summary>
/// Reconciliação RF-10 pela opção (b) de ADR-0002 item 7, uma transação READ COMMITTED por linha (merchant, data) como
/// recon_writer: lock da linha ANTES de ler a fonte; fonte em transação curta como recon_ro (SET LOCAL
/// statement_timeout 3 s); Inbox da linha; M = S \ I inserido no Inbox e somado (catch_up); E = I \ S => anomaly;
/// absoluto Σ S só quando o resíduo persiste (corrected_corruption). A ordem de locks (linha da projeção, depois Inbox)
/// é a do worker. Modo rebuild: sob o mesmo lock apaga o Inbox da linha, reinsere S e grava Σ S.
/// </summary>
public sealed class Reconciler(NpgsqlDataSource consolidation, NpgsqlDataSource source, TextWriter output)
{
    /// <summary>= ConsolidationSchema.ReconConsumer (igualdade verificada em ReconToolTests; a ferramenta não depende do EF).</summary>
    public const string Consumer = "recon-tool";

    /// <summary>Meta de posse do lock por linha (ADR-0002 item 7): estourada, a linha é desfeita ("fonte lenta, repetir").</summary>
    public static readonly TimeSpan LockBudget = TimeSpan.FromSeconds(5);

    private const string LockTimeout = "55P03";
    private const string QueryCanceled = "57014";
    private const string NumericValueOutOfRange = "22003";

    private const string DatesSql = """
        SELECT effective_date FROM daily_consolidations WHERE merchant_id = @merchant AND effective_date BETWEEN @from AND @to
        UNION
        SELECT effective_date FROM processed_messages WHERE merchant_id = @merchant AND effective_date BETWEEN @from AND @to
        """;

    private const string SourceDatesSql = """
        SELECT DISTINCT effective_date FROM transactions WHERE merchant_id = @merchant AND effective_date BETWEEN @from AND @to
        """;

    // Passo 1: cria a linha se ausente e trava (o upsert já toma o lock; o FOR UPDATE lê sob ele).
    private const string LockSql = """
        INSERT INTO daily_consolidations (merchant_id, effective_date, credits, debits, entries_count, last_applied_at, updated_at)
        VALUES (@merchant, @date, 0, 0, 0, NULL, now())
        ON CONFLICT (merchant_id, effective_date) DO UPDATE SET updated_at = now()
        """;

    private const string RowSql = """
        SELECT credits, debits, entries_count, now() FROM daily_consolidations
        WHERE merchant_id = @merchant AND effective_date = @date FOR UPDATE
        """;

    private const string SourceSql = """
        SELECT id, entry_type, amount, occurred_at FROM transactions WHERE merchant_id = @merchant AND effective_date = @date
        """;

    private const string InboxSql = "SELECT transaction_id FROM processed_messages WHERE merchant_id = @merchant AND effective_date = @date";

    private const string InboxInsertSql = """
        INSERT INTO processed_messages (transaction_id, event_id, merchant_id, effective_date, consumer, processed_at)
        VALUES (@transaction, NULL, @merchant, @date, @consumer, now())
        ON CONFLICT (transaction_id) DO NOTHING
        RETURNING transaction_id
        """;

    private const string InboxDeleteSql = "DELETE FROM processed_messages WHERE merchant_id = @merchant AND effective_date = @date";

    private const string AddSql = """
        UPDATE daily_consolidations
           SET credits = credits + @credits, debits = debits + @debits, entries_count = entries_count + @entries,
               last_applied_at = GREATEST(last_applied_at, @occurred), updated_at = now()
         WHERE merchant_id = @merchant AND effective_date = @date
        """;

    private const string AbsoluteSql = """
        UPDATE daily_consolidations
           SET credits = @credits, debits = @debits, entries_count = @entries,
               last_applied_at = CASE WHEN @rebuild THEN @occurred ELSE GREATEST(last_applied_at, @occurred) END,
               updated_at = now()
         WHERE merchant_id = @merchant AND effective_date = @date
        """;

    public async Task<ReconReport> RunAsync(Guid merchantId, DateOnly from, DateOnly to, bool rebuild, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var dates = await DatesAsync(merchantId, from, to, cancellationToken);
        var rows = new List<RowResult>(dates.Count);
        foreach (var date in dates)
        {
            var row = await ProcessRowAsync(merchantId, date, rebuild, cancellationToken);
            rows.Add(row);
            var kinds = string.Join(' ', row.Kinds.Where(k => k.Value > 0).Select(k => $"{k.Key}={k.Value}"));
            await output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{date:yyyy-MM-dd} {row.Outcome.ToString().ToLowerInvariant()} divergencias={row.Divergences} {kinds} posse_lock_ms={row.LockHeld.TotalMilliseconds:0}{(row.Note is null ? "" : " " + row.Note)}"));
        }

        var report = new ReconReport(merchantId, from, to, rebuild, rows);
        await RecordExecutionAsync(report, started, cancellationToken);
        return report;
    }

    private async Task<List<DateOnly>> DatesAsync(Guid merchantId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        // Varredura FORA do lock (statement_timeout de 60 s do papel): datas com movimento na fonte ou com estado na projeção.
        var dates = new SortedSet<DateOnly>();
        foreach (var (dataSource, sql) in new[] { (source, SourceDatesSql), (consolidation, DatesSql) })
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("merchant", merchantId);
            command.Parameters.AddWithValue("from", from);
            command.Parameters.AddWithValue("to", to);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dates.Add(reader.GetFieldValue<DateOnly>(0));
            }
        }

        return [.. dates];
    }

    private async Task<RowResult> ProcessRowAsync(Guid merchantId, DateOnly date, bool rebuild, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        await using var connection = await consolidation.OpenConnectionAsync(cancellationToken);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        long lockedAt = 0;
        TimeSpan Held() => lockedAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(lockedAt);

        async Task<RowResult> AbortAsync(string kind, string note, object? extra = null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            var held = Held();
            var details = new { scope = "row", mode = rebuild ? "rebuild" : "reconcile", date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), aborted = kind, note, lockHeldMs = (long)held.TotalMilliseconds, extra };
            await RunsWriter.InsertAsync(connection, null, merchantId, date, date, started, 0, new Dictionary<string, int> { [kind] = 1 }, details, CancellationToken.None);
            return new RowResult(date, RowOutcome.Aborted, 0, new Dictionary<string, int> { [kind] = 1 }, note, held);
        }

        try
        {
            await ExecuteAsync(connection, transaction, LockSql, merchantId, date, cancellationToken);
            lockedAt = Stopwatch.GetTimestamp();

            Totals current;
            DateTime dbNow;
            await using (var read = Command(connection, transaction, RowSql, merchantId, date))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                current = new Totals(reader.GetDecimal(0), reader.GetDecimal(1), reader.GetInt32(2));
                dbNow = reader.GetFieldValue<DateTime>(3);
            }

            // Passo 2: só agora, com a linha travada, a fonte.
            var sourceEntries = await ReadSourceAsync(merchantId, date, cancellationToken);
            var inbox = await ReadInboxAsync(connection, transaction, merchantId, date, cancellationToken);

            var sourceIds = sourceEntries.Select(e => e.TransactionId).ToHashSet();
            var missing = sourceEntries.Where(e => !inbox.Contains(e.TransactionId)).ToList();
            var extra = inbox.Where(id => !sourceIds.Contains(id)).ToList();
            var expected = Totals.Of(sourceEntries);
            var residual = current + Totals.Of(missing) != expected;
            var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
            int divergences;

            if (!rebuild)
            {
                // Passo 4 (guarda): só importa para o que vai ser SOMADO. Um lançamento de M mais antigo que a guarda pode
                // ter sido aplicado e ter o registro do Inbox já expurgado (expurgo por processed_at aos 31 d, e
                // processed_at >= occurred_at); somá-lo contaria em dobro => abortar e usar rebuild. Com M vazio, todo id
                // de S está no Inbox (nada expurgado) e a linha antiga continua reconciliável (resíduo => absoluto).
                if (missing.Any(e => e.OccurredAt < dbNow.AddDays(-InboxRetention.GuardDays)))
                {
                    return await AbortAsync("out_of_retention", "fora da retenção do Inbox; usar rebuild");
                }

                // Passo 5: a fonte é append-only e effective_date é imutável; id no Inbox sem fonte não se corrige sozinho.
                if (extra.Count > 0)
                {
                    return await AbortAsync("anomaly", "Inbox com lançamento ausente da fonte", new { extra });
                }

                foreach (var entry in missing)
                {
                    if (!await InsertInboxAsync(connection, transaction, merchantId, date, entry.TransactionId, cancellationToken))
                    {
                        return await AbortAsync("anomaly", "lançamento já no Inbox sob outra linha", new { transactionId = entry.TransactionId });
                    }
                }

                if (missing.Count > 0)
                {
                    var add = Totals.Of(missing);
                    await UpdateAsync(connection, transaction, AddSql, merchantId, date, add, missing.Max(e => e.OccurredAt), rebuild: null, cancellationToken);
                    kinds["catch_up"] = missing.Count;
                }

                // Passo 6: só com 4 e 5 concluídos todo id de S está no Inbox; o absoluto é seguro.
                if (residual)
                {
                    await UpdateAsync(connection, transaction, AbsoluteSql, merchantId, date, expected, MaxOccurred(sourceEntries), rebuild: false, cancellationToken);
                    kinds["corrected_corruption"] = 1;
                }

                divergences = missing.Count + (residual ? 1 : 0);
            }
            else
            {
                await ExecuteAsync(connection, transaction, InboxDeleteSql, merchantId, date, cancellationToken);
                foreach (var entry in sourceEntries)
                {
                    if (!await InsertInboxAsync(connection, transaction, merchantId, date, entry.TransactionId, cancellationToken))
                    {
                        return await AbortAsync("anomaly", "lançamento já no Inbox sob outra linha", new { transactionId = entry.TransactionId });
                    }
                }

                await UpdateAsync(connection, transaction, AbsoluteSql, merchantId, date, expected, MaxOccurred(sourceEntries), rebuild: true, cancellationToken);
                kinds["rebuild"] = 1;
                divergences = missing.Count + extra.Count + (residual ? 1 : 0);
            }

            if (Held() > LockBudget)
            {
                return await AbortAsync("source_timeout", "posse do lock acima de 5 s: fonte lenta, repetir");
            }

            if (divergences > 0 || rebuild)
            {
                var details = new
                {
                    scope = "row",
                    mode = rebuild ? "rebuild" : "reconcile",
                    date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    before = current.ToJson(),
                    after = expected.ToJson(),
                    missing = missing.Select(e => e.TransactionId),
                    extra,
                    lockHeldMs = (long)Held().TotalMilliseconds,
                };
                await RunsWriter.InsertAsync(connection, transaction, merchantId, date, date, started, divergences, kinds, details, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new RowResult(date, divergences > 0 || rebuild ? RowOutcome.Corrected : RowOutcome.Clean, divergences, kinds, null, Held());
        }
        catch (PostgresException error) when (error.SqlState == LockTimeout)
        {
            return await AbortAsync("lock_timeout", "linha ativa, repetir");
        }
        catch (PostgresException error) when (error.SqlState == QueryCanceled)
        {
            return await AbortAsync("source_timeout", "fonte lenta, repetir");
        }
        catch (PostgresException error) when (error.SqlState == NumericValueOutOfRange)
        {
            // Soma não cabe na coluna da projeção: não transitório, repetir não resolve (exit 2, não 1).
            return await AbortAsync("projection_overflow", "estouro da projeção: exige rebuild/decisão sobre teto");
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    private async Task<List<SourceEntry>> ReadSourceAsync(Guid merchantId, DateOnly date, CancellationToken cancellationToken)
    {
        // Transação curta própria: SET LOCAL fora de transação não teria efeito (ADR-0002 item 7).
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using (var timeout = new NpgsqlCommand("SET LOCAL statement_timeout = '3s'", connection, transaction))
        {
            await timeout.ExecuteNonQueryAsync(cancellationToken);
        }

        var entries = new List<SourceEntry>();
        await using (var command = Command(connection, transaction, SourceSql, merchantId, date))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entries.Add(new SourceEntry(reader.GetGuid(0), reader.GetInt16(1), reader.GetDecimal(2), reader.GetFieldValue<DateTime>(3)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return entries;
    }

    private static async Task<HashSet<Guid>> ReadInboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid merchantId, DateOnly date, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        await using var command = Command(connection, transaction, InboxSql, merchantId, date);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<bool> InsertInboxAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid merchantId, DateOnly date, Guid transactionId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, InboxInsertSql, merchantId, date);
        command.Parameters.AddWithValue("transaction", transactionId);
        command.Parameters.AddWithValue("consumer", Consumer);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task UpdateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid merchantId, DateOnly date, Totals totals,
        DateTime? occurred, bool? rebuild, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql, merchantId, date);
        command.Parameters.AddWithValue("credits", totals.Credits);
        command.Parameters.AddWithValue("debits", totals.Debits);
        command.Parameters.AddWithValue("entries", totals.Entries);
        command.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.TimestampTz) { Value = (object?)occurred ?? DBNull.Value });
        if (rebuild is { } flag)
        {
            command.Parameters.AddWithValue("rebuild", flag);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid merchantId, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, sql, merchantId, date);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid merchantId, DateOnly date)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("merchant", merchantId);
        command.Parameters.AddWithValue("date", date);
        return command;
    }

    private static DateTime? MaxOccurred(List<SourceEntry> entries) => entries.Count == 0 ? null : entries.Max(e => e.OccurredAt);

    private async Task RecordExecutionAsync(ReconReport report, DateTime started, CancellationToken cancellationToken)
    {
        // Uma linha por execução e merchant (target.md 9.2), além das linhas por data corrigida/abortada.
        var kinds = report.Rows.SelectMany(r => r.Kinds)
            .GroupBy(k => k.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(k => k.Value), StringComparer.Ordinal);
        var details = new
        {
            scope = "execution",
            mode = report.Rebuild ? "rebuild" : "reconcile",
            rows = report.Rows.Count,
            aborted = report.Aborted,
            maxLockHeldMs = (long)report.MaxLockHeld.TotalMilliseconds,
        };
        await using var connection = await consolidation.OpenConnectionAsync(cancellationToken);
        await RunsWriter.InsertAsync(connection, null, report.MerchantId, report.From, report.To, started, report.Divergences, kinds, details, cancellationToken);
    }
}

/// <summary>INSERT em reconciliation_runs (recon_writer só tem SELECT/INSERT: a trilha nunca é reescrita).</summary>
public static class RunsWriter
{
    private const string Sql = """
        INSERT INTO reconciliation_runs (id, started_at, finished_at, merchant_id, from_date, to_date, divergences, kinds, details)
        VALUES (@id, @started, clock_timestamp(), @merchant, @from, @to, @divergences, @kinds, @details)
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task InsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid merchantId, DateOnly from, DateOnly to, DateTime started,
        int divergences, IReadOnlyDictionary<string, int> kinds, object details, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(Sql, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.Add(new NpgsqlParameter("started", NpgsqlDbType.TimestampTz) { Value = started });
        command.Parameters.AddWithValue("merchant", merchantId);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("divergences", divergences);
        command.Parameters.Add(new NpgsqlParameter("kinds", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(kinds, Json) });
        command.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(details, Json) });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
