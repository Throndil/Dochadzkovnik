using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class AddMaterialPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE — this migration was edited by hand on 2026-05-06.
            // The CLI-generated form additionally emitted ~266 AlterColumn
            // operations against unrelated existing tables (drift between the
            // model snapshot and the historical SQLite→PostgreSQL-mutated
            // schema). Those AlterColumns were no-ops or outright crashes on
            // prod-shaped schemas (e.g. trying to add an identity to
            // TimeEntries.Id which is already an identity column).
            //
            // The .Designer.cs companion + AppDbContextModelSnapshot.cs are
            // intact and reflect the post-change model, so EF's migration
            // chain is unaffected. The PostgreSQL self-heal block in
            // Program.cs is the idempotent backstop and creates the same
            // two tables if for any reason this migration is skipped.
            //
            // See PROJECT_NOTES.md "Known Issues / Technical Debt" and
            // MATERIAL_PURCHASES_PLAN.md for context.

            migrationBuilder.CreateTable(
                name: "MaterialPurchases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PurchaseDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    LocationId = table.Column<int>(type: "integer", nullable: true),
                    TimeEntryId = table.Column<int>(type: "integer", nullable: true),
                    SupplierName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReceiptPhotoUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TotalCost = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialPurchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialPurchases_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialPurchases_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_MaterialPurchases_TimeEntries_TimeEntryId",
                        column: x => x.TimeEntryId,
                        principalTable: "TimeEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MaterialPurchaseLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PurchaseId = table.Column<int>(type: "integer", nullable: false),
                    MaterialId = table.Column<int>(type: "integer", nullable: true),
                    MaterialNameRaw = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialPurchaseLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLines_MaterialPurchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "MaterialPurchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaterialPurchaseLines_Materials_MaterialId",
                        column: x => x.MaterialId,
                        principalTable: "Materials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLines_MaterialId",
                table: "MaterialPurchaseLines",
                column: "MaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchaseLines_PurchaseId",
                table: "MaterialPurchaseLines",
                column: "PurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchases_EmployeeId_PurchaseDate",
                table: "MaterialPurchases",
                columns: new[] { "EmployeeId", "PurchaseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchases_LocationId",
                table: "MaterialPurchases",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchases_PurchaseDate",
                table: "MaterialPurchases",
                column: "PurchaseDate");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialPurchases_TimeEntryId",
                table: "MaterialPurchases",
                column: "TimeEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaterialPurchaseLines");

            migrationBuilder.DropTable(
                name: "MaterialPurchases");
        }
    }
}
