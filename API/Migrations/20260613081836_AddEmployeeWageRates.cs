using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeeWageRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: Only the genuinely-new EmployeeWageRates table is created here.
            // EF's model snapshot had drifted — earlier columns/tables (WageAtTime,
            // EmployeeAdvances, HourlyWage, ScanSource, ContractValue, the
            // MaterialUsages invoice-link columns, etc.) were added to the database
            // via the app's defensive startup SQL in Program.cs rather than tracked
            // migrations. `dotnet ef migrations add` therefore regenerated
            // AddColumn/CreateTable operations for objects that ALREADY exist,
            // which fail with "already exists". Those operations were removed; the
            // regenerated model snapshot is now complete and correct for future
            // migrations, and only this brand-new table is applied.
            migrationBuilder.CreateTable(
                name: "EmployeeWageRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    RatePerHour = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeWageRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeWageRates_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeWageRates_EmployeeId_EffectiveFrom",
                table: "EmployeeWageRates",
                columns: new[] { "EmployeeId", "EffectiveFrom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeWageRates");
        }
    }
}
