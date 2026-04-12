# Dochadzkovník — Project Notes

> Last updated: 2026-04-10

## What is this project?

**Dochadzkovník** ("attendance ledger" in Slovak) is a full-stack employee attendance tracking system built for a Slovak construction firm. Workers clock in/out at job sites via a PIN-based kiosk; managers use an admin panel to manage records and generate reports.

---

## Tech Stack

| Layer | Technology |
|---|---|
| Backend | ASP.NET Core (C#), Entity Framework Core |
| Frontend | Angular v20 |
| Database | SQLite (local dev) / PostgreSQL (production) |
| Auth | JWT + ASP.NET Identity (admin users only) |
| Image storage | Cloudinary |
| Hosting | Railway (API) + Vercel (frontend) |
| Container | Docker (Dockerfile present) |

---

## Core Data Models

- **Employee** — FirstName, LastName, PIN (hashed), PhoneNumber, Address, City, PhotoUrl, IsActive
- **Location** — Name, Address, PhotoUrl, IsActive — represents a *construction site*
- **Car** — Name, LicensePlate, PhotoUrl, IsActive — represents a *company vehicle*
- **TimeEntry** — EmployeeId, LocationId, CarId (optional), ClockIn, ClockOut, Note
- **AppUser** — ASP.NET Identity admin user (separate from Employee)

---

## Architecture

### Two distinct surfaces

1. **Kiosk** (`/api/kiosk/*`) — **no JWT required**. Field workers authenticate with a PIN. Used on shared tablets at construction sites.
2. **Admin panel** (`/api/employees`, `/api/locations`, `/api/cars`, `/api/time-entries`, `/api/reports`) — **JWT protected**. Used by managers.

### Key API endpoints

| Controller | Routes |
|---|---|
| AuthController | login, forgot-password, reset-password |
| KioskController | clock-in, clock-out, manual-entry, log-hours, status, my-hours, overview (weekly), locations, cars |
| EmployeesController | CRUD + photo upload |
| LocationsController | CRUD + photo upload |
| CarsController | CRUD + photo upload |
| TimeEntriesController | GET (filterable by date/employee/location), POST, PUT, DELETE |
| ReportsController | daily, summary, export/csv |

---

## Business Logic Highlights

- **PIN auth**: Kiosk loads all active employees, verifies PIN client-side via `PinHasher.Verify()`. No individual device logins needed for workers.
- **Timezone**: All kiosk timestamps use `Europe/Bratislava`. Admin/report queries use UTC.
- **Cars on time entries**: Optional — workers can log which company vehicle they used.
- **Weekly overview** (`/api/kiosk/overview`): Returns a grid of all active employees × 7 days with hours and locations per day.
- **CSV export**: UTF-8 BOM prepended so Excel opens Slovak characters (č, š, ž) correctly without import wizard. Headers: `Zamestnanec, Pracovisko, Auto, Hodiny, Poznámka`.
- **Log-hours endpoint**: Worker enters total hours rather than specific clock-in/out times. System back-calculates timestamps (uses 17:00 as clockOut anchor for past dates).
- **Manual entry**: Admin can retroactively create time entries via kiosk or admin panel.

---

## Database Notes

- Local dev uses SQLite; production uses PostgreSQL via `DATABASE_URL` env var.
- `Program.cs` contains extensive **self-healing SQL** that runs at startup — it patches schema gaps caused by a SQLite→PostgreSQL migration (missing sequences, boolean/integer type mismatches, timestamp stored as TEXT, late-added Cars table and CarId FK). This is technical debt that should eventually be replaced with proper migrations.
- Admin seed: default username `vladosroka`, default password `Nikolasko1` (overridable via config).

---

## ⚠️ CRITICAL: Migration Safety Rules

**The customer's production database contains real employee attendance records. Data loss or a locked/inaccessible database is unacceptable.**

### The problem we have already hit
EF migrations hand-written without `dotnet ef migrations add` (i.e., created manually as `.cs` files) **do not get a `.Designer.cs` companion file**. Without it, EF does not register the migration in its internal chain — so `MigrateAsync()` silently skips it even after `dotnet ef database update`. The model and the physical schema fall out of sync, causing runtime query failures (`no such column`).

### Rules for every future schema change

1. **Always generate migrations with the CLI:**
   ```
   cd API
   dotnet ef migrations add <MigrationName>
   ```
   This creates both the `.cs` and the `.Designer.cs` file. Never write migration files by hand.

2. **Test locally before pushing.** Restart the API locally and confirm there are no `no such column` / `no such table` errors before committing.

3. **Every new column MUST have a SQLite self-heal block in `Program.cs`.** SQLite's `ALTER TABLE ... ADD COLUMN` is safe — it is idempotent when wrapped in a try/catch. This is the final safety net that keeps local and production in sync even if the EF migration chain drifts:
   ```csharp
   // SQLite self-heal
   if (string.IsNullOrEmpty(databaseUrl))
       try { await db.Database.ExecuteSqlRawAsync(@"ALTER TABLE ""TableName"" ADD COLUMN ""ColName"" TYPE"); } catch { }
   ```

4. **Every new column that targets PostgreSQL MUST also have a PostgreSQL self-heal block** (the `DO $$ IF NOT EXISTS ... ALTER TABLE ... END $$` pattern already used in `Program.cs`). Railway runs `MigrateAsync()` on every deploy, but the self-heal is the backstop.

5. **Never DROP or RENAME a column via a migration without first confirming it is completely unused** — including by old Railway deploys that might still be running. Prefer adding a new column and deprecating the old one.

6. **Never truncate or DELETE from tables in a migration.** Schema migrations must only add/alter structure, never touch data.

7. **Before any deploy that includes a migration, verify:**
   - `dotnet ef migrations list` shows the new migration as pending locally
   - The migration's `Up()` only uses `AddColumn`, `CreateTable`, `CreateIndex`, or safe `AlterColumn` — nothing destructive
   - A local `dotnet run` succeeds with no EF errors in the console

---

## Frontend Structure (Angular)

Pages: `account`, `car-detail`, `cars`, `dashboard`, `employee-detail`, `employees`, `forgot-password`, `kiosk`, `location-detail`, `locations`, `login`, `reports`, `reset-password`, `time-entries`

Services mirror the backend: `auth`, `car`, `employee`, `kiosk`, `location`, `report`, `time-entry`, `theme`

---

## Deployment

- `railway.json` — Railway config for backend
- `vercel.json` — Vercel config for frontend
- `nixpacks.toml` — Nixpacks build config
- `Dockerfile` — Docker container support
- `apply_migration.py` — helper script for running EF migrations against production DB

---

## Localization

The entire UI and all API messages are in **Slovak**. Error messages, kiosk responses, and CSV exports all use Slovak text. The customer/firm is based in Slovakia.

---

## Known Issues / Technical Debt

- Self-healing SQL in `Program.cs` is a workaround for migration history gaps. It must be kept in sync every time a new column is added (see Migration Safety Rules above).
- The `20260411000000_AddTimeEntryPhotoUrl` migration was written by hand and lacks a `.Designer.cs`. It is covered by the SQLite try/catch self-heal block. Future migrations must be generated via `dotnet ef migrations add`.
- No backend unit/integration tests visible in the repo.
- `FindEmployeeByPin` loads **all active employees** into memory to verify PIN — acceptable at small scale, could be a concern as employee count grows.

---

## Customer Context

- Construction firm, Slovak market
- Field workers are not tech-savvy — PIN kiosk on a shared tablet is the primary interaction
- Multiple job sites (Locations) active simultaneously
- Company vehicles (Cars) assigned per shift
- Managers need daily/weekly attendance reports and CSV exports for payroll
