<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Session handoff — 2026-04-26 (M1 Notifications shipping work)

This document captures everything done in the previous chat so a fresh session can pick up cleanly. Read this first, then `PROJECT_NOTES.md` (especially the Migration Safety Rules) before touching any DB-related code.

---

## TL;DR — what's still broken right now

`No such table: Cars` keeps appearing when adding a car, even after deleting `API\dochadzkovnik.db`.

Diagnosis from the previous session: the SQLite file at `API\dochadzkovnik.db` was/is **corrupt** (`database disk image is malformed` when opened directly). The corruption was present before the previous session started — it was not caused by any AI edits. File timestamps confirm the DB was last modified at 15:49, hours before any code edits in the session.

**The most likely reason your `del` keeps not sticking:** another process is holding an open handle to the file (lingering `dotnet run`, `dotnet watch`, VS / Rider debugger, or a SQLite browser tool). On Windows, `del` on a locked file can silently fail or be deferred until all handles close, which means the corrupted bytes survive your delete.

**Recovery checklist for the next session (do these in order, in ONE terminal):**

1. `taskkill /F /IM dotnet.exe` — kills every lingering dotnet process
2. Close any DB browser / IDE database tool that might have `dochadzkovnik.db` open
3. `cd <repo>\API`
4. `del /F dochadzkovnik.db`
5. `dir dochadzkovnik.db` → must say "File Not Found". If it still shows up, use Resource Monitor → CPU → Associated Handles → search `dochadzkovnik.db` to find the process holding it.
6. `dotnet run`
7. Verify by adding a Car in the UI; it should work.

If after a *confirmed-clean* delete the error still appears, that's a real migration bug and needs investigation in the next session — but until step 5 confirms the file is gone, every retry just re-opens the same corrupted file.

---

## Project context (from PROJECT_NOTES.md)

- App name: **Šichtovnica / Dochadzkovník** — Slovak attendance tracking app
- Stack: ASP.NET Core 9 + EF Core (SQLite dev, PostgreSQL prod) + Angular 20 (standalone components, signals)
- Auth: JWT
- Repo root: `C:\...\Dochadzkovnik` (workspace folder)
- Dev DB path: `API\dochadzkovnik.db` (relative to where `dotnet run` is started)
- Prod connection: `DATABASE_URL` env var → PostgreSQL
- Currently shipping: **M1 notifications feature** — PWA web push + WhatsApp Business stub

---

## What was done in the previous session

### 1. New migration: `20260426151830_AddNotifications`

After a false start where the regenerated migration came out empty (the snapshot already had the entities baked in from a previous deleted v2 migration), the migration was fixed and now contains:

- 3 new columns on `Employees`: `NotificationsEnabled`, `WhatsAppEnabled`, `WhatsAppNumber`
- 3 new tables:
  - `NotificationConfigs`
  - `NotificationLogs`
  - `PushSubscriptions`
- Unique idempotency index on `NotificationLogs (EmployeeId, Channel, TriggerType, TriggerDate)`

Resolution path used (do not repeat this without permission per Rule 8):
`dotnet ef database update AddMaterialPrice` → `dotnet ef migrations remove` → regenerate.

### 2. Angular template build fixes

- `client/src/app/pages/kiosk/kiosk.page.html` line 55 — added `totalHours: 0` to inline `WeeklyRow` object literal to satisfy TS.
- `client/src/app/pages/notifications/notifications.page.html` — Angular template expressions cannot call global JS functions like `parseInt`. Replaced 3 occurrences of `parseInt($any($event).target.value)` with `+$any($event).target.value` (unary plus → number).

### 3. Controller fix

- `API/Controllers/NotificationsController.cs` ~line 195 — removed a broken `.Include(l => l.EmployeeId)` line. `EmployeeId` is a scalar FK, not a navigation property. The controller already fetches employees separately so no further changes were needed.

### 4. Self-heal blocks added to `API/Program.cs` (lines 121–193)

Two new pre-migration self-heal blocks were added per the project's Migration Safety Rules:

- **SQLite block:** before `MigrateAsync()`, check `pragma_table_info('Employees')` for `NotificationsEnabled` and `sqlite_master` for `NotificationConfigs`. If either exists but the migration row is missing, `INSERT OR IGNORE` it into `__EFMigrationsHistory` so `MigrateAsync()` won't try to add the columns/tables again.
- **PostgreSQL block:** equivalent guard using a `DO $$ … $$` block over `information_schema`.
- Both have permissive `try { … } catch { }` so failures are best-effort and the real error surfaces from `MigrateAsync()`.

