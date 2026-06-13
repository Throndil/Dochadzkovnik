<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Šichtovnica — Customer Call Backlog

> Translated from SK call notes. Check off items as they are implemented.

---

## ✅ Done

- [x] Rename app title from "Dochádzka / Prehľad" to **Šichtovnica**
- [x] Company logo displayed next to app name on kiosk and admin navbar
- [x] Favicon changed to company logo
- [x] `−`/`+` hour adjustment circles in the hours entry modal made bigger (`w-12` → `w-16`)
- [x] Default date range changed from current week to current month (Reports, Time Entries, My Hours)
- [x] Default admin credentials confirmed: username `vladosroka` / password `Nikolasko1`

---

## 🖼️ Photo / Image Features

- [x] **Photo attachment when logging hours (kiosk clock-in/out flow)**
  - Camera + gallery buttons in the hours modal; HEIC normalised client-side via `heic2any`
  - Photo uploaded to Cloudinary after hours entry is saved; failure is non-blocking

- [x] **Photo attachment on manual entry (manager side)**
  - Admin time-entries add/edit form has photo upload with thumbnail preview and replace/delete

- [x] **Location photo gallery**
  - Grid gallery on the location detail page, filtered by month
  - Individual photo deletion and bulk delete (with date cutoff)
  - "Download all" as ZIP via Cloudinary API

- [x] **Photo storage strategy**
  - Extended existing Cloudinary `IBlobStorageService`; work photos stored under `work-photos/{locationId}/{year-month}/`
  - Admin can download per-location ZIPs and delete old photos

- [x] **HEIC → PNG conversion**
  - Frontend: `heic2any` in `client/src/app/utils/image-utils.ts`
  - Backend: `SixLabors.ImageSharp` in `API/Services/ImageProcessingService.cs` — normalises all uploads to PNG, caps at 2048px
  - **Note:** Run `npm install heic2any` in `client/` once before building

- [x] **"Nahral fotografiu" / "Nenahral fotografiu" status badges on clocked hours**
  - Admin "Záznamy dochádzky" table: Foto column shows thumbnail + green "✓ Nahral" badge, or grey "✗ Nenahral" badge (both desktop table and mobile cards)
  - Kiosk "Moje hodiny" table: new "Fotografia" column with same green/grey badges per row

- [x] **"Moje hodiny" date pickers work on mobile**
  - Removed `appDate` (flatpickr with `disableMobile: true`) from the Od/Do fields in "Moje hodiny"
  - Now uses plain `type="date"` with `[color-scheme:dark]` — triggers native mobile date picker

- [x] **Location gallery delete button visible on mobile**
  - Delete `×` button was `hidden group-hover:flex` (hover-only) — invisible on touch devices
  - Now always visible as `flex` with semi-transparent red background

- [x] **"Nahrať fotografiu" tab (proof-of-work standalone photos)**
  - Replaced the "Ručný záznam" tab with a new "Nahrať fotografiu" tab on the kiosk
  - Step flow: PIN numpad → location selection → photo capture/pick → result with employee name badge
  - Three camera options: selfie (front camera for identity), rear camera, gallery
  - Photos saved to new `WorkPhotos` table (separate from `TimeEntries`), uploaded to Cloudinary under `work-photos/{locationId}/{year-month}/`
  - Location gallery now unions both TimeEntry photos and standalone WorkPhotos
  - Standalone work photos show a "Foto" badge in the gallery grid; delete works correctly for both types
  - **Note (future):** Consider making front-facing selfie mandatory for stronger identity verification

---

## 📱 Mobile / PWA UX (April 15 call)

- [x] **Disable double-tap zoom on mobile/tablet**
  - Viewport meta updated: `maximum-scale=1, user-scalable=no`
  - Global CSS: `touch-action: manipulation` on `<html>` — disables double-tap zoom while keeping normal scroll/tap

- [x] **Fix PWA app icon resolution (distorted on home screen)**
  - Original logo was 1920×1280 (not square) — looked stretched/cropped when added to home screen
  - Generated proper square icons: 512×512, 192×192, and 180×180 (apple-touch-icon)
  - Updated `manifest.webmanifest` with separate `any` and `maskable` purpose entries at correct sizes
  - Updated `index.html` to reference the new square icons

