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
//
// PostgreSQL only — local dev now runs Postgres via the docker-compose.yml at repo
// root, the same engine prod runs on Railway. Source priority:
//   1. DATABASE_URL env var (Railway prod + dev convention) — parsed from the
//      postgres://user:pass@host:port/db URL form Railway hands us.
//   2. ConnectionStrings:DefaultConnection from config — local dev default lives
//      in appsettings.json and matches the docker-compose container; devs override
//      via appsettings.Local.json if they run Postgres natively / on a different port.
// Throws at startup if neither is set, so a misconfigured env never silently
// downgrades to an in-memory or fallback DB.
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
string? npgsqlConnectionString;
if (!string.IsNullOrEmpty(databaseUrl))
{
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(new char[] { ':' }, 2);
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = Uri.UnescapeDataString(userInfo[1]);
    var database = uri.AbsolutePath.TrimStart('/');
    npgsqlConnectionString = $"Host={uri.Host};Port={uri.Port};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true";
}
else
{
    npgsqlConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
}

if (string.IsNullOrWhiteSpace(npgsqlConnectionString))
    throw new InvalidOperationException(
        "No database connection configured. Set DATABASE_URL (prod/Railway) or ConnectionStrings:DefaultConnection (local dev — see API/appsettings.Local.example.json and the docker-compose.yml at repo root).");

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(npgsqlConnectionString);
    options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// Self-heal blocks further down gate on `databaseUrl` being non-empty — that
// gate originally meant "we're on Postgres in production, run the Postgres
// self-heals". Now that local dev runs Postgres too (via the root
// docker-compose.yml), every self-heal must run on every startup. Promote
// `databaseUrl` to a non-empty sentinel on the local-dev path so the existing
// gates read as "yes, run the Postgres self-heal". The sentinel value is never
// used as a connection string — UseNpgsql above already received the resolved
// connection string. Follow-up cleanup will remove the gating altogether.
if (string.IsNullOrEmpty(databaseUrl)) databaseUrl = "(local-dev-postgres)";

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
builder.Services.AddScoped<IPayrollExcelExportService, PayrollExcelExportService>();

// Material purchases Excel export (ClosedXML) — admin Materiál → Nákupy tab
builder.Services.AddScoped<IMaterialPurchasesExcelExportService, MaterialPurchasesExcelExportService>();

// Invoice scanning — Google Document AI Invoice Parser. Singleton because the
// client holds a long-lived gRPC channel; per-request scope would tear that
// down on every upload. Construction throws if credentials are missing, so
// the API fails loud on the InvoiceScanning code path (controllers only
// resolve this when the flag is on).
// Registered when EITHER credentials option is set:
//   - Google:DocumentAi:CredentialsPath (file path — local dev)
//   - Google:DocumentAi:CredentialsJson (inline — Railway env var)
// When neither is set, registration is skipped and the InvoicesController
// surfaces a Slovak "OCR not configured" error instead of crashing.
{
    var hasPath = !string.IsNullOrWhiteSpace(builder.Configuration["Google:DocumentAi:CredentialsPath"]);
    var hasJson = !string.IsNullOrWhiteSpace(builder.Configuration["Google:DocumentAi:CredentialsJson"]);
    if (hasPath || hasJson)
    {
        builder.Services.AddSingleton<IDocumentAiClient, DocumentAiClient>();
    }
}
// InvoiceParser is the SK-specific mapper from Document AI output to our
// domain shape (header + delivery lists + lines). Pure logic, no I/O,
// scoped lifetime is fine. Always registered — the controller decides
// whether OCR is available and gates accordingly.
builder.Services.AddScoped<IInvoiceParser, InvoiceParser>();

// Vision-LLM fallback for scans the deterministic parser can't reconcile
// (Gemini — same data boundary as Document AI). Always registered;
// IsConfigured stays false until Gemini__ApiKey is set and every caller
// treats an unconfigured / failed extraction as "no result".
builder.Services.AddHttpClient<ILlmInvoiceExtractor, GeminiInvoiceExtractor>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
});

