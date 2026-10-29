using System.Diagnostics;
using System.Text.Json;
using CashFlow.Hosting.Security;
using CashFlow.Transactions.Api.Domain;
using CashFlow.Transactions.Api.Http;
using CashFlow.Transactions.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CashFlow.Transactions.Api.Application;

/// <summary>
/// Escrita de lançamentos e estornos. Ponto de confirmação (target.md 7.3): numa ÚNICA transação de banco,
/// idempotency_keys → transactions → outbox_messages → COMMIT; o 201 só existe depois do commit.
/// A chave de idempotência é inserida primeiro porque sua PK é a barreira de concorrência: duas requisições
/// com a mesma chave serializam ali, antes de qualquer outro efeito.
/// </summary>
public sealed class TransactionService(TransactionsDbContext db, TimeProvider clock, ILogger<TransactionService> logger)
{
    /// <summary>
    /// Tempo máximo esperando o lock de uma chave em processamento concorrente (HIPÓTESE). Estourar devolve 409
    /// com Retry-After. O Postgres sinaliza esse estouro com SQLSTATE 55P03 (lock_not_available); a constante e o
    /// teste de duas conexões estão em DatabaseContractTests.
    /// </summary>
    public const string LockTimeout = "2s";

    public const string LockNotAvailable = "55P03";

    private const string UniqueViolation = "23505";

    public async Task<Outcome> RegisterAsync(
        Caller caller, string idempotencyKey, string requestHash, RegisterCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var created = Transaction.Register(
            caller.MerchantId, command.Type, command.Money, command.EffectiveDate, command.Description, new Author(caller.Sub, caller.ClientId), now);
        if (!created.IsSuccess)
        {
            return new Outcome.Invalid(created.Errors!);
        }

        // O replay comum (retry) já foi tratado por FindReplayAsync, antes da validação. A corrida entre dois
        // pedidos novos com a mesma chave é decidida pela PK, em PersistAsync.
        return await PersistAsync(caller, idempotencyKey, requestHash, created.Value, cancellationToken);
    }

    public async Task<Outcome> ReverseAsync(
        Caller caller, Guid originalId, string idempotencyKey, string requestHash, string? reason, CancellationToken cancellationToken)
    {
        // O retry de um estorno já feito é tratado por FindReplayAsync antes de chegar aqui (não vira 409);
        // e, mesmo sem ele, a chave é inserida antes do estorno, então a PK de idempotência decide primeiro.
        var original = await db.Transactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.MerchantId == caller.MerchantId && t.Id == originalId, cancellationToken);
        if (original is null)
        {
            return new Outcome.NotFound();
        }

        // Sem leitura prévia de "já estornado": a unicidade é do índice único parcial (ver PersistAsync).
        var reversal = original.Reverse(reason, new Author(caller.Sub, caller.ClientId), clock.GetUtcNow().UtcDateTime);
        if (!reversal.IsSuccess)
        {
            return new Outcome.Invalid(reversal.Errors!);
        }

