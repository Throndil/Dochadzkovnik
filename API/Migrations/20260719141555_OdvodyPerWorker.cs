using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class OdvodyPerWorker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "CompanyRates",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "OdvodyPct",
                table: "Employees",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OdvodyPct",
                table: "Employees");

            migrationBuilder.InsertData(
                table: "CompanyRates",
                columns: new[] { "Id", "Amount", "Key", "Label", "Unit", "UpdatedAt" },
                values: new object[] { 1, 0m, "odvody", "Odvody", "€/h na pracovníka", new DateTime(2026, 7, 18, 0, 0, 0, 0, DateTimeKind.Unspecified) });
        }
    }
}
