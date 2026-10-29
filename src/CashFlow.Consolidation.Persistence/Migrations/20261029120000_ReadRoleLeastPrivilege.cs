using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashFlow.Consolidation.Persistence.Migrations
{
    /// <summary>
    /// F6 (P-DB-1): privilégio mínimo explícito para os papéis de leitura/escrita restrita do consolidation_db.
    /// Os ALTER DEFAULT PRIVILEGES de infra/postgres/init davam a cons_ro SELECT em toda tabela futura (inclusive
    /// reconciliation_runs e processed_messages); a migração agora concede apenas o necessário, de modo que a
    /// postura não dependa de passo manual pós-migração. cons_ro só lê daily_consolidations (ConsolidationQueries);
    /// cons_worker não precisa ler a trilha de reconciliação (só recon_writer a usa).
    /// O REVOKE precede o GRANT. Executado por migrator, dono das tabelas.
    /// </summary>
    public partial class ReadRoleLeastPrivilege : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM cons_ro;");
            migrationBuilder.Sql("GRANT SELECT ON TABLE public.daily_consolidations TO cons_ro;");
            migrationBuilder.Sql("REVOKE ALL ON TABLE public.reconciliation_runs FROM cons_worker;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT SELECT ON TABLE public.processed_messages, public.reconciliation_runs TO cons_ro;");
            migrationBuilder.Sql("GRANT SELECT ON TABLE public.reconciliation_runs TO cons_worker;");
        }
    }
}