- [x] **Add car (Auto) column to "Moje Hodiny" kiosk view**
  - Workers can now see which car was used for each entry when checking their logged hours
  - Shows 🚗 + car name, or "—" if no car

- [x] **"Záznamy" default to full month with week toggle**
  - Default date range now covers the entire current month (1st to last day), not just up to today
  - Added "Mesiac / Týždeň" toggle buttons above the date filters
  - Manual date changes switch to "custom" mode so the toggle doesn't override user picks

- [x] **Remove Príchod / Odchod columns from "Moje hodiny", add Dátum column**
  - Table on the kiosk "Moje hodiny" view no longer shows the exact clock-in / clock-out timestamps
  - Added a Dátum column (formatted `dd.MM.yyyy` from `clockIn`) as the first column so workers can still tell which day each row belongs to
  - Final column order: Dátum, Pracovisko, Auto, Hodiny, Foto záznamu, Poznámka
  - Footer "Spolu" colspan adjusted accordingly (3 → Dátum + Pracovisko + Auto)

- [x] **Spolu column on kiosk weekly overview = full-month total**
  - The per-employee "Spolu" column next to the 7-day grid now sums hours for the whole calendar month that contains the currently viewed week, not just the 7 visible days
  - Backend `GET /api/kiosk/overview` expands its query range to cover the whole month and restricts `TotalHours` to that month; the daily cells still only show the 7 days of the viewed week

- **Spolu column on month-cutoff weeks — confirmed: keep the per-month split.** Briefly collapsed to one number on 2026-04-30 and reverted at customer request. The display must show two stacked sub-totals like `Apríl: 80h / Máj: 12h` so the manager sees what the worker did in each calendar month separately. Do not collapse this again.

- [x] **Manual time entry (admin) is now hours-based, not clockIn/clockOut**
  - Replaced Príchod / Odchod date+time fields on the admin Záznamy dochádzky add and edit forms with a single Dátum picker and a Počet hodín control (±0.5h buttons plus preset chips: 0.5, 1, 2, 4, 5, 5.5, 6, 7, 7.5, 8, 9, 10)
  - Mirrors the kiosk "log hours" UX — easier/faster for managers
  - Frontend back-calculates clockIn/clockOut before POST/PUT using the same convention as `/api/kiosk/log-hours`: past date → clockOut = 17:00 that day; today → clockOut = now; clockIn = clockOut − hoursWorked. No backend/API change.

- [x] **"Týždeň" quick-range button actually updates the Od/Do pickers**
  - The "Mesiac / Týždeň" toggle on `Záznamy dochádzky` was reassigning `from`/`to` component fields, but flatpickr's visible alt-input didn't re-render because the directive only read `input.value` at initialisation
  - `DatepickerDirective` now implements `ControlValueAccessor`, so any programmatic `[(ngModel)]` write (e.g. "Týždeň") calls `fp.setDate(...)` and the picker label updates in-place
  - Side benefit: the same fix applies to any future programmatic date changes on the Reports/Time-entries pages

- [x] **Admin add-entry dropdown filters out inactive employees + backend guard**
  - A manually added entry for a deactivated employee would be invisible in the kiosk "Moje hodiny" view, because `FindEmployeeByPin` only resolves `IsActive = true` employees. This was the root cause of the "worker can't see their entry" bug reported on 2026-04-21.
  - The add form now uses `activeEmployees()` (computed from `employees().filter(e => e.isActive)`); the filter dropdown above the list still shows everyone so historical entries remain searchable.
  - `POST /api/time-entries` also rejects creates for inactive employees with a clear Slovak error message, so the frontend filter is a defensive layer rather than the sole gate.

- [x] **Moje hodiny surfaces backend errors instead of silent empty state**
  - When the kiosk returned 401 (wrong PIN) or another error, the UI previously cleared entries and showed "Žiadne záznamy za toto obdobie" — indistinguishable from a legitimate empty month
  - Added a `myHoursError` signal; the result card now renders the backend message in red when the request fails

