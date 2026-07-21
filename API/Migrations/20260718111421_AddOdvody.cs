using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class AddOdvody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Division",
                table: "Employees",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.CreateTable(
                name: "CompanyRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyRates", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "CompanyRates",
                columns: new[] { "Id", "Amount", "Key", "Label", "Unit", "UpdatedAt" },
                values: new object[,]
                {
                    { 1, 0m, "odvody", "Odvody", "€/h na pracovníka", new DateTime(2026, 7, 18, 0, 0, 0, 0, DateTimeKind.Unspecified) },
                    { 2, 1m, "ubytovanie", "Ubytovanie", "€/h na pracovníka", new DateTime(2026, 7, 18, 0, 0, 0, 0, DateTimeKind.Unspecified) },
                    { 3, 30m, "vyjazd_auta", "Výjazd auta", "€/výjazd", new DateTime(2026, 7, 18, 0, 0, 0, 0, DateTimeKind.Unspecified) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyRates");

            migrationBuilder.AlterColumn<string>(
                name: "Division",
                table: "Employees",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }
    }
}
