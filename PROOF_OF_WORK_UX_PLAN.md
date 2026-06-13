<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Proof-of-Work UX — Plan & Reference

> Created 2026-05-25 from the 2026-05-24 customer call.
> Status: **Not started. This file is the brief for the implementation session.**
>
> This file scopes three closely-coupled items from the 2026-05-24 batch in
> `BACKLOG.md`: construction diary (stavebný denník), the option to NOT
> upload a photo, and the broader "speed up hour logging" feedback. The
> three are bundled because they all touch the same kiosk step — the
> proof-of-work prompt that fires after the worker has entered hours.
> Splitting them would produce three plans that overwrite each other's UI
> decisions; a single plan keeps the flow coherent.

## Read first

1. `PROJECT_NOTES.md` — Core Data Models, Migration Safety Rules, the
   two-surface architecture (kiosk no-JWT vs. admin JWT). The new diary
   write endpoint is PIN-validated like the rest of `/api/kiosk/*`.
2. `PHOTO_PLAN.md` — current proof-of-work photo strategy. The diary
   does not replace photos; it adds a second proof type with equal
   weight.
3. `NOTIFICATIONS_PLAN.md` §10 — older-worker UX rules. Every kiosk
   string in this plan obeys them: plain Slovak, no jargon, big touch
   targets, no animations, high contrast.
4. `MATERIAL_PURCHASES_PLAN.md` — feature-flag wiring template and the
   "Location-as-trigger" pattern (`MaterialPurchases:TriggerLocationId`).
   This plan reuses the same flag shape.
5. `BACKLOG.md` §Customer call (2026-05-24) — the three items being
   scoped here: "Construction diary", "Option to NOT upload a photo
   when logging hours", and "Speed up hour logging".

## What the customer asked for

From the 2026-05-24 call notes (translated):

- **Construction diary (stavebný denník).** A second proof-of-work type:
  a free-text day log, optionally with a PDF or scanned page attachment.
  When the worker submits a diary, the system MUST NOT also demand a
  photo. The customer's example flow has two equal-weight tiles after
  hours are entered ("Fotografia" / "Stavebný denník"); picking either
  satisfies the proof-of-work requirement.

- **Option to NOT upload a photo.** A `Nenahrať fotografiu` tile /
  button alongside `Fotoaparát` / `Galéria` in the kiosk hours modal.
  Backend already tolerates entries with no photo (the
  `Nahral / Nenahral` badges exist) — this is purely a UI affordance so
  workers stop feeling forced to take a photo every time.

- **Speed up hour logging.** Customer feedback that the current flow has
  too many steps. Customer's own ideas (do not implement blindly):
  - Skip the photo step when the worker already attached a diary /
    photo earlier today for the same site.
  - Remember the most recent site + car selection per PIN and prefill
    them.
  - One-tap presets (`Celý deň 8h na X, žiadna poznámka`) on the
    worker's tile.
  The backlog explicitly warns: "Profile a real session with the
  customer (timed run-through) before redesigning — the bottleneck
  might be the photo upload, not the form."

## Design decisions locked in this session

- **(a) Three options, one step.** The hours modal closes with a single
  "Ako doložíte šichtu?" prompt that shows three equal-weight tiles:
  `Fotografia` / `Stavebný denník` / `Pokračovať bez dôkazu`. Picking
  any one satisfies proof-of-work. This is a flattening of the
  customer's two-step sketch (first photo-vs-diary, then
  photo-source-vs-skip) into a single decision — fewer taps, fewer
  branches in the codebase, and the worker still has the same three
  outcomes. The two-step variant remains an open question (§Open
  questions, item 1).

- **(b) Separate `WorkDiary` table, not a column on `WorkPhoto`.**
  Diary rows have a required `BodyText` and an optional file (PDF or
  image) — different shape from `WorkPhoto`'s mandatory `PhotoUrl`.
  Reusing `WorkPhoto` with a `Type` enum would force a nullable
  `BodyText` plus a re-typing of `PhotoUrl` as nullable, muddying the
  semantics. A separate table is cleaner and matches the precedent of
  `WorkPhoto` itself (standalone, sibling of `TimeEntry`, not a
  subclass).

- **(c) Diary attaches to a (site, date) like `WorkPhoto`, with an
  optional `TimeEntryId` link.** When the diary is submitted inside the
  same kiosk session as an hours entry, the resulting `WorkDiary` row
  is linked via `WorkDiary.TimeEntryId` so the admin Záznamy dochádzky
  view can show a badge alongside the existing `Nahral / Nenahral`
  photo badge. When submitted standalone (the customer's "second tile"
  flow), `TimeEntryId` stays NULL — identical to the way standalone
  `WorkPhoto` rows already work.

