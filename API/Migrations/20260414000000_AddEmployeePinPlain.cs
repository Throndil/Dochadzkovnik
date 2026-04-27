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

            // SQLite: ModalUpdate (20260412205651) always runs before this migration and adds
            // PinPlain unconditionally, so this is a no-op on SQLite. SQLite doesn't support
            // ALTER TABLE ADD COLUMN IF NOT EXISTS, hence the difference from the PG branch.
            // (Program.cs also has a pre-migration self-heal that handles legacy DBs where
            // PinPlain was added some other way.)
            return;
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"ALTER TABLE ""Employees"" DROP COLUMN IF EXISTS ""PinPlain"";");
                return;
            }
            // SQLite: no-op; ModalUpdate.Down() will drop the column when rolling back further.
            return;
        }
    }
}
