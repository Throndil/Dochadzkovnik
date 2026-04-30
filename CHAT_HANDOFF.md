<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Chat handoff — read me first

> Last session: 2026-04-30 (V1.3.1) — **Env-var hardening + secrets refactor.** Stripped every credential out of `appsettings.json`, removed all hardcoded password fallbacks from `Program.cs`, added fail-loud `Jwt__Key` startup check, gitignored `appsettings.Local.json`, shipped `SECRETS.md` as the canonical env-var reference. Pre-allocated `Commander__Username` / `Commander__Password` slots for the upcoming Commander API integration. Discovered + flagged a Railway naming bug where single-underscore env vars (`Jwt_Key`, `AdminSeed_Password`) were silently ignored by ASP.NET Core's config provider — operator fixed by renaming to `Jwt__Key`, `AdminSeed__Password`, etc. before this code change deploys. **Read `SECRETS.md` before doing any infra/secrets work.**
> Previous (same day, V1.3.0): Superadmin + FeatureFlags. Notifications now invisible to customer by default. New `admin` / `Superadmin12345!!` user controls a runtime "Funkcie" card on the Account page. Spolu column on month-cutoff weeks **kept** as the per-month split (Apríl: X / Máj: Y) — briefly collapsed to one number, reverted at the customer's request. **Migration must be generated locally**: `cd API && dotnet ef migrations add AddFeatureFlags` (the user reports it's already generated as `20260430172404_AddFeatureFlags`).
> Previous: 2026-04-27 dev branch set up (Vercel/Railway env split). 2026-04-26 V1.2.0 Notifications M1 shipped.
> Next session focus: **Run the migration locally + verify build, then push to dev.** Once dev is green, customer can be invited to test the new Spolu UI without seeing any of the still-hidden notification work.
> ⚠️ **SMS path is deferred** — `SMS_PLAN.md` is superseded by `NOTIFICATIONS_PLAN.md`. V1 ships free-channel only (push + WhatsApp).
> ⚠️ **Workers are older / non-tech-savvy.** Anything we ship must be obvious, big, plain Slovak, no jargon. See `NOTIFICATIONS_PLAN.md` §10 for UX rules.

---

## What was shipped in this session (2026-04-30 — V1.3.1, env-var hardening)

### The real-world trigger

