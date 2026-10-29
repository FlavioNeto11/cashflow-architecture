using Microsoft.EntityFrameworkCore;

namespace CashFlow.Consolidation.Persistence;

/// <summary>
/// Contexto do consolidation_db (target.md 9.2). Existe para gerar e aplicar as migrações (jobs --migrate com o
/// papel migrator); em runtime o worker, a API de consulta e o recon-tool usam SQL explícito com Npgsql, porque o
/// upsert aditivo, o Inbox e o lock da linha (ADR-0002 itens 4 e 7) são instruções que o EF não expressa.
/// Os nomes de tabelas e colunas são contrato desse SQL: ver <see cref="ConsolidationSchema"/>.
/// </summary>
public sealed class ConsolidationDbContext(DbContextOptions<ConsolidationDbContext> options) : DbContext(options)
{
    public DbSet<DailyConsolidation> DailyConsolidations => Set<DailyConsolidation>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DailyConsolidation>(d =>
        {
            d.ToTable(ConsolidationSchema.DailyConsolidations, table =>
            {
                // Créditos e débitos são somas de valores positivos; contagem nunca negativa.
                table.HasCheckConstraint("ck_daily_consolidations_credits", "credits >= 0");
                table.HasCheckConstraint("ck_daily_consolidations_debits", "debits >= 0");
                table.HasCheckConstraint("ck_daily_consolidations_entries", "entries_count >= 0");
            });
            d.HasKey(x => new { x.MerchantId, x.EffectiveDate }).HasName("pk_daily_consolidations");
            d.Property(x => x.MerchantId).HasColumnName("merchant_id");
            d.Property(x => x.EffectiveDate).HasColumnName("effective_date").HasColumnType("date");
            // numeric(24,2) (hipótese F4, migração WidenProjectionTotals): a soma diária de vários lançamentos no teto de
            // Money (numeric(18,2)) não cabe em 18,2; 24,2 dá folga de ~10^6 lançamentos no teto por linha. O teto do
            // domínio não muda. Se mesmo assim estourar (SQLSTATE 22003), o worker manda à DLQ (projection_overflow).
            d.Property(x => x.Credits).HasColumnName("credits").HasColumnType("numeric(24,2)");
            d.Property(x => x.Debits).HasColumnName("debits").HasColumnType("numeric(24,2)");
            d.Property(x => x.EntriesCount).HasColumnName("entries_count");
            d.Property(x => x.LastAppliedAt).HasColumnName("last_applied_at").HasColumnType("timestamptz");
            d.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<ProcessedMessage>(p =>
        {
            p.ToTable(ConsolidationSchema.ProcessedMessages);
            // Chave = transaction_id (ADR-0002 item 4): identidade compartilhada entre worker e recon-tool.
            p.HasKey(x => x.TransactionId).HasName(ConsolidationSchema.ProcessedMessagesPk);
            p.Property(x => x.TransactionId).HasColumnName("transaction_id").ValueGeneratedNever();
            p.Property(x => x.EventId).HasColumnName("event_id");
            p.Property(x => x.MerchantId).HasColumnName("merchant_id");
            p.Property(x => x.EffectiveDate).HasColumnName("effective_date").HasColumnType("date");
            p.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(64);
            p.Property(x => x.ProcessedAt).HasColumnName("processed_at").HasColumnType("timestamptz");

            p.HasIndex(x => new { x.MerchantId, x.EffectiveDate }).HasDatabaseName("ix_processed_messages_merchant_effective");
            p.HasIndex(x => x.ProcessedAt).HasDatabaseName("ix_processed_messages_processed_at");
        });

        modelBuilder.Entity<ReconciliationRun>(r =>
        {
            r.ToTable(ConsolidationSchema.ReconciliationRuns);
            r.HasKey(x => x.Id).HasName("pk_reconciliation_runs");
            r.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            r.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamptz");
            r.Property(x => x.FinishedAt).HasColumnName("finished_at").HasColumnType("timestamptz");
            r.Property(x => x.MerchantId).HasColumnName("merchant_id");
            r.Property(x => x.FromDate).HasColumnName("from_date").HasColumnType("date");
            r.Property(x => x.ToDate).HasColumnName("to_date").HasColumnType("date");
            r.Property(x => x.Divergences).HasColumnName("divergences");
            r.Property(x => x.Kinds).HasColumnName("kinds").HasColumnType("jsonb");
            r.Property(x => x.Details).HasColumnName("details").HasColumnType("jsonb");

            r.HasIndex(x => new { x.MerchantId, x.StartedAt }).HasDatabaseName("ix_reconciliation_runs_merchant_started");
        });
    }
}

/// <summary>Nomes usados pelo SQL explícito do worker, da API e do recon-tool.</summary>
public static class ConsolidationSchema
{
    public const string DailyConsolidations = "daily_consolidations";
    public const string ProcessedMessages = "processed_messages";
    public const string ReconciliationRuns = "reconciliation_runs";
    public const string ProcessedMessagesPk = "pk_processed_messages";

    /// <summary>Valor de processed_messages.consumer gravado pelo worker.</summary>
    public const string WorkerConsumer = "worker";

    /// <summary>Valor de processed_messages.consumer gravado pelo recon-tool (event_id nulo).</summary>
    public const string ReconConsumer = "recon-tool";
}

/// <summary>Linha da projeção merchant/dia. daily_net = credits − debits, calculado na leitura.</summary>
public sealed class DailyConsolidation
{
    public Guid MerchantId { get; set; }

    public DateOnly EffectiveDate { get; set; }

    public decimal Credits { get; set; }

    public decimal Debits { get; set; }

    public int EntriesCount { get; set; }

    /// <summary>occurred_at do último evento aplicado (asOf do contrato); nulo em linha criada pelo recon sem efeito.</summary>
    public DateTime? LastAppliedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Inbox: um lançamento aplicado à projeção (pelo worker ou pelo recon-tool).</summary>
public sealed class ProcessedMessage
{
    public Guid TransactionId { get; set; }

    /// <summary>outbox_messages.id do evento; nulo nas linhas inseridas pelo recon-tool.</summary>
    public Guid? EventId { get; set; }

    public Guid MerchantId { get; set; }

    public DateOnly EffectiveDate { get; set; }

    public string Consumer { get; set; } = string.Empty;

    public DateTime ProcessedAt { get; set; }
}

/// <summary>Trilha de RF-10 e das operações de DLQ (target.md 9.2).</summary>
public sealed class ReconciliationRun
{
    public Guid Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public Guid MerchantId { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public int Divergences { get; set; }

    public string Kinds { get; set; } = "{}";

    public string Details { get; set; } = "{}";
}
