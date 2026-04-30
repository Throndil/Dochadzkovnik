<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Dochadzkovník — Project Notes

> Last updated: 2026-04-30 (V1.3.0 — Superadmin + FeatureFlags, Spolu fix)

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

## 📱 PWA / Mobile Compatibility (READ THIS FIRST FOR ANY UI WORK)

**The app is intended to be used as a Progressive Web App, added to the home screen on iOS / Android phones and tablets.** The kiosk in particular runs full-screen on wall-mounted tablets at construction sites. Every UI change must be tested with this in mind.

### Required infrastructure (already in place — do not break)

- **`client/src/index.html`** must include:
  - `<meta name="viewport" content="... viewport-fit=cover">` — *required* for `env(safe-area-inset-*)` to populate
  - `<meta name="apple-mobile-web-app-capable" content="yes">`
  - `<meta name="mobile-web-app-capable" content="yes">`
  - `<meta name="apple-mobile-web-app-status-bar-style" content="default">`
  - `<meta name="apple-mobile-web-app-title" content="Sichtovnica">`
  - Two `theme-color` meta tags scoped to `prefers-color-scheme` light / dark
- **`client/public/manifest.webmanifest`** must include `scope`, `start_url`, `display: standalone`, `display_override`, `orientation: any`, `lang: sk`, and 192/512 icons in both `any` and `maskable` purposes.

### Safe-area rules (notch / dynamic island / home indicator)

- Any sticky/fixed element at the **top** of the viewport must have inline `padding-top: env(safe-area-inset-top)` plus left/right insets — see the navbar (`navbar.component.html`) and the kiosk header (`kiosk.page.html`) for the canonical pattern.
- Any sticky/fixed element at the **bottom** of the viewport must include the `safe-bottom` class (defined in `styles.css`) so it lifts above the home indicator.
- Reusable helpers in `styles.css`: `.safe-top`, `.safe-bottom`, `.safe-left`, `.safe-right`, `.min-h-dvh`, `.h-dvh`.

### iOS-specific gotchas baked into `styles.css`

- **No input zoom on focus**: a `@media (max-width: 1024px)` rule forces every text-like `<input>` and `<select>` / `<textarea>` to `font-size: 16px !important`. Below 16px, iOS Safari triggers a viewport zoom when the field is tapped. Do NOT lower this threshold.
- **No rubber-band white flash**: `overscroll-behavior-y: contain` on `html, body` kills the iOS Safari overscroll bounce that revealed white background in dark mode.
- **No grey/blue tap highlight**: `-webkit-tap-highlight-color: transparent` on `*`. We render our own `:active` / `:hover` states.
- **`touch-action: manipulation`** on `html` plus `maximum-scale=1` in viewport meta disables double-tap-zoom — important for the kiosk where double taps would be common.

### Layout rules

- The admin **navbar is `sticky top-0 z-30`** and uses opaque backgrounds — content scrolls cleanly behind it. Modals/slide-overs use `z-40` (backdrop) and `z-50` (panel) to overlay the navbar.
- Pages that should fill exactly the visible viewport (e.g. kiosk) should prefer `.h-dvh` / `.min-h-dvh` over Tailwind's `min-h-screen` when targeting iOS — `100dvh` correctly tracks the dynamic toolbar; `100vh` does not.
- Mobile-first: every page uses Tailwind's `md:` / `sm:` breakpoints. The navbar has a hamburger that takes over below `md`.

### Testing checklist for any UI change

1. Chrome/Firefox desktop — sanity check
2. iPhone Safari (regular tab) — check URL bar collapse/expand behaviour
3. iPhone Safari "Add to Home Screen" → standalone — **most important**, check dynamic island clearance and home indicator
4. iPad Safari (regular + landscape) — landscape notch on Pro models
5. Android Chrome (regular + installed) — back gesture, theme color in switcher

---

## Core Data Models