Audit before plumbing in the Commander API (which carries the customer's third-party login credentials). Found:

- `appsettings.json` shipped `vladosroka` / `Nikolasko1` as `AdminSeed:Username/Password` since commit `09d4d22`. In git history forever.
- `Program.cs` had `?? "Nikolasko1"` and `?? "Superadmin12345!!"` fallbacks meaning the API would silently boot with the leaked defaults when env was misconfigured.
- Railway env vars were named with **single underscore** (`Jwt_Key`, `AdminSeed_Password`, ...) which ASP.NET Core's config provider does NOT translate — values were being ignored, prod was running on the placeholder `appsettings.json` JWT key.

Repo is private with two collaborators — owner accepted residual risk on `Nikolasko1` staying in history rather than rewriting git. Operational password kept; everything else hardened.

### Code changes

- `appsettings.json` — every credential field now empty string. Top `_README` block explains the policy. New `Cloudinary` and `Commander` placeholder sections added so future secrets have an obvious slot.
- `Program.cs`:
  - Loads `appsettings.Local.json` (gitignored) before env vars in the config chain — local dev source of truth.
  - `Jwt:Key` now fails loud at startup if missing or shorter than 32 bytes, with a message naming the exact env var to set.
  - `SeedAdminUser` no longer takes positional fallback strings. Reads `AdminSeed:*` and `SuperAdminSeed:*` straight from config; if username or password is missing it logs a clear `[SeedAdminUser] X skipped` warning and returns. Existing users keep their passwords on a config-less redeploy.
- `Services/TokenService.cs` — `superAdminUsername` no longer falls back to `"admin"`. If config is missing, NO user gets the `isSuperAdmin` claim (safer than implicit default).
- `.gitignore` — added `appsettings.Local.json` (root + nested glob).
- `appsettings.Local.example.json` — committed template for devs to copy → `appsettings.Local.json` and fill in.
- `SECRETS.md` (new, root) — canonical env-var reference. Required vs optional, the `__` vs `_` trap, fresh-environment runbook, what-to-do-when-Commander-ships block.

### Operator changes (already done in Railway by the user before this code shipped)

- Renamed `Jwt_Key` → `Jwt__Key`, `Jwt_Audience` → `Jwt__Audience`, `Jwt_Issuer` → `Jwt__Issuer`, `AdminSeed_Password` → `AdminSeed__Password`.
- Deleted the dead `ALLOWED__ORIGINS` (not picked up; the `AllowedOrigins__0/1` array form is what works).
- Generated a new strong `Jwt__Key`, replacing the leaked placeholder.
- Added `AdminSeed__Username = vladosroka`, `SuperAdminSeed__Username = admin`, `SuperAdminSeed__Password = <strong>`, `SuperAdminSeed__DisplayName = Superadmin`.
- `AdminSeed__Password` value left as `Nikolasko1` per owner decision (already in git history; private repo; rotation cost > residual risk).

### Pre-deploy checklist for THIS commit

1. `dotnet build` locally — should succeed (no API surface change beyond the JWT length check).
2. Run `dotnet run` locally with **no** `appsettings.Local.json` and **no** `Jwt__Key` env var → confirm it throws with the helpful `Jwt:Key is not configured...` message. Then create `appsettings.Local.json` from the example, fill in a 32+ char dev key + dev passwords, re-run → confirm boots clean and seeds the local users.
3. `cd client && npx tsc --noEmit -p tsconfig.app.json` — clean (no frontend changes this round).
4. Push to dev → Railway dev redeploys. Watch logs for:
   - `[SeedAdminUser] AdminSeed skipped …` — means the dev env vars aren't set; fix them.
   - JWT crash — means `Jwt__Key` isn't set on dev; set it (different value from prod).
5. Log in as `admin` (superadmin) on the Vercel dev preview, confirm the Funkcie card still works, confirm Notifikácie toggle still works.
6. Once dev is green, promote to master.

### What's NOT in this commit

- Commander API controller. Just placeholder `Commander__Username` / `Commander__Password` slots in env config + a section in `SECRETS.md` describing how to wire it.
- Git history rewrite for `Nikolasko1`. Owner declined; private repo + 2 collaborators makes residual risk acceptable.

---

## What was shipped earlier the same day (2026-04-30 — V1.3.0)

### Spolu month-cutoff display — kept the per-month split

Briefly collapsed the column to one combined number (`row.totalHours + row.totalHoursMonth2`) and reverted at the customer's request. The original behaviour stays: when the viewed week straddles a month boundary, the Spolu column shows two stacked rows like `Apríl: 80h / Máj: 12h` so the manager sees per-month hours, not a fused total. `client/src/app/pages/kiosk/kiosk.page.html` ends up unchanged from before the session.

### Superadmin + FeatureFlags (hides unfinished work from customer)

The customer is about to see a fresh deploy and we want notifications invisible until they sign off. Solution: a second admin identity that can flip features on/off at runtime.

**New superadmin user** seeded alongside `vladosroka`:
- Username: `admin`
- Password: `Superadmin12345!!`
- Configurable via `SuperAdminSeed:Username` / `:Password` / `:DisplayName` (defaults match the above)
- Marked by an `isSuperAdmin: "true"` JWT claim in `TokenService.CreateToken`
- Seed block in `Program.cs` refactored to `FindByNameAsync` per username so both users coexist (was "first user wins")

**New `FeatureFlags` table** (Key string PK + Enabled bool + UpdatedAt UTC):
- One row per feature; seeded `Notifications=false` on first run
- Migration: `cd API && dotnet ef migrations add AddFeatureFlags` — **must run locally before pushing**
- SQLite + PostgreSQL self-heal blocks added to `Program.cs` (CREATE TABLE IF NOT EXISTS pattern matching prior migrations)

**New `FeatureFlagsController`**:
- `GET /api/feature-flags` — anonymous, returns `{ notifications: bool }`. Kiosk needs this to know what to hide.
- `PUT /api/feature-flags/{key}` — superadmin-only (checks `isSuperAdmin` claim), body `{ enabled: bool }`.

**Action filter** `[RequireFeatureOrSuperAdmin("Notifications")]` (`API/Filters/`) applied to `NotificationsController`. Bypassed by superadmin claim; everyone else gets a 404 when the flag is off — defence in depth at the API level too.

**Frontend wiring**:
- `services/feature-flag.service.ts` — loaded via `provideAppInitializer` in `app.config.ts` so flags are populated before first navigation. Failures default to all-off.
- `services/auth.service.ts` — new `isSuperAdmin: Signal<boolean>` computed from a JWT claim decode (no library, just a `try/atob/JSON.parse`).
- `guards/notifications-feature.guard.ts` — `/admin/notifikacie` bounces to `/admin/dashboard` when not allowed.
- Navbar `Notifikácie` link gated by `flags.notifications() || auth.isSuperAdmin()` on both desktop and mobile menus.
- Kiosk push setup in `ngOnInit` skipped entirely when the flag is off.
- Employee detail "Odmietnutie upozornení" card gated by the same combined check.
- New "Funkcie" card on the Account page (`/admin/ucet`), superadmin-only, with the toggle. Amber "Superadmin" badge so it's clear which account it belongs to.

**Default behaviour:**
- Prod boots with `Notifications=false`. Customer logs in as `vladosroka` and sees zero notification UI anywhere.
- Dev: developer logs in as `admin`/`Superadmin12345!!` once after deploy, opens Account → Funkcie → flips Notifikácie ON. Flag persists in the dev DB (separate from prod).

**Pre-deploy checklist:**
1. `cd API && dotnet ef migrations add AddFeatureFlags` → confirm `.cs` and `.Designer.cs` both generated.
2. `dotnet run` locally; SQLite boots clean with no `fail:` log lines.
3. `cd client && npx tsc --noEmit -p tsconfig.app.json` — must be clean. The sandbox's bash mount served stale file content during this session so this needs running on Windows.
4. `dotnet build` — same caveat.
5. Push dev branch → Railway dev redeploys → migration applies → log in as `admin` and flip Notifikácie ON for dev only.
6. After the customer signs off on Notifications later, log into prod as `admin` → Funkcie → toggle on.

**Files touched:**
- `client/src/app/pages/kiosk/kiosk.page.ts` — gate push init on flag
- `client/src/app/services/auth.service.ts` — `isSuperAdmin` signal + JWT decoder
- `client/src/app/services/feature-flag.service.ts` — NEW
- `client/src/app/guards/notifications-feature.guard.ts` — NEW
- `client/src/app/app.config.ts` — `provideAppInitializer`
- `client/src/app/app.routes.ts` — guard on `/admin/notifikacie`
- `client/src/app/components/navbar/navbar.component.{ts,html}` — gated link
- `client/src/app/pages/account/account.page.{ts,html}` — Funkcie card
- `client/src/app/pages/employee-detail/employee-detail.page.{ts,html}` — gated decline card
- `API/Models/FeatureFlag.cs` — NEW
- `API/Data/AppDbContext.cs` — DbSet + OnModelCreating
- `API/Filters/RequireFeatureOrSuperAdminAttribute.cs` — NEW
- `API/Controllers/FeatureFlagsController.cs` — NEW
- `API/Controllers/NotificationsController.cs` — `[RequireFeatureOrSuperAdmin("Notifications")]`
- `API/Services/TokenService.cs` — `isSuperAdmin` claim
- `API/Program.cs` — refactored seed for two users + FeatureFlags self-heal + seed

---

## Dev branch setup (2026-04-27)

Two environments: **master** = production, **dev** = development/staging.

### What was done (code)

- `git checkout -b dev` — branch exists locally; push with `git push -u origin dev`
- `client/src/environments/environment.dev.ts` — placeholder dev API URL; update after Railway dev env is provisioned
- `angular.json` — new `dev` build configuration (fileReplacement → environment.dev.ts, sourceMap on, optimization off)
- `client/package.json` — `build:prod` and `build:dev` scripts
- `vercel.json` — buildCommand now uses `${BUILD_ENV:-prod}` so Vercel picks the right Angular config per environment

### Railway — finish in the dashboard

1. Open the Railway project → click **New Environment** → name it `dev`.
2. In the dev environment, open the service → **Settings → Source → Branch** → set to `dev`.
3. Copy all production environment variables into the dev environment (DB connection, VAPID keys, Cloudinary, etc.) then swap values for dev-specific ones (separate DB, separate Cloudinary folder if needed).
4. After first deploy, copy the generated dev service URL (e.g. `https://dochadzkovnik-dev.up.railway.app`) into `client/src/environments/environment.dev.ts` and push.

### Vercel — finish in the dashboard

1. Open the Vercel project → **Settings → Environment Variables**.
2. Add variable `BUILD_ENV` = `prod` for the **Production** environment.
3. Add variable `BUILD_ENV` = `dev` for the **Preview** environment.
4. Vercel auto-deploys the `dev` branch as a preview deployment whenever you push. The preview build will now run `npm run build:dev`, which uses `environment.dev.ts` and points to the Railway dev backend.

### Workflow going forward

- Feature work: commit and push to `dev` → Vercel preview URL is generated automatically → test there.
- Production release: open a PR from `dev` → `master` (or merge directly) → Vercel production + Railway production redeploy.
- Never commit directly to `master` for new features.

---

## What was shipped in this session (2026-04-26 — V1.2.0)

### Notifications M1 — PWA push, admin page, demo controls (code-complete, awaiting customer test)

**Backend (ASP.NET Core 9):**
- Models: `PushSubscription`, `NotificationLog`, `NotificationConfig`. `Employee` extended with `NotificationsEnabled`, `WhatsAppEnabled`, `WhatsAppNumber`.
- Services: `WebPushService` (lib-net-webpush), `WhatsAppCloudApiService` (stub for M3), `NoActivity48hEvaluator`.
- `NotificationBackgroundService` — 60s tick, fires at the configured `Europe/Bratislava` time. Idempotent via unique `(EmployeeId, Channel, TriggerType, TriggerDate)`.
- `NotificationsController` — 13 endpoints incl. demo controls (`fire-now`, `fire-for-employee` w/ `ignoreIdempotency`, `reset-today`, `test/push`, `test/whatsapp`).
- Migration `20260426150000_AddNotifications` (generated via `dotnet ef`) with SQLite + PostgreSQL self-heal blocks.

**Frontend (Angular 20 standalone + Tailwind):**
- `client/public/sw.js` — push + notificationclick handlers.
- `client/src/app/services/push.service.ts` — VAPID subscribe flow.
- `client/src/app/services/notification-config.service.ts` — typed HTTP client.
- `client/src/app/pages/notifications/` — admin Notifikácie page (4 cards: Konfigurácia, Zamestnanci, Test & Ukážka, História).

**Pre-deploy checklist:**
1. Generate VAPID keypair (`npx web-push generate-vapid-keys`); set `Notifications:Vapid:PublicKey`/`PrivateKey`/`Subject` in `appsettings.json` / Railway env.
2. Run `dotnet ef database update` once against staging PostgreSQL (self-heal in `Program.cs` covers gaps but a fresh apply is preferred).
3. HTTPS only — push refuses to work on plain HTTP.
4. Use admin → Notifikácie → Test & Ukážka card to verify demo controls live.

**Known follow-ups (not blocking M1):**
- M2 frontend: kiosk-side "Povoliť upozornenia" tile so workers can subscribe their own device (UX call: prominent banner vs. small button).
- M3: WhatsApp template approval at Meta + credential plumbing (`Notifications:WhatsApp:Token`, `:PhoneNumberId`, `:TemplateName`).
- Manager daily summary push (M4 — optional).
- `NotificationLog` retention sweeper (90-day default).

**Pre-existing fix done in passing:** `client/src/app/pages/time-entries/time-entries.page.ts:259` was using `null` instead of `undefined` for `photoUrl` after deleting the last photo (introduced by the V1.1.2 multi-photo refactor). Fixed during typecheck verification.

This file is the short, opinionated summary. The full project context lives in **`PROJECT_NOTES.md`** — read both before doing any work.

---

## What was shipped in the previous chat (2026-04-26)

### V1.1.1 — PWA / mobile polish

- **Sticky navbar** (`client/src/app/components/navbar/navbar.component.html`) — `sticky top-0 z-30` plus inline `padding: env(safe-area-inset-*)`. Fixes the "I have to scroll up to see the navbar on iPhone 15" complaint.
- **PWA meta + manifest hardening** (`client/src/index.html`, `client/public/manifest.webmanifest`) — `viewport-fit=cover`, status bar style, light/dark `theme-color`, manifest scope/orientation/lang/categories/display_override.
- **iOS-friendly `styles.css`** — `overscroll-behavior-y: contain`, transparent tap highlight, forced 16px font-size on inputs below `1024px` to prevent iOS zoom-on-focus, plus reusable `.safe-top` / `.safe-bottom` / `.safe-left` / `.safe-right` / `.min-h-dvh` / `.h-dvh` utilities.
- **Sticky footer safe-area** in `location-manage-panel.component.html`. **Kiosk header safe-area** in `kiosk.page.html` using `padding: max(default, env(safe-area-inset-*))`.
- **Quantity formatting** in `location-manage-panel.component.ts` — `formatQty()` capped at 2 decimals max; `19.501 → "19,5"`, not `"19,501"`.
- **Explicit dates in the location panel** — replaced `relativeDay(...)` calls with `formatDate(...)` (`dd.mm.yyyy`) per customer feedback "date doesnt make sense just show a date".
- **Cleaner SQLite self-heal log** (`API/Program.cs`) — V1.1 self-heal `ALTER TABLE ADD COLUMN` blocks now check `pragma_table_info(...)` first instead of relying on try/catch, so no more noisy `fail:` log lines on startup.

### V1.1.2 — Multi-photo upload for manual time entries

Customer ask: *"When adding a manual entry, make it possible for multiple photos to be uploaded."*

- **Backend** (`API/Controllers/TimeEntriesController.cs` `UploadPhoto`) — appends to comma-separated `entry.PhotoUrl` instead of replacing. DELETE behaviour unchanged: `?url=` removes one URL, no `?url` removes all.
- **Frontend TS** (`client/src/app/pages/time-entries/time-entries.page.ts`):
  - `newPhotoFile/Preview` and `editPhotoFile/Preview` (singular) → `newPhotoFiles/Previews` and `editPhotoFiles/Previews` (`signal<File[]>` / `signal<string[]>`).
  - New handlers: `onNewPhotosSelected`, `onEditPhotosSelected`, `removeNewPhotoAt(idx)`, `removeEditStagedPhotoAt(idx)`, `onEditDeleteExistingPhoto(url)`.
  - Sequential upload after create/update via `firstValueFrom` from rxjs inside an async subscribe — backend appends in order; per-file errors are swallowed so one bad upload doesn't kill the rest.
- **Frontend HTML** (`client/src/app/pages/time-entries/time-entries.page.html`):
  - Both add and edit forms now span `md:col-span-3` for the photo block and render a `flex-wrap` grid of 80×80 thumbnails plus a dashed-border `+ ďalšiu` tile.
  - Edit form distinguishes saved photos (slate border, `×` deletes server-side) from staged photos (amber border, `×` only removes locally). Hint text *"Žltý okraj = nahrá sa po uložení."* shown when staged additions exist.
  - File inputs carry `multiple`. Lightbox calls `openLightbox(arr, $index)` so the manager can swipe.

**No DB schema or migration change** in either V1.1.1 or V1.1.2.

---

## App reminders that always apply

- **The app is a PWA.** Every UI change must be tested on iPhone Safari standalone (added to home screen). See the `📱 PWA / Mobile Compatibility` section of `PROJECT_NOTES.md` for the full checklist.
- **Slovak only** — every UI string and API message is in Slovak.
- **Migration safety** — never write migration `.cs` files by hand. Always `cd API && dotnet ef migrations add <Name>`. Every new column also needs a SQLite self-heal block in `Program.cs` (now using `pragma_table_info` checks rather than try/catch).
- **Production data is real.** No destructive migrations, no DELETE / TRUNCATE in migrations, no DROP without confirming the column is unused.
- **Two distinct surfaces:** kiosk endpoints (`/api/kiosk/*`, no JWT) vs admin endpoints (JWT-protected). Don't accidentally route worker-facing data behind admin auth.

---

## 🔜 Next chat: Customer test of M1, then M2 kiosk tile + M3 WhatsApp creds

M1 ships the trigger, the admin UI, and demo controls. The customer can already see and demo it. Next priorities, in order:

### 1. Customer test (highest priority)

- Deploy with VAPID keys configured. HTTPS required.
- Open admin → Notifikácie. Walk the customer through the four cards.
- Use **Test & Ukážka → Test push** to send a push to your own subscribed device first.
- Then **Fire for employee** with `ignoreIdempotency: true` to demo a real reminder.
- Confirm the customer accepts the Slovak copy in `NOTIFICATIONS_PLAN.md` §7.

### 2. M2 — kiosk-side subscription tile

The admin can subscribe themselves now, but workers cannot. Next iteration:
- Add "Povoliť upozornenia" tile on the kiosk start screen (older-worker friendly per `NOTIFICATIONS_PLAN.md` §10 — big target, plain Slovak, no jargon, links to existing install-guide PDFs).
- Permission prompt + VAPID subscribe flow (the service already exists in `client/src/app/services/push.service.ts`, just call from the kiosk).
- UX call: do we show this banner *every* visit until subscribed, or just once with a settings entry to re-prompt?

### 3. M3 — WhatsApp credentials + template approval

The `WhatsAppCloudApiService` skeleton is in place. To go live:
- Customer creates Meta Business account + WhatsApp Business sender.
- Submit Slovak Utility Template (copy in `NOTIFICATIONS_PLAN.md` §7) for approval.
- Set `Notifications:WhatsApp:Token`, `:PhoneNumberId`, `:TemplateName` in Railway env.
- Per-employee `WhatsAppEnabled` toggle + optional `WhatsAppNumber` (falls back to `Employee.PhoneNumber`) already work.

### Open questions still worth confirming with the customer

1. Confirm the trigger time (default shipped: 18:00 `Europe/Bratislava`).
2. Working days only vs. every day (default shipped: working days only).
3. Manager daily summary push — yes / no? (toggle exists; not yet wired to a real send loop — would be M4).
4. Approve / amend the Slovak copy in `NOTIFICATIONS_PLAN.md` §7.
5. Push-only first, or set up Meta Business now?