- **(d) "Pokračovať bez dôkazu" stamps a soft skip-reason, no schema
  change to `TimeEntry`.** The customer wants this option visible, not
  punished. Backend already accepts a `TimeEntry` with no photo. To
  give the admin a clue when a worker explicitly skipped (as opposed to
  forgetting), the kiosk POSTs the choice to a tiny new
  `TimeEntry.ProofOfWorkSkipped` boolean (default `false`). The admin
  table can then distinguish three states for the Foto column:
  `✓ Foto` / `✓ Denník` / `Bez dôkazu` (the new flag) / `Nenahral`
  (no skip flag, no photo, no diary — the historical "forgot" state).

- **(e) Speed-up V1 ships only the cheap wins.** Per the customer's own
  warning, the bigger redesigns are deferred until a real session is
  profiled. V1 ships:
  - Remember last-selected `Location` and `Car` per PIN, stored in
    `localStorage` on the kiosk device (per-device, not server-side).
    Preselects them on the next clock-in; worker can still change.
  - The flattened proof-of-work step from (a) — one decision instead
    of two.
  - Auto-skip the proof-of-work prompt when the worker has already
    submitted a `WorkPhoto`, a `WorkDiary`, or a `TimeEntry` with
    proof-of-work for the same `(LocationId, Date)` in the current
    kiosk session. The kiosk POSTs the new `TimeEntry` and goes
    straight to Hotovo with a small Slovak hint: `Dôkaz pre dnešok je
    už pripojený k predošlému záznamu.`
  The big ideas (one-tap day presets, photo upload move to background)
  are deferred to V1.1 after the customer has timed a real session
  with the V1 build (§Open questions, item 6).

- **(f) Feature flag.** `ProofOfWorkChoices`, same pattern as
  `Notifications` / `CommanderIntegration` / `MaterialPurchases` /
  `PayrollAndPnL`. Default OFF in prod. Superadmin flips it on per
  environment after demo. When OFF, the kiosk hours modal renders
  exactly as it does today (single `Fotoaparát` / `Galéria` step, no
  diary, no skip tile, no last-selection memory). One flag covers all
  three customer items so they ship and demo together.

- **(g) Older-worker UX rules apply hard.** No icons-only buttons; every
  tile has a Slovak word label below the icon. Tiles are at minimum 88
  CSS px tall (well above the `.touch-target` 44 px floor). The skip
  tile uses neutral copy (`Pokračovať bez dôkazu`), never red, never a
  warning icon — the customer's framing is "stop making them feel
  guilty about not having a photo", not "warn them about skipping".

## Schema

Generate the migration via the CLI per Migration Safety Rule 1:

```
cd API
dotnet ef migrations add AddProofOfWorkChoices
```

PostgreSQL self-heal blocks for the new table and the new `TimeEntry`
column in `Program.cs` per Rule 3.

### `WorkDiary` — standalone day log

```
Id              int            PK
EmployeeId      int?           FK -> Employees(Id) ON DELETE SetNull
                                -- mirrors WorkPhoto.EmployeeId; null
                                -- = admin-uploaded on behalf of a worker
LocationId      int            FK -> Locations(Id) ON DELETE Restrict   NOT NULL
TimeEntryId     int?           FK -> TimeEntries(Id) ON DELETE SetNull
                                -- populated when the diary is submitted
                                -- in the same kiosk session as hours
Date            date           NOT NULL    -- day the work happened
BodyText        text           NOT NULL    -- the free-form diary entry
AttachmentUrl   varchar(1000)  NULL        -- optional PDF / image scan
                                            -- of the physical diary page
CreatedAt       timestamp NOT NULL
UpdatedAt       timestamp NOT NULL

INDEX (LocationId, Date)
INDEX (EmployeeId, Date)
INDEX (TimeEntryId)
```

`BodyText` is `text` (unbounded) rather than `varchar(N)` — diary
entries can run long, and Postgres handles `text` and `varchar` at the
same performance for our scale. Trimming is the client's job.

### `TimeEntry.ProofOfWorkSkipped` — new column

```
ProofOfWorkSkipped   bool   NOT NULL  DEFAULT FALSE
                            -- stamped TRUE when the worker explicitly
                            -- picks "Pokračovať bez dôkazu" on the
                            -- new proof-of-work step. FALSE on every
                            -- pre-migration row and on every worker
                            -- who attached a photo or diary.
```

