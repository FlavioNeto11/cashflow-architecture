using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashFlow.Consolidation.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_consolidations",
                columns: table => new
                {
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    credits = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    debits = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    entries_count = table.Column<int>(type: "integer", nullable: false),
                    last_applied_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_consolidations", x => new { x.merchant_id, x.effective_date });
                    table.CheckConstraint("ck_daily_consolidations_credits", "credits >= 0");
                    table.CheckConstraint("ck_daily_consolidations_debits", "debits >= 0");
                    table.CheckConstraint("ck_daily_consolidations_entries", "entries_count >= 0");
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                columns: table => new
                {
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    consumer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_messages", x => x.transaction_id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: false),
                    divergences = table.Column<int>(type: "integer", nullable: false),
                    kinds = table.Column<string>(type: "jsonb", nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reconciliation_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_processed_messages_merchant_effective",
                table: "processed_messages",
                columns: new[] { "merchant_id", "effective_date" });

            migrationBuilder.CreateIndex(
                name: "ix_processed_messages_processed_at",
                table: "processed_messages",
                column: "processed_at");

            migrationBuilder.CreateIndex(
                name: "ix_reconciliation_runs_merchant_started",
                table: "reconciliation_runs",
                columns: new[] { "merchant_id", "started_at" });

            // Concessões por tabela (security.md 4.3, ADR-0002 itens 4 e 7). cons_ro já recebe SELECT pelos
            // privilégios padrão de 02-databases.sql; cons_worker e recon_writer não têm privilégio padrão.
            // cons_worker não recebe DELETE no Inbox: apagar uma linha de processed_messages reabre a janela de
            // dupla contabilização; só o rebuild do recon-tool (recon_writer) pode fazê-lo, sob o lock da linha.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON TABLE public.daily_consolidations TO cons_worker;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON TABLE public.processed_messages TO cons_worker;");
            migrationBuilder.Sql("GRANT SELECT ON TABLE public.reconciliation_runs TO cons_worker;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON TABLE public.daily_consolidations TO recon_writer;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, DELETE ON TABLE public.processed_messages TO recon_writer;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON TABLE public.reconciliation_runs TO recon_writer;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "daily_consolidations");

            migrationBuilder.DropTable(
                name: "processed_messages");

            migrationBuilder.DropTable(
                name: "reconciliation_runs");
        }
    }
}
