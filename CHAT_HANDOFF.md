<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Chat handoff — read me first

> Last session: 2026-04-27 — **dev branch set up** (git + Angular env files + Vercel/Railway config). See "Dev branch setup" section below for the remaining dashboard steps.
> Previous: V1.2.0 **Notifications M1 shipped** (PWA push + admin Notifikácie page + demo controls). Frontend `tsc --noEmit` clean. Backend `dotnet build` not run in sandbox — verify locally before publish.
> Next session focus: **Customer test of M1, then M2 kiosk-side "Povoliť upozornenia" tile + M3 WhatsApp credentials.** See `NOTIFICATIONS_PLAN.md`.
> ⚠️ **SMS path is deferred** — `SMS_PLAN.md` is superseded by `NOTIFICATIONS_PLAN.md`. V1 ships free-channel only (push + WhatsApp).
> ⚠️ **Workers are older / non-tech-savvy.** Anything we ship must be obvious, big, plain Slovak, no jargon. See `NOTIFICATIONS_PLAN.md` §10 for UX rules.

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