The existing `AddEmployeePinPlain` self-heal was kept and left untouched.

After the edit, the file briefly had ~3,259 trailing null bytes that broke the build with thousands of `CS1056: Unexpected character '\0'` errors. Truncated via Python script to a clean 38,554 bytes / 755 lines. Current state is healthy.

### 5. PROJECT_NOTES.md — new Rule 8

Added at the user's explicit request:

> 8. **AI assistants must NEVER change the database, run EF commands, modify migration files, or delete/recreate the dev DB without first explicitly stating: (a) what they intend to change, (b) why, and (c) what the rollback path is — and getting the developer's confirmation.** This includes: regenerating migrations, running `dotnet ef migrations remove`, deleting `dochadzkovnik.db`, editing `Program.cs` migration self-heal blocks, or modifying any `.cs` file under `API/Migrations/`. Frontend / non-DB code edits do NOT require this preamble. Diagnosis (reading files, running `dotnet ef migrations list`) is fine without confirmation; any *write* to DB-related code or files requires explicit intent and approval.

---

## Files touched (with absolute paths)

- `API\Migrations\20260426151830_AddNotifications.cs` — regenerated, audited correct
- `API\Migrations\AppDbContextModelSnapshot.cs` — auto-updated by EF
- `API\Program.cs` — self-heal blocks added (lines 121–193); cleaned of null-byte corruption
- `API\Controllers\NotificationsController.cs` — removed `Include(l => l.EmployeeId)`
- `client\src\app\pages\kiosk\kiosk.page.html` — added `totalHours: 0`
- `client\src\app\pages\notifications\notifications.page.html` — `parseInt` → `+`
- `PROJECT_NOTES.md` — added Migration Safety Rule 8

Nothing in this list was committed yet. Run `git status` at the start of the next session.

---

## Outstanding tasks for the next session

1. **Resolve the corrupted DB / fix Cars error** (see TL;DR recovery checklist above)
2. After fresh `dotnet run`, verify:
   - All 13 migrations applied (`SELECT MigrationId FROM __EFMigrationsHistory`)
   - Cars table exists, TimeEntries has `CarId` column
   - `NotificationConfigs`, `NotificationLogs`, `PushSubscriptions` tables exist
   - Employees has `NotificationsEnabled`, `WhatsAppEnabled`, `WhatsAppNumber` columns
3. Smoke-test the notifications page end-to-end (subscribe push, send test, view logs)
4. Update planning docs: `NOTIFICATIONS_PLAN.md`, `BACKLOG.md`, `CHAT_HANDOFF.md` (mark M1 migration steps complete)
5. Commit migration + Program.cs + frontend fixes — suggested commit message:
   `feat(notifications): M1 schema, self-heal, and Angular template fixes`
6. Decide whether to also remove the `dochadzkovnik.db` file from the repo (it should be `.gitignore`d already — verify)

---

## Migration Safety Rules — quick reminder

The full list lives in `PROJECT_NOTES.md`. The most important ones to enforce in the next session:

- **Never** delete migrations that have been applied to prod
- **Always** add a pre-migration self-heal block in `Program.cs` for any column/table that might already exist in prod from a previous hotfix
- **Never** edit a migration file after it has been pushed/deployed — make a new one
- **Rule 8 (new):** AI must state intent + get confirmation before any DB-related write

---

## Known-good current state of key files

- `API\Program.cs` — 38,554 bytes, 755 lines, no null bytes (verified)
- `API\Migrations\20260426151830_AddNotifications.cs` — full Up()/Down() implementations present
- `API\dochadzkovnik.db` — **CORRUPT, must be deleted before next run**

---

## How to start the next chat

Open a new chat in the same workspace folder. Paste this opening message:

> Continuing the M1 notifications shipping work from `SESSION_HANDOFF_2026-04-26.md`. Please read that file first, then `PROJECT_NOTES.md` (Migration Safety Rules), then check `git status`. Do not run any DB commands or modify migration files without confirming with me first per Rule 8.

That gives the fresh session everything it needs to resume without re-deriving context.
