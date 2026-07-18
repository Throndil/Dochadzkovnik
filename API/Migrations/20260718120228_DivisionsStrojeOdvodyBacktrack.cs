using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class DivisionsStrojeOdvodyBacktrack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CarId",
                table: "InvoiceDocuments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MachineId",
                table: "InvoiceDocuments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Division",
                table: "Cars",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceDocuments_CarId",
                table: "InvoiceDocuments",
                column: "CarId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceDocuments_MachineId",
                table: "InvoiceDocuments",
                column: "MachineId");

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceDocuments_Cars_CarId",
                table: "InvoiceDocuments",
                column: "CarId",
                principalTable: "Cars",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceDocuments_Machines_MachineId",
                table: "InvoiceDocuments",
                column: "MachineId",
                principalTable: "Machines",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceDocuments_Cars_CarId",
                table: "InvoiceDocuments");

            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceDocuments_Machines_MachineId",
                table: "InvoiceDocuments");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceDocuments_CarId",
                table: "InvoiceDocuments");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceDocuments_MachineId",
                table: "InvoiceDocuments");

            migrationBuilder.DropColumn(
                name: "CarId",
                table: "InvoiceDocuments");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "InvoiceDocuments");

            migrationBuilder.DropColumn(
                name: "Division",
                table: "Cars");
        }
    }
}
