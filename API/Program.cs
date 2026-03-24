using System.Text;
using API.Data;
using API.Models;
using API.Services;
using Azure.Storage.Blobs;
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

// Azure Blob Storage
var blobConnectionString = builder.Configuration["AzureBlobStorage:ConnectionString"];
if (!string.IsNullOrEmpty(blobConnectionString))
{
    builder.Services.AddSingleton(new BlobServiceClient(blobConnectionString));
    builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
}

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

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Migrate database and seed admin user
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
                    WHERE table_name = 'Employees' AND column_name = 'Id' AND column_default IS NOT NULL
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""Employees_Id_seq"";
                    ALTER TABLE ""Employees"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""Employees_Id_seq""');
                    PERFORM setval('""Employees_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""Employees""), 0) + 1, false);
                END IF;
                -- Locations
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'Locations' AND column_name = 'Id' AND column_default IS NOT NULL
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""Locations_Id_seq"";
                    ALTER TABLE ""Locations"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""Locations_Id_seq""');
                    PERFORM setval('""Locations_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""Locations""), 0) + 1, false);
                END IF;
                -- TimeEntries
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'TimeEntries' AND column_name = 'Id' AND column_default IS NOT NULL
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""TimeEntries_Id_seq"";
                    ALTER TABLE ""TimeEntries"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""TimeEntries_Id_seq""');
                    PERFORM setval('""TimeEntries_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""TimeEntries""), 0) + 1, false);
                END IF;
                -- AspNetRoleClaims
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'AspNetRoleClaims' AND column_name = 'Id' AND column_default IS NOT NULL
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""AspNetRoleClaims_Id_seq"";
                    ALTER TABLE ""AspNetRoleClaims"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""AspNetRoleClaims_Id_seq""');
                    PERFORM setval('""AspNetRoleClaims_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""AspNetRoleClaims""), 0) + 1, false);
                END IF;
                -- AspNetUserClaims
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = 'AspNetUserClaims' AND column_name = 'Id' AND column_default IS NOT NULL
                ) THEN
                    CREATE SEQUENCE IF NOT EXISTS ""AspNetUserClaims_Id_seq"";
                    ALTER TABLE ""AspNetUserClaims"" ALTER COLUMN ""Id"" SET DEFAULT nextval('""AspNetUserClaims_Id_seq""');
                    PERFORM setval('""AspNetUserClaims_Id_seq""', COALESCE((SELECT MAX(""Id"") FROM ""AspNetUserClaims""), 0) + 1, false);
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

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    if (!userManager.Users.Any())
    {
        var admin = new AppUser
        {
            UserName = builder.Configuration["AdminSeed:Username"] ?? "admin",
            DisplayName = builder.Configuration["AdminSeed:DisplayName"] ?? "Administrator"
        };
        await userManager.CreateAsync(admin, builder.Configuration["AdminSeed:Password"] ?? "Admin123!");
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

