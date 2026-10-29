using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashFlow.Consolidation.Persistence.Migrations
{
    /// <summary>
    /// F4 (hipótese): credits/debits de numeric(18,2) para numeric(24,2). Dois lançamentos no teto de Money no mesmo
    /// comerciante/dia estouravam a projeção (SQLSTATE 22003). Mesma escala com precisão maior: o Postgres não reescreve
    /// a tabela, mas toma ACCESS EXCLUSIVE por instante. O Down falha (22003) se alguma linha já passar de 18,2: nesse
    /// caso o rollback exige decisão manual, nunca truncar valores.
    /// </summary>
    public partial class WidenProjectionTotals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "debits",
                table: "daily_consolidations",
                type: "numeric(24,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "credits",
                table: "daily_consolidations",
                type: "numeric(24,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "debits",
                table: "daily_consolidations",
                type: "numeric(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,2)");

            migrationBuilder.AlterColumn<decimal>(
                name: "credits",
                table: "daily_consolidations",
                type: "numeric(18,2)",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(24,2)");
        }
    }
}