---

## 📦 Material tracking (April 26)

- [x] **Per-location material consumption tracking with Excel export (V1)**
  - New `Material` catalogue and `MaterialUsage` records, full backend CRUD and a two-sheet Excel export (Súhrn + Detailný záznam) generated on demand from the database
  - "Spravovať" button on every Lokácia card opens a slide-over right-hand panel — backdrop / Esc / ✕ dismiss, mobile-friendly, body scroll locked while open
  - Panel features: date-range filter (defaults to current month, "Tento mesiac" pill), summary table, add/edit/delete entries with quick-quantity chips, sticky footer with top-2 totals and green "Stiahnuť Excel" button
  - New `/admin/materials` admin page for managing the catalogue (add / edit / toggle active / delete with soft-delete fallback when usage exists)
  - New "Materiál" link in the navbar; first-run seed inserts 10 common Slovak items (Cement, Voda, Piesok, Štrk, Obklad, Dlažba, Omietka, Lepidlo, Sadrokartón, Skrutky)
  - Approach: DB-as-truth + Excel as report format (chosen over two-way file sync — see `MATERIALS_PLAN.md` for rationale)
  - **Required before first use:** `cd API && dotnet ef migrations add AddMaterialsAndUsage && dotnet run`

- [x] **Per-unit cost / spend totals (V1.1, 2026-04-26)** — added `Material.PricePerUnit` (EUR) and `MaterialUsage.UnitPriceAtTime` (snapshot). Costs are inflation-protected: changing the price later does not affect existing records. Summary table, detail list, sticky-footer grand total, and the Excel export (both sheets) now show line costs and grand totals in `#,##0.00 €` format.
- [x] **Inline "+ Nový materiál" in the location panel (V1.1, 2026-04-26)** — customer no longer has to leave the panel to add a missing catalogue item; a collapsible mini-form sits beside the material dropdown.
- [x] **Smart default date when adding to a non-current month (V1.1, 2026-04-26)** — when the date filter is on a previous month, clicking + Pridať záznam defaults to the last day of that filter's range instead of today. Today is still used when it falls inside the filter.

- [ ] **Kiosk material logging (V2)** — let workers log material usage from the tablet right after clocking out
- [ ] **Excel import (V2)** — accept a customer-provided .xlsx and seed `MaterialUsages` from it (one-time migration of historical data)
- [ ] **Stock / inventory mode (V2)** — current model is consumption only, not warehouse stock; confirm with customer before building
- [ ] **Cross-location material dashboard (V2)** — "How much cement did all sites use in March?" report
- [ ] **Photo retention policy for material delivery-slip photos (V2)** — same open question as work photos

---

## 💶 Financial management (parked — customer's "back pocket")

> **Scoped 2026-05-25** — the three items below are now bundled into
> `PAYROLL_AND_PNL_PLAN.md`, together with the Mzdy view and Per-workplace
> net profit view from the 2026-05-24 call. Customer confirmed on
> 2026-05-24 by asking for Mzdy concretely. Read the plan before starting.

- [ ] **Hourly wage per employee** — add `Employee.HourlyWage` (EUR/hr). Combined with TimeEntries, this enables full per-site financial management (labour cost + material cost = total spend per Lokácia). Customer asked to keep this in their back pocket on 2026-04-26 — flag/build only when they confirm. *See `PAYROLL_AND_PNL_PLAN.md` §Schema.*
- [ ] **Per-location P&L view** — once both wages and material prices are in place, surface a single "Náklady na pracovisko" panel showing labour + material breakdown, optionally with a contract value field for margin tracking. *See `PAYROLL_AND_PNL_PLAN.md` §Admin UX — `/admin/locations/:id`.*
- [ ] **Historical wage snapshotting** — same inflation-protection pattern used for `MaterialUsage.UnitPriceAtTime`: store the wage on each `TimeEntry` row at the moment it's logged, so retroactive raises don't rewrite payroll history. *See `PAYROLL_AND_PNL_PLAN.md` §Design decision (a).*

---

## 📅 Date / Time Rules

