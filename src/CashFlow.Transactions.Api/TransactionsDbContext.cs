using CashFlow.Transactions.Api.Domain;
using CashFlow.Transactions.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Transactions.Api;

/// <summary>
/// Contexto do transactions_db (target.md 9.1). O DDL vem das migrações (jobs --migrate com o papel migrator);
/// a aplicação roda como tx_app, sem UPDATE/DELETE em transactions (REVOKE na migração, decisão C-05).
/// </summary>
public sealed class TransactionsDbContext(DbContextOptions<TransactionsDbContext> options) : DbContext(options)
{
    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Transaction>(t =>
        {
            t.ToTable("transactions", table =>
            {
                table.HasCheckConstraint("ck_transactions_amount_positive", "amount > 0");
                table.HasCheckConstraint("ck_transactions_currency_brl", "currency = 'BRL'");
                table.HasCheckConstraint("ck_transactions_entry_type", "entry_type IN (1, 2)");
            });
            t.HasKey(x => x.Id).HasName("pk_transactions");
            t.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            t.Property(x => x.MerchantId).HasColumnName("merchant_id");
            t.Property(x => x.EntryType).HasColumnName("entry_type").HasColumnType("smallint").HasConversion<short>();
            t.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(18,2)");
            t.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            t.Property(x => x.EffectiveDate).HasColumnName("effective_date").HasColumnType("date");
            t.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            t.Property(x => x.Description).HasColumnName("description").HasMaxLength(Transaction.MaxDescriptionLength);
            t.Property(x => x.ReversalOf).HasColumnName("reversal_of");
            t.Property(x => x.CreatedBySub).HasColumnName("created_by_sub").HasMaxLength(255);
            t.Property(x => x.CreatedByClientId).HasColumnName("created_by_client_id").HasMaxLength(255);
            t.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");

            t.HasOne<Transaction>().WithMany().HasForeignKey(x => x.ReversalOf).HasConstraintName("fk_transactions_reversal_of");
            t.HasIndex(x => new { x.MerchantId, x.EffectiveDate, x.OccurredAt, x.Id }).HasDatabaseName("ix_transactions_merchant_effective_occurred_id");
            // Um estorno por original: a barreira é o banco (concorrência), não uma leitura prévia.
            t.HasIndex(x => x.ReversalOf).IsUnique().HasFilter("reversal_of IS NOT NULL").HasDatabaseName(DbNames.ReversalOfUnique);
        });

        modelBuilder.Entity<IdempotencyRecord>(i =>
        {
            i.ToTable("idempotency_keys");
            i.HasKey(x => new { x.MerchantId, x.IdempotencyKey }).HasName(DbNames.IdempotencyPk);
            i.Property(x => x.MerchantId).HasColumnName("merchant_id");
            i.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(255);
            i.Property(x => x.RequestHash).HasColumnName("request_hash").HasColumnType("char(64)");
            i.Property(x => x.TransactionId).HasColumnName("transaction_id");
            i.Property(x => x.ResponseStatus).HasColumnName("response_status").HasColumnType("smallint");
            i.Property(x => x.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
            i.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            i.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<OutboxMessage>(o =>
        {
            o.ToTable("outbox_messages");
            o.HasKey(x => x.Id).HasName("pk_outbox_messages");
            o.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            o.Property(x => x.MerchantId).HasColumnName("merchant_id");
            o.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            o.Property(x => x.Type).HasColumnName("type").HasMaxLength(100);
            o.Property(x => x.Version).HasColumnName("version");
            o.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            o.Property(x => x.Headers).HasColumnName("headers").HasColumnType("jsonb");
            o.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            o.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamptz");
            o.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
            o.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("timestamptz");
            o.Property(x => x.LastError).HasColumnName("last_error").HasColumnType("text");
            o.Property(x => x.LockedUntil).HasColumnName("locked_until").HasColumnType("timestamptz");
            o.Property(x => x.LockedBy).HasColumnName("locked_by").HasMaxLength(64);

            o.HasIndex(x => new { x.AggregateId, x.Type, x.Version }).IsUnique().HasDatabaseName(DbNames.OutboxEventUnique);
            o.HasIndex(x => x.NextAttemptAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_messages_pending");
        });
    }
}
