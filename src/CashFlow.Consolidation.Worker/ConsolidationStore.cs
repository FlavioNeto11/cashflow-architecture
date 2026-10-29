using System.Data;
using CashFlow.Consolidation.Persistence;
using CashFlow.Messaging;
using Npgsql;
using NpgsqlTypes;

namespace CashFlow.Consolidation.Worker;

public enum ApplyResult
{
    /// <summary>Projeção e Inbox gravados na mesma transação (COMMIT feito).</summary>
    Applied,

    /// <summary>Inbox já tinha o lançamento: ROLLBACK, nada somado.</summary>
    Duplicate,

    /// <summary>Evento além da retenção do Inbox (29 d pelo relógio do banco): não aplicado, vai à DLQ.</summary>
    Stale,
}

/// <summary>
/// Aplicação idempotente de um TransactionRegistered (ADR-0002 itens 3 e 4), como cons_worker, SQL explícito.
/// Uma transação READ COMMITTED: (1) upsert aditivo da linha (merchant, data), que trava a linha; (2) INSERT no
/// Inbox ON CONFLICT DO NOTHING RETURNING. Zero linhas no Inbox => ROLLBACK (a soma de (1) é desfeita).
/// Ordem de travas fixa (linha da projeção, depois Inbox), a mesma do recon-tool, para não haver deadlock entre eles.
/// </summary>
public sealed class ConsolidationStore(NpgsqlDataSource dataSource)
{
    /// <summary>Guarda de obsolescência compartilhada (InboxRetention): Inbox 30 d, purga em 31 d, guarda 29 d.</summary>
    public const int RetentionGuardDays = InboxRetention.GuardDays;

    private const string StaleSql = "SELECT @occurred < now() - make_interval(days => @days)";

    private const string UpsertSql = """
        INSERT INTO daily_consolidations AS d
               (merchant_id, effective_date, credits, debits, entries_count, last_applied_at, updated_at)
        VALUES (@merchant, @date, @credit, @debit, 1, @occurred, now())
        ON CONFLICT (merchant_id, effective_date) DO UPDATE
           SET credits = d.credits + EXCLUDED.credits,
               debits = d.debits + EXCLUDED.debits,
               entries_count = d.entries_count + 1,
               last_applied_at = GREATEST(d.last_applied_at, EXCLUDED.last_applied_at),
               updated_at = now()
        """;

    private const string InboxSql = """
        INSERT INTO processed_messages (transaction_id, event_id, merchant_id, effective_date, consumer, processed_at)
        VALUES (@transaction, @event, @merchant, @date, @consumer, now())
        ON CONFLICT (transaction_id) DO NOTHING
        RETURNING transaction_id
        """;

    public async Task<ApplyResult> ApplyAsync(TransactionRegisteredEvent message, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using (var stale = new NpgsqlCommand(StaleSql, connection, transaction))
        {
            stale.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.TimestampTz) { Value = message.OccurredAt });
            stale.Parameters.Add(new NpgsqlParameter("days", NpgsqlDbType.Integer) { Value = RetentionGuardDays });
            if ((bool)(await stale.ExecuteScalarAsync(cancellationToken))!)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ApplyResult.Stale;
            }
        }

        await using (var upsert = new NpgsqlCommand(UpsertSql, connection, transaction))
        {
            upsert.Parameters.Add(new NpgsqlParameter("merchant", NpgsqlDbType.Uuid) { Value = message.MerchantId });
            upsert.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = message.EffectiveDate });
            upsert.Parameters.Add(new NpgsqlParameter("credit", NpgsqlDbType.Numeric) { Value = message.IsCredit ? message.Amount : 0m });
            upsert.Parameters.Add(new NpgsqlParameter("debit", NpgsqlDbType.Numeric) { Value = message.IsCredit ? 0m : message.Amount });
            upsert.Parameters.Add(new NpgsqlParameter("occurred", NpgsqlDbType.TimestampTz) { Value = message.OccurredAt });
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var inbox = new NpgsqlCommand(InboxSql, connection, transaction))
        {
            inbox.Parameters.Add(new NpgsqlParameter("transaction", NpgsqlDbType.Uuid) { Value = message.TransactionId });
            inbox.Parameters.Add(new NpgsqlParameter("event", NpgsqlDbType.Uuid) { Value = message.EventId });
            inbox.Parameters.Add(new NpgsqlParameter("merchant", NpgsqlDbType.Uuid) { Value = message.MerchantId });
            inbox.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = message.EffectiveDate });
            inbox.Parameters.Add(new NpgsqlParameter("consumer", NpgsqlDbType.Varchar) { Value = ConsolidationSchema.WorkerConsumer });
            if (await inbox.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return ApplyResult.Duplicate;
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return ApplyResult.Applied;
    }

    /// <summary>Sonda usada durante a pausa: o banco voltou a aceitar conexões e consultas?</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