- **Employee** — FirstName, LastName, PIN (hashed), PhoneNumber, Address, City, PhotoUrl, IsActive
- **Location** — Name, Address, PhotoUrl, IsActive — represents a *construction site*
- **Car** — Name, LicensePlate, PhotoUrl, IsActive — represents a *company vehicle*
- **TimeEntry** — EmployeeId, LocationId, CarId (optional), ClockIn, ClockOut, Note
- **WorkPhoto** — standalone proof-of-work photos uploaded from the kiosk (separate from TimeEntry photos)
- **Material** — catalogue entry: Name, Unit (free-text Slovak: vrece / kg / m² / ks / l / …), **PricePerUnit (decimal 12,4 EUR)**, IsActive
- **MaterialUsage** — per-location consumption record: LocationId, MaterialId, Quantity (decimal 12,3), **UnitPriceAtTime (decimal 12,4 EUR — snapshot at insert time, inflation-protected)**, Date, EmployeeId? (who logged it), Note?, PhotoUrl? (delivery slip)
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
| LocationsController | CRUD + photo upload + per-location material usage (`/{id}/materials[/...]`, summary, export to .xlsx, photo upload per usage) |
| CarsController | CRUD + photo upload |
| MaterialsController | Catalogue CRUD at `/api/materials` (case-insensitive duplicate-name guard; soft-delete fallback if any usage exists) |
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

8. **AI assistants must NEVER change the database, run EF commands, modify migration files, or delete/recreate the dev DB without first explicitly stating: (a) what they intend to change, (b) why, and (c) what the rollback path is — and getting the developer's confirmation.** This includes: regenerating migrations, running `dotnet ef migrations remove`, deleting `dochadzkovnik.db`, editing `Program.cs` migration self-heal blocks, or modifying any `.cs` file under `API/Migrations/`. Frontend / non-DB code edits do NOT require this preamble. Diagnosis (reading files, running `dotnet ef migrations list`) is fine without confirmation; any *write* to DB-related code or files requires explicit intent and approval.

---

## Frontend Structure (Angular)

Pages: `account`, `car-detail`, `cars`, `dashboard`, `employee-detail`, `employees`, `forgot-password`, `kiosk`, `location-detail`, `locations`, `login`, `materials`, `reports`, `reset-password`, `time-entries`

Components of note: `location-manage-panel` — a slide-over right-hand panel for per-location material consumption (mounted from the Lokácie page).

Services mirror the backend: `auth`, `car`, `employee`, `kiosk`, `location`, `material`, `report`, `time-entry`, `theme`

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

## 🔜 Pending Customer Requests (read first in any new chat)

### Notifications — PWA push + WhatsApp (V1) — **M1 SHIPPED 2026-04-26**, awaiting customer test
> Backend + admin "Notifikácie" page + service worker code-complete. Frontend `tsc --noEmit` passes clean. Backend `dotnet build` not run in sandbox — verify locally before publish. Before first deploy: generate VAPID keypair and set `Notifications:Vapid:PublicKey`/`PrivateKey`/`Subject` in `appsettings.json`. WhatsApp send is stubbed pending Meta credentials + approved template.
> See "Session Log → V1.2.0" below for the full shipped surface.

### Notifications — original plan (kept for reference)

> **Reminder for any future work on this:** The customer's workforce is **older / non-tech-savvy** Slovak construction workers. Anything we build must be obvious, big, plain-Slovak, no jargon. See `NOTIFICATIONS_PLAN.md` §10 for the full UX rules.

**Customer ask:** remind a worker only if they have **not clocked any hours in the past 2 days**. One trigger, nothing else.

**Channels chosen (free):**
- **PWA push notifications** — primary. Free forever. Works on Android Chrome and iOS 16.4+ Safari (when added to home screen — install guides already exist as `Sichtovnica_iOS_Sprievodca.pdf` / `Sichtovnica_Android_Sprievodca.pdf`).
- **WhatsApp Business Cloud API** — secondary, opt-in per employee. Free tier: 1000 service conversations/month from Meta is plenty for ~30 workers. Many older Slovak users already have WhatsApp for family.
- **SMS path is deferred** to V2 (`SMS_PLAN.md`). Only revisited if push + WhatsApp coverage proves insufficient.

