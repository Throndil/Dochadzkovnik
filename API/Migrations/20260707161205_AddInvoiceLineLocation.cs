using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceLineLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LocationId",
                table: "MaterialPurchaseLines",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLines_LocationId",
                table: "MaterialPurchaseLines",
                column: "LocationId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialPurchaseLines_Locations_LocationId",
                table: "MaterialPurchaseLines",
                column: "LocationId",
                principalTable: "Locations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaterialPurchaseLines_Locations_LocationId",
                table: "MaterialPurchaseLines");

            migrationBuilder.DropIndex(
                name: "IX_MaterialPurchaseLines_LocationId",
                table: "MaterialPurchaseLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "MaterialPurchaseLines");
        }
    }
}