        return await PersistAsync(caller, idempotencyKey, requestHash, reversal.Value, cancellationToken);
    }

    /// <summary>
    /// Resposta já armazenada para esta chave: replay se o hash é igual, 422 se a chave foi usada com outro
    /// payload, null se a chave é nova. O endpoint chama antes de validar o corpo.
    /// </summary>
    public async Task<Outcome?> FindReplayAsync(
        Caller caller, string idempotencyKey, string requestHash, CancellationToken cancellationToken) =>
        await FindStoredAsync(caller.MerchantId, idempotencyKey, requestHash, cancellationToken);

    private async Task<Outcome?> FindStoredAsync(
        Guid merchantId, string idempotencyKey, string requestHash, CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MerchantId == merchantId && r.IdempotencyKey == idempotencyKey, cancellationToken);
        return record is null ? null : Evaluate(record, requestHash);
    }

    // Mesmo hash: replay (devolve a resposta armazenada). Hash diferente: a chave foi usada com outro payload (422, sem efeito).
    private static Outcome Evaluate(IdempotencyRecord record, string requestHash) =>
        record.RequestHash == requestHash
            ? new Outcome.Stored(record.ResponseStatus, record.ResponseBody, record.TransactionId, Replayed: true)
            : new Outcome.KeyReusedWithDifferentPayload();

    private async Task<Outcome> PersistAsync(
        Caller caller, string idempotencyKey, string requestHash, Transaction transaction, CancellationToken cancellationToken)
    {
        var now = transaction.CreatedAt;
        var record = new IdempotencyRecord
        {
            MerchantId = caller.MerchantId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            TransactionId = transaction.Id,
            ResponseStatus = StatusCodes.Status201Created,
            // A resposta é conhecida antes do commit porque o id nasce no servidor.
            ResponseBody = JsonSerializer.Serialize(TransactionResponse.From(transaction), Json.Output),
            CreatedAt = now,
            ExpiresAt = now + IdempotencyRecord.Retention,
        };
        var outbox = OutboxFactory.Create(transaction, Activity.Current?.Id);

        await using var dbTransaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // SET LOCAL vale só até o fim desta transação.
            await db.Database.ExecuteSqlRawAsync($"SET LOCAL lock_timeout = '{LockTimeout}'", cancellationToken);

            // Um SaveChanges por tabela: o EF ordena inserts por dependência, não por ordem de Add, e a ordem
            // (chave primeiro) é o que faz a PK de idempotência decidir a corrida antes de qualquer outro efeito.
            db.IdempotencyKeys.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            db.Transactions.Add(transaction);
            await db.SaveChangesAsync(cancellationToken);
            db.OutboxMessages.Add(outbox);
            await db.SaveChangesAsync(cancellationToken);

            await dbTransaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (FindPostgresError(ex) is { } pg && IsHandled(pg))
        {
            await dbTransaction.RollbackAsync(cancellationToken);
            // O contexto ainda rastreia as entidades Added da tentativa que falhou.
            db.ChangeTracker.Clear();
            return await MapFailureAsync(pg, caller, idempotencyKey, requestHash, transaction, cancellationToken);
        }

        logger.LogInformation(
            "Lançamento {TransactionId} registrado para o merchant {MerchantId} (tipo {EntryType}, estorno de {ReversalOf}).",
            transaction.Id, transaction.MerchantId, transaction.EntryType, transaction.ReversalOf);
        return new Outcome.Stored(record.ResponseStatus, record.ResponseBody, transaction.Id, Replayed: false);
    }

    private async Task<Outcome> MapFailureAsync(
        PostgresException error, Caller caller, string idempotencyKey, string requestHash, Transaction transaction, CancellationToken cancellationToken)
    {
        switch (error)
        {
            case { SqlState: LockNotAvailable }:
                logger.LogWarning("Chave de idempotência em processamento concorrente para o merchant {MerchantId}.", caller.MerchantId);
                return new Outcome.Busy();

            case { SqlState: UniqueViolation, ConstraintName: DbNames.IdempotencyPk }:
                // Outro pedido com a mesma chave confirmou antes: a linha agora é visível. Se não estiver (apagada entre
                // o 23505 e esta leitura; hoje não há expurgo de idempotency_keys), responde "em processamento" (409 +
                // Retry-After) em vez de 500: o cliente repete e cai no caminho normal.
                var record = await db.IdempotencyKeys.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.MerchantId == caller.MerchantId && r.IdempotencyKey == idempotencyKey, cancellationToken);
                return record is null ? new Outcome.Busy() : Evaluate(record, requestHash);

            case { SqlState: UniqueViolation, ConstraintName: DbNames.ReversalOfUnique }:
                logger.LogInformation(
                    "Estorno duplicado negado: o lançamento {OriginalId} já foi estornado.", transaction.ReversalOf);
                return new Outcome.AlreadyReversed();

            default:
                throw new UnreachableException("IsHandled e MapFailureAsync devem listar os mesmos casos.");
        }
    }

    // O EF/Npgsql embrulha erros que o Npgsql considera transitórios (inclusive 55P03) em
    // InvalidOperationException("...likely due to a transient failure") > DbUpdateException > PostgresException;
    // os demais (ex.: 23505) vêm como DbUpdateException > PostgresException. Por isso a busca percorre a cadeia.
    private static PostgresException? FindPostgresError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    // Só estes erros do Postgres têm significado de negócio; qualquer outro propaga como falha (500).
    // Também decide o nível de log do EF (Program.cs): tratados não são Error.
    internal static bool IsHandled(PostgresException error) => error is
    { SqlState: LockNotAvailable }
        or { SqlState: UniqueViolation, ConstraintName: DbNames.IdempotencyPk }
        or { SqlState: UniqueViolation, ConstraintName: DbNames.ReversalOfUnique };
}