**Full plan: `NOTIFICATIONS_PLAN.md` (in workspace root).**

**Open questions to ask the customer before writing code (full list in `NOTIFICATIONS_PLAN.md` §2):**
1. Confirm the trigger: "remind a worker if they have not logged any hours in the past 2 days".
2. What time of day should it fire? (default 18:00 `Europe/Bratislava`)
3. Working days only (Mon–Fri), or every day? Holidays?
4. Per-employee: who has WhatsApp, who doesn't?
5. Approve / amend the Slovak copy in `NOTIFICATIONS_PLAN.md` §7.
6. Manager daily summary push — yes / no?
7. Is the customer willing to set up a Meta Business / WhatsApp Business account (one-time), or start push-only first?

**What already exists in the codebase to support this:**
- `Employee.PhoneNumber` is on the model — reused for `WhatsAppNumber` if no explicit value.
- `Europe/Bratislava` timezone handling is centralised in `KioskController`.
- The PWA is fully set up: manifest, icons, safe-area insets (V1.1.1), install guides. Push only needs a service-worker push handler + VAPID wiring.
- Admin auth/JWT already in place — new "Notifikácie" page plugs in without auth scaffolding.

**Implementation skeleton to propose to the customer (validate first):**

1. **Push abstraction** — `IPushNotificationService` + `WebPushService` (NuGet `WebPush` / `lib-net-webpush`). VAPID keypair stored in Railway env (`VAPID_PUBLIC_KEY` / `VAPID_PRIVATE_KEY`).
2. **WhatsApp abstraction** — `IWhatsAppService` + `WhatsAppCloudApiService` calling Meta's Cloud API (`graph.facebook.com/.../messages`). Reads `WHATSAPP_TOKEN`, `WHATSAPP_PHONE_NUMBER_ID`, `WHATSAPP_TEMPLATE_NAME` from env.
3. **Hosted `BackgroundService`** — 1-min tick, evaluates `NoActivity48h`, dispatches to whichever channels each employee is subscribed to. `LastTickAt` persisted to survive deploys.
4. **Tables:** `PushSubscription` (per device), `NotificationLog` (append-only audit across both channels), `NotificationConfig` (single-row schedule). Unique index `(EmployeeId, Channel, TriggerType, TriggerDate)` for idempotency.
5. **`Employee` columns added** (always via `dotnet ef migrations add` + SQLite + PostgreSQL self-heal — see Migration Safety Rules above):
   - `NotificationsEnabled` (bool, default true)
   - `WhatsAppEnabled` (bool, default false — opt-in)
   - `WhatsAppNumber` (string, nullable)
6. **Kiosk UI change** — big "Povoliť upozornenia" tile on the start screen, with a link to the relevant install-guide PDF. Older-worker UX rules apply (`NOTIFICATIONS_PLAN.md` §10): big targets, plain Slovak, no tech jargon, no animations, high contrast.
7. **Admin "Notifikácie" page** — toggle the trigger on/off, set fire time, working-days flag; per-employee `NotificationsEnabled` / `WhatsAppEnabled` checkboxes + last-notified date; 30-day history table; "Test push" + "Test WhatsApp" buttons.

**DO NOT start any of this until the customer answers the open questions above.** It is cheaper to ask 7 questions up-front than to rebuild around a misunderstood requirement.

---

## Known Issues / Technical Debt

- Self-healing SQL in `Program.cs` is a workaround for migration history gaps. It must be kept in sync every time a new column is added (see Migration Safety Rules above).
- The `20260411000000_AddTimeEntryPhotoUrl` migration was written by hand and lacks a `.Designer.cs`. It is covered by the SQLite try/catch self-heal block. Future migrations must be generated via `dotnet ef migrations add`.
- No backend unit/integration tests visible in the repo.
- `FindEmployeeByPin` loads **all active employees** into memory to verify PIN — acceptable at small scale, could be a concern as employee count grows.

