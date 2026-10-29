using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashFlow.Transactions.Api.Migrations
{
    /// <inheritdoc />
    public partial class OutboxColumnGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0002 item 1: tx_app só insere eventos (F3) e, no publicador (F4), atualiza o estado de entrega.
            // Payload, headers, ids e datas do evento ficam imutáveis; DELETE (expurgo) não é papel da aplicação.
            // O REVOKE em nível de tabela precede o GRANT por coluna (revogar na tabela revoga também nas colunas).
            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON TABLE public.outbox_messages FROM tx_app;");
            migrationBuilder.Sql(
                "GRANT UPDATE (published_at, locked_until, locked_by, attempts, next_attempt_at, last_error) " +
                "ON TABLE public.outbox_messages TO tx_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "REVOKE UPDATE (published_at, locked_until, locked_by, attempts, next_attempt_at, last_error) " +
                "ON TABLE public.outbox_messages FROM tx_app;");
            migrationBuilder.Sql("GRANT UPDATE, DELETE ON TABLE public.outbox_messages TO tx_app;");
        }
    }
}
