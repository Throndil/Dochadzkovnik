using API.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace API.Data;

public class AppDbContext : IdentityDbContext<AppUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Car> Cars => Set<Car>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<WorkPhoto> WorkPhotos => Set<WorkPhoto>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<MaterialUsage> MaterialUsages => Set<MaterialUsage>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<NotificationConfig> NotificationConfigs => Set<NotificationConfig>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.Pin).IsUnique();
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Pin).HasMaxLength(256).IsRequired();
        });

        builder.Entity<Location>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Address).HasMaxLength(500);
        });

        builder.Entity<Car>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.LicensePlate).HasMaxLength(20);
        });

        builder.Entity<WorkPhoto>(e =>
        {
            e.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.LocationId, x.CreatedAt });
        });

        builder.Entity<TimeEntry>(e =>
        {
            e.HasOne(x => x.Employee)
                .WithMany(x => x.TimeEntries)
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Location)
                .WithMany(x => x.TimeEntries)
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Car)
                .WithMany(x => x.TimeEntries)
                .HasForeignKey(x => x.CarId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.EmployeeId, x.ClockIn });
        });

        builder.Entity<Material>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(50).IsRequired();
            // EUR price per unit; 4 decimals lets us store fractions like 0.0125 €/skrutka
            e.Property(x => x.PricePerUnit).HasPrecision(12, 4);
            e.HasIndex(x => x.Name);
        });

        builder.Entity<MaterialUsage>(e =>
        {
            // Decimal precision: enough for "12.50 m²" or "1234.5 kg"
            e.Property(x => x.Quantity).HasPrecision(12, 3);
            // Snapshot of catalogue price at the time the usage was logged.
            e.Property(x => x.UnitPriceAtTime).HasPrecision(12, 4);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.PhotoUrl).HasMaxLength(1000);

            e.HasOne(x => x.Location)
                .WithMany(x => x.MaterialUsages)
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Material)
                .WithMany(x => x.Usages)
                .HasForeignKey(x => x.MaterialId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.LocationId, x.Date });
            e.HasIndex(x => new { x.LocationId, x.MaterialId });
        });

        builder.Entity<PushSubscription>(e =>
        {
            e.Property(x => x.Endpoint).HasMaxLength(2048);
            e.Property(x => x.P256dhKey).HasMaxLength(200);
            e.Property(x => x.AuthKey).HasMaxLength(200);
            e.Property(x => x.UserAgent).HasMaxLength(500);
            e.HasIndex(x => x.Endpoint).IsUnique();
            e.HasIndex(x => x.EmployeeId);
        });

        builder.Entity<NotificationLog>(e =>
        {
            e.Property(x => x.Channel).HasMaxLength(50);
            e.Property(x => x.TriggerType).HasMaxLength(50);
            e.Property(x => x.Body).HasMaxLength(2000);
            e.Property(x => x.Status).HasMaxLength(50);
            e.Property(x => x.ProviderMessageId).HasMaxLength(500);
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.HasIndex(x => new { x.EmployeeId, x.Channel, x.TriggerType, x.TriggerDate }).IsUnique();
            e.HasIndex(x => new { x.EmployeeId, x.TriggerDate });
        });

        builder.Entity<NotificationConfig>(e =>
        {
            e.Property(x => x.VapidPublicKey).HasMaxLength(1000);
            e.Property(x => x.VapidPrivateKey).HasMaxLength(1000);
            e.Property(x => x.VapidSubject).HasMaxLength(200);
            e.HasKey(x => x.Id);
        });
    }

    public override int SaveChanges()
    {
        SetTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SetTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void SetTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

        foreach (var entry in entries)
        {
            if (entry.Entity is Employee emp)
            {
                emp.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) emp.CreatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is Location loc)
            {
                loc.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) loc.CreatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is Car car)
            {
                car.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) car.CreatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is TimeEntry te)
            {
                te.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) te.CreatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is Material mat)
            {
                mat.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) mat.CreatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is MaterialUsage mu)
            {
                mu.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) mu.CreatedAt = DateTime.UtcNow;
            }
            // WorkPhoto.CreatedAt is set explicitly by the controller (to support backdating).
            // The model property initializer (= DateTime.UtcNow) covers the default case,
            // so we intentionally do NOT override it here.
        }
    }
}