---

## Session Log

### 2026-04-30 (V1.3.0) — Superadmin + FeatureFlags

**Context.** The customer is about to see the dev/prod cut-over. Notifications work is real but not yet signed off, so the customer cannot see any of it on their next demo. We need a way to ship hidden features behind a runtime toggle that only an internal user can flip — without redeploys.

**Shipped:**

- **Spolu column — kept the existing per-month split.** Briefly tried collapsing the `Apríl: X / Máj: Y` stacked display into one combined number, then reverted at the customer's request: when the viewed week straddles a month boundary the column must show each month's hours separately so the manager sees what the worker did in each calendar month. No net change to `kiosk.page.html` after the round trip.
- **Superadmin user** (`API/Program.cs`, `API/Services/TokenService.cs`) — second admin user seeded alongside `vladosroka`: username `admin`, password `Superadmin12345!!` (configurable via `SuperAdminSeed:Username` / `:Password` / `:DisplayName`). The seed block was refactored from "first user wins" to per-username `FindByNameAsync` so both users coexist cleanly. `TokenService.CreateToken` adds an `isSuperAdmin: "true"` claim to the JWT when the username matches the configured superadmin. Token signature is verified server-side per request, so the claim cannot be forged.
- **`FeatureFlags` table** (`API/Models/FeatureFlag.cs`, `API/Data/AppDbContext.cs`) — single new model: `Key` (string, PK), `Enabled` (bool, default false), `UpdatedAt` (UTC). One row per feature. Seeded `Notifications=false` on startup if missing. SQLite + PostgreSQL self-heal blocks added in `Program.cs` so prod boots cleanly even before the EF migration is applied. **Migration must be generated locally**: `cd API && dotnet ef migrations add AddFeatureFlags`.
- **Action filter** (`API/Filters/RequireFeatureOrSuperAdminAttribute.cs`) — `[RequireFeatureOrSuperAdmin("Notifications")]` applied to `NotificationsController`. Bypassed by the `isSuperAdmin` claim; everyone else (regular admin and unauthenticated kiosk callers) gets 404 when the flag is off — feature is completely invisible to the customer at the API level too.
- **`FeatureFlagsController`** (`API/Controllers/FeatureFlagsController.cs`) — `GET /api/feature-flags` (anonymous, returns `{ notifications: bool }`) and `PUT /api/feature-flags/{key}` (authenticated, superadmin claim required, body `{ enabled: bool }`).
- **Frontend wiring**:
  - `services/feature-flag.service.ts` — loads flags via `provideAppInitializer` so signals are populated before first navigation. `notifications: Signal<boolean>` exposed for templates and guards. Failures default to all-off (under-show beats leaking a hidden feature).
  - `services/auth.service.ts` — new `isSuperAdmin: Signal<boolean>` computed from a JWT claim decode helper. No new dependency.
  - `guards/notifications-feature.guard.ts` — `/admin/notifikacie` route guard; bounces non-superadmin to `/admin/dashboard` when the flag is off (no 404 — the page simply doesn't exist as far as they're concerned).
  - `components/navbar/navbar.component.html` — Notifikácie link gated by `flags.notifications() || auth.isSuperAdmin()` on both desktop and mobile menus.
  - `pages/kiosk/kiosk.page.ts` — push-related `ngOnInit` setup wrapped in `if (this.flags.notifications())` so the kiosk shows zero push UI when the flag is off.
  - `pages/employee-detail/employee-detail.page.html` — notification decline-reason card gated by the same combined check.
  - `pages/account/account.page.*` — new "Funkcie" card visible only to superadmin, with a single toggle for `notifications`. Card has an amber "Superadmin" badge so it's obvious which account this belongs to.

**Default state:** `Notifications` flag defaults to **false** in any newly-seeded DB. The dev DB stays whatever the developer last set it to (separate DB from prod). Plan: in dev, the developer logs in as `admin`/`Superadmin12345!!` once after deploy and flips it on; flag persists in the dev DB. Prod ships off; customer never sees the feature until the customer signs off.