- [x] **Maximum 2 days back for hour logging**
  - Kiosk date picker has `[min]="twoDaysAgoString()"` and `[max]="todayString()"`
  - Added `clampSelectedDate()` called on `(change)` and before every `submitHours()` — enforces the range in TypeScript so mobile browsers that allow typing/scrolling past min/max cannot bypass it
  - Manager manual entry remains unrestricted

- [ ] **Date column in CSV export**
  - The CSV export currently has: Employee, Location, Car, Hours, Note
  - Add a **Date** column to the export

---

## 🔔 Notifications & Reminders

> **Direction (decided 2026-04-26):** V1 ships free channels only — **PWA push notifications** (primary) + **WhatsApp Business Cloud API** (secondary, opt-in per employee). One trigger only: worker has not clocked any hours in the past 2 days. SMS path deferred to V2 fallback.
> Customer's workforce is older / non-tech-savvy — see `NOTIFICATIONS_PLAN.md` §10 for the UX rules.

- [x] **V1 — 48h-no-activity reminder via PWA push** (`NOTIFICATIONS_PLAN.md` M1) — *shipped 2026-04-26*
  - Trigger: worker with no `TimeEntry` in the past 48h on a working day
  - Channel: web push via `WebPush` NuGet, VAPID keypair in Railway env
  - Subscription: kiosk-side "Povoliť upozornenia" tile (big, plain-Slovak, links to existing install-guide PDFs) — *kiosk tile pending (M2 frontend follow-up)*
  - Persistence: `PushSubscription`, `NotificationLog`, `NotificationConfig` tables; new `Employee.NotificationsEnabled` column

- [x] **V1 — Admin "Notifikácie" page** (`NOTIFICATIONS_PLAN.md` M2) — *shipped 2026-04-26*
  - Toggle trigger on/off, set fire time, working-days flag
  - Per-employee `NotificationsEnabled` / `WhatsAppEnabled` checkboxes + last-notified date
  - 30-day send-history table
  - "Test push" + "Test WhatsApp" buttons (replaces the old "Internal SMS reminder tester" backlog item)
  - Demo controls: "Fire now", "Fire for employee" (with `ignoreIdempotency`), "Reset today" — for live customer demos

- [~] **V1 — WhatsApp Business Cloud API channel** (`NOTIFICATIONS_PLAN.md` M3) — *stub shipped, awaiting Meta credentials*
  - `IWhatsAppService` + `WhatsAppCloudApiService` interface and skeleton in place; real send blocked on customer setting up Meta Business account + approved template
  - Approved Utility Template (Slovak copy in `NOTIFICATIONS_PLAN.md` §7) — *needs submission to Meta*
  - Per-employee opt-in via `Employee.WhatsAppEnabled` toggle — *shipped*
  - Uses `Employee.WhatsAppNumber` (or falls back to `Employee.PhoneNumber`) — *shipped, fallback works in admin UI*

- [ ] **V1 polish — manager daily summary push** (`NOTIFICATIONS_PLAN.md` M4)
  - Single push to the manager listing all workers who triggered today
  - Click action opens admin "Záznamy dochádzky" filtered to today

- [ ] **V1 polish — `NotificationLog` retention sweeper**
  - Default 90 days; configurable in `appsettings`
  - Background sweeper deletes older rows (GDPR considerations — phone numbers / names in audit log)

- [ ] **V2 — SMS as fallback channel** (parked — see `SMS_PLAN.md`)
  - Only revisited if push + WhatsApp coverage proves insufficient for some workers
  - Provider candidates: Twilio (~€0.075/SMS to SK), SMSAPI.sk (~€0.03/SMS), smsmanager.cz (~€0.025/SMS), or self-hosted GSM gateway with an unlimited-SMS SIM (4ka / O2)

- [ ] **V2 — Telegram / Viber bot channels** (parked)
  - Both completely free, but require workers to start a chat with the bot to opt in
  - Viber more common than Telegram in Slovakia

- [ ] **V2 — Two-way replies** (parked)
  - Worker replies "OK" → marks them as still active, suppresses tomorrow's reminder

---

