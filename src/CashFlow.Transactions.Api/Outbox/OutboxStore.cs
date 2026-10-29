using Npgsql;
using NpgsqlTypes;

namespace CashFlow.Transactions.Api.Outbox;

/// <summary>Linha reclamada do outbox.</summary>
public sealed record ClaimedMessage(
    Guid Id, Guid MerchantId, string Type, int Version, string Payload, string Headers, DateTime OccurredAt, int Attempts);

/// <summary>
/// Lote reclamado. LeaseRemaining = locked_until - now() na mesma instrução, ou seja, o lease pedido; o publisher mede o
/// consumo do lease no relógio local a partir de antes do claim (estimativa conservadora).
/// </summary>
public sealed record ClaimedBatch(IReadOnlyList<ClaimedMessage> Messages, TimeSpan LeaseRemaining);

/// <summary>
/// SQL explícito do publisher (ADR-0002 item 1), no pool próprio "Publisher" (C-06), sem EF e sem retry automático.
/// Cada método é uma transação curta: A = reclamar lease; B = marcar publicado; falha = agendar nova tentativa.
/// tx_app só pode atualizar as colunas de entrega (migração OutboxColumnGrants).
/// </summary>
public sealed class OutboxStore(NpgsqlDataSource dataSource)
{
    private const string ClaimSql = """
        UPDATE outbox_messages o
           SET locked_until = now() + make_interval(secs => @lease), locked_by = @instance
         WHERE o.id IN (
               SELECT id
                 FROM outbox_messages
                WHERE published_at IS NULL
                  AND next_attempt_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                ORDER BY occurred_at
                LIMIT @batch
                  FOR UPDATE SKIP LOCKED)
        RETURNING o.id, o.merchant_id, o.type, o.version, o.payload::text, o.headers::text, o.occurred_at, o.attempts,
                  EXTRACT(EPOCH FROM (o.locked_until - now()))::float8
        """;

    public async Task<ClaimedBatch> ClaimAsync(string instance, int batchSize, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ClaimSql);
        command.Parameters.Add(new NpgsqlParameter("lease", NpgsqlDbType.Double) { Value = lease.TotalSeconds });
        command.Parameters.Add(new NpgsqlParameter("instance", NpgsqlDbType.Varchar) { Value = instance });
        command.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = batchSize });

        var messages = new List<ClaimedMessage>();
        var remaining = lease;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ClaimedMessage(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                reader.GetString(5), reader.GetDateTime(6), reader.GetInt32(7)));
            var seconds = TimeSpan.FromSeconds(reader.GetDouble(8));
            remaining = seconds < remaining ? seconds : remaining;
        }

        // RETURNING não garante ordem: a publicação segue occurred_at (a projeção é aditiva; a ordem só reduz o atraso).
        messages.Sort((a, b) => a.OccurredAt.CompareTo(b.OccurredAt));
        return new ClaimedBatch(messages, remaining);
    }

    /// <summary>Transação B. Vale mesmo se o lease expirou e outra instância reclamou: o evento FOI publicado.</summary>
    public async Task MarkPublishedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages
               SET published_at = now(), locked_until = NULL, locked_by = NULL
             WHERE id = @id AND published_at IS NULL
            """);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Falha: nova tentativa após o backoff. Só altera a linha se o lease ainda for desta instância.</summary>
    public async Task MarkFailedAsync(Guid id, string instance, TimeSpan retryAfter, string error, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages
               SET attempts = attempts + 1,
                   next_attempt_at = now() + make_interval(secs => @delay),
                   last_error = @error,
                   locked_until = NULL,
                   locked_by = NULL
             WHERE id = @id AND published_at IS NULL AND locked_by = @instance
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("delay", NpgsqlDbType.Double) { Value = retryAfter.TotalSeconds });
        command.Parameters.AddWithValue("error", error);
        command.Parameters.Add(new NpgsqlParameter("instance", NpgsqlDbType.Varchar) { Value = instance });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Devolve o lease sem contar tentativa (lote interrompido por fim de lease, queda do canal ou parada).</summary>
    public async Task ReleaseAsync(IReadOnlyCollection<Guid> ids, string instance, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var command = dataSource.CreateCommand("""
            UPDATE outbox_messages
               SET locked_until = NULL, locked_by = NULL
             WHERE id = ANY(@ids) AND published_at IS NULL AND locked_by = @instance
            """);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        command.Parameters.Add(new NpgsqlParameter("instance", NpgsqlDbType.Varchar) { Value = instance });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Pendentes e idade do mais antigo (segundos, pelo relógio do banco); usa o índice parcial de pendentes.</summary>
    public async Task<(long Pending, double OldestAgeSeconds)> BacklogAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT count(*), COALESCE(EXTRACT(EPOCH FROM (now() - min(occurred_at))), 0)::float8
              FROM outbox_messages
             WHERE published_at IS NULL
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetDouble(1));
    }
}