**Migration command (must run locally before pushing):**

```
cd API
dotnet ef migrations add AddFeatureFlags
dotnet run    # local SQLite must boot clean
```

The CLI generates both the `.cs` and `.Designer.cs` files (per Rule 1 of Migration Safety). The self-heal blocks in `Program.cs` are an idempotent backstop and will quietly do nothing if the migration runs first.

**Verification status:** Frontend `tsc --noEmit` not run in this sandbox — the workspace bash mount serves stale file content so verification has to happen on Windows. Backend `dotnet build` likewise. Both should be run locally before pushing the dev branch.

---

### 2026-04-26 (V1.2.0) — Notifications M1: PWA push + admin Notifikácie page + demo controls

**Shipped (code-complete, awaiting customer test):**

Backend (ASP.NET Core 9):
- New models: `PushSubscription`, `NotificationLog`, `NotificationConfig`. `Employee` extended with `NotificationsEnabled` (bool, default true), `WhatsAppEnabled` (bool, default false), `WhatsAppNumber` (string?, nullable).
- New services: `IPushNotificationService` / `WebPushService` (lib-net-webpush), `IWhatsAppService` / `WhatsAppCloudApiService` (stub — wired but real send blocked on Meta credentials), `NoActivity48hEvaluator`.
- New `NotificationBackgroundService` — 60-second tick, fires the evaluator at the configured `Europe/Bratislava` time. Idempotent via unique index `(EmployeeId, Channel, TriggerType, TriggerDate)`.
- New `NotificationsController` — 13 endpoints: `vapid-public-key`, `subscribe` (POST/DELETE), `config` (GET/PUT), `employees` (GET/PUT), `history` (GET), `test/push`, `test/whatsapp`, `fire-now`, `fire-for-employee`, `reset-today`.
- Migration `20260426150000_AddNotifications` (generated via `dotnet ef migrations add`) with SQLite + PostgreSQL self-heal blocks added to `Program.cs` per Migration Safety Rules.
- New DTOs in `API/DTOs/Dtos.cs`: `PushSubscribeDto`, `PushSubscriptionDto`, `PushKeysDto`, `NotificationConfigDto`, `NotificationLogEntryDto`, `NotificationEmployeeStatusDto` (now includes `PhoneNumber`), test/fire request DTOs, etc.

Frontend (Angular 20 standalone + Tailwind):
- `client/public/sw.js` — plain JS service worker with push + notificationclick handlers (opens the PWA / focuses existing tab).
- `client/src/app/services/push.service.ts` — VAPID subscribe flow (registers SW, requests permission, fetches public key, subscribes, posts to `/api/notifications/subscribe`).
- `client/src/app/services/notification-config.service.ts` — typed HTTP client for all endpoints. `NotificationEmployeeStatus` interface mirrors backend DTO including `phoneNumber?`.
- `client/src/app/pages/notifications/` — admin Notifikácie page with 4 cards: Konfigurácia (toggle, fire-time, working-days, manager summary), Zamestnanci (per-employee toggles + WhatsApp number), Test & Ukážka (test push, test WhatsApp, fire-now, fire-for-employee, reset-today), História (filterable 30-day log).