// App services
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPinHasher, PinHasher>();
builder.Services.AddScoped<IWageService, WageService>();
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

// OpenRouteService — road-snapping for the Commander ride map. Optional;
// without an API key the client returns null and the frontend keeps the
// existing dashed straight line. Free tier is 2 000 directions/day,
// 40/min — more than enough with the in-memory cache. See SECRETS.md.
builder.Services.AddHttpClient<IRouteSnappingService, OpenRouteServiceClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
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

    // PostgreSQL pre-migration: mark AddNotifications as applied if its tables already exist
    // (created by the post-migration self-heal block on a previous deployment, before the
    // migration file was added to the project). Prevents "table already exists" on MigrateAsync.
    if (!string.IsNullOrEmpty(databaseUrl)) // always true post-2026-05-01 (local dev is also Postgres)
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

    // Fix DateTime + decimal columns left as `text` by the SQLite→PostgreSQL migration
    // scar. Two parallel passes:
    //   1. DateTime: scan every text-typed column whose name matches one of our DateTime
    //      domain fields and convert it to `timestamp without time zone`.
    //   2. Decimal: a small allowlist of (table, column, precision, scale) tuples,
    //      converting any still-text-typed match to `numeric(p,s)`.
    // Both passes are idempotent — once the column is fixed they yield zero rows on the
    // next deploy, and the EF Core SQL logger has been dropped to Warning so they emit
    // zero log lines on a healthy DB.
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE rec RECORD;
            BEGIN
                -- ── DateTime columns: text → timestamp without time zone ──
                FOR rec IN
                    SELECT table_name, column_name
                    FROM information_schema.columns
                    WHERE table_schema = 'public'
                      AND data_type = 'text'
                      AND column_name IN (
                          'CreatedAt','UpdatedAt',
                          'ClockIn','ClockOut',
                          'Date','TriggerDate','LastTickAt','LastUsedAt',
                          'StartedAt','FinishedAt','SentAt'
                      )
                LOOP
                    EXECUTE format(
                        'ALTER TABLE %I ALTER COLUMN %I TYPE timestamp without time zone USING %I::timestamptz::timestamp',
                        rec.table_name, rec.column_name, rec.column_name
                    );
                END LOOP;

                -- ── Decimal columns: text → numeric(p,s) ──
                -- Allowlist matches the [HasPrecision(p,s)] declarations in AppDbContext.
                FOR rec IN
                    SELECT * FROM (VALUES
                        ('Materials',      'PricePerUnit',     12, 4),
                        ('MaterialUsages', 'Quantity',         12, 3),
                        ('MaterialUsages', 'UnitPriceAtTime',  12, 4)
                    ) AS t(table_name, column_name, prec, scl)
                LOOP
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns c
                        WHERE c.table_schema = 'public'
                          AND c.table_name   = rec.table_name
                          AND c.column_name  = rec.column_name
                          AND c.data_type    = 'text'
                    ) THEN
                        -- DROP DEFAULT first: the text column was created with DEFAULT '0'
                        -- (or similar) by the original self-heal block, and Postgres refuses
                        -- to auto-cast a text default to numeric during ALTER TYPE
                        -- (SqlState 42804). We drop, convert, and re-add a numeric default
                        -- in one statement. COALESCE handles legacy rows that wrote empty
                        -- string into the text column — empty becomes 0 rather than tripping
                        -- NOT NULL on the ALTER.
                        EXECUTE format(
                            'ALTER TABLE %I '
                            || 'ALTER COLUMN %I DROP DEFAULT, '
                            || 'ALTER COLUMN %I TYPE numeric(%s,%s) USING COALESCE(NULLIF(%I, '''')::numeric, 0), '
                            || 'ALTER COLUMN %I SET DEFAULT 0',
                            rec.table_name,
                            rec.column_name,
                            rec.column_name, rec.prec, rec.scl, rec.column_name,
                            rec.column_name
                        );
                    END IF;
                END LOOP;
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

    // Self-heal: MaterialPurchases + MaterialPurchaseLines (PostgreSQL).
    // Backstop for the AddMaterialPurchases EF migration. Idempotent — does
    // nothing on a fresh DB where the migration created the tables first.
    // See MATERIAL_PURCHASES_PLAN.md for the schema rationale.
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'MaterialPurchases') THEN
                    CREATE TABLE ""MaterialPurchases"" (
                        ""Id""              SERIAL PRIMARY KEY,
                        ""PurchaseDate""    TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                        ""EmployeeId""      INTEGER NOT NULL REFERENCES ""Employees""(""Id"")  ON DELETE RESTRICT,
                        ""LocationId""      INTEGER NULL     REFERENCES ""Locations""(""Id"")  ON DELETE SET NULL,
                        ""TimeEntryId""     INTEGER NULL     REFERENCES ""TimeEntries""(""Id"") ON DELETE SET NULL,
                        ""SupplierName""    VARCHAR(200)  NULL,
                        ""ReceiptPhotoUrl"" VARCHAR(1000) NULL,
                        ""Note""            VARCHAR(500)  NULL,
                        ""TotalCost""       NUMERIC(14,4) NOT NULL DEFAULT 0,
                        ""CreatedAt""       TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""UpdatedAt""       TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialPurchases_PurchaseDate""              ON ""MaterialPurchases"" (""PurchaseDate"");
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialPurchases_EmployeeId_PurchaseDate""   ON ""MaterialPurchases"" (""EmployeeId"", ""PurchaseDate"");
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialPurchases_LocationId""                ON ""MaterialPurchases"" (""LocationId"");
                END IF;

                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'MaterialPurchaseLines') THEN
                    CREATE TABLE ""MaterialPurchaseLines"" (
                        ""Id""              SERIAL PRIMARY KEY,
                        ""PurchaseId""      INTEGER NOT NULL REFERENCES ""MaterialPurchases""(""Id"") ON DELETE CASCADE,
                        ""MaterialId""      INTEGER NULL     REFERENCES ""Materials""(""Id"")        ON DELETE SET NULL,
                        ""MaterialNameRaw"" VARCHAR(200)  NOT NULL,
                        ""Unit""            VARCHAR(50)   NOT NULL,
                        ""Quantity""        NUMERIC(12,3) NOT NULL,
                        ""UnitPrice""       NUMERIC(12,4) NOT NULL,
                        ""LineTotal""       NUMERIC(14,4) NOT NULL,
                        ""CreatedAt""       TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialPurchaseLines_PurchaseId"" ON ""MaterialPurchaseLines"" (""PurchaseId"");
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialPurchaseLines_MaterialId"" ON ""MaterialPurchaseLines"" (""MaterialId"");
                END IF;

                -- NOTE: do NOT add the ""CREATE SEQUENCE + ALTER COLUMN SET DEFAULT nextval""
                -- safety net here. That pattern is only correct for the older
                -- Materials/MaterialUsages tables whose 2026-04-26 migration was generated
                -- with SQLite annotations and left no Id-generation strategy on PostgreSQL.
                -- The AddMaterialPurchases migration is Postgres-native and creates Id as
                -- ""GENERATED BY DEFAULT AS IDENTITY"". Running ALTER ... SET DEFAULT on top
                -- of an identity column raises Postgres error 42601 — see git log of this
                -- file on 2026-05-06 for the full story.
                -- For the same reason, the self-heal CREATE TABLE branch above uses SERIAL,
                -- which auto-creates an implicit sequence and works without further fixup.
            END $$;
        ");
    }

    // Self-heal: WorkDiaries table + TimeEntries.ProofOfWorkSkipped column (PostgreSQL).
    // Backstop for the AddProofOfWorkChoices EF migration. Idempotent — does
    // nothing on a fresh DB where the migration created the table + column first.
    // See PROOF_OF_WORK_UX_PLAN.md for the schema rationale.
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'WorkDiaries') THEN
                    CREATE TABLE ""WorkDiaries"" (
                        ""Id""            SERIAL PRIMARY KEY,
                        ""EmployeeId""    INTEGER NULL     REFERENCES ""Employees""(""Id"")  ON DELETE SET NULL,
                        ""LocationId""    INTEGER NOT NULL REFERENCES ""Locations""(""Id"")  ON DELETE RESTRICT,
                        ""TimeEntryId""   INTEGER NULL     REFERENCES ""TimeEntries""(""Id"") ON DELETE SET NULL,
                        ""Date""          TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                        ""BodyText""      TEXT          NOT NULL,
                        ""AttachmentUrl"" VARCHAR(1000) NULL,
                        ""CreatedAt""     TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""UpdatedAt""     TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_WorkDiaries_LocationId_Date"" ON ""WorkDiaries"" (""LocationId"", ""Date"");
                    CREATE INDEX IF NOT EXISTS ""IX_WorkDiaries_EmployeeId_Date"" ON ""WorkDiaries"" (""EmployeeId"", ""Date"");
                    CREATE INDEX IF NOT EXISTS ""IX_WorkDiaries_TimeEntryId""     ON ""WorkDiaries"" (""TimeEntryId"");
                END IF;

                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'TimeEntries' AND column_name = 'ProofOfWorkSkipped'
                ) THEN
                    ALTER TABLE ""TimeEntries"" ADD COLUMN ""ProofOfWorkSkipped"" BOOLEAN NOT NULL DEFAULT FALSE;
                END IF;
            END $$;
        ");
    }

    // Self-heal: InvoiceDocuments table + new columns on MaterialPurchases /
    // MaterialPurchaseLines (PostgreSQL). Backstop for the AddInvoiceScanning
    // EF migration. Idempotent — does nothing on a fresh DB where the migration
    // created the table + columns first. See INVOICE_SCANNING_PLAN.md §Schema.
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                -- ── InvoiceDocuments table ─────────────────────────
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'InvoiceDocuments') THEN
                    CREATE TABLE ""InvoiceDocuments"" (
                        ""Id""                  SERIAL PRIMARY KEY,
                        ""InvoiceNumber""       VARCHAR(100)  NOT NULL,
                        ""SupplierName""        VARCHAR(200)  NOT NULL,
                        ""SupplierIco""         VARCHAR(50)   NULL,
                        ""SupplierIcDph""       VARCHAR(50)   NULL,
                        ""SupplierIban""        VARCHAR(50)   NULL,
                        ""IssueDate""           DATE          NOT NULL,
                        ""DeliveryDate""        DATE          NULL,
                        ""DueDate""             DATE          NULL,
                        ""PeriodFrom""          DATE          NULL,
                        ""PeriodTo""            DATE          NULL,
                        ""Currency""            VARCHAR(3)    NOT NULL DEFAULT 'EUR',
                        ""TotalExclVat""        NUMERIC(14,2) NOT NULL DEFAULT 0,
                        ""TotalVat""            NUMERIC(14,2) NOT NULL DEFAULT 0,
                        ""TotalInclVat""        NUMERIC(14,2) NOT NULL DEFAULT 0,
                        ""PdfUrl""              VARCHAR(1000) NOT NULL,
                        ""RawOcrJson""          TEXT          NOT NULL,
                        ""Status""              VARCHAR(30)   NOT NULL,
                        ""ReconciliationOk""    BOOLEAN       NOT NULL DEFAULT FALSE,
                        ""ReconciliationNote""  VARCHAR(500)  NULL,
                        ""UploadedBy""          VARCHAR(100)  NOT NULL,
                        ""UploadedAt""          TIMESTAMP WITHOUT TIME ZONE NOT NULL DEFAULT NOW(),
                        ""CommittedBy""         VARCHAR(100)  NULL,
                        ""CommittedAt""         TIMESTAMP WITHOUT TIME ZONE NULL,
                        ""Note""                VARCHAR(2000) NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS ""IX_InvoiceDocuments_InvoiceNumber_SupplierIco"" ON ""InvoiceDocuments"" (""InvoiceNumber"", ""SupplierIco"");
                    CREATE INDEX IF NOT EXISTS ""IX_InvoiceDocuments_Status""                          ON ""InvoiceDocuments"" (""Status"");
                    CREATE INDEX IF NOT EXISTS ""IX_InvoiceDocuments_UploadedAt""                      ON ""InvoiceDocuments"" (""UploadedAt"");
                    CREATE INDEX IF NOT EXISTS ""IX_InvoiceDocuments_SupplierName_IssueDate""          ON ""InvoiceDocuments"" (""SupplierName"", ""IssueDate"");
                END IF;

                -- ── MaterialPurchases new columns ───────────────────
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchases' AND column_name = 'InvoiceDocumentId') THEN
                    ALTER TABLE ""MaterialPurchases"" ADD COLUMN ""InvoiceDocumentId"" INTEGER NULL REFERENCES ""InvoiceDocuments""(""Id"") ON DELETE SET NULL;
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialPurchases_InvoiceDocumentId"" ON ""MaterialPurchases"" (""InvoiceDocumentId"");
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchases' AND column_name = 'DeliveryNoteRef') THEN
                    ALTER TABLE ""MaterialPurchases"" ADD COLUMN ""DeliveryNoteRef"" VARCHAR(100) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchases' AND column_name = 'PickedUpBy') THEN
                    ALTER TABLE ""MaterialPurchases"" ADD COLUMN ""PickedUpBy"" VARCHAR(200) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchases' AND column_name = 'DeliveryNote') THEN
                    ALTER TABLE ""MaterialPurchases"" ADD COLUMN ""DeliveryNote"" VARCHAR(2000) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchases' AND column_name = 'SubtotalExclVat') THEN
                    ALTER TABLE ""MaterialPurchases"" ADD COLUMN ""SubtotalExclVat"" NUMERIC(14,2) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchases' AND column_name = 'SubtotalVat') THEN
                    ALTER TABLE ""MaterialPurchases"" ADD COLUMN ""SubtotalVat"" NUMERIC(14,2) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchases' AND column_name = 'AkciaName') THEN
                    ALTER TABLE ""MaterialPurchases"" ADD COLUMN ""AkciaName"" VARCHAR(200) NULL;
                    -- Self-heal already added the column. Mark the matching EF
                    -- migration as applied so MigrateAsync at startup doesn't
                    -- try to ALTER TABLE a second time and crash with
                    -- ""column already exists"". Matches the AddWorkPhotos pattern.
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260526083440_AddMaterialPurchaseAkciaName', '9.0.0')
                    ON CONFLICT DO NOTHING;
                END IF;

                -- ── MaterialPurchaseLines new columns ───────────────
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'SupplierItemCode') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""SupplierItemCode"" VARCHAR(50) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'ListPriceExclVat') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""ListPriceExclVat"" NUMERIC(12,4) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'DiscountPercent') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""DiscountPercent"" NUMERIC(5,2) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'UnitPriceInclVat') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""UnitPriceInclVat"" NUMERIC(12,4) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'VatRate') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""VatRate"" NUMERIC(5,2) NOT NULL DEFAULT 23;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'IsReverseCharge') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""IsReverseCharge"" BOOLEAN NOT NULL DEFAULT FALSE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'IsService') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""IsService"" BOOLEAN NOT NULL DEFAULT FALSE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialPurchaseLines' AND column_name = 'LineEditHistory') THEN
                    ALTER TABLE ""MaterialPurchaseLines"" ADD COLUMN ""LineEditHistory"" TEXT NULL;
                END IF;

                -- ── MaterialUsages.SourceMaterialPurchaseLineId (Option A) ─
                -- Origin tag on auto-created usages so a discarded invoice
                -- cascades cleanly. Matches INVOICE_SCANNING_PLAN.md ""Option A"".
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialUsages' AND column_name = 'SourceMaterialPurchaseLineId') THEN
                    ALTER TABLE ""MaterialUsages"" ADD COLUMN ""SourceMaterialPurchaseLineId"" INTEGER NULL REFERENCES ""MaterialPurchaseLines""(""Id"") ON DELETE CASCADE;
                    CREATE INDEX IF NOT EXISTS ""IX_MaterialUsages_SourceMaterialPurchaseLineId"" ON ""MaterialUsages"" (""SourceMaterialPurchaseLineId"");
                END IF;

                -- ── MaterialUsages.IsService (Item C of INVOICE_SCANNING_V1_FOLLOWUPS.md) ─
                -- Mirror of MaterialPurchaseLine.IsService so the per-Pracovisko
                -- Spotreba view can render a 'Faktúra (služba)' badge on rentals
                -- without joining back through SourceMaterialPurchaseLineId.
                -- Default FALSE: all historical usages were materials.
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'MaterialUsages' AND column_name = 'IsService') THEN
                    ALTER TABLE ""MaterialUsages"" ADD COLUMN ""IsService"" BOOLEAN NOT NULL DEFAULT FALSE;
                END IF;

                -- ── InvoiceDocuments.ScanSource / ScanPageCount (V1.1 camera scan) ─
                -- Provenance for whether the invoice arrived via file-picker
                -- ('file') or in-app camera ('camera'). Page count populated only
                -- on camera uploads. See INVOICE_SCANNING_CAMERA_PLAN.md.
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'InvoiceDocuments' AND column_name = 'ScanSource') THEN
                    ALTER TABLE ""InvoiceDocuments"" ADD COLUMN ""ScanSource"" VARCHAR(20) NOT NULL DEFAULT 'file';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'InvoiceDocuments' AND column_name = 'ScanPageCount') THEN
                    ALTER TABLE ""InvoiceDocuments"" ADD COLUMN ""ScanPageCount"" INTEGER NULL;
                END IF;

                -- ── Payroll: Employee.HourlyWage / TimeEntry.WageAtTime / EmployeeAdvance ─
                -- PAYROLL_AND_PNL_PLAN.md schema. HourlyWage NULL means not set yet;
                -- new TimeEntry rows snapshot the value at insert time so past
                -- payroll calculations stay correct after wage changes.
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Employees' AND column_name = 'HourlyWage') THEN
                    ALTER TABLE ""Employees"" ADD COLUMN ""HourlyWage"" NUMERIC(12,4) NULL;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'TimeEntries' AND column_name = 'WageAtTime') THEN
                    ALTER TABLE ""TimeEntries"" ADD COLUMN ""WageAtTime"" NUMERIC(12,4) NOT NULL DEFAULT 0;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'EmployeeAdvances') THEN
                    CREATE TABLE ""EmployeeAdvances"" (
                        ""Id""          SERIAL PRIMARY KEY,
                        ""EmployeeId""  INTEGER NOT NULL REFERENCES ""Employees""(""Id"") ON DELETE RESTRICT,
                        ""Date""        TIMESTAMP NOT NULL,
                        ""Amount""      NUMERIC(12,2) NOT NULL,
                        ""Note""        VARCHAR(500) NULL,
                        ""CreatedAt""   TIMESTAMP NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC'),
                        ""UpdatedAt""   TIMESTAMP NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC'),
                        ""CreatedBy""   VARCHAR(100) NULL
                    );
                    CREATE INDEX IF NOT EXISTS ""IX_EmployeeAdvances_EmployeeId_Date"" ON ""EmployeeAdvances"" (""EmployeeId"", ""Date"");
                END IF;

                -- ── P&L: Location.ContractValue (Príjem / zmluvná hodnota) ─
                -- PAYROLL_AND_PNL_PLAN.md schema. NULL = no contract recorded;
                -- the Náklady a zisk card shows the revenue row as ""—"" and
                -- hides the profit total.
                IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Locations' AND column_name = 'ContractValue') THEN
                    ALTER TABLE ""Locations"" ADD COLUMN ""ContractValue"" NUMERIC(14,2) NULL;
                END IF;
            END $$;
        ");
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
        var knownFlags = new[] { "Notifications", "CommanderIntegration", "MaterialPurchases", "ProofOfWorkChoices", "InvoiceScanning", "InvoiceCameraScan", "PayrollAndPnL" };
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

    // Seed the materials catalogue with ~10 common Slovak construction items.
    //
    // Per-item idempotent (runs on every startup, not just first-run):
    //   - if no row with this Name exists → insert it (active, default price 0)
    //   - if a row with this Name exists and is inactive → flip IsActive back on
    //   - if a row with this Name exists and is active → leave it alone
    //
    // Customer edits to Unit / PricePerUnit on existing rows are preserved; we only
    // touch IsActive on the predefined names. Rationale: in production we observed
    // a soft-deleted "Cement" row blocking a Create with the same name (catalogue
    // listing showed empty, the Create endpoint 409'd). The Create endpoint now
    // resurrects soft-deleted matches; this seed block is the belt-and-braces
    // backstop that guarantees the standard 10 items are always present and
    // visible after a deploy, regardless of how the DB got into its current state.
    //
    // If the customer explicitly deactivates one of these later, it WILL come back
    // active on the next deploy. That's the trade-off we accept for the safety net.
    var predefinedMaterials = new[]
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
    // Pull only the columns we need (Id / Name / IsActive). DO NOT materialise
    // full Material entities here — production has legacy rows where CreatedAt /
    // UpdatedAt are stored as `text` (a SQLite→PostgreSQL migration scar) and
    // EF will throw InvalidCastException trying to read them as DateTime. The
    // type-fix self-heal block earlier in this file already corrects those
    // columns, but this projection is the safety belt that keeps startup alive
    // even if something about the order of operations regresses.
    var existingIndex = await db.Materials
        .Select(m => new { m.Id, m.Name, m.IsActive })
        .ToListAsync();
    var firstByKey = new Dictionary<string, (int Id, bool IsActive)>(StringComparer.Ordinal);
    foreach (var row in existingIndex)
    {
        var k = (row.Name ?? string.Empty).Trim().ToLowerInvariant();
        if (!firstByKey.ContainsKey(k)) firstByKey[k] = (row.Id, row.IsActive);
    }
    var idsToReactivate = new List<int>();
    var namesToInsert = new List<(string name, string unit)>();
    foreach (var (name, unit) in predefinedMaterials)
    {
        var key = name.Trim().ToLowerInvariant();
        if (firstByKey.TryGetValue(key, out var hit))
        {
            if (!hit.IsActive) idsToReactivate.Add(hit.Id);
        }
        else
        {
            namesToInsert.Add((name, unit));
        }
    }
    // Reactivate via ExecuteUpdateAsync (EF Core 7+) so we never have to
    // materialise the entity — keeps us clear of the legacy text-typed
    // CreatedAt / UpdatedAt columns. Generates a pure UPDATE … WHERE Id IN (…)
    // that works on both SQLite (local dev) and PostgreSQL (production).
    if (idsToReactivate.Count > 0)
    {
        await db.Materials
            .Where(m => idsToReactivate.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, true));
    }
    foreach (var (name, unit) in namesToInsert)
    {
        db.Materials.Add(new Material { Name = name, Unit = unit });
    }
    if (namesToInsert.Count > 0)
    {
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

