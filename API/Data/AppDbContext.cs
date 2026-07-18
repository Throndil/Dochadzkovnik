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
    public DbSet<WorkDiary> WorkDiaries => Set<WorkDiary>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<MaterialUsage> MaterialUsages => Set<MaterialUsage>();
    public DbSet<MaterialPurchase> MaterialPurchases => Set<MaterialPurchase>();
    public DbSet<MaterialPurchaseLine> MaterialPurchaseLines => Set<MaterialPurchaseLine>();
    public DbSet<InvoiceDocument> InvoiceDocuments => Set<InvoiceDocument>();
    public DbSet<EmployeeAdvance> EmployeeAdvances => Set<EmployeeAdvance>();
    public DbSet<EmployeeWageRate> EmployeeWageRates => Set<EmployeeWageRate>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<NotificationConfig> NotificationConfigs => Set<NotificationConfig>();
    public DbSet<FeatureFlag> FeatureFlags => Set<FeatureFlag>();
    public DbSet<CompanyRate> CompanyRates => Set<CompanyRate>();
    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<AiSpend> AiSpends => Set<AiSpend>();
    public DbSet<FuelCard> FuelCards => Set<FuelCard>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Employee>(e =>
        {
            e.HasIndex(x => x.Pin).IsUnique();
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Pin).HasMaxLength(256).IsRequired();
            e.Property(x => x.Division).HasMaxLength(20);
            e.Property(x => x.Position).HasMaxLength(100);
        });

        builder.Entity<FuelCard>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(100).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
            // Deleting an employee frees the card instead of deleting it.
            e.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CompanyRate>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(50);
            e.Property(x => x.Label).HasMaxLength(100).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(50);
            // Seed the customer's three known amounts — editable on /admin/odvody.
            e.HasData(
                new CompanyRate { Id = 1, Key = "odvody",       Label = "Odvody",       Amount = 0m,  Unit = "€/h na pracovníka", UpdatedAt = new DateTime(2026, 7, 18) },
                new CompanyRate { Id = 2, Key = "ubytovanie",   Label = "Ubytovanie",   Amount = 1m,  Unit = "€/h na pracovníka", UpdatedAt = new DateTime(2026, 7, 18) },
                new CompanyRate { Id = 3, Key = "vyjazd_auta",  Label = "Výjazd auta",  Amount = 30m, Unit = "€/výjazd",          UpdatedAt = new DateTime(2026, 7, 18) });
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
            e.Property(x => x.Division).HasMaxLength(20);
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

        builder.Entity<WorkDiary>(e =>
        {
            e.Property(x => x.BodyText).IsRequired();
            e.Property(x => x.AttachmentUrl).HasMaxLength(1000);

            e.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);

            // Optional link back to the šichta TimeEntry that produced this
            // diary (populated by the in-šichta combined flow). SetNull so
            // deleting a TimeEntry does not also wipe the diary audit trail.
            e.HasOne(x => x.TimeEntry)
                .WithMany()
                .HasForeignKey(x => x.TimeEntryId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.LocationId, x.Date });
            e.HasIndex(x => new { x.EmployeeId, x.Date });
            e.HasIndex(x => x.TimeEntryId);
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

            e.HasOne(x => x.Machine)
                .WithMany(x => x.TimeEntries)
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => new { x.EmployeeId, x.ClockIn });
        });

        builder.Entity<Machine>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Note).HasMaxLength(500);
        });

        builder.Entity<AiSpend>(e =>
        {
            e.Property(x => x.Month).HasMaxLength(7).IsRequired();
            e.Property(x => x.CostEur).HasPrecision(10, 4);
            e.HasIndex(x => x.Month).IsUnique();
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

            // Option A: usages minted from scanned-invoice lines carry a back-
            // pointer so discarding the invoice (which removes the line via
            // MaterialPurchase cascade) sweeps the usage too. Cascade matches
            // that intent: line gone → usage gone.
            e.HasOne(x => x.SourceMaterialPurchaseLine)
                .WithMany()
                .HasForeignKey(x => x.SourceMaterialPurchaseLineId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => new { x.LocationId, x.Date });
            e.HasIndex(x => new { x.LocationId, x.MaterialId });
            e.HasIndex(x => x.SourceMaterialPurchaseLineId);
        });

        builder.Entity<MaterialPurchase>(e =>
        {
            e.Property(x => x.SupplierName).HasMaxLength(200);
            e.Property(x => x.ReceiptPhotoUrl).HasMaxLength(1000);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.TotalCost).HasPrecision(14, 4);

            // Invoice-scanning extensions
            e.Property(x => x.DeliveryNoteRef).HasMaxLength(100);
            e.Property(x => x.PickedUpBy).HasMaxLength(200);
            e.Property(x => x.DeliveryNote).HasMaxLength(2000);
            e.Property(x => x.AkciaName).HasMaxLength(200);
            e.Property(x => x.SubtotalExclVat).HasPrecision(14, 2);
            e.Property(x => x.SubtotalVat).HasPrecision(14, 2);

            // Mašina/Auto assignment (F1) — deleting the asset clears the tag.
            e.HasOne(x => x.Machine)
                .WithMany()
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Car)
                .WithMany()
                .HasForeignKey(x => x.CarId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Employee)
                .WithMany()
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);

            // The "site this is for" — null = general / company stock.
            e.HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.SetNull);

            // Optional link back to the kiosk šichta TimeEntry that produced this
            // purchase (populated by the in-šichta combined flow). SetNull so
            // deleting a TimeEntry does not also wipe the purchase audit trail.
            e.HasOne(x => x.TimeEntry)
                .WithMany()
                .HasForeignKey(x => x.TimeEntryId)
                .OnDelete(DeleteBehavior.SetNull);

            // Optional parent invoice (when this purchase came from a scanned PDF).
            // SetNull so deleting the invoice document doesn't cascade-wipe the
            // committed MaterialPurchase records — they're financial history.
            e.HasOne(x => x.InvoiceDocument)
                .WithMany(x => x.Purchases)
                .HasForeignKey(x => x.InvoiceDocumentId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.PurchaseDate);
            e.HasIndex(x => new { x.EmployeeId, x.PurchaseDate });
            e.HasIndex(x => x.LocationId);
            e.HasIndex(x => x.InvoiceDocumentId);
        });

        builder.Entity<MaterialPurchaseLine>(e =>
        {
            e.Property(x => x.MaterialNameRaw).HasMaxLength(200).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(50).IsRequired();
            // Same precisions as MaterialUsage so quantities + prices are
            // comparable across the two sides of the materials story.
            e.Property(x => x.Quantity).HasPrecision(12, 3);
            e.Property(x => x.UnitPrice).HasPrecision(12, 4);
            e.Property(x => x.LineTotal).HasPrecision(14, 4);

            // Invoice-scanning extensions
            e.Property(x => x.SupplierItemCode).HasMaxLength(50);
            e.Property(x => x.ListPriceExclVat).HasPrecision(12, 4);
            e.Property(x => x.DiscountPercent).HasPrecision(5, 2);
            e.Property(x => x.UnitPriceInclVat).HasPrecision(12, 4);
            e.Property(x => x.VatRate).HasPrecision(5, 2);

            // Per-line Mašina/Auto override (F1).
            e.HasOne(x => x.Machine)
                .WithMany()
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Car)
                .WithMany()
                .HasForeignKey(x => x.CarId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Purchase)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.PurchaseId)
                .OnDelete(DeleteBehavior.Cascade);

            // Optional per-line site override; null = inherit the delivery
            // list's Location. SetNull so deleting a Location reverts affected
            // lines to "inherit" rather than blocking the delete or cascading.
            e.HasOne(x => x.Location)
                .WithMany()
                .HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.LocationId);

            // MaterialId is nullable — line starts unidentified and is
            // promoted to a catalogue row by the admin afterwards. SetNull
            // means deleting a Material does not wipe historical purchase
            // lines (they keep their MaterialNameRaw + Unit + price).
            e.HasOne(x => x.Material)
                .WithMany()
                .HasForeignKey(x => x.MaterialId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.PurchaseId);
            e.HasIndex(x => x.MaterialId);
        });

        builder.Entity<InvoiceDocument>(e =>
        {
            e.Property(x => x.InvoiceNumber).HasMaxLength(100).IsRequired();
            e.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
            e.Property(x => x.SupplierIco).HasMaxLength(50);
            e.Property(x => x.SupplierIcDph).HasMaxLength(50);
            e.Property(x => x.SupplierIban).HasMaxLength(50);
            e.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            e.Property(x => x.TotalExclVat).HasPrecision(14, 2);
            e.Property(x => x.TotalVat).HasPrecision(14, 2);
            e.Property(x => x.TotalInclVat).HasPrecision(14, 2);
            e.Property(x => x.PdfUrl).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.ReconciliationNote).HasMaxLength(500);
            e.Property(x => x.UploadedBy).HasMaxLength(100).IsRequired();
            e.Property(x => x.CommittedBy).HasMaxLength(100);
            e.Property(x => x.Note).HasMaxLength(2000);
            // RawOcrJson stays unbounded (text) — Document AI responses can be
            // hundreds of KB on a multi-page invoice. Postgres text handles it.

            // Dedup guard: re-uploading the same invoice number from the same
            // supplier is a hard error. The controller catches the exception
            // and surfaces a Slovak message rather than letting EF crash.
            e.HasIndex(x => new { x.InvoiceNumber, x.SupplierIco }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.UploadedAt);
            e.HasIndex(x => new { x.SupplierName, x.IssueDate });

            // Informational backtrack tags (F1) — deleting a mašina/auto just
            // clears the tag, the document stays.
            e.HasOne(x => x.Machine)
                .WithMany()
                .HasForeignKey(x => x.MachineId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Car)
                .WithMany()
                .HasForeignKey(x => x.CarId)
                .OnDelete(DeleteBehavior.SetNull);
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

        builder.Entity<FeatureFlag>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });

        builder.Entity<EmployeeWageRate>(e =>
        {
            // 4 decimals matches the other money rates (e.g. material price/unit).
            e.Property(x => x.RatePerHour).HasPrecision(12, 4);
            e.Property(x => x.CreatedBy).HasMaxLength(100);

            e.HasOne(x => x.Employee)
                .WithMany(x => x.WageRates)
                .HasForeignKey(x => x.EmployeeId)
                .OnDelete(DeleteBehavior.Cascade);

            // Lookups are always "this employee's rates, ordered by date".
            e.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom });
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
            else if (entry.Entity is MaterialPurchase mp)
            {
                mp.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) mp.CreatedAt = DateTime.UtcNow;
            }
            else if (entry.Entity is MaterialPurchaseLine mpl)
            {
                if (entry.State == EntityState.Added) mpl.CreatedAt = DateTime.UtcNow;
                // Lines are append-only on insert path; subsequent edits go
                // through the controller which recomputes LineTotal.
            }
            else if (entry.Entity is WorkDiary wd)
            {
                wd.UpdatedAt = DateTime.UtcNow;
                if (entry.State == EntityState.Added) wd.CreatedAt = DateTime.UtcNow;
            }
            // InvoiceDocument.UploadedAt is set explicitly by the controller (it
            // is the moment of upload, not save). CommittedAt is set explicitly
            // by the commit endpoint. Neither uses SetTimestamps semantics.
            // WorkPhoto.CreatedAt is set explicitly by the controller (to support backdating).
            // The model property initializer (= DateTime.UtcNow) covers the default case,
            // so we intentionally do NOT override it here.
        }
    }
}