## 📱 PWA / Mobile

- [ ] **Add to home screen guide — iOS**
  - Create a short in-app or PDF guide for adding the web app to the iOS home screen (Safari → Share → Add to Home Screen)

- [ ] **Add to home screen guide — Android**
  - Create a short in-app or PDF guide for Android (Chrome → three-dot menu → Add to Home Screen / Install App)

- [ ] **App icon for PWA (from email)**
  - Customer sent an app icon image via email — add it as the PWA manifest icon (`manifest.json` `icons` array) so it appears correctly when installed on home screen

---

## ⚙️ Technical / Infrastructure

- [ ] **Commander page perf — additional optimisations (parked)**
  - First wave shipped 2026-05-05: tacho+summary parallelised per vehicle inside `BuildOneVehicleStatsAsync`; fleet-stats moved off the initial `forkJoin` and lazy-loaded via an effect when the user opens the Prehlad tab; reverse-geocoding refactored to a view-aware effect (Detail → only the selected vehicle, Prehlad → all visible). For a Detail-only session at any fleet size, page open now costs **2 cheap Commander calls + 1 ORS call** instead of `4N + 5` Commander + `N` ORS.
  - The following are deferred. Pick when the customer's actual fleet size + behaviour data shows we still need them:
    1. **Bump fleet-stats cache TTL to 2-3 min.** Currently 60 s. Halves Commander load on rapid Obnoviť cycles. Trade-off: today's km / avg-speed are up to 3 min stale; rides happening "right now" don't show up until the next refresh window.
    2. **Batched reverse-geocode endpoint.** New `POST /api/commander/reverse-geocode-batch` taking N coords, returning N labels. Backend fans out to ORS in parallel (cache-first per coord cell). Saves the per-load HTTP round-trip count from N to 1. Only worth it past N ≈ 20 vehicles.
    3. **Background fleet-stats warmer.** A `BackgroundService` (same shape as `NotificationBackgroundService`) hits `/fleet-stats` every 2 min during business hours so the cache is always primed. Eliminates the first-Prehlad-open latency. Adds steady idle Commander quota cost — at 4N calls / 2 min = ~2N calls/min, fine even for 30-vehicle fleets (~60/min vs. 300/min cap).
    4. **Per-row expansion in Prehlad.** Show only `Tachometer` + `Dnes` by default; `Týždeň` + `Mesiac` revealed on row click. Drops the per-vehicle `/rides` window from 31 days to today-only, which collapses pagination from up to 5 calls/vehicle to 1. Costs UX clarity — managers lose at-a-glance week/month context.
    5. **Smarter `/rides` pagination cap.** Currently `MaxRidePages = 5` (≈ 500 rides ≈ 31 days). Drop to 3 pages (~300 rides ~18 days) and rely on the existing `Truncated` flag to surface incomplete data. Most vehicles never hit the cap; high-mileage / high-frequency vehicles would lose Mesiac accuracy until we paginate further on demand.
  - All five layer additively. Combination is fine; #2 + #3 together gets the strongest result for large fleets.

- [ ] **Developer vs Production build configuration**
  - Set up separate environment configs (`environment.ts` / `environment.prod.ts`) with distinct API URLs, feature flags, logging levels
  - Ensure `ng build --configuration production` targets production API and disables dev tooling

- [ ] **Commander API integration (next session)** — see `COMMANDER_PLAN.md`. Env-var slots and security guard rails are already in place; seven open questions need to be answered with the customer before any code is written. Customer credentials must never enter `appsettings.json`, logs, DTOs, or frontend code; gate the controller behind a `CommanderIntegration` feature flag matching the Notifications pattern.

