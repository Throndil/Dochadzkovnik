using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class UpdatesToPhotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Re-create WorkPhotos if the previous migration's table rebuild left it missing.
            // Uses raw SQL so we can use IF NOT EXISTS and avoid another EF table-rebuild.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""WorkPhotos"" (
                    ""Id""         INTEGER NOT NULL CONSTRAINT ""PK_WorkPhotos"" PRIMARY KEY AUTOINCREMENT,
                    ""EmployeeId"" INTEGER NOT NULL,
                    ""LocationId"" INTEGER NOT NULL,
                    ""PhotoUrl""   TEXT NOT NULL,
                    ""Note""       TEXT,
                    ""CreatedAt""  TEXT NOT NULL,
                    CONSTRAINT ""FK_WorkPhotos_Employees_EmployeeId"" FOREIGN KEY (""EmployeeId"") REFERENCES ""Employees"" (""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_WorkPhotos_Locations_LocationId"" FOREIGN KEY (""LocationId"") REFERENCES ""Locations"" (""Id"") ON DELETE RESTRICT
                );
            ");

            // Ensure both indexes exist (IF NOT EXISTS is idempotent)
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_WorkPhotos_LocationId_CreatedAt""
                ON ""WorkPhotos"" (""LocationId"", ""CreatedAt"");
            ");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_WorkPhotos_EmployeeId""
                ON ""WorkPhotos"" (""EmployeeId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""WorkPhotos"";");
        }
    }
}
