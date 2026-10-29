using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashFlow.Transactions.Api.Migrations
{
    /// <summary>
    /// F6 (P-DB-1): recon_ro (recon-tool, fonte somente leitura) só lê "transactions" (Reconciler.cs). O privilégio
    /// padrão de infra/postgres/init lhe daria SELECT também em outbox_messages e idempotency_keys.
    /// Executado por migrator, dono das tabelas.
    /// </summary>
    public partial class ReconRoLeastPrivilege : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA public FROM recon_ro;");
            migrationBuilder.Sql("GRANT SELECT ON TABLE public.transactions TO recon_ro;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT SELECT ON ALL TABLES IN SCHEMA public TO recon_ro;");
        }
    }
}
