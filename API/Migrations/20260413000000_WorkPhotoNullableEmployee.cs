using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace API.Migrations
{
    /// <inheritdoc />
    public partial class WorkPhotoNullableEmployee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // PostgreSQL: simple inline ALTER — no table rebuild needed
                migrationBuilder.Sql(@"ALTER TABLE ""WorkPhotos"" ALTER COLUMN ""EmployeeId"" DROP NOT NULL;");
                return;
            }

            // SQLite does not support ALTER COLUMN — recreate the table instead
            migrationBuilder.Sql(@"
                CREATE TABLE ""WorkPhotos_new"" (
                    ""Id""         INTEGER NOT NULL CONSTRAINT ""PK_WorkPhotos"" PRIMARY KEY AUTOINCREMENT,
                    ""EmployeeId"" INTEGER NULL,
                    ""LocationId"" INTEGER NOT NULL,
                    ""PhotoUrl""   TEXT    NOT NULL,
                    ""Note""       TEXT    NULL,
                    ""CreatedAt""  TEXT    NOT NULL,
                    CONSTRAINT ""FK_WorkPhotos_Employees_EmployeeId"" FOREIGN KEY (""EmployeeId"") REFERENCES ""Employees""(""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_WorkPhotos_Locations_LocationId"" FOREIGN KEY (""LocationId"") REFERENCES ""Locations""(""Id"") ON DELETE RESTRICT
                );
                INSERT INTO ""WorkPhotos_new""
                    SELECT ""Id"", ""EmployeeId"", ""LocationId"", ""PhotoUrl"", ""Note"", ""CreatedAt"" FROM ""WorkPhotos"";
                DROP TABLE ""WorkPhotos"";
                ALTER TABLE ""WorkPhotos_new"" RENAME TO ""WorkPhotos"";
                CREATE INDEX ""IX_WorkPhotos_LocationId_CreatedAt"" ON ""WorkPhotos"" (""LocationId"", ""CreatedAt"");
                CREATE INDEX ""IX_WorkPhotos_EmployeeId"" ON ""WorkPhotos"" (""EmployeeId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"ALTER TABLE ""WorkPhotos"" ALTER COLUMN ""EmployeeId"" SET NOT NULL;");
                return;
            }

            // SQLite rollback — recreate with NOT NULL (drops any admin-uploaded rows)
            migrationBuilder.Sql(@"
                CREATE TABLE ""WorkPhotos_old"" (
                    ""Id""         INTEGER NOT NULL CONSTRAINT ""PK_WorkPhotos"" PRIMARY KEY AUTOINCREMENT,
                    ""EmployeeId"" INTEGER NOT NULL,
                    ""LocationId"" INTEGER NOT NULL,
                    ""PhotoUrl""   TEXT    NOT NULL,
                    ""Note""       TEXT    NULL,
                    ""CreatedAt""  TEXT    NOT NULL,
                    CONSTRAINT ""FK_WorkPhotos_Employees_EmployeeId"" FOREIGN KEY (""EmployeeId"") REFERENCES ""Employees""(""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_WorkPhotos_Locations_LocationId"" FOREIGN KEY (""LocationId"") REFERENCES ""Locations""(""Id"") ON DELETE RESTRICT
                );
                INSERT INTO ""WorkPhotos_old""
                    SELECT ""Id"", ""EmployeeId"", ""LocationId"", ""PhotoUrl"", ""Note"", ""CreatedAt""
                    FROM ""WorkPhotos"" WHERE ""EmployeeId"" IS NOT NULL;
                DROP TABLE ""WorkPhotos"";
                ALTER TABLE ""WorkPhotos_old"" RENAME TO ""WorkPhotos"";
                CREATE INDEX ""IX_WorkPhotos_LocationId_CreatedAt"" ON ""WorkPhotos"" (""LocationId"", ""CreatedAt"");
                CREATE INDEX ""IX_WorkPhotos_EmployeeId"" ON ""WorkPhotos"" (""EmployeeId"");
            ");
        }
    }
}
