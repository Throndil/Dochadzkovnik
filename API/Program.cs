using System.Text;
using API.BackgroundServices;
using API.Data;
using API.Models;
using API.Services;
using CloudinaryDotNet;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;


// Fix Npgsql 6+ timestamp behaviour so DateTime<->timestamp without time zone works correctly
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Local-dev secrets file. Optional + gitignored. Loaded last so it overrides
// appsettings.json + appsettings.{Environment}.json but stays below env vars.
// Devs copy appsettings.Local.example.json → appsettings.Local.json and fill it in.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Database
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        var uri = new Uri(databaseUrl);
        var userInfo = uri.UserInfo.Split(new char[] { ':' }, 2);
        var username = Uri.UnescapeDataString(userInfo[0]);
        var password = Uri.UnescapeDataString(userInfo[1]);
        var database = uri.AbsolutePath.TrimStart('/');
        var connStr = $"Host={uri.Host};Port={uri.Port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";
        options.UseNpgsql(connStr);
        options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }
    else
    {
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"));
    }
});

// Identity
builder.Services.AddIdentityCore<AppUser>(opt =>
    {
        opt.Password.RequireNonAlphanumeric = false;
        opt.Password.RequireUppercase = false;
        opt.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// JWT Authentication.
// Jwt:Key is REQUIRED — fail loud at startup so a missing/blank value never
// silently downgrades the app to an unsigned or trivially-forgeable token.
// HMAC-SHA256 needs at least 32 bytes of key material.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
    throw new InvalidOperationException(
        "Jwt:Key is not configured. Set the Jwt__Key environment variable on Railway " +
        "(double underscore is required to map to the Jwt:Key config path) or define it " +
        "in a gitignored appsettings.Local.json. See SECRETS.md.");
if (Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException(
        "Jwt:Key is too short. HMAC-SHA256 requires at least 32 bytes / characters of " +
        "random key material. Generate one with: openssl rand -base64 48");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });
builder.Services.AddAuthorization();

// Cloudinary image storage
var cloudName = builder.Configuration["Cloudinary:CloudName"];
var cloudApiKey = builder.Configuration["Cloudinary:ApiKey"];
var cloudApiSecret = builder.Configuration["Cloudinary:ApiSecret"];
if (!string.IsNullOrEmpty(cloudName) && !string.IsNullOrEmpty(cloudApiKey) && !string.IsNullOrEmpty(cloudApiSecret))
{
    var account = new Account(cloudName, cloudApiKey, cloudApiSecret);
    builder.Services.AddSingleton(new Cloudinary(account));
    builder.Services.AddScoped<IBlobStorageService, CloudinaryStorageService>();
}

// Image processing (normalise all uploads to PNG)
builder.Services.AddScoped<IImageProcessingService, ImageProcessingService>();

// Material consumption Excel export (ClosedXML)
builder.Services.AddScoped<IMaterialExcelExportService, MaterialExcelExportService>();

// App services
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPinHasher, PinHasher>();
builder.Services.AddScoped<IEmailService, EmailService>();

// Notification services (push only)
builder.Services.AddScoped<IPushNotificationService, WebPushService>();
builder.Services.AddScoped<NoActivity48hEvaluator>();
builder.Services.AddHostedService<NotificationBackgroundService>();

// Commander API integration — read-only fleet data, gated by the
// CommanderIntegration feature flag. BaseUrl defaults to the public spec URL
// (https://online.commander-systems.com/api/v1) and can be overridden with
// the Commander__BaseUrl env var if the customer ever moves to a different
// host. HTTPS is enforced here so a misconfigured value fails the boot
// rather than silently downgrading the channel. Username/Password are
// validated per request inside CommanderClient.SendAsync; they MUST NOT be
// logged from this code, ever. See SECRETS.md and COMMANDER_PLAN.md.
var commanderBaseUrl = builder.Configuration["Commander:BaseUrl"]?.Trim();
if (string.IsNullOrWhiteSpace(commanderBaseUrl))
    commanderBaseUrl = "https://online.commander-systems.com/api/v1";
if (!commanderBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException(
        "Commander:BaseUrl must use HTTPS. Set Commander__BaseUrl to a https://... " +
        "value, or remove the env var to fall back to the spec default. See SECRETS.md.");
if (!commanderBaseUrl.EndsWith('/'))
    commanderBaseUrl += "/";
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<ICommanderClient, CommanderClient>(c =>
{
    c.BaseAddress = new Uri(commanderBaseUrl);
    c.Timeout = TimeSpan.FromSeconds(15);
});

// CORS
// Base origins come from appsettings / Railway config section.
// ALLOWED_ORIGINS env var adds extra comma-separated origins at runtime.
// Entries prefixed with "*" are treated as suffix wildcards:
//   e.g. "*throndils-projects.vercel.app" matches every Vercel preview URL for this project.
// Set in Railway dev env: ALLOWED_ORIGINS=*throndils-projects.vercel.app
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        var origins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
            ?? new[] { "http://localhost:4200" };

        var extraRaw = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS") ?? string.Empty;
        var extra = extraRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var allOrigins = origins.Concat(extra).Distinct().ToArray();

        // Split into exact origins and wildcard-suffix origins (those starting with "*").
        var exactOrigins   = allOrigins.Where(o => !o.StartsWith('*')).ToArray();
        var wildcardSuffixes = allOrigins
            .Where(o => o.StartsWith('*'))
            .Select(o => o[1..]) // strip leading "*"
            .ToArray();

        if (wildcardSuffixes.Length > 0)
        {
            // Use a predicate so we can mix exact and suffix-wildcard matching.
            policy.SetIsOriginAllowed(origin =>
                    exactOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase) ||
                    wildcardSuffixes.Any(suffix => origin.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
        else
        {
            policy.WithOrigins(exactOrigins)
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.Converters.Add(new API.Converters.UtcDateTimeConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHttpClient(); // used by LocationsController to stream photos for ZIP download

var app = builder.Build();

// Migrate database and seed admin user
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // SQLite pre-migration: if a column was added manually (self-heal style) before the migration
    // was recorded, mark it as applied so MigrateAsync() won't try to add it again and crash.
    if (string.IsNullOrEmpty(databaseUrl)) // SQLite only (no DATABASE_URL = local dev)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();

            // AddEmployeePinPlain: Check if PinPlain already exists in Employees
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name = 'PinPlain'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    using var ins = conn.CreateCommand();
                    ins.CommandText = @"
                        INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES ('20260414000000_AddEmployeePinPlain', '9.0.0')";
                    await ins.ExecuteNonQueryAsync();
                }
            }

            // AddEmployeeDeclineReason: Check if NotificationsDeclineReason already exists in Employees
            // (added by self-heal in earlier deploys, before the migration file existed)
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name = 'NotificationsDeclineReason'";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    using var ins = conn.CreateCommand();
                    ins.CommandText = @"
                        INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES ('20260427151230_AddEmployeeDeclineReason', '9.0.0')";
                    await ins.ExecuteNonQueryAsync();
                }
            }

            // AddNotifications: Check if NotificationsEnabled column or NotificationConfigs table already exists
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT
                        (SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name = 'NotificationsEnabled') +
                        (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = 'NotificationConfigs')";
                var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (count > 0)
                {
                    using var ins = conn.CreateCommand();
                    ins.CommandText = @"
                        INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES ('20260426150946_AddNotifications', '9.0.0')";
                    await ins.ExecuteNonQueryAsync();
                }
            }

            await conn.CloseAsync();
        }
        catch { /* best-effort — MigrateAsync will surface any real problems */ }
    }

    // PostgreSQL pre-migration: mark AddNotifications as applied if its tables already exist
    // (created by the post-migration self-heal block on a previous deployment, before the
    // migration file was added to the project). Prevents "table already exists" on MigrateAsync.
    if (!string.IsNullOrEmpty(databaseUrl)) // PostgreSQL only (DATABASE_URL set = prod)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory')
                       AND EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'NotificationConfigs')
                       AND NOT EXISTS (
                           SELECT 1 FROM ""__EFMigrationsHistory""
                           WHERE ""MigrationId"" = '20260426150946_AddNotifications'
                       ) THEN
                        INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES ('20260426150946_AddNotifications', '9.0.0');
                    END IF;
                END $$;
            ");
        }
        catch { /* best-effort — MigrateAsync will surface any real problems */ }
    }

    // PostgreSQL pre-migration: mark AddEmployeeDeclineReason as applied if the column was
    // already added by the existing self-heal block (Employees.NotificationsDeclineReason).
    // Prevents "column already exists" on MigrateAsync.
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = '__EFMigrationsHistory')
                       AND EXISTS (
                           SELECT 1 FROM information_schema.columns
                           WHERE table_name = 'Employees' AND column_name = 'NotificationsDeclineReason'
                       )
                       AND NOT EXISTS (
                           SELECT 1 FROM ""__EFMigrationsHistory""
                           WHERE ""MigrationId"" = '20260427151230_AddEmployeeDeclineReason'
                       ) THEN
                        INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES ('20260427151230_AddEmployeeDeclineReason', '9.0.0');
                    END IF;
                END $$;
            ");
        }
        catch { /* best-effort — MigrateAsync will surface any real problems */ }
    }

    await db.Database.MigrateAsync();

    // Fix DateTime columns created as TEXT by the old SQLite-specific type annotations
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Employees' AND column_name = 'CreatedAt' AND data_type = 'text'
                ) THEN
                    ALTER TABLE ""Employees""
                        ALTER COLUMN ""CreatedAt"" TYPE timestamp without time zone USING ""CreatedAt""::timestamptz::timestamp,
                        ALTER COLUMN ""UpdatedAt"" TYPE timestamp without time zone USING ""UpdatedAt""::timestamptz::timestamp;
                    ALTER TABLE ""Locations""
                        ALTER COLUMN ""CreatedAt"" TYPE timestamp without time zone USING ""CreatedAt""::timestamptz::timestamp,
                        ALTER COLUMN ""UpdatedAt"" TYPE timestamp without time zone USING ""UpdatedAt""::timestamptz::timestamp;
                    ALTER TABLE ""TimeEntries""
                        ALTER COLUMN ""ClockIn""   TYPE timestamp without time zone USING ""ClockIn""::timestamptz::timestamp,
                        ALTER COLUMN ""ClockOut""  TYPE timestamp without time zone USING ""ClockOut""::timestamptz::timestamp,
                        ALTER COLUMN ""CreatedAt"" TYPE timestamp without time zone USING ""CreatedAt""::timestamptz::timestamp,
                        ALTER COLUMN ""UpdatedAt"" TYPE timestamp without time zone USING ""UpdatedAt""::timestamptz::timestamp;
                END IF;
            END $$;
        ");
    }

    // Self-heal: ensure Cars table and related schema exist (migration may not have run on prod)
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                -- Create Cars table if it was never migrated
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_name = 'Cars'
                ) THEN
                    CREATE TABLE ""Cars"" (
                        ""Id""           SERIAL PRIMARY KEY,
                        ""Name""         VARCHAR(200) NOT NULL,
                        ""LicensePlate"" VARCHAR(20),
                        ""IsActive""     BOOLEAN NOT NULL DEFAULT TRUE,
                        ""PhotoUrl""     VARCHAR(1000),
                        ""CreatedAt""    TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""UpdatedAt""    TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    -- Mark both Cars migrations as applied so MigrateAsync won't try to run them
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260328100000_AddCars', '9.0.0')
                    ON CONFLICT DO NOTHING;
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260328120000_AddCarPhotoUrl', '9.0.0')
                    ON CONFLICT DO NOTHING;
                END IF;

                -- Add PhotoUrl to Cars if missing (migration 2 may have been skipped)
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Cars' AND column_name = 'PhotoUrl'
                ) THEN
                    ALTER TABLE ""Cars"" ADD COLUMN ""PhotoUrl"" VARCHAR(1000);
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260328120000_AddCarPhotoUrl', '9.0.0')
                    ON CONFLICT DO NOTHING;
                END IF;

                -- Add CarId FK column to TimeEntries if missing
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'TimeEntries' AND column_name = 'CarId'
                ) THEN
                    ALTER TABLE ""TimeEntries"" ADD COLUMN ""CarId"" INTEGER REFERENCES ""Cars""(""Id"") ON DELETE SET NULL;
                    CREATE INDEX IF NOT EXISTS ""IX_TimeEntries_CarId"" ON ""TimeEntries"" (""CarId"");
                END IF;

                -- Ensure Cars.Id has a sequence (same pattern as other tables)
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Cars' AND column_name = 'Id'
                      AND (column_default IS NOT NULL OR is_identity = 'YES')
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""Cars_Id_seq"";
                    ALTER TABLE ""Cars"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""Cars_Id_seq""');
                    PERFORM setval('""Cars_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""Cars""), 0) + 1, false);
                END IF;
            END $$;
        ");
    }

    // Self-heal: add PhotoUrl column to TimeEntries if missing (migration may not have run on prod)
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'TimeEntries' AND column_name = 'PhotoUrl'
                ) THEN
                    ALTER TABLE ""TimeEntries"" ADD COLUMN ""PhotoUrl"" VARCHAR(1000);
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260411000000_AddTimeEntryPhotoUrl', '9.0.0')
                    ON CONFLICT DO NOTHING;
                END IF;
            END $$;
        ");
    }

    // Self-heal: ensure WorkPhotos table exists (migration may not have run on prod)
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.tables
                    WHERE table_name = 'WorkPhotos'
                ) THEN
                    CREATE TABLE ""WorkPhotos"" (
                        ""Id""         SERIAL PRIMARY KEY,
                        ""EmployeeId"" INTEGER NOT NULL REFERENCES ""Employees""(""Id"") ON DELETE RESTRICT,
                        ""LocationId"" INTEGER NOT NULL REFERENCES ""Locations""(""Id"") ON DELETE RESTRICT,
                        ""PhotoUrl""   VARCHAR(1000) NOT NULL,
                        ""Note""       TEXT,
                        ""CreatedAt""  TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_WorkPhotos_LocationId_CreatedAt""
                        ON ""WorkPhotos""(""LocationId"", ""CreatedAt"");
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260412000000_AddWorkPhotos', '9.0.0')
                    ON CONFLICT DO NOTHING;
                END IF;
            END $$;
        ");
    }

    // Self-heal: make WorkPhotos.EmployeeId nullable (admin gallery uploads have no employee)
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'WorkPhotos' AND column_name = 'EmployeeId' AND is_nullable = 'NO'
                ) THEN
                    ALTER TABLE ""WorkPhotos"" ALTER COLUMN ""EmployeeId"" DROP NOT NULL;
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260413000000_WorkPhotoNullableEmployee', '9.0.0')
                    ON CONFLICT DO NOTHING;
                END IF;
            END $$;
        ");
    }

    // Self-heal: add PinPlain column to Employees (stores plain-text PIN for manager view)
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Employees' AND column_name = 'PinPlain'
                ) THEN
                    ALTER TABLE ""Employees"" ADD COLUMN ""PinPlain"" TEXT;
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260414000000_AddEmployeePinPlain', '9.0.0')
                    ON CONFLICT DO NOTHING;
                END IF;
            END $$;
        ");
    }

    // Fix boolean columns that may have been created as INTEGER by the SQLite provider
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'AspNetUsers'
                      AND column_name = 'EmailConfirmed'
                      AND data_type = 'integer'
                ) THEN
                    ALTER TABLE ""AspNetUsers"" ALTER COLUMN ""EmailConfirmed"" TYPE boolean USING ""EmailConfirmed""::boolean;
                    ALTER TABLE ""AspNetUsers"" ALTER COLUMN ""PhoneNumberConfirmed"" TYPE boolean USING ""PhoneNumberConfirmed""::boolean;
                    ALTER TABLE ""AspNetUsers"" ALTER COLUMN ""TwoFactorEnabled"" TYPE boolean USING ""TwoFactorEnabled""::boolean;
                    ALTER TABLE ""AspNetUsers"" ALTER COLUMN ""LockoutEnabled"" TYPE boolean USING ""LockoutEnabled""::boolean;
                    ALTER TABLE ""Employees"" ALTER COLUMN ""IsActive"" TYPE boolean USING ""IsActive""::boolean;
                    ALTER TABLE ""Locations"" ALTER COLUMN ""IsActive"" TYPE boolean USING ""IsActive""::boolean;
                END IF;
            END $$;
        ");
    }

    // Self-heal: ensure Cars + TimeEntries.{CarId,PhotoUrl} schema exists on SQLite (local dev).
    // The hand-written AddCars / AddCarPhotoUrl / AddTimeEntryPhotoUrl migrations have no
    // .Designer.cs companion, so EF's MigrateAsync silently skips them. A fresh local DB
    // therefore has no Cars table and no CarId/PhotoUrl on TimeEntries.
    // Mirrors the existing PostgreSQL self-heal block above; idempotent (IF NOT EXISTS /
    // pragma_table_info). Wrapped in try/catch so any unexpected failure doesn't block startup.
    if (string.IsNullOrEmpty(databaseUrl))
    {
        try
        {
            // Cars table — column set matches the model + AddCars/AddCarPhotoUrl migrations.
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""Cars"" (
                    ""Id""           INTEGER NOT NULL CONSTRAINT ""PK_Cars"" PRIMARY KEY AUTOINCREMENT,
                    ""Name""         TEXT NOT NULL,
                    ""LicensePlate"" TEXT NULL,
                    ""PhotoUrl""     TEXT NULL,
                    ""IsActive""     INTEGER NOT NULL DEFAULT 1,
                    ""CreatedAt""    TEXT NOT NULL DEFAULT '',
                    ""UpdatedAt""    TEXT NOT NULL DEFAULT ''
                );
            ");

            var carsConn = db.Database.GetDbConnection();
            var carsConnWasClosed = carsConn.State != System.Data.ConnectionState.Open;
            if (carsConnWasClosed) await carsConn.OpenAsync();
            try
            {
                async Task<bool> ColExists(string table, string column)
                {
                    using var cmd = carsConn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                    return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                }

                // Cars.PhotoUrl (defensive — covered by the CREATE TABLE above on a fresh DB,
                // but ensures the column exists on a DB that was created before AddCarPhotoUrl).
                if (!await ColExists("Cars", "PhotoUrl"))
                    await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Cars"" ADD COLUMN ""PhotoUrl"" TEXT NULL;");

                // TimeEntries.CarId + index (was supposed to be added by AddCars).
                if (!await ColExists("TimeEntries", "CarId"))
                {
                    await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""TimeEntries"" ADD COLUMN ""CarId"" INTEGER NULL;");
                    await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_TimeEntries_CarId"" ON ""TimeEntries"" (""CarId"");");
                }

                // TimeEntries.PhotoUrl (was supposed to be added by AddTimeEntryPhotoUrl).
                if (!await ColExists("TimeEntries", "PhotoUrl"))
                    await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""TimeEntries"" ADD COLUMN ""PhotoUrl"" TEXT NULL;");
            }
            finally
            {
                if (carsConnWasClosed) await carsConn.CloseAsync();
            }

            // Mark the corresponding hand-written migrations as applied so EF doesn't ever
            // try to re-run them if a .Designer.cs is generated later.
            await db.Database.ExecuteSqlRawAsync(@"
                INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"") VALUES
                    ('20260328100000_AddCars', '9.0.0'),
                    ('20260328120000_AddCarPhotoUrl', '9.0.0'),
                    ('20260411000000_AddTimeEntryPhotoUrl', '9.0.0'),
                    ('20260413000000_WorkPhotoNullableEmployee', '9.0.0');
            ");
        }
        catch { /* best-effort SQLite self-heal */ }
    }

    // Self-heal: ensure Materials and MaterialUsages tables exist.
    // SQLite path — local dev. The CREATE TABLE statements are idempotent (IF NOT EXISTS).
    if (string.IsNullOrEmpty(databaseUrl))
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""Materials"" (
                    ""Id""           INTEGER NOT NULL CONSTRAINT ""PK_Materials"" PRIMARY KEY AUTOINCREMENT,
                    ""Name""         TEXT NOT NULL,
                    ""Unit""         TEXT NOT NULL,
                    ""PricePerUnit"" TEXT NOT NULL DEFAULT '0',
                    ""IsActive""     INTEGER NOT NULL DEFAULT 1,
                    ""CreatedAt""    TEXT NOT NULL,
                    ""UpdatedAt""    TEXT NOT NULL
                );
            ");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_Materials_Name"" ON ""Materials"" (""Name"");");

            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""MaterialUsages"" (
                    ""Id""              INTEGER NOT NULL CONSTRAINT ""PK_MaterialUsages"" PRIMARY KEY AUTOINCREMENT,
                    ""LocationId""      INTEGER NOT NULL,
                    ""MaterialId""      INTEGER NOT NULL,
                    ""EmployeeId""      INTEGER NULL,
                    ""Quantity""        TEXT NOT NULL,
                    ""UnitPriceAtTime"" TEXT NOT NULL DEFAULT '0',
                    ""Date""            TEXT NOT NULL,
                    ""Note""            TEXT NULL,
                    ""PhotoUrl""        TEXT NULL,
                    ""CreatedAt""       TEXT NOT NULL,
                    ""UpdatedAt""       TEXT NOT NULL,
                    CONSTRAINT ""FK_MaterialUsages_Locations_LocationId"" FOREIGN KEY (""LocationId"") REFERENCES ""Locations"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_MaterialUsages_Materials_MaterialId"" FOREIGN KEY (""MaterialId"") REFERENCES ""Materials"" (""Id"") ON DELETE RESTRICT,
                    CONSTRAINT ""FK_MaterialUsages_Employees_EmployeeId"" FOREIGN KEY (""EmployeeId"") REFERENCES ""Employees"" (""Id"") ON DELETE SET NULL
                );
            ");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_LocationId_Date"" ON ""MaterialUsages"" (""LocationId"", ""Date"");");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_LocationId_MaterialId"" ON ""MaterialUsages"" (""LocationId"", ""MaterialId"");");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_MaterialId"" ON ""MaterialUsages"" (""MaterialId"");");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_EmployeeId"" ON ""MaterialUsages"" (""EmployeeId"");");

            // V1.1 — add PricePerUnit / UnitPriceAtTime columns to existing rows on
            // databases that already had the V1 schema. We check pragma_table_info
            // first so EF's command logger doesn't print a noisy "fail:" line when
            // the column was already added by the migration on this same startup.
            {
                var conn = db.Database.GetDbConnection();
                var wasClosed = conn.State != System.Data.ConnectionState.Open;
                if (wasClosed) await conn.OpenAsync();
                try
                {
                    async Task<bool> ColumnExistsAsync(string table, string column)
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
                        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                    }

                    if (!await ColumnExistsAsync("Materials", "PricePerUnit"))
                        await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""Materials"" ADD COLUMN ""PricePerUnit"" TEXT NOT NULL DEFAULT '0';");
                    if (!await ColumnExistsAsync("MaterialUsages", "UnitPriceAtTime"))
                        await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""MaterialUsages"" ADD COLUMN ""UnitPriceAtTime"" TEXT NOT NULL DEFAULT '0';");
                }
                finally
                {
                    if (wasClosed) await conn.CloseAsync();
                }
            }
        }
        catch { /* best-effort SQLite self-heal */ }
    }

    // Self-heal: ensure Materials + MaterialUsages tables exist on PostgreSQL (production).
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Materials') THEN
                    CREATE TABLE ""Materials"" (
                        ""Id""           SERIAL PRIMARY KEY,
                        ""Name""         VARCHAR(200)  NOT NULL,
                        ""Unit""         VARCHAR(50)   NOT NULL,
                        ""PricePerUnit"" NUMERIC(12,4) NOT NULL DEFAULT 0,
                        ""IsActive""     BOOLEAN       NOT NULL DEFAULT TRUE,
                        ""CreatedAt""    TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""UpdatedAt""    TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_Materials_Name"" ON ""Materials"" (""Name"");
                END IF;

                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'MaterialUsages') THEN
                    CREATE TABLE ""MaterialUsages"" (
                        ""Id""              SERIAL PRIMARY KEY,
                        ""LocationId""      INTEGER NOT NULL REFERENCES ""Locations""(""Id"") ON DELETE CASCADE,
                        ""MaterialId""      INTEGER NOT NULL REFERENCES ""Materials""(""Id"") ON DELETE RESTRICT,
                        ""EmployeeId""      INTEGER NULL     REFERENCES ""Employees""(""Id"") ON DELETE SET NULL,
                        ""Quantity""        NUMERIC(12,3) NOT NULL,
                        ""UnitPriceAtTime"" NUMERIC(12,4) NOT NULL DEFAULT 0,
                        ""Date""            TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                        ""Note""            VARCHAR(2000) NULL,
                        ""PhotoUrl""        VARCHAR(1000) NULL,
                        ""CreatedAt""       TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""UpdatedAt""       TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_LocationId_Date""       ON ""MaterialUsages"" (""LocationId"", ""Date"");
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_LocationId_MaterialId"" ON ""MaterialUsages"" (""LocationId"", ""MaterialId"");
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_MaterialId""            ON ""MaterialUsages"" (""MaterialId"");
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_EmployeeId""            ON ""MaterialUsages"" (""EmployeeId"");
                END IF;

                -- V1.1: add PricePerUnit / UnitPriceAtTime to existing tables.
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Materials' AND column_name = 'PricePerUnit') THEN
                    ALTER TABLE ""Materials"" ADD COLUMN ""PricePerUnit"" NUMERIC(12,4) NOT NULL DEFAULT 0;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialUsages' AND column_name = 'UnitPriceAtTime') THEN
                    ALTER TABLE ""MaterialUsages"" ADD COLUMN ""UnitPriceAtTime"" NUMERIC(12,4) NOT NULL DEFAULT 0;
                END IF;

                -- The AddMaterialsAndUsage migration used Sqlite:Autoincrement which PostgreSQL ignores,
                -- leaving Id with no sequence. Ensure sequences exist and are wired as the column default.
                CREATE SEQUENCE IF NOT EXISTS ""Materials_Id_seq"";
                ALTER TABLE ""Materials"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""Materials_Id_seq""');
                PERFORM setval('""Materials_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""Materials""), 0) + 1, false);

                CREATE SEQUENCE IF NOT EXISTS ""MaterialUsages_Id_seq"";
                ALTER TABLE ""MaterialUsages"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""MaterialUsages_Id_seq""');
                PERFORM setval('""MaterialUsages_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""MaterialUsages""), 0) + 1, false);
            END $$;
        ");
    }

    // Self-heal: add NotificationsEnabled, WhatsAppEnabled, WhatsAppNumber columns to Employees (SQLite)
    if (string.IsNullOrEmpty(databaseUrl))
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        // Check for NotificationsEnabled
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name = 'NotificationsEnabled'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                using var addCmd = conn.CreateCommand();
                addCmd.CommandText = "ALTER TABLE \"Employees\" ADD COLUMN \"NotificationsEnabled\" INTEGER NOT NULL DEFAULT 1";
                try { await addCmd.ExecuteNonQueryAsync(); } catch { }
            }
        }

        // Check for WhatsAppEnabled
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name = 'WhatsAppEnabled'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                using var addCmd = conn.CreateCommand();
                addCmd.CommandText = "ALTER TABLE \"Employees\" ADD COLUMN \"WhatsAppEnabled\" INTEGER NOT NULL DEFAULT 0";
                try { await addCmd.ExecuteNonQueryAsync(); } catch { }
            }
        }

        // Check for WhatsAppNumber
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name = 'WhatsAppNumber'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                using var addCmd = conn.CreateCommand();
                addCmd.CommandText = "ALTER TABLE \"Employees\" ADD COLUMN \"WhatsAppNumber\" TEXT";
                try { await addCmd.ExecuteNonQueryAsync(); } catch { }
            }
        }

        // Check for NotificationsDeclineReason
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Employees') WHERE name = 'NotificationsDeclineReason'";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            if (count == 0)
            {
                using var addCmd = conn.CreateCommand();
                addCmd.CommandText = "ALTER TABLE \"Employees\" ADD COLUMN \"NotificationsDeclineReason\" TEXT";
                try { await addCmd.ExecuteNonQueryAsync(); } catch { }
            }
        }

        await conn.CloseAsync();
    }

    // Self-heal: add notification columns to Employees (PostgreSQL)
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Employees' AND column_name = 'NotificationsEnabled') THEN
                    ALTER TABLE ""Employees"" ADD COLUMN ""NotificationsEnabled"" BOOLEAN NOT NULL DEFAULT TRUE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Employees' AND column_name = 'WhatsAppEnabled') THEN
                    ALTER TABLE ""Employees"" ADD COLUMN ""WhatsAppEnabled"" BOOLEAN NOT NULL DEFAULT FALSE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Employees' AND column_name = 'WhatsAppNumber') THEN
                    ALTER TABLE ""Employees"" ADD COLUMN ""WhatsAppNumber"" VARCHAR(30);
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Employees' AND column_name = 'NotificationsDeclineReason') THEN
                    ALTER TABLE ""Employees"" ADD COLUMN ""NotificationsDeclineReason"" VARCHAR(500);
                END IF;
            END $$;
        ");
    }

    // Self-heal: ensure notification tables exist on PostgreSQL
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                -- Ensure PushSubscriptions table
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'PushSubscriptions') THEN
                    CREATE TABLE ""PushSubscriptions"" (
                        ""Id""          SERIAL PRIMARY KEY,
                        ""EmployeeId""  INTEGER NULL REFERENCES ""Employees""(""Id""),
                        ""Endpoint""    VARCHAR(2048) NOT NULL UNIQUE,
                        ""P256dhKey""   VARCHAR(200) NOT NULL,
                        ""AuthKey""     VARCHAR(200) NOT NULL,
                        ""UserAgent""   VARCHAR(500),
                        ""CreatedAt""   TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""LastUsedAt""  TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_PushSubscriptions_EmployeeId"" ON ""PushSubscriptions"" (""EmployeeId"");
                    CREATE SEQUENCE IF NOT EXISTS ""PushSubscriptions_Id_seq"";
                    ALTER TABLE ""PushSubscriptions"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""PushSubscriptions_Id_seq""');
                END IF;

                -- Ensure NotificationLogs table
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'NotificationLogs') THEN
                    CREATE TABLE ""NotificationLogs"" (
                        ""Id""                SERIAL PRIMARY KEY,
                        ""EmployeeId""        INTEGER NULL REFERENCES ""Employees""(""Id""),
                        ""Channel""           VARCHAR(50) NOT NULL,
                        ""TriggerType""       VARCHAR(50) NOT NULL,
                        ""Body""              VARCHAR(2000) NOT NULL,
                        ""TriggerDate""       DATE NOT NULL,
                        ""SentAt""            TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""Status""            VARCHAR(50) NOT NULL,
                        ""ProviderMessageId"" VARCHAR(500),
                        ""ErrorMessage""      VARCHAR(1000)
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_NotificationLogs_EmployeeId_Channel_TriggerType_TriggerDate""
                        ON ""NotificationLogs"" (""EmployeeId"", ""Channel"", ""TriggerType"", ""TriggerDate"");
                    CREATE INDEX IF NOT EXISTS ""IX_NotificationLogs_EmployeeId_TriggerDate""
                        ON ""NotificationLogs"" (""EmployeeId"", ""TriggerDate"");
                    CREATE SEQUENCE IF NOT EXISTS ""NotificationLogs_Id_seq"";
                    ALTER TABLE ""NotificationLogs"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""NotificationLogs_Id_seq""');
                END IF;

                -- Ensure NotificationConfigs table
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'NotificationConfigs') THEN
                    CREATE TABLE ""NotificationConfigs"" (
                        ""Id""                       INTEGER PRIMARY KEY,
                        ""NoActivity48hEnabled""     BOOLEAN NOT NULL DEFAULT FALSE,
                        ""NoActivity48hTime""        TIME WITHOUT TIME ZONE NOT NULL DEFAULT '18:00',
                        ""WorkingDaysOnly""          BOOLEAN NOT NULL DEFAULT TRUE,
                        ""ManagerSummaryEnabled""    BOOLEAN NOT NULL DEFAULT FALSE,
                        ""ManagerSummaryEmployeeId"" INTEGER NULL REFERENCES ""Employees""(""Id""),
                        ""LastTickAt""               TIMESTAMP WITHOUT TIME ZONE,
                        ""VapidPublicKey""           VARCHAR(1000) NOT NULL DEFAULT '',
                        ""VapidPrivateKey""          VARCHAR(1000) NOT NULL DEFAULT '',
                        ""VapidSubject""             VARCHAR(200) NOT NULL DEFAULT ''
                    );
                END IF;

                -- The AddNotifications migration was generated targeting SQLite which maps bool to INTEGER.
                -- When that migration runs against PostgreSQL the columns land as integer type, not boolean,
                -- causing a type-mismatch error on INSERT/UPDATE.
                -- Heal: cast every affected bool column to BOOLEAN if it is still stored as integer.
                -- NoActivity48hTime was stored as TEXT by the SQLite migration; cast to TIME.
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'NotificationConfigs'
                      AND column_name = 'NoActivity48hTime'
                      AND data_type   = 'text'
                ) THEN
                    ALTER TABLE ""NotificationConfigs""
                        ALTER COLUMN ""NoActivity48hTime"" TYPE TIME WITHOUT TIME ZONE USING (""NoActivity48hTime""::time);
                END IF;

                -- DateTime / DateOnly columns landed as `text` on PostgreSQL because the
                -- AddNotifications migration was generated for SQLite (DateTime → TEXT).
                -- Npgsql then refuses to read them: 'Reading as System.DateTime is not
                -- supported for fields having DataTypeName text'. Cast each affected
                -- column to its proper type. Guarded by data_type='text' so re-deploys
                -- are no-ops once healed; safe (USING ::timestamp/::date) if the existing
                -- text values are ISO-formatted (which EF wrote them as).
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'NotificationLogs'
                      AND column_name = 'SentAt'
                      AND data_type   = 'text'
                ) THEN
                    ALTER TABLE ""NotificationLogs""
                        ALTER COLUMN ""SentAt"" TYPE TIMESTAMP WITHOUT TIME ZONE USING (""SentAt""::timestamp);
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'NotificationLogs'
                      AND column_name = 'TriggerDate'
                      AND data_type   = 'text'
                ) THEN
                    ALTER TABLE ""NotificationLogs""
                        ALTER COLUMN ""TriggerDate"" TYPE DATE USING (""TriggerDate""::date);
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'PushSubscriptions'
                      AND column_name = 'CreatedAt'
                      AND data_type   = 'text'
                ) THEN
                    ALTER TABLE ""PushSubscriptions""
                        ALTER COLUMN ""CreatedAt"" TYPE TIMESTAMP WITHOUT TIME ZONE USING (""CreatedAt""::timestamp);
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'PushSubscriptions'
                      AND column_name = 'LastUsedAt'
                      AND data_type   = 'text'
                ) THEN
                    ALTER TABLE ""PushSubscriptions""
                        ALTER COLUMN ""LastUsedAt"" TYPE TIMESTAMP WITHOUT TIME ZONE USING (""LastUsedAt""::timestamp);
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'NotificationConfigs'
                      AND column_name = 'LastTickAt'
                      AND data_type   = 'text'
                ) THEN
                    ALTER TABLE ""NotificationConfigs""
                        ALTER COLUMN ""LastTickAt"" TYPE TIMESTAMP WITHOUT TIME ZONE USING (""LastTickAt""::timestamp);
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'NotificationConfigs'
                      AND column_name = 'ManagerSummaryEnabled'
                      AND data_type   = 'integer'
                ) THEN
                    ALTER TABLE ""NotificationConfigs""
                        ALTER COLUMN ""NoActivity48hEnabled""  DROP DEFAULT,
                        ALTER COLUMN ""NoActivity48hEnabled""  TYPE BOOLEAN USING (""NoActivity48hEnabled""::int::boolean),
                        ALTER COLUMN ""NoActivity48hEnabled""  SET DEFAULT FALSE,
                        ALTER COLUMN ""WorkingDaysOnly""       DROP DEFAULT,
                        ALTER COLUMN ""WorkingDaysOnly""       TYPE BOOLEAN USING (""WorkingDaysOnly""::int::boolean),
                        ALTER COLUMN ""WorkingDaysOnly""       SET DEFAULT TRUE,
                        ALTER COLUMN ""ManagerSummaryEnabled"" DROP DEFAULT,
                        ALTER COLUMN ""ManagerSummaryEnabled"" TYPE BOOLEAN USING (""ManagerSummaryEnabled""::int::boolean),
                        ALTER COLUMN ""ManagerSummaryEnabled"" SET DEFAULT FALSE;
                END IF;

                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Employees'
                      AND column_name = 'NotificationsEnabled'
                      AND data_type   = 'integer'
                ) THEN
                    -- Must drop the integer DEFAULT before changing type; PostgreSQL cannot cast it automatically.
                    ALTER TABLE ""Employees""
                        ALTER COLUMN ""NotificationsEnabled"" DROP DEFAULT,
                        ALTER COLUMN ""NotificationsEnabled"" TYPE BOOLEAN USING (""NotificationsEnabled""::int::boolean),
                        ALTER COLUMN ""NotificationsEnabled"" SET DEFAULT FALSE,
                        ALTER COLUMN ""WhatsAppEnabled"" DROP DEFAULT,
                        ALTER COLUMN ""WhatsAppEnabled""      TYPE BOOLEAN USING (""WhatsAppEnabled""::int::boolean),
                        ALTER COLUMN ""WhatsAppEnabled"" SET DEFAULT FALSE;
                END IF;

                -- Materials.IsActive was also bool→INTEGER in the AddMaterialsAndUsage migration.
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Materials'
                      AND column_name = 'IsActive'
                      AND data_type   = 'integer'
                ) THEN
                    ALTER TABLE ""Materials""
                        ALTER COLUMN ""IsActive"" DROP DEFAULT,
                        ALTER COLUMN ""IsActive"" TYPE BOOLEAN USING (""IsActive""::int::boolean),
                        ALTER COLUMN ""IsActive"" SET DEFAULT TRUE;
                END IF;

                -- The AddNotifications migration was generated for SQLite: it uses Sqlite:Autoincrement
                -- which EF's PostgreSQL provider ignores. The Id columns on PushSubscriptions and
                -- NotificationLogs land as INTEGER NOT NULL with no sequence, so every INSERT fails
                -- with: null value in column Id violates not-null constraint.
                -- Fix: create the sequence and attach it if the column has no default yet.
                -- This runs unconditionally (not inside the IF NOT EXISTS table block) so it also
                -- covers the case where EF MigrateAsync created the table before we could.
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'PushSubscriptions') THEN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'PushSubscriptions'
                          AND column_name = 'Id'
                          AND column_default IS NOT NULL
                    ) THEN
                        CREATE SEQUENCE IF NOT EXISTS ""PushSubscriptions_Id_seq"";
                        ALTER TABLE ""PushSubscriptions""
                            ALTER COLUMN ""Id"" SET DEFAULT nextval('""PushSubscriptions_Id_seq""');
                    END IF;
                END IF;

                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'NotificationLogs') THEN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'NotificationLogs'
                          AND column_name = 'Id'
                          AND column_default IS NOT NULL
                    ) THEN
                        CREATE SEQUENCE IF NOT EXISTS ""NotificationLogs_Id_seq"";
                        ALTER TABLE ""NotificationLogs""
                            ALTER COLUMN ""Id"" SET DEFAULT nextval('""NotificationLogs_Id_seq""');
                    END IF;
                END IF;
            END $$;
        ");
    }

    // Self-heal: ensure FeatureFlags table exists on SQLite.
    // Backstop in case the AddFeatureFlags EF migration has not been applied yet.
    if (string.IsNullOrEmpty(databaseUrl))
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""FeatureFlags"" (
                    ""Key""       TEXT NOT NULL CONSTRAINT ""PK_FeatureFlags"" PRIMARY KEY,
                    ""Enabled""   INTEGER NOT NULL DEFAULT 0,
                    ""UpdatedAt"" TEXT NOT NULL
                );
            ");
        }
        catch { /* best-effort SQLite self-heal */ }
    }

    // Self-heal: ensure FeatureFlags table exists on PostgreSQL (production).
    // Also guards against the well-known EF-SQLite-to-PostgreSQL trap where bool
    // columns from the migration land as INTEGER/text on Postgres — same trap
    // already documented around NotificationConfigs above. We cast to BOOLEAN
    // and TIMESTAMP if the EF migration created the table with the wrong types.
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'FeatureFlags') THEN
                    CREATE TABLE ""FeatureFlags"" (
                        ""Key""       VARCHAR(100) PRIMARY KEY,
                        ""Enabled""   BOOLEAN NOT NULL DEFAULT FALSE,
                        ""UpdatedAt"" TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                END IF;

                -- Cast Enabled to BOOLEAN if the EF migration left it as integer/text.
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'FeatureFlags'
                      AND column_name = 'Enabled'
                      AND data_type IN ('integer', 'text')
                ) THEN
                    ALTER TABLE ""FeatureFlags""
                        ALTER COLUMN ""Enabled"" DROP DEFAULT,
                        ALTER COLUMN ""Enabled"" TYPE BOOLEAN USING (""Enabled""::int::boolean),
                        ALTER COLUMN ""Enabled"" SET DEFAULT FALSE;
                END IF;

                -- Cast UpdatedAt to TIMESTAMP WITHOUT TIME ZONE if it landed as text.
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'FeatureFlags'
                      AND column_name = 'UpdatedAt'
                      AND data_type   = 'text'
                ) THEN
                    ALTER TABLE ""FeatureFlags""
                        ALTER COLUMN ""UpdatedAt"" TYPE TIMESTAMP WITHOUT TIME ZONE USING (""UpdatedAt""::timestamp);
                END IF;
            END $$;
        ");
    }

    // Seed FeatureFlags. Default OFF so the customer-facing prod environment ships with
    // hidden features invisible. The superadmin flips them on via the Funkcie card on
    // the Account page; the dev environment has its own DB so devs can keep them on.
    {
        var knownFlags = new[] { "Notifications", "CommanderIntegration" };
        foreach (var key in knownFlags)
        {
            if (!await db.FeatureFlags.AnyAsync(f => f.Key == key))
            {
                db.FeatureFlags.Add(new FeatureFlag { Key = key, Enabled = false, UpdatedAt = DateTime.UtcNow });
            }
        }
        await db.SaveChangesAsync();
    }

    // Seed / heal NotificationConfig with defaults on first run.
    // VAPID keys are auto-generated locally if env vars and DB are both empty so
    // the developer doesn't have to install any tooling to test push.
    {
        var existing = await db.NotificationConfigs.FirstOrDefaultAsync(c => c.Id == 1);

        var vapidPublic  = Environment.GetEnvironmentVariable("VAPID_PUBLIC_KEY")  ?? string.Empty;
        var vapidPrivate = Environment.GetEnvironmentVariable("VAPID_PRIVATE_KEY") ?? string.Empty;
        var vapidSubject = Environment.GetEnvironmentVariable("VAPID_SUBJECT")     ?? "mailto:support@example.com";

        if (existing == null)
        {
            // Auto-generate if not provided via env. Local dev convenience.
            if (string.IsNullOrEmpty(vapidPublic) || string.IsNullOrEmpty(vapidPrivate))
            {
                var generated = WebPush.VapidHelper.GenerateVapidKeys();
                vapidPublic  = generated.PublicKey;
                vapidPrivate = generated.PrivateKey;
                System.Console.WriteLine("NotificationConfig: generated new VAPID keys for first run.");
            }

            db.NotificationConfigs.Add(new NotificationConfig
            {
                Id = 1,
                NoActivity48hEnabled = false,
                NoActivity48hTime = TimeSpan.FromHours(18),
                WorkingDaysOnly = true,
                ManagerSummaryEnabled = false,
                ManagerSummaryEmployeeId = null,
                LastTickAt = null,
                VapidPublicKey = vapidPublic,
                VapidPrivateKey = vapidPrivate,
                VapidSubject = vapidSubject
            });
            await db.SaveChangesAsync();
        }
        else if (string.IsNullOrEmpty(existing.VapidPublicKey) || string.IsNullOrEmpty(existing.VapidPrivateKey))
        {
            // Existing row from a prior install with empty keys — heal it.
            if (string.IsNullOrEmpty(vapidPublic) || string.IsNullOrEmpty(vapidPrivate))
            {
                var generated = WebPush.VapidHelper.GenerateVapidKeys();
                vapidPublic  = generated.PublicKey;
                vapidPrivate = generated.PrivateKey;
                System.Console.WriteLine("NotificationConfig: filled in missing VAPID keys.");
            }

            existing.VapidPublicKey  = vapidPublic;
            existing.VapidPrivateKey = vapidPrivate;
            if (string.IsNullOrEmpty(existing.VapidSubject))
                existing.VapidSubject = vapidSubject;
            await db.SaveChangesAsync();
        }
    }

    // Seed the materials catalogue with ~10 common Slovak construction items on first run only.
    // Idempotent — only inserts items that don't already exist by name.
    if (!await db.Materials.AnyAsync())
    {
        var seed = new[]
        {
            ("Cement",     "vrece"),
            ("Voda",       "l"),
            ("Piesok",     "kg"),
            ("Štrk",       "kg"),
            ("Obklad",     "m²"),
            ("Dlažba",     "m²"),
            ("Omietka",    "kg"),
            ("Lepidlo",    "vrece"),
            ("Sadrokartón","ks"),
            ("Skrutky",    "ks"),
        };
        foreach (var (name, unit) in seed)
        {
            db.Materials.Add(new Material { Name = name, Unit = unit });
        }
        await db.SaveChangesAsync();
    }

    var userManager    = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    var targetUsername = builder.Configuration["AdminSeed:Username"];
    var targetPassword = builder.Configuration["AdminSeed:Password"];
    var targetDisplay  = builder.Configuration["AdminSeed:DisplayName"] ?? "Manažér";

    var superAdminUsername = builder.Configuration["SuperAdminSeed:Username"];
    var superAdminPassword = builder.Configuration["SuperAdminSeed:Password"];
    var superAdminDisplay  = builder.Configuration["SuperAdminSeed:DisplayName"] ?? "Superadmin";

    // Ensure both the regular admin (customer-facing) and the superadmin (internal feature-flag
    // controller) exist with their configured passwords. Looked up by username so renames via
    // env var require deleting the old row first — acceptable trade-off for two-user clarity.
    //
    // SECURITY: there are NO hardcoded fallback credentials. If username or password is missing
    // we log loudly and skip — better than silently using a leaked default. Existing users
    // keep their existing passwords on a config-less redeploy, so the API stays usable while
    // the operator fixes the Railway env vars.
    async Task SeedAdminUser(string label, string? username, string? password, string displayName)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine(
                $"[SeedAdminUser] {label} skipped — username or password not configured. " +
                $"Set {label}__Username and {label}__Password as Railway env vars (or via " +
                $"appsettings.Local.json for local dev). See SECRETS.md.");
            return;
        }

        var existing = await userManager.FindByNameAsync(username);
        if (existing == null)
        {
            var user = new AppUser { UserName = username, DisplayName = displayName };
            await userManager.CreateAsync(user, password);
        }
        else
        {
            if (existing.DisplayName != displayName)
            {
                existing.DisplayName = displayName;
                await userManager.UpdateAsync(existing);
            }
            // Always reset password so config/env changes take effect on next deploy
            var resetToken = await userManager.GeneratePasswordResetTokenAsync(existing);
            await userManager.ResetPasswordAsync(existing, resetToken, password);
        }
    }

    await SeedAdminUser("AdminSeed",      targetUsername,     targetPassword,     targetDisplay);
    await SeedAdminUser("SuperAdminSeed", superAdminUsername, superAdminPassword, superAdminDisplay);

    // Seed the system admin Employee used as the FK target for admin-uploaded gallery photos.
    // IsActive = false so it is invisible to the kiosk and employee lists.
    // We identify it by a fixed sentinel PIN value — never a real hashed PIN.
    const string adminEmployeePin = "SYSTEM_ADMIN_GALLERY_UPLOADER";
    var adminEmp = await db.Employees.FirstOrDefaultAsync(e => e.Pin == adminEmployeePin);
    if (adminEmp == null)
    {
        db.Employees.Add(new Employee
        {
            FirstName = "Admin",
            LastName  = "",
            Pin       = adminEmployeePin,
            IsActive  = false
        });
        await db.SaveChangesAsync();
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseHttpsRedirection();
}

app.UseCors("CorsPolicy");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

