using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class MasinaMappingAiSpend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CarId",
                table: "MaterialPurchases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MachineId",
                table: "MaterialPurchases",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CarId",
                table: "MaterialPurchaseLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MachineId",
                table: "MaterialPurchaseLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiSpends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    InputTokens = table.Column<long>(type: "bigint", nullable: false),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                    Calls = table.Column<int>(type: "integer", nullable: false),
                    CostEur = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSpends", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchases_CarId",
                table: "MaterialPurchases",
                column: "CarId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchases_MachineId",
                table: "MaterialPurchases",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLines_CarId",
                table: "MaterialPurchaseLines",
                column: "CarId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLines_MachineId",
                table: "MaterialPurchaseLines",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_AiSpends_Month",
                table: "AiSpends",
                column: "Month",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialPurchaseLines_Cars_CarId",
                table: "MaterialPurchaseLines",
                column: "CarId",
                principalTable: "Cars",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialPurchaseLines_Machines_MachineId",
                table: "MaterialPurchaseLines",
                column: "MachineId",
                principalTable: "Machines",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialPurchases_Cars_CarId",
                table: "MaterialPurchases",
                column: "CarId",
                principalTable: "Cars",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialPurchases_Machines_MachineId",
                table: "MaterialPurchases",
                column: "MachineId",
                principalTable: "Machines",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaterialPurchaseLines_Cars_CarId",
                table: "MaterialPurchaseLines");

            migrationBuilder.DropForeignKey(
                name: "FK_MaterialPurchaseLines_Machines_MachineId",
                table: "MaterialPurchaseLines");

            migrationBuilder.DropForeignKey(
                name: "FK_MaterialPurchases_Cars_CarId",
                table: "MaterialPurchases");

            migrationBuilder.DropForeignKey(
                name: "FK_MaterialPurchases_Machines_MachineId",
                table: "MaterialPurchases");

            migrationBuilder.DropTable(
                name: "AiSpends");

            migrationBuilder.DropIndex(
                name: "IX_MaterialPurchases_CarId",
                table: "MaterialPurchases");

            migrationBuilder.DropIndex(
                name: "IX_MaterialPurchases_MachineId",
                table: "MaterialPurchases");

            migrationBuilder.DropIndex(
                name: "IX_MaterialPurchaseLines_CarId",
                table: "MaterialPurchaseLines");

            migrationBuilder.DropIndex(
                name: "IX_MaterialPurchaseLines_MachineId",
                table: "MaterialPurchaseLines");

            migrationBuilder.DropColumn(
                name: "CarId",
                table: "MaterialPurchases");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "MaterialPurchases");

            migrationBuilder.DropColumn(
                name: "CarId",
                table: "MaterialPurchaseLines");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "MaterialPurchaseLines");
        }
    }
}
