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
            // WorkPhoto.CreatedAt is set explicitly by the controller (to support backdating).
            // The model property initializer (= DateTime.UtcNow) covers the default case,
            // so we intentionally do NOT override it here.
        }
    }
}