- [x] **Removed worker phone numbers from public kiosk view + split into two endpoints (V1.3.2, 2026-04-30)**
  - The kiosk's "Bez záznamu hodín za posledný týždeň" list rendered each missing worker's phone number as a clickable `tel:` link. The kiosk runs on a wall-mounted tablet at construction sites — anyone walking past could read it
  - Worse, the kiosk endpoint `/api/kiosk/missing-hours-overview` is anonymous (no JWT), so the phone numbers were also being served to any unauthenticated HTTP client
  - The admin Notifikácie page legitimately needs phone numbers (for call/SMS), so a single shared DTO doesn't work
  - Fix: split into two endpoints with two separate DTOs:
    - `/api/kiosk/missing-hours-overview` — anonymous, **no phone**, used by the kiosk public banner. DTO `EmployeeMissingDaysDto` (no PhoneNumber field at all)
    - `/api/employees/missing-hours-overview` — JWT-protected, **with phone**, used by the admin Notifikácie page. DTO `EmployeeMissingDaysAdminDto`
  - Frontend mirror: `EmployeeMissingDays` (in `kiosk.service.ts`, no phone) vs `EmployeeMissingDaysAdmin` (in `employee.service.ts`, with phone). Type system enforces the boundary
  - Comments in DTOs and services explain the split so a future change can't accidentally merge them and reintroduce the leak

- [x] **Env-var hardening + secrets out of `appsettings.json` (V1.3.1, 2026-04-30)**
  - All credential fields in committed config files are now empty strings; values come from Railway env vars (prod) or gitignored `appsettings.Local.json` (local dev)
  - `Program.cs` fails loud on missing/short `Jwt__Key`; seed skips with a logged warning if `AdminSeed__*` / `SuperAdminSeed__*` aren't configured
  - `Commander__Username` / `Commander__Password` placeholder slots pre-allocated for the upcoming Commander API integration so customer credentials never enter code
  - `SECRETS.md` is the canonical env-var reference; read it before touching infra
  - The single-underscore env-var trap (`Jwt_Key` ignored vs. `Jwt__Key` mapped to `Jwt:Key`) is documented so it doesn't bite again

- [x] **Superadmin user + runtime feature-flag toggles (V1.3.0, 2026-04-30)**
  - Second admin identity `admin` / `Superadmin12345!!` (configurable via `SuperAdminSeed:Username` / `:Password`) seeded alongside `vladosroka`
  - JWT carries `isSuperAdmin: "true"` claim when the username matches the configured superadmin
  - New `FeatureFlags` table (Key string PK + Enabled bool + UpdatedAt) stores per-feature on/off state
  - `GET /api/feature-flags` (anonymous) returns the current map; `PUT /api/feature-flags/{key}` (superadmin only) flips a flag
  - `[RequireFeatureOrSuperAdmin("Notifications")]` action filter applied to `NotificationsController` — superadmin always passes; everyone else gets 404 when flag is off
  - Frontend `FeatureFlagService` loads the map via `provideAppInitializer`; `auth.isSuperAdmin()` exposed for templates and route guards
  - "Funkcie" card on the Account page (superadmin-only) hosts the toggles; first toggle is for Notifications
  - Prod boots with Notifications=false so the customer sees zero notification UI; flag is flipped on per-environment by the superadmin once the feature is signed off
  - **Migration to run before first deploy:** `cd API && dotnet ef migrations add AddFeatureFlags`

---

## 🗓️ Customer call (2026-05-24)

> Raw notes captured during the 2026-05-24 call. Not yet scoped — each item below
> needs a short brief / plan file before implementation starts, same pattern as
> `MATERIAL_PURCHASES_PLAN.md`. Cross-references to existing parked items noted
> where they overlap.

- [ ] **Block / deactivate an employee from a single place**
  - Customer wants a clear way to block an employee (stop them from clocking in, hide them from kiosk tiles, keep their history intact).
  - Today there is an `IsActive` flag and inactive employees are filtered out of the kiosk PIN resolver + admin add-entry dropdown (see V1 fixes around 2026-04-21), but there is no obvious "block" action surfaced in the admin UI.
  - Clarify: should "blocked" be the same as `IsActive = false`, or a separate state (e.g. `IsBlocked`) that also disables WhatsApp / push notifications and the missing-hours banner? Confirm with customer before adding a column.

