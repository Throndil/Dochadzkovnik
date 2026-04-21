# Dochadzkovník — Project Notes

> Last updated: 2026-04-21

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

## Session Log

### 2026-04-21 (continued) — Týždeň toggle fix & "worker can't see entry" diagnosis

**Implemented:**

- **`DatepickerDirective` now implements `ControlValueAccessor`** (`client/src/app/directives/datepicker.directive.ts`)
  - Previously the directive was a plain flatpickr wrapper. ngModel wrote to the underlying input's `value`, but flatpickr's **alt-input** (the visible picker label) ignored programmatic input changes because flatpickr only reads `input.value` at initialisation.
  - Symptom: clicking the "Týždeň" quick-range toggle on `Záznamy dochádzky` correctly reassigned `from`/`to` on the component and reloaded the list — but the picker labels kept showing the old month range.
  - Fix: added `NG_VALUE_ACCESSOR` provider and implemented `writeValue` / `registerOnChange` / `registerOnTouched` / `setDisabledState`. `writeValue` now calls `fp.setDate(value || null, false)` (the `false` prevents flatpickr firing `onChange` and creating an ngModel feedback loop).
  - `onChange` callback also routes into `onChangeCb(dateStr)` so user-picked dates continue to work.
  - Pending-value handling retained for the case where `writeValue` arrives before `AfterViewInit`.

- **Admin add-entry dropdown filters to active employees only + backend guard** (`client/src/app/pages/time-entries/time-entries.page.ts`, `...page.html`, `API/Controllers/TimeEntriesController.cs`)
  - Diagnosed the "Mrkvička Janko (PIN 88888) can't see his 8h entry" report: `KioskController.FindEmployeeByPin` only resolves employees with `IsActive = true`. If the admin creates a TimeEntry against an inactive employee, it saves fine and appears in the admin list, but the kiosk PIN lookup returns 401 and the worker's Moje hodiny shows no entries.
  - Added a `activeEmployees = computed(() => this.employees().filter(e => e.isActive))` and switched the "Nový záznam dochádzky > Zamestnanec" dropdown to use it. The filter dropdown above the list still uses the full `employees()` so historical entries for deactivated workers remain searchable/editable.
  - `POST /api/time-entries` (`Create`) now rejects `!employee.IsActive` with a clear Slovak message ("Zamestnanec je neaktívny — aktivujte ho pred pridaním záznamu."). This is a backstop in case anything else (e.g. a Swagger / curl call) tries to book for an inactive employee.

- **Kiosk "Moje hodiny" surfaces backend error messages** (`client/src/app/pages/kiosk/kiosk.page.ts`, `...page.html`)
  - Added `myHoursError = signal('')` and populated it from the error handler. The result card now shows the backend error in red (e.g. "Neplatný PIN") instead of collapsing into the generic "Žiadne záznamy za toto obdobie" empty state, which was indistinguishable from a legitimate empty month.

**Diagnostic note for the reporter:**
If, after this fix, PIN 88888 still shows an empty Moje hodiny *after* a successful load (name shown, no red error, no entries), the most likely remaining cause is that the entry was booked against a DIFFERENT Employee record that happens to have the same name. On the admin side, open the employee detail page for the employee the entry is actually attached to and confirm their PIN matches 88888.

---

### 2026-04-21 — Customer change requests

**Implemented:**

- **Removed "Príchod" / "Odchod" columns from kiosk "Moje hodiny"** (`client/src/app/pages/kiosk/kiosk.page.html`)
  - Dropped the two `<th>` headers and corresponding `<td>` cells that showed `clockIn` and `clockOut` as formatted timestamps
  - `<tfoot>` "Spolu" row's leading `colspan` reduced from 4 → 2 to stay aligned with the remaining columns (Pracovisko, Auto, Hodiny, Foto záznamu, Poznámka)
  - API response still carries `clockIn` / `clockOut`; only the rendered table was trimmed

