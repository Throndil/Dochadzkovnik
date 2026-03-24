using System.Text;
using API.Data;
using API.Models;
using API.Services;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;


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
        else {
            options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"));
        
        }
    }

);

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