Used by the admin Záznamy dochádzky table to distinguish "skipped on
purpose" from "forgot" in the Foto column.

## Feature flag wiring

Identical to the four flags already shipped.

- Backend:
  - Add `"ProofOfWorkChoices"` to the `knownFlags` array in
    `Program.cs` so the row is seeded `Enabled = false` on first boot.
  - Apply `[RequireFeatureOrSuperAdmin("ProofOfWorkChoices")]` at the
    class level on the new `WorkDiariesController` AND on the new kiosk
    endpoint(s). Non-superadmin / unauthenticated callers see 404 when
    the flag is off, exactly as `MaterialPurchases` does.
- Frontend:
  - `feature-flag.service.ts` — extend the typed map with
    `proofOfWorkChoices: Signal<boolean>` next to the existing flags.
  - Kiosk `pages/kiosk/kiosk.page.ts` — the new three-tile step is
    rendered only when `flags.proofOfWorkChoices() ||
    auth.isSuperAdmin()` is true. When false, the modal falls back to
    the current single-step photo flow with no behaviour change.
  - `account.page.html` — fifth toggle row in the Funkcie superadmin
    card.
- Default state: prod boots with the flag off; customer never sees the
  diary tile, the skip tile, or the last-selection memory. Dev
  superadmin flips it on once dev is green. Promotion to prod happens
  after customer sign-off.

## Endpoints

### Kiosk (PIN, no JWT, behind `ProofOfWorkChoices` flag)

```
POST  /api/kiosk/work-diaries
      body { pin, locationId, date, bodyText, timeEntryId? }
      returns { id }

POST  /api/kiosk/work-diaries/{id}/attachment
      multipart photo / PDF upload (mirrors the existing
      MaterialUsage / WorkPhoto upload pattern)

POST  /api/kiosk/log-hours                    -- existing endpoint
      body extended with optional
      { proofOfWorkSkipped?: bool }           -- defaults to false
                                                 when the field is absent
                                                 (preserves the current
                                                 behaviour for the flag-off
                                                 kiosk and for any non-kiosk
                                                 callers)
```

The PIN check resolves the active employee exactly like
`KioskController.FindEmployeeByPin`. Workers cannot edit or delete past
diaries from the kiosk in V1.

### Admin (JWT, behind same flag)

```
GET     /api/work-diaries?from=&to=&locationId=&employeeId=
GET     /api/work-diaries/{id}
PUT     /api/work-diaries/{id}    body { date?, bodyText?, attachmentUrl? }
DELETE  /api/work-diaries/{id}
POST    /api/work-diaries/{id}/attachment    multipart
DELETE  /api/work-diaries/{id}/attachment
```

Admin can create a diary on a worker's behalf via PUT to a newly-POSTed
row; no separate admin POST in V1. Mirrors how `WorkPhoto` admin
moderation works today.

## Kiosk UX flow

### Existing flow (unchanged when flag off)

PIN → Location picker → Hours numpad → optional Car picker → optional
Note → Photo step (`Fotoaparát` / `Galéria`) → Hotovo.

### New flow (flag on)

PIN → Location picker (preselect = last `LocationId` for this PIN from
`localStorage`) → Hours numpad → optional Car picker (preselect = last
`CarId` for this PIN) → optional Note → **Proof-of-work step** →
Hotovo.

**Proof-of-work step.** Three equal-weight tiles, full-bleed on the
tablet:

```
+---------------+   +-------------------+   +--------------------------+
|   Fotografia  |   |  Stavebný denník  |   |  Pokračovať bez dôkazu   |
+---------------+   +-------------------+   +--------------------------+
```

- `Fotografia` → existing photo capture / gallery sub-flow runs
  unchanged. On submit, the photo is attached to the `TimeEntry` and
  `ProofOfWorkSkipped` stays `false`.
- `Stavebný denník` → opens an inline form: large textarea (`Čo ste
  dnes urobili?`), optional `Pridať skenovaný denník (PDF alebo
  fotografia)` button. On submit, a `WorkDiary` row is created and
  linked to the new `TimeEntry` via `TimeEntryId`. `ProofOfWorkSkipped`
  stays `false`.
- `Pokračovať bez dôkazu` → confirmation Slovak: `Tento záznam uložíme
  bez fotografie a bez denníka. Pokračovať?` with `Áno, uložiť` /
  `Späť`. On `Áno`, `ProofOfWorkSkipped = true` on the new `TimeEntry`.