- **Kiosk dashboard "Spolu" column now totals the full month, not the week** (`API/Controllers/KioskController.cs` — `GetOverview`)
  - `monthStart` / `monthEnd` derived from the incoming `weekStart.Date`
  - DB query widened to `max(week, month)` so both the 7-day grid and the monthly total can be computed from one fetch
  - `WeeklyRowDto.TotalHours` now sums entries where `ClockIn` falls inside the calendar month of the viewed week; the 7 day-cells still only show the currently viewed week
  - No schema change — no migration / self-heal needed

- **Admin "Záznamy dochádzky" add/edit is now hours-based** (`client/src/app/pages/time-entries/time-entries.page.html`, `...page.ts`)
  - Both the "+ Pridať záznam" form and the "Upraviť záznam" form no longer ask for Príchod / Odchod date+time pairs
  - New layout: a single Dátum picker + a large Počet hodín display with −/+ 0.5h buttons and a grid of preset chips (`hoursPresets = [0.5, 1, 2, 4, 5, 5.5, 6, 7, 7.5, 8, 9, 10]`)
  - `newEntry` / `editForm` shapes changed from `{clockInDate, clockInTime, clockOutDate, clockOutTime}` to `{date, hoursWorked}`
  - Added `adjustNewHours` / `setNewHours` / `adjustEditHours` / `setEditHours` helpers and a private `buildClockWindow(dateStr, hoursWorked)` that re-uses the kiosk convention:
    - if `date` is today → `clockOut = new Date()` (local now)
    - otherwise → `clockOut = date at 17:00`
    - `clockIn = clockOut − hoursWorked * 3600_000`
  - `onEdit` derives `{date, hoursWorked}` from the existing `entry.clockIn` / `entry.hoursWorked` so editing round-trips cleanly (hours rounded to nearest 0.5). Note: saving an edited entry will re-anchor the stored clockOut to 17:00 (past) / now (today); the original exact timestamps are not preserved. This matches kiosk behaviour and the customer's request.
  - `TimepickerDirective` import removed (no more `appTime` usage on this page); `DatepickerDirective` kept for the filter row and the new Dátum picker
  - No backend/API change — `POST /api/time-entries` and `PUT /api/time-entries/{id}` still take `ClockIn` / `ClockOut`

---

### 2026-04-15 — Customer call follow-up

**Implemented:**

- **Disable double-tap zoom** (`client/src/index.html`, `client/src/styles.css`)
  - Viewport meta: `maximum-scale=1, user-scalable=no`
  - Global CSS: `touch-action: manipulation` on `html` element
  - Covers both iOS Safari (viewport meta) and Android Chrome (touch-action)

- **PWA icon fix** (`client/public/`, `client/public/manifest.webmanifest`, `client/src/index.html`)
  - Original `profistav_logo.png` was 1920×1280 — not square, causing distortion on home screen
  - Generated `profistav_logo_512.png` (512×512), `profistav_logo_192.png` (192×192), `apple-touch-icon.png` (180×180)
  - Manifest now lists explicit sizes with separate `any` and `maskable` purpose entries
  - `index.html` favicon and apple-touch-icon updated to reference new files

- **Car column in "Moje Hodiny"** (`client/src/app/pages/kiosk/kiosk.page.html`)
  - Added "Auto" column to the kiosk My Hours table
  - Shows 🚗 + car name if a car was used, "—" otherwise
  - Footer colspan adjusted from 3 to 4

- **Záznamy: full-month default + Mesiac/Týždeň toggle** (`client/src/app/pages/time-entries/time-entries.page.ts`, `...html`)
  - Default range now spans the full current month (1st → last day) instead of 1st → today
  - Added `dateRangeMode: 'month' | 'week' | 'custom'` state
  - Toggle buttons "Mesiac" / "Týždeň" snap to the current full month or current Mon–Sun week
  - Manual date picker changes flip mode to `'custom'` so the toggle doesn't override user input

**Deliberately NOT implemented (deferred):**
- Automatic/scheduled photo deletion from Cloudinary — no such system exists yet, manual admin deletion only. Customer to decide on retention policy before implementing.

---

## Customer Context

- Construction firm, Slovak market
- Field workers are not tech-savvy — PIN kiosk on a shared tablet is the primary interaction
- Multiple job sites (Locations) active simultaneously
- Company vehicles (Cars) assigned per shift
- Managers need daily/weekly attendance reports and CSV exports for payroll