Demo controls (per the user's "I can test it on prod / show it to the customer" requirement):
- Test push (custom title/body to one employee)
- Test WhatsApp (real send when configured)
- "Fire now" — runs evaluator immediately for everyone (with confirm)
- "Fire for employee" with `ignoreIdempotency: true` — resends to a chosen worker on demand
- "Reset today" — clears today's `NotificationLog` rows so demos can be re-run cleanly

Verification:
- Frontend `npx tsc --noEmit -p tsconfig.app.json` — clean.
- `dotnet build` not run in sandbox — must be verified locally on Windows before publish.
- Pre-existing minor TS error in `time-entries.page.ts:259` (multi-photo refactor used `null` instead of `undefined` for `photoUrl`) was fixed in passing to enable clean verification.

Pre-deploy checklist:
1. Generate VAPID keypair (e.g., `npx web-push generate-vapid-keys`); put public/private/subject under `Notifications:Vapid:*` in `appsettings.json` / Railway env.
2. Run `dotnet ef database update` against staging PostgreSQL once (the self-heal in `Program.cs` covers existing-db gaps but a fresh apply is preferred).
3. Subscribe one device end-to-end on the deployed URL (HTTPS only — push requires it).
4. Use the admin Notifikácie → Test & Ukážka card to verify the demo controls.

Follow-ups (not blocking M1):
- M2 frontend: kiosk-side "Povoliť upozornenia" tile to drive subscription on the worker's own device.
- M3: WhatsApp template approval + Meta credential plumbing (set `Notifications:WhatsApp:Token`, `:PhoneNumberId`, `:TemplateName`).
- Manager daily summary (M4 — optional polish).
- `NotificationLog` retention sweeper (90-day default).

### 2026-04-26 (V1.1.2) — Multi-photo upload for manual time entries

**Context.** Customer feedback: *"When adding a manual entry, make it possible for multiple photos to be uploaded."* Workers on mobile already attach multiple photos through the kiosk flow; the admin "Záznamy dochádzky" form only supported a single image per entry. Backend already stored photo URLs as a comma-separated list in `TimeEntry.PhotoUrl`, and the DELETE `/photo?url=...` endpoint already supported per-URL removal — only the *upload* endpoint and the admin UI needed changes.

**Implemented:**

- **Backend: append instead of replace** (`API/Controllers/TimeEntriesController.cs`, `UploadPhoto` action) — `entry.PhotoUrl` is now built as `existing + "," + newUrl` (or just `newUrl` if empty). Removed the old "delete previous photo before saving the new one" branch. Existing kiosk multi-upload flow unchanged. DELETE behaviour unchanged: `?url=...` removes one URL, no `?url` removes all.
- **Frontend signals → arrays** (`client/src/app/pages/time-entries/time-entries.page.ts`):
  - `newPhotoFile/Preview` and `editPhotoFile/Preview` (singular `signal<File|string|null>`) replaced with `newPhotoFiles/Previews` and `editPhotoFiles/Previews` (`signal<File[]>` / `signal<string[]>`).
  - New handlers: `onNewPhotosSelected` / `onEditPhotosSelected` iterate `input.files`, run each through `normaliseFile` + `compressImage` + `fileToDataUrl`, then `update(arr => [...arr, ...])`.
  - Per-thumbnail removal: `removeNewPhotoAt(idx)`, `removeEditStagedPhotoAt(idx)`, plus `onEditDeleteExistingPhoto(url)` which calls `DELETE /api/time-entries/{id}/photo?url=...` and patches `editingEntry().photoUrl` in place.
  - Sequential server upload after entry create/update using `firstValueFrom(...)` from rxjs inside an async subscribe handler, so the backend appends them in selection order. Individual upload errors are swallowed so a single bad file does not abort the rest.
- **Frontend HTML grid UI** (`client/src/app/pages/time-entries/time-entries.page.html`):
  - Both add and edit forms now span `md:col-span-3` for the photo block and render a `flex-wrap` grid of 80×80 thumbnails plus a dashed-border "+" tile.
  - The edit form distinguishes already-saved photos (slate border, `×` deletes server-side immediately) from staged-but-not-uploaded photos (amber border, `×` only removes locally). A small Slovak hint *"Žltý okraj = nahrá sa po uložení."* is shown when there are staged additions.
  - Both file inputs now carry the `multiple` attribute.
  - Lightbox calls `openLightbox(arr, $index)` so previewing one staged photo lets the manager swipe through the rest.

**No DB schema or migration change.** Pure controller + UI work.

---

### 2026-04-26 (V1.1.1) — PWA polish, mobile/tablet compatibility, formatter cleanup

**Context.** The customer confirmed the app is intended to be used as a PWA (Add to Home Screen) on iPhones and tablets. Two specific complaints during testing on iPhone 15 prompted this pass:

1. *"On mobile I need to scroll up just a tiny bit to see the navbar."* — caused by the navbar being in document flow under a `min-h-screen` content wrapper, so the document was always slightly taller than the viewport.
2. Quantity values rendered with up to 3 decimals (`19,501 vrece`) and date metadata used relative labels (`dnes`, `pred 5 dňami`) where the customer wanted explicit dates.

**Implemented:**

- **Sticky navbar** (`components/navbar/navbar.component.html`) — added `sticky top-0 z-30` plus inline `padding-top/left/right: env(safe-area-inset-*)`. Modals/slide-overs already use `z-40`/`z-50`, so they continue to overlay correctly.
- **PWA meta + manifest hardening** (`client/src/index.html`, `client/public/manifest.webmanifest`):
  - `viewport-fit=cover` (enables `env(safe-area-inset-*)` on iOS)
  - `apple-mobile-web-app-status-bar-style: default`
  - Two `theme-color` meta tags for light / dark `prefers-color-scheme`
  - Manifest now has `lang: sk`, `dir: ltr`, `scope`, `display_override`, `orientation: any`, `categories: ["business", "productivity"]`
- **Global iOS / mobile tweaks** (`client/src/styles.css`):
  - `overscroll-behavior-y: contain` (no white rubber-band band on iOS standalone)
  - `-webkit-tap-highlight-color: transparent` (no grey tap rectangle)
  - `@media (max-width: 1024px)` forces all text-like inputs to `font-size: 16px !important` so iOS Safari does not zoom on focus
  - New utility classes: `.safe-top`, `.safe-bottom`, `.safe-left`, `.safe-right`, `.min-h-dvh`, `.h-dvh`
- **Sticky footer safe-area** (`components/location-manage-panel/location-manage-panel.component.html`) — applied `.safe-bottom` to the grand-total / Excel-download footer.
- **Kiosk header safe-area** (`pages/kiosk/kiosk.page.html`) — applied inline `padding-*: max(default, env(safe-area-inset-*))` so the wall-mounted-tablet header stays clear of the dynamic island and landscape notch.
- **Quantity formatting** (`components/location-manage-panel/location-manage-panel.component.ts`) — `formatQty()` now caps at 2 decimals max and lets Intl drop trailing zeros: `5` → `"5"`, `19.501` → `"19,5"`, `19.55` → `"19,55"`.
- **Explicit dates instead of relative labels** (`location-manage-panel.component.html`) — both summary and detail rows now call `formatDate(...)` (`dd.mm.yyyy`) instead of `relativeDay(...)`. `relativeDay` helper retained but unused.
- **Cleaner SQLite self-heal log** (`API/Program.cs`) — V1.1 self-heal `ALTER TABLE ... ADD COLUMN` blocks now use `pragma_table_info` checks instead of try/catch swallowing duplicate-column errors. Result: no more noisy `fail:` log lines on startup when the EF migration already added the columns.

**No backend / migration changes.** This release is pure UI / runtime polish.

---

### 2026-04-26 (V1.1) — Material costs, inline catalogue add, smart date default

**Customer feedback addressed:**

- **Smart "Add entry" date default.** When the date filter is set to a non-current month (e.g. last month), clicking + Pridať záznam now defaults to the LAST day of the filtered range instead of today. If today is inside the filter range, today is still used. Implemented as `defaultEntryDate()` in `LocationManagePanelComponent`, called by `toggleAdd()` and after a successful save.
- **Material price (inflation-protected).** Added `Material.PricePerUnit` (EUR, decimal 12,4) and `MaterialUsage.UnitPriceAtTime` (EUR, decimal 12,4 — snapshot). Changing the catalogue price does **not** rewrite history: existing usages keep their original price snapshot, so old reports stay accurate. The snapshot is taken on `POST /api/locations/{id}/materials` and is preserved on `PUT` unless the caller (a) explicitly sends `unitPriceAtTime`, or (b) switches the entry to a different material (in which case the new material's current price is snapshotted).
- **Inline "Pridať nový materiál" in the panel.** The slide-over now has a "+ Nový materiál" link beside the material dropdown — opens a small inline form (name + unit + price + unit-preset chips) so the customer can add a missing item without leaving the location they're working on. The new material is auto-selected in the entry form after creation.
- **Cost calculations everywhere.** Summary table shows per-material totals in EUR (`r.totalCost`) plus a grand-total row. Detail rows show line cost. Live cost preview is shown beside the quantity input in the add form. Sticky footer shows the grand total in big bold green. The Excel export gained two new columns on the detail sheet (Cena/jednotka, Náklady) and one on the summary sheet (Spolu náklady), each with grand-total rows formatted as `#,##0.00 €`.
- **Catalogue page now exposes price.** `/admin/materials` has a Cena/jednotka column (right-aligned, tabular-nums) and a price field in both add and edit forms. Materials with no price set show the value in amber as a hint that costs will be 0.

**Migration command for V1.1:**

```
cd API
dotnet ef migrations add AddMaterialPrice
dotnet run
```

Self-heal blocks already cover both new columns on SQLite (`ALTER TABLE … ADD COLUMN … DEFAULT '0'`) and PostgreSQL (`information_schema.columns` guard + `ALTER TABLE … ADD COLUMN … DEFAULT 0`), so the app will start cleanly even if the migration is run after the dev DB already has data.

**Future work parked (per customer):**

- Per-employee `HourlyWage` for full financial management (combined labour + material spend per site). Customer asked to keep this in their back pocket for now — see BACKLOG.md.

---

### 2026-04-26 — Material consumption tracking (V1)

**Implemented (full-stack):**

- **Backend** — new `Material` and `MaterialUsage` entities, full CRUD for the catalogue (`/api/materials`), and per-location usage endpoints (`/api/locations/{id}/materials[...]`) with summary and Excel export. Photo upload per-usage uses the existing Cloudinary blob service; deleting a usage also removes its photo. `MaterialExcelExportService` (ClosedXML, already in `API.csproj`) produces a two-sheet `.xlsx` (Súhrn + Detailný záznam) with amber header, frozen panes, autofit columns, real Excel dates and numeric quantities, and Cloudinary hyperlinks for photos. Filename: `Spotreba_{Location}_{from}_{to}.xlsx` (Slovak diacritics stripped, spaces → `_`). Both SQLite and PostgreSQL self-heal blocks added in `Program.cs`, plus a first-run catalogue seed of 10 common Slovak items.
- **Frontend** — new `MaterialService`, new `/admin/materials` admin catalogue page, new "Materiál" link in the navbar (desktop + mobile), and a new `LocationManagePanelComponent` slide-over right-hand panel mounted from the Lokácie page (300ms slide-in, backdrop / Esc / ✕ all dismiss, body scroll locked while open, mobile-friendly width).
- **UX details** — date filter defaults to the current calendar month with a "Tento mesiac" pill that re-snaps after manual changes. Quick-quantity chips `[1, 5, 10, 20, 50]` mirror the kiosk hours-presets pattern. Detailed log shows friendly relative dates (`dnes` / `včera` / `pred N dňami`) within the last week. Sticky footer keeps the top-2 material totals and a green "Stiahnuť Excel" button visible while scrolling.
- **Approach** — chose *DB-as-truth + Excel as report format* (Approach B) over a two-way Excel-file sync. Rationale documented in `MATERIALS_PLAN.md`.

**Migration command (must be run before first use):**

```
cd API
dotnet ef migrations add AddMaterialsAndUsage
dotnet run
```

The CLI generates both `.cs` and `.Designer.cs` files (per the Migration Safety Rules above). The self-heal blocks are an idempotent backstop and will quietly do nothing if the migration runs first.

---

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