**Auto-skip.** If a `WorkPhoto`, `WorkDiary`, or `TimeEntry` with proof
exists for the resolved `(EmployeeId, LocationId, Date)` in the past
hour, skip the proof-of-work step entirely and show a small Slovak
hint above the Hotovo screen: `Dôkaz pre dnešok je už pripojený k
predošlému záznamu o 11:42.` `ProofOfWorkSkipped` stays `false` in
this case — the worker did attach a proof earlier, just not on this
specific entry.

### Last-selection memory

`localStorage` keys, scoped per device (per kiosk tablet), per PIN
hash:

```
kiosk:lastLocation:{pinHash}  →  number  (LocationId)
kiosk:lastCar:{pinHash}       →  number  (CarId)  or "none"
```

Set after every successful clock-in. Read when the kiosk paints the
Location / Car pickers. PIN hash (not the raw PIN) keeps the key out
of plain text in DevTools. A device's memory is cleared when the
worker explicitly picks a different value (still recorded for next
time) — not when they cancel mid-flow.

### Two entry points, both V1

Same shape as the existing kiosk:

**A. In-šichta diary** — the `Stavebný denník` tile on the proof-of-work
step (the customer's primary flow). `WorkDiary.TimeEntryId` populated.

**B. Standalone diary** — a tile on the main kiosk screen next to the
existing `Nahrať fotografiu` tile, mirroring its standalone-photo
behaviour. Useful for workers who want to log a diary entry on a day
they did not clock hours. `WorkDiary.TimeEntryId = NULL`.

Both reuse the same diary form component. The standalone tile is
gated by the same feature flag.

### Older-worker UX rules

- All tile labels are word-only Slovak, no abbreviations, no English.
- The skip tile is the same visual weight as the others — neither
  highlighted nor de-emphasised. The customer wants "no guilt" copy.
- Textarea for the diary uses `inputmode="text"` and a font size of
  at least 18 px so older eyes can read what they typed.
- No animations on the tiles beyond the existing `:active` press
  feedback. The kiosk's broader animation rules in
  `NOTIFICATIONS_PLAN.md` §10 apply.

## Admin UX

### Záznamy dochádzky — extended Foto column

The column already shows `✓ Nahral` (green) / `✗ Nenahral` (grey).
Extend to four states:

```
✓ Foto              -- entry has PhotoUrl
✓ Denník            -- entry has a linked WorkDiary (TimeEntryId match)
Bez dôkazu          -- ProofOfWorkSkipped = true
✗ Nenahral          -- no photo, no diary, ProofOfWorkSkipped = false
                       (i.e. pre-migration rows + workers who forgot)
```

`Bez dôkazu` is the only neutral grey; the other three are colour-coded
(green / blue / grey / amber respectively). Both desktop table and
mobile cards.

### Location detail — diary tab next to photo gallery

Existing location detail has a Photo gallery section. Add a sibling
`Stavebný denník` section: month-filtered list of `WorkDiary` rows
with date, employee, body text excerpt, attachment thumbnail. Click a
row to expand the full body text and view the attachment.

Delete is admin-only (workers can't delete from kiosk in V1).

### Mobile / tablet

Match the 2026-05-01 tablet pass: `.min-h-dvh`, `.touch-target`,
stacked-card fallback below `md`. Reference `materials.page.html`,
`commander.page.html`, `kiosk.page.html`.

## Out of scope (V1)

- OCR on uploaded diary scans. Pure-text body is the source of truth;
  the scan is a reference image. Promote to its own plan if the
  customer asks (overlaps with `BACKLOG.md` "Receipt OCR" and "PDF /
  multi-photo document scanning" items).
- Worker self-edit of past diaries. Workers submit; admin edits.
- Photo-AND-diary on a single entry. V1 enforces one or the other —
  the proof-of-work step is a single choice. Backend allows both
  (`TimeEntry.PhotoUrl` and a linked `WorkDiary` can coexist) but the
  kiosk does not produce them; an admin who wants both can attach a
  photo via the admin Záznamy dochádzky form.
- Push notification when a diary is submitted.
- Multi-page diary attachments (one file per `WorkDiary` in V1). The
  parked PDF / multi-photo scanning item in `BACKLOG.md` covers the
  generalisation.
- One-tap day presets (`Celý deň 8h na X`) — deferred until the
  customer times a real session.
- Background photo upload (kiosk submits hours immediately and uploads
  the photo asynchronously) — deferred. Same profiling-first rationale.
- Cross-device last-selection sync — last-selection is per-device on
  purpose. A worker who moves between kiosks gets the default picker
  on the new device, which is acceptable for V1.

## Open questions still worth asking the customer

1. **One step or two?** V1 ships the flattened single-step
   proof-of-work prompt. Should the photo-vs-diary choice be a
   separate prior step (the customer's literal wording), with the
   `Nenahrať fotografiu` tile appearing only on the photo branch? The
   single-step version is fewer taps; the two-step version is closer
   to the customer's description. Confirm before merge.
2. **Auto-skip window.** V1 auto-skips the proof-of-work step when a
   proof already exists for `(employee, location, date)` in the past
   hour. Is one hour the right window? Same-day might be too wide
   (different shifts), 15 minutes too narrow. Confirm.
3. **Diary attachment format.** PDF only, image only, or both? V1
   accepts either. Confirm the customer expects PDFs (their physical
   diary is paper; they would scan it).
4. **Standalone diary tile on the main kiosk screen.** V1 ships both
   the in-šichta and the standalone path. Drop the standalone tile if
   the customer says workers will only ever submit a diary as part of
   logging hours.
5. **Diary visibility to other workers.** The "Expandable per-site
   roll-up of today's logged hours + notes" item from `BACKLOG.md`
   shows other workers what colleagues did today. Should the
   `WorkDiary` body be part of that roll-up, or admin-only? V1 default
   = admin-only; the roll-up item gets its own plan.
6. **Speed-up validation.** V1 ships three cheap speed-ups (last
   selection memory, flattened proof-of-work step, auto-skip when
   already proven). Customer should time a real session before V1.1
   adds the bigger redesigns (one-tap presets, background upload).

## What "done" looks like for V1

- Migration `AddProofOfWorkChoices` generated via CLI; PostgreSQL
  self-heal blocks for the `WorkDiaries` table and the
  `TimeEntry.ProofOfWorkSkipped` column in `Program.cs`.
- One new EF entity (`WorkDiary`), one new admin controller
  (`WorkDiariesController`), one new kiosk endpoint set
  (`KioskWorkDiariesController` or extension of `KioskController`),
  one new frontend service (`WorkDiaryService`).
- Kiosk proof-of-work step renders three tiles when the flag is on;
  falls back to the current single-photo flow when off. Verified by
  reviewer toggling the flag in the Funkcie card and reloading the
  kiosk.
- `localStorage`-backed last-selection memory works per PIN per
  device. Verified by clocking in twice on the same kiosk and seeing
  the second clock-in preselect the first one's Location and Car.
- Auto-skip behaves correctly: a worker who attached a photo earlier
  today at the same location does not get the proof-of-work step
  again; the Hotovo screen shows the Slovak hint with the prior
  timestamp.
- Admin Záznamy dochádzky Foto column shows the four states from the
  table above, on both desktop and mobile cards.
- Admin Location detail page has a `Stavebný denník` section listing
  the month's diary rows.
- `ProofOfWorkChoices` feature flag wired across backend filter +
  frontend service + Account toggle row; default off in prod, on in
  dev once dev is green. Kiosk and admin both fall back to current
  behaviour when off.
- No `/api/kiosk/*` endpoint leaks admin-only data through the new
  diary surface. The diary body is worker-visible by design (the
  worker wrote it); reviewer confirms no other admin field is
  surfaced through the kiosk diary read endpoints.
- `dotnet build` clean. `npx tsc --noEmit -p tsconfig.app.json` clean.
  Local Postgres dev DB starts cleanly with no `no such column`
  warnings.

## Notes for the implementation session

- All blocking design decisions have been answered. Feel free to start
  when ready; the open questions above are V1.1 polish, not blockers.
- Older-worker UX rules (`NOTIFICATIONS_PLAN.md` §10) apply to every
  string on the new proof-of-work step and the diary form. No
  marketing copy. Plain Slovak. Big targets. The skip tile in
  particular must not look like a warning — the customer is removing
  guilt, not adding it.
- Mobile-first layouts; match the patterns shipped in the 2026-05-01
  tablet pass.
- `WorkPhoto` stays the precedent for the new `WorkDiary` table shape
  and lifecycle. Where the two diverge (required `BodyText` vs.
  required `PhotoUrl`, optional attachment vs. mandatory photo), the
  divergence is explicit, not accidental — do not "harmonise" the two
  tables back together in V1.
- The speed-up bucket is research-led. V1 ships only the three items
  in design decision (e). Resist scope creep on this front until the
  customer has timed a real V1 session.

---

*End of plan.*
