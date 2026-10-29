using CashFlow.Transactions.Api.Http;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Transactions.Api.Application;

/// <summary>
/// Consultas. TODA consulta filtra por merchant_id (o do token, já igual ao da rota): nunca há leitura só por id.
/// </summary>
public sealed class TransactionQueries(TransactionsDbContext db)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<TransactionDetail?> GetAsync(Guid merchantId, Guid id, CancellationToken cancellationToken)
    {
        var row = await WithReversedBy(Owned(merchantId).Where(t => t.Id == id)).FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : TransactionDetail.From(row.Transaction, row.ReversedBy);
    }

    /// <param name="from">Inclusivo, por effective_date.</param>
    /// <param name="to">Inclusivo, por effective_date.</param>
    public async Task<TransactionPage> ListAsync(
        Guid merchantId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = Owned(merchantId);
        if (from is { } start)
        {
            query = query.Where(t => t.EffectiveDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(t => t.EffectiveDate <= end);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await WithReversedBy(query
                .OrderBy(t => t.EffectiveDate).ThenBy(t => t.OccurredAt).ThenBy(t => t.Id)
                .Skip((page - 1) * pageSize).Take(pageSize))
            .ToListAsync(cancellationToken);

        return new TransactionPage(rows.ConvertAll(r => TransactionDetail.From(r.Transaction, r.ReversedBy)), page, pageSize, total);
    }

    private IQueryable<Domain.Transaction> Owned(Guid merchantId) =>
        db.Transactions.AsNoTracking().Where(t => t.MerchantId == merchantId);

    // "reversedBy" é derivado na leitura: o estorno, se existir, é a linha com reversal_of = este id
    // (no máximo uma, pelo índice único parcial). Filtros e paginação são aplicados ANTES desta projeção.
    private IQueryable<Row> WithReversedBy(IQueryable<Domain.Transaction> transactions) =>
        transactions.Select(t => new Row(
            t,
            db.Transactions
                .Where(r => r.ReversalOf == t.Id && r.MerchantId == t.MerchantId)
                .Select(r => (Guid?)r.Id)
                .FirstOrDefault()));

    private sealed record Row(Domain.Transaction Transaction, Guid? ReversedBy);
}
