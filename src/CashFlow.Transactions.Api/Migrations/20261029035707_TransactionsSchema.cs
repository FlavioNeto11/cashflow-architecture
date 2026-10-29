using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashFlow.Transactions.Api.Migrations
{
    /// <inheritdoc />
    public partial class TransactionsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<string>(type: "char(64)", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    response_status = table.Column<short>(type: "smallint", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => new { x.merchant_id, x.idempotency_key });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    published_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    locked_until = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    locked_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_type = table.Column<short>(type: "smallint", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    description = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    reversal_of = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_sub = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_by_client_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.CheckConstraint("ck_transactions_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_transactions_currency_brl", "currency = 'BRL'");
                    table.CheckConstraint("ck_transactions_entry_type", "entry_type IN (1, 2)");
                    table.ForeignKey(
                        name: "fk_transactions_reversal_of",
                        column: x => x.reversal_of,
                        principalTable: "transactions",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                table: "outbox_messages",
                column: "next_attempt_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_outbox_messages_aggregate_type_version",
                table: "outbox_messages",
                columns: new[] { "aggregate_id", "type", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_merchant_effective_occurred_id",
                table: "transactions",
                columns: new[] { "merchant_id", "effective_date", "occurred_at", "id" });

            migrationBuilder.CreateIndex(
                name: "ux_transactions_reversal_of",
                table: "transactions",
                column: "reversal_of",
                unique: true,
                filter: "reversal_of IS NOT NULL");

            // Imutabilidade (P-06, decisão C-05): o default privilege de 02-databases.sql deu DML completo ao tx_app
            // nas tabelas criadas pelo migrator; para transactions a correção é estorno, nunca UPDATE/DELETE.
            // outbox_messages e idempotency_keys mantêm UPDATE (publisher de F4 atualiza o outbox).
            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON TABLE public.transactions FROM tx_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "transactions");
        }
    }
}
