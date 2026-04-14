using System.Text;
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

// JWT Authentication
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new Exception("JWT key not configured");
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

// App services
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IPinHasher, PinHasher>();
builder.Services.AddScoped<IEmailService, EmailService>();

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        var origins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>()
            ?? new[] { "http://localhost:4200" };
        policy.WithOrigins(origins)
            .AllowAnyMethod()
            .AllowAnyHeader();
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
            // Check if PinPlain already exists in Employees
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

            await conn.CloseAsync();
        }
        catch { /* best-effort — MigrateAsync will surface any real problems */ }
    }

    await db.Database.MigrateAsync();

    // Self-heal: add sequences/defaults to int PK columns that were created without them
    if (!string.IsNullOrEmpty(databaseUrl))
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DO $$
            BEGIN
                -- Employees
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Employees' AND column_name = 'Id'
                      AND (column_default IS NOT NULL OR is_identity = 'YES')
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""Employees_Id_seq"";
                    ALTER TABLE ""Employees"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""Employees_Id_seq""');
                    PERFORM setval('""Employees_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""Employees""), 0) + 1, false);
                END IF;
                -- Locations
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Locations' AND column_name = 'Id'
                      AND (column_default IS NOT NULL OR is_identity = 'YES')
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""Locations_Id_seq"";
                    ALTER TABLE ""Locations"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""Locations_Id_seq""');
                    PERFORM setval('""Locations_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""Locations""), 0) + 1, false);
                END IF;
                -- TimeEntries
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'TimeEntries' AND column_name = 'Id'
                      AND (column_default IS NOT NULL OR is_identity = 'YES')
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""TimeEntries_Id_seq"";
                    ALTER TABLE ""TimeEntries"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""TimeEntries_Id_seq""');
                    PERFORM setval('""TimeEntries_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""TimeEntries""), 0) + 1, false);
                END IF;
                -- AspNetRoleClaims
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'AspNetRoleClaims' AND column_name = 'Id'
                      AND (column_default IS NOT NULL OR is_identity = 'YES')
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""AspNetRoleClaims_Id_seq"";
                    ALTER TABLE ""AspNetRoleClaims"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""AspNetRoleClaims_Id_seq""');
                    PERFORM setval('""AspNetRoleClaims_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""AspNetRoleClaims""), 0) + 1, false);
                END IF;
                -- AspNetUserClaims
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'AspNetUserClaims' AND column_name = 'Id'
                      AND (column_default IS NOT NULL OR is_identity = 'YES')
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""AspNetUserClaims_Id_seq"";
                    ALTER TABLE ""AspNetUserClaims"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""AspNetUserClaims_Id_seq""');
                    PERFORM setval('""AspNetUserClaims_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""AspNetUserClaims""), 0) + 1, false);
                END IF;
                -- WorkPhotos: AddWorkPhotos migration used SQLite-only Autoincrement annotation,
                -- so no SERIAL/IDENTITY was created on PostgreSQL. Fix it here.
                IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'WorkPhotos')
                   AND NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'WorkPhotos' AND column_name = 'Id'
                      AND (column_default IS NOT NULL OR is_identity = 'YES')
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""WorkPhotos_Id_seq"";
                    ALTER TABLE ""WorkPhotos"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""WorkPhotos_Id_seq""');
                    PERFORM setval('""WorkPhotos_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""WorkPhotos""), 0) + 1, false);
                END IF;
            END $$;
        ");
    }

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

    var userManager    = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    var targetUsername = builder.Configuration["AdminSeed:Username"]    ?? "vladosroka";
    var targetPassword = builder.Configuration["AdminSeed:Password"]    ?? "Nikolasko1";
    var targetDisplay  = builder.Configuration["AdminSeed:DisplayName"] ?? "Administrator";

    var admin = userManager.Users.FirstOrDefault();
    if (admin == null)
    {
        // First run — create the admin user
        admin = new AppUser { UserName = targetUsername, DisplayName = targetDisplay };
        await userManager.CreateAsync(admin, targetPassword);
    }
    else
    {
        // Sync username if it changed
        if (admin.UserName != targetUsername)
        {
            admin.UserName           = targetUsername;
            admin.NormalizedUserName = targetUsername.ToUpperInvariant();
            admin.DisplayName        = targetDisplay;
            await userManager.UpdateAsync(admin);
        }
        // Always reset password so Railway env var changes take effect on next deploy
        var token = await userManager.GeneratePasswordResetTokenAsync(admin);
        await userManager.ResetPasswordAsync(admin, token, targetPassword);
    }

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

