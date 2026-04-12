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
            // This migration is a SQLite-only recovery patch — it re-creates WorkPhotos
            // with IF NOT EXISTS in case a previous run left the table missing.
            // On Postgres, AddWorkPhotos (the prior migration) already created the table
            // via EF Core's provider-aware CreateTable, so nothing to do here.
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                return;

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
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.Sqlite")
                return;

            migrationBuilder.Sql(@"DROP TABLE IF EXISTS ""WorkPhotos"";");
        }
    }
}
