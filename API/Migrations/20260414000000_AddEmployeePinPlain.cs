using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeePinPlain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // ModalUpdate (20260412205651) may already have added this column.
                // Use IF NOT EXISTS so this migration is safe to run in any order.
                migrationBuilder.Sql(@"ALTER TABLE ""Employees"" ADD COLUMN IF NOT EXISTS ""PinPlain"" text;");
                return;
            }

            // SQLite: Program.cs pre-migration self-heal already marks this migration as applied
            // if PinPlain already exists, so we only reach here when the column is truly absent.
            migrationBuilder.AddColumn<string>(
                name: "PinPlain",
                table: "Employees",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"ALTER TABLE ""Employees"" DROP COLUMN IF EXISTS ""PinPlain"";");
                return;
            }
            migrationBuilder.DropColumn(name: "PinPlain", table: "Employees");
        }
    }
}