- [ ] **Mzdy (payroll) view — monthly per-employee summary** — *scoped in `PAYROLL_AND_PNL_PLAN.md` 2026-05-25.*
  - New admin page or sub-tab. One row per employee for the selected month with columns:
    `Meno | Hodiny v mesiaci | Hodinová sadzba | Zálohy | Výplata`
  - "Výplata" = `Hodiny × Sadzba − Zálohy`.
  - Default month = previous calendar month (customer's phrasing "vyjde apríl" = the moment April ends, you open this and pay people for April).
  - Depends on the parked **Hourly wage per employee** item in the Financial management section + a new `EmployeeAdvance` (záloha) table (date, amount, note). Snapshot the wage on each `TimeEntry` at log time so retroactive raises don't rewrite history — same pattern as `MaterialUsage.UnitPriceAtTime`.
  - This is the customer's first concrete ask that unblocks the parked Financial management section. Build the wage + advance schema first, then the payroll view on top.

- [ ] **Construction diary (stavebný denník) as an alternative to the work photo** — *scoped in `PROOF_OF_WORK_UX_PLAN.md` 2026-05-25.*
  - Today the kiosk hours flow nudges the worker towards a photo (camera / gallery / "Nenahrať" if we ship the toggle below).
  - Customer wants a second proof type: a **stavebný denník** entry (free-text day log, possibly with a PDF / scanned page attachment). When the worker submits a denník, the system must NOT also demand a photo.
  - UI: two equal-weight tiles in the kiosk after hours are entered — "Fotografia" or "Stavebný denník". Picking either satisfies the proof-of-work requirement.
  - Data model: probably a new `WorkDiary` table (or reuse `WorkPhotos` with a `Type` enum: Photo / Diary). Diary rows carry text + optional file blob.

- [ ] **Expandable per-site roll-up of today's logged hours + notes**
  - On the kiosk (and admin Lokácia detail), show a collapsible list per site for the current day: each worker who already clocked hours appears as a row, with their hours and their note stacked underneath.
  - Purpose: when the next worker arrives at a site and starts clocking in, they can see what colleagues already did there today and write their own note in context (avoid duplicate notes, see ongoing tasks).
  - Read-only roll-up — workers can only edit / append to their own row.

- [ ] **Show site / workplace names inside the Material view**
  - In the admin Materiál page (catalogue side), each material's detail / row should surface the list of sites where it was consumed in the selected period.
  - Roughly an inverse of the current per-location `MaterialUsage` panel — instead of "site → materials" it's "material → sites + quantities".
  - Confirm with customer whether they want this as a column on the catalogue list, a drawer on row click, or a separate "Material by site" report.

- [ ] **Receipt (blocek) OCR — scan a receipt image and pre-fill purchase line** — *scoped in `INVOICE_SCANNING_PLAN.md` 2026-05-25.*
  - Worker takes a photo of the till receipt in the Nákup materiálu flow (already scoped in `MATERIAL_PURCHASES_PLAN.md`). Goal: extract supplier name, line items, quantities, unit prices, total from the image so the worker doesn't retype them.
  - Provider options to evaluate: Google Cloud Vision / Document AI, Azure Document Intelligence (receipts model), Mindee, or a local Tesseract pipeline. SK-language receipt accuracy is the deciding factor — pilot with real Stavebniny / OBI / Hornbach receipts before choosing.
  - Surface as a non-blocking enhancement: if OCR confidence is low, fall back to manual entry. Keep the original photo on the `MaterialPurchase` regardless.

- [ ] **Auto-fill current price from the most recent receipt when adding material to a workplace** — *partially scoped in `INVOICE_SCANNING_PLAN.md` 2026-05-25 (line price preserved at scan time; auto-fill on later entry is a separate small follow-up).*
  - When a worker selects a catalogue material in the šichta / Nákup flow for site X, default the unit price to the most recent `MaterialPurchase.UnitPrice` (or OCR-extracted price) for that material, not `Material.PricePerUnit`.
  - Keep `Material.PricePerUnit` as the manually-curated reference; never auto-update it from a worker entry (locked-in decision in `MATERIAL_PURCHASES_PLAN.md` — admin promotes prices via the "Aktualizovať katalógovú cenu" button).
  - This item is purely about the *default* shown in the input — the worker can always override.

- [ ] **PDF / multi-photo document scanning** — *scoped in `INVOICE_SCANNING_PLAN.md` 2026-05-25 (same OCR pipeline as receipt scan).*
  - Generalisation of the receipt OCR item: also accept PDF (e.g. supplier invoice, scanned construction diary page) and multi-page photo sets.
  - Likely shared service layer between the receipt-scan flow and the stavebný denník flow above.

- [ ] **Speed up hour logging (zrýchliť buchovanie hodín)** — *scoped in `PROOF_OF_WORK_UX_PLAN.md` 2026-05-25.*
  - Customer feedback: the current kiosk hour-logging flow has too many steps. Goal is fewer taps from "I want to clock 8h today" to confirmation.
  - Ideas to evaluate, do not implement blindly:
    - Skip the photo step when the worker has already attached a denník / photo earlier today for the same site.
    - Remember the most recent site + car selection per PIN and prefill them.
    - One-tap presets ("Celý deň 8h na X, žiadna poznámka") on the worker's tile.
  - Profile a real session with the customer (timed run-through) before redesigning — the bottleneck might be the photo upload, not the form.

- [ ] **Option to NOT upload a photo when logging hours** — *scoped in `PROOF_OF_WORK_UX_PLAN.md` 2026-05-25.*
  - Add a "Nenahrať fotografiu" tile / button alongside "Fotoaparát" / "Galéria" in the kiosk hours modal.
  - Backend already tolerates entries with no photo (the "Nahral / Nenahral" badges exist) — this is purely a UI affordance so workers stop feeling forced to take a photo every time.
  - Pair with the **Construction diary** item above so the proof-of-work expectation is "photo OR diary OR explicit skip", not "photo or guilt".

- [ ] **Per-workplace net profit view (Náklady → Čistý zisk)** — *scoped in `PAYROLL_AND_PNL_PLAN.md` 2026-05-25.*
  - Full P&L per `Location`: `Príjem (contract value) − (Mzdové náklady + Materiálové náklady) = Čistý zisk`.
  - Direct continuation of the parked **Per-location P&L view** + **Historical wage snapshotting** items in the Financial management section. Cannot ship before:
    - `Employee.HourlyWage` (parked) is added,
    - `TimeEntry.WageAtTime` snapshot is added,
    - The Mzdy view above is in place (proves the wage pipeline works),
    - `MaterialPurchase` is shipped per `MATERIAL_PURCHASES_PLAN.md` so material spend is captured at *purchase* price, not just consumption price.
  - Add a `Location.ContractValue` field (EUR, nullable) for the income side. If empty, show only the cost side and label profit as "—".
  - Lives on the same future "financial & statistics" sub-site mentioned in `MATERIAL_PURCHASES_PLAN.md` §"Long-term direction" — do not bolt it onto the main admin nav.

---

## ❓ To Clarify / Investigate

- [ ] **"Firma" location workaround — what does the customer envision?** (parked 2026-05-06)
  - The customer currently uses a `Location` row called "Firma" the same way they use "Nákup materiálu" — as a catch-all for time spent at the company / shop / office rather than at a real construction site.
  - During the 2026-05-06 conversation that scoped `MATERIAL_PURCHASES_PLAN.md`, the customer raised "Firma" as the next workaround they want addressed but did not say what the new flow should capture (admin tasks? travel? maintenance? meetings?).
  - **Do not start implementing anything for Firma until the customer describes what they want recorded against it.** Once they clarify, give it its own brief (e.g. `FIRMA_PLAN.md`) following the same pattern as `MATERIAL_PURCHASES_PLAN.md` — schema, kiosk vs. admin surface, feature flag, mobile/tablet rules, open questions.
  - Likely shape (do not assume yet): keep "Firma" as a Location, add a Location-triggered capture in the kiosk šichta flow same as the materials path, behind its own feature flag.

- [x] **Red tile for employees with no hours today**
  - Employee tiles on the kiosk main view turn red (border + background tint) when that employee has no completed entries for today
  - Only flags today — past days will be handled separately via SMS/email reminders
  - Flag disappears automatically if today is not in the current week view
