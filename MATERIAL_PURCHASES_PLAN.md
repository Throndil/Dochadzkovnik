<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Material Purchases (Kiosk Flow) — Plan & Reference

> Created 2026-05-06.
> Status: **Not started. This file is the brief for the implementation session.**
>
> This is the kiosk-led companion to `WAREHOUSE_PLAN.md` Phase 1
> (`MaterialPurchase`). The original plan scoped purchases as an admin-only
> page; this version adds a worker-facing kiosk flow on top of it and
> reorganises the admin Materiál page around the richer data we are about
> to start collecting. When this lands, `WAREHOUSE_PLAN.md` Phase 1's
> `MaterialPurchase` section should be updated to point here.

## Read first

1. `WAREHOUSE_PLAN.md` — Phase 1 schema sketch + the long-game per-location P&L vision. The schema below is a small evolution of that one (header + lines split, kiosk-friendly fields).
2. `MATERIALS_PLAN.md` — existing `Material` catalogue (V1.1) and `MaterialUsage` shape. This work extends both but does not change either. `MaterialUsage.UnitPriceAtTime` stays the *consumption-side* snapshot; the new `MaterialPurchase.UnitPrice` is the *purchase-side* snapshot. They are independent.
3. `PROJECT_NOTES.md` — Migration Safety Rules, two-surface architecture (kiosk no-JWT vs. admin JWT). The new kiosk endpoint is PIN-validated like the rest of `/api/kiosk/*`.
4. `NOTIFICATIONS_PLAN.md` §10 — older-worker UX rules. The kiosk pre-screen and Nákup flow must follow them.

## What the customer asked for (this session)

- A **mode picker before** the existing PIN flow on the kiosk. Two big tiles:
  - **Zaznamenať šichtu** — current clock-in / log-hours flow.
  - **Nákup materiálu** — standalone purchase flow: pick or create a material, enter quantity + actual paid price, optionally upload a receipt photo.
- The Nákup mode also has to be reachable **from inside the šichta flow** — when the worker picks the existing "Nákup materiálu" `Location` while clocking hours, the same purchase-capture step opens automatically and the resulting `MaterialPurchase` is linked to the `TimeEntry` in one session. This is the customer's preferred end-state per 2026-05-06; until they explicitly choose, both entry points (pre-screen tile + Location-triggered) ship together in V1.
- The system must record **who** bought, **when**, **for which site (or general stock)**, **at what price**.
- New "made up" materials must NOT corrupt the catalogue. They live in a holding state until the admin reviews and promotes them, with the option to **rename** before promotion (or merge into an existing catalogue entry).
- The admin Materiál page must be rebuilt with a richer view — purchases by date, by person, by site, by supplier, with totals — to feed future statistics.
- **Long-term direction:** the material / cost / spend data graduates to a separate "financial & statistics" sub-site, distinct from the day-to-day Šichtovnica admin. Do not build the sub-site yet, but do not paint ourselves into a corner.
- The whole feature ships behind a `MaterialPurchases` runtime flag, identical pattern to `Notifications` and `CommanderIntegration` — see the section below.

## Design decisions locked in this session

- **(a) New material on the fly** — kiosk lines are stored free-typed. Admin promotes to the catalogue afterwards, with the option to rename before promoting (covers typos like `Cemnt` / `cement 25kg`). The original raw name + the worker who entered it are preserved on the line for audit.
- **(b) Allocation to a target site** — each purchase header optionally points at a `Location` (the site the materials are *for*). "Nezadané / všeobecné stock" is a first-class option. `EmployeeId` (the buyer) is captured in every case.
- **Two entry points, both shipped in V1.** A standalone Nákup tile on the new pre-screen, AND an automatic purchase-capture step inside the existing šichta flow whenever the worker selects the "Nákup materiálu" `Location`. The Location-triggered path produces a `TimeEntry` and a `MaterialPurchase` linked via `MaterialPurchase.TimeEntryId` in one session. The standalone path leaves `TimeEntryId` NULL. Customer's stated preference is the combined Location-triggered path; we keep the standalone tile until they explicitly say to drop it.
- **Receipt photo** — captured at the **header** level (one image per receipt), not per line. Reuse the existing Cloudinary `BlobService` and the `MaterialUsage` photo upload pattern.
- **"Nákup materiálu" and "Firma" stay as `Location` rows** — preserves existing reports and the weekly grid. "Nákup materiálu" is *also* the trigger for the in-šichta purchase-capture step: when its `Id` is selected the kiosk reveals the same Položky / Účtenka steps as the standalone flow, just inside the existing hours wizard. Detection is by `Location.Id`, configured once in `appsettings` (`MaterialPurchases:TriggerLocationId`) so the customer can rename it later without breaking the flow.
- **Catalogue price** — `Material.PricePerUnit` is NOT auto-updated by purchases. The admin gets an explicit one-click "Aktualizovať katalógovú cenu na 4,80 €" action when reviewing a purchase, and a small hint when current paid price differs from catalogue: `Posledná nákupná cena: 4,80 € (katalóg: 4,20 €)`.
- **Feature flag** — `MaterialPurchases`, same pattern as `Notifications` / `CommanderIntegration`. Default OFF in prod. Superadmin flips it on per environment once the customer signs off. See "Feature flag wiring" below.

## Schema

Generate the migration via the CLI per Migration Safety Rule 1:

```
cd API
dotnet ef migrations add AddMaterialPurchases
```

PostgreSQL self-heal blocks for both new tables in `Program.cs` per Rule 3. (Postgres-only since the 2026-05-01 SQLite retirement; no parallel block needed.)

### `MaterialPurchase` — one shopping trip / one receipt

```
Id              int      PK
PurchaseDate    timestamp     NOT NULL    -- Europe/Bratislava local at insert time
EmployeeId      int      FK -> Employees(Id) ON DELETE Restrict      NOT NULL
                          -- the buyer; required for accountability
LocationId      int?     FK -> Locations(Id) ON DELETE SetNull
                          -- nullable: "general stock" leaves it null
TimeEntryId     int?     FK -> TimeEntries(Id) ON DELETE SetNull
                          -- nullable; populated only when the purchase
                          -- shares a kiosk session with a hours entry
                          -- (V1.1 polish — see Kiosk UX flow below)
SupplierName    varchar(200)  NULL    -- free text in V1; promote to a
                                       -- Supplier table only if customer asks
ReceiptPhotoUrl varchar(1000) NULL    -- Cloudinary URL of the receipt scan
Note            varchar(500)  NULL
TotalCost       decimal(14,4) NOT NULL    -- denormalised sum of line totals
CreatedAt       timestamp     NOT NULL
UpdatedAt       timestamp     NOT NULL

INDEX (PurchaseDate)
INDEX (EmployeeId, PurchaseDate)
INDEX (LocationId)
```

### `MaterialPurchaseLine` — one item on the receipt

```
Id              int      PK
PurchaseId      int      FK -> MaterialPurchases(Id) ON DELETE Cascade  NOT NULL
MaterialId      int?     FK -> Materials(Id) ON DELETE SetNull
                          -- NULL while the line is "neidentifikovaný";
                          -- gets populated when admin promotes / merges
MaterialNameRaw varchar(200) NOT NULL
                          -- always stored; survives material renames
                          -- and proves what the worker actually typed
Unit            varchar(20)  NOT NULL    -- snapshotted from catalogue
                                          -- or typed by the worker
Quantity        decimal(12,3) NOT NULL
UnitPrice       decimal(12,4) NOT NULL    -- price actually paid this trip
LineTotal       decimal(14,4) NOT NULL    -- denormalised = Quantity * UnitPrice
CreatedAt       timestamp NOT NULL

INDEX (PurchaseId)
INDEX (MaterialId)        -- partial-able later if PG is happy
```

### Why header + lines, not flat

- One receipt, many items — single photo upload, single supplier / date.
- The kiosk UI can grow `+ Pridať položku` without a new round-trip per line.
- Promotion of a free-typed line into the catalogue does not touch the header.

### Promotion flow for `MaterialId == null` lines

Admin opens the line and chooses one of:
1. **Promote as new** — supply a clean name + unit (defaults to the raw values), optional starting `PricePerUnit`. Backend creates a `Material`, sets `MaterialId` on this and all *other* matching `MaterialNameRaw` lines (case-insensitive, same Unit), returns count.
2. **Merge into existing** — pick a catalogue row from a search box. Sets `MaterialId` on this line only, or all matching raw-name lines on demand.
3. **Leave** — keep the line in "Neidentifikované" until later.

The original `MaterialNameRaw` is never overwritten.

## Feature flag wiring

Identical to the `Notifications` and `CommanderIntegration` patterns. Confirmed working on both controllers + the kiosk surface, no new infrastructure.

- Backend:
  - Add `"MaterialPurchases"` to the `knownFlags` array in `Program.cs` so the row is seeded `Enabled = false` on first boot of any environment.
  - Apply `[RequireFeatureOrSuperAdmin("MaterialPurchases")]` at the class level on the new admin `MaterialPurchasesController` AND on the new kiosk endpoints (the kiosk's no-JWT entry points still get the filter; non-superadmin / unauthenticated callers see 404 when the flag is off, exactly as `Commander` does).
- Frontend:
  - `feature-flag.service.ts` — extend the typed map with `materialPurchases: Signal<boolean>` next to `notifications` and `commanderIntegration`.
  - Kiosk `pages/kiosk/kiosk.page.ts` — both entry points (the new pre-screen tile AND the in-šichta Location trigger) check `flags.materialPurchases() || auth.isSuperAdmin()`. When false, the pre-screen falls back to the current single-flow layout and the Location-triggered capture is skipped.
  - `pages/account/account.page.html` — third toggle row in the Funkcie superadmin card.
- Default state: prod boots with the flag off; customer never sees the pre-screen, never gets a purchase prompt on the existing Location flow. Dev superadmin flips it on after deploy. Promotion to prod happens after customer sign-off.

## Endpoints

### Kiosk (PIN, no JWT, behind `MaterialPurchases` feature flag)

```
POST   /api/kiosk/material-purchases
       body { pin, locationId?, supplierName?, note?,
              lines: [{ materialId? OR materialNameRaw, unit, quantity, unitPrice }] }
       returns { id, totalCost }

POST   /api/kiosk/material-purchases/{id}/receipt
       multipart photo upload (same shape as MaterialUsage photo upload)

GET    /api/kiosk/materials                       -- already exists; reused for the picker
```

The PIN check resolves the active employee exactly like `KioskController.FindEmployeeByPin` (which only matches `IsActive = true`); the resolved `EmployeeId` is what gets stamped on the purchase. Workers cannot edit or delete past purchases from the kiosk in V1.

### Admin (JWT, behind same flag)

```
GET    /api/material-purchases?from=&to=&locationId=&employeeId=&materialId=&supplier=
GET    /api/material-purchases/{id}
PUT    /api/material-purchases/{id}
DELETE /api/material-purchases/{id}
GET    /api/material-purchases/{id}/lines
PUT    /api/material-purchases/{id}/lines/{lineId}
DELETE /api/material-purchases/{id}/lines/{lineId}
POST   /api/material-purchases/{id}/lines/{lineId}/promote
       body { mode: "new" | "merge",
              newName?, newUnit?, addCatalogueRow?: bool, catalogueMaterialId?,
              applyToAllMatchingRawName?: bool }
GET    /api/material-purchases/export?from=&to=  → application/vnd.openxmlformats-officedocument.spreadsheetml.sheet
```

Mirror `MaterialExcelExportService` for the Excel export — `MaterialPurchasesExcelExportService`, two sheets (Súhrn po materiáli + Detailný záznam), amber header, frozen panes, `#,##0.00 €` totals.

## Kiosk UX flow

### New pre-screen (root of the kiosk)

Two large tiles, full-bleed on the tablet, big touch targets, plain Slovak:

```
+---------------------+   +---------------------+
|  Zaznamenať šichtu  |   |  Nákup materiálu    |
+---------------------+   +---------------------+
```

Older-worker rules (`NOTIFICATIONS_PLAN.md` §10): big targets, no jargon, no animations, high contrast.

### Šichtu path

Identical to the existing kiosk flow. No regressions allowed.

### Nákup materiálu path

1. **PIN numpad** (existing component reused).
2. **"Pre pracovisko"** — same Location picker as the šichta flow, with a first chip `Nezadané / všeobecné stock` that maps to `LocationId = null`.
3. **Položky** — list of line rows. Each row:
   - search box over the catalogue (`Material.Name`, case-insensitive contains)
   - or `+ Nový materiál` inline form (name, unit, no catalogue write yet)
   - quantity numpad with the same `[1, 5, 10, 20, 50]` chips used elsewhere
   - unit price numpad (€, decimal)
   - live line total
   - bin icon to remove the row
   - `+ Ďalšiu položku` adds a row
4. **Účtenka** — optional photo upload (camera + gallery, HEIC normalised, same `image-utils.ts` pipeline). Hint: `Účtenka pomôže šéfovi pri kontrole.` Not enforced in V1.
5. **Hotovo** — confirm screen with grand total in big bold green; Slovak: `Uložené. Účtenka prijatá.`

### Two entry points, both V1

Both paths reuse the same Položky / Účtenka components — just embedded in different hosts.

**A. Pre-screen tile** (standalone). PIN → optional `Pre pracovisko` → Položky → Účtenka → Hotovo. Produces a `MaterialPurchase` with `TimeEntryId = NULL` (worker did not log hours through this path).

**B. In-šichta, triggered by Location** (combined; customer's preferred end-state). Standard hours flow runs as today: PIN → Location picker → hours numpad. When the picked Location matches `MaterialPurchases:TriggerLocationId` (the existing "Nákup materiálu" Location), an extra Položky + Účtenka block appears between hours and Hotovo. On submit, one round-trip writes a `TimeEntry` AND a `MaterialPurchase`, linked via `MaterialPurchase.TimeEntryId`. The Location stays "Nákup materiálu" on the `TimeEntry` (so weekly reports / Excel exports keep working), and the Nákup data lives on the linked purchase.

The trigger Location ID is read from config once at boot, so the customer can rename "Nákup materiálu" or move it without code changes. If the config value is missing or points at a deleted Location, the in-šichta capture is silently skipped — the šichta flow keeps working unchanged.

When the customer eventually picks one entry point only, removing the other is a one-flag flip in `appsettings`, not a code change. Until then, both ship.

## Admin UX — richer Materiál page

Reorganise `/admin/materials` into tabs (preserves the current page as one of them):

### 1. Katalóg
The current flat catalogue table. No changes in V1.

### 2. Nákupy (new)
Filterable list of `MaterialPurchase` records.
- Filters: date range (default current month), Lokácia, Zamestnanec, Materiál (matches both linked + raw name), Dodávateľ.
- Per row: date, employee, supplier, target site (or "Všeobecné stock"), line count, totalCost, receipt thumbnail.
- Row click → expanded panel: line items table (raw name + linked catalogue name side-by-side when they differ), receipt photo full-size, per-line cost vs. catalogue price hint.
- Footer: grand total of filtered range + `Stiahnuť Excel`.
- Per row delete → soft, with confirmation; restores via a `DeletedAt` column (V1.1 polish — V1 hard-deletes since this is admin-only).

### 3. Neidentifikované (new)
Every `MaterialPurchaseLine` with `MaterialId == null`, grouped by `MaterialNameRaw + Unit`.
- Per group: count of occurrences, all employees who entered it, total quantity, total spend, last-seen date.
- Bulk action `Promovať do katalógu` opens a small form: clean name (defaults to raw), unit (defaults to entered unit), optional starting `PricePerUnit` (defaults to weighted average of the group's `UnitPrice`).
- Bulk action `Zlúčiť s existujúcim` opens a search picker over the catalogue.

### 4. Spotreba (moved + extended) — **V1.1 polish, deferred**
Today's per-location consumption summary lives inside the slide-over `LocationManagePanelComponent`. Surface a global cross-location read here too: "How much cement did all sites use in March?" — answers the V2 ask in `MATERIALS_PLAN.md`. Requires a new backend endpoint (`GET /api/material-usages/summary?from=&to=` aggregating across all locations) — not in V1's scope.

### Per-material drill-in (used from any tab) — **V1.1 polish, deferred**

Click a material name → drawer / page showing:
- Catalogue card (current PricePerUnit, unit, IsActive).
- All purchases of this material in the filtered range, with employee + site + paid price.
- All usages of this material in the filtered range, with site + UnitPriceAtTime.
- Mini line chart of UnitPrice over time (server-rendered SVG or a small Chart.js — pick whatever the rest of the admin UI already uses).
- Totals: bought N at avg X €, used N at avg Y €.

This per-material drill-in is the most direct "future statistic" affordance the customer asked for, and it ports cleanly to the future financial sub-site without rewrites. Deferred from V1 to keep the initial slice shippable; the Nákupy and Neidentifikované tabs already expose the underlying data in a usable form.

### Mobile / tablet

Match the patterns from the 2026-05-01 tablet pass: `min-h-dvh`, `.touch-target`, stacked-card layouts below `md`. Reference `commander.page.html`, `materials.page.html`, `kiosk.page.html`.

## Future direction — separate financial & statistics sub-site

The customer wants this material / cost data to eventually move into a dedicated financial / statistics sub-site, distinct from Šichtovnica admin. To keep that path open today:

- All financial reads stay as **API endpoints**, not page-coupled DTOs. The Angular admin reuses them; a future site reuses them too.
- Don't bury cost calculations inside the Excel exporter. Push them into a service like `IMaterialPurchaseQueryService` returning aggregate DTOs that any front-end can consume.
- Same auth model. A future sub-site can be a second Angular project pointing at the same Railway API behind the same JWT. No schema split. Same `vladosroka` / `admin` users.
- Phase 4 of `WAREHOUSE_PLAN.md` (revenue / per-location P&L) lands on the same future sub-site, not on the kiosk admin. Build the read endpoints with that destination in mind.

Do not build the sub-site yet. This is just a reminder to keep the data layer clean.

## Out of scope (V1)

- Live stock counts (per `WAREHOUSE_PLAN.md` deferral; revisit only if the customer asks).
- OCR / line-item extraction from the receipt photo. Receipt is just an image attached to the header.
- Multi-currency. EUR only.
- Returns / refunds. Negative quantities not allowed in V1; admin deletes-and-re-enters via the admin Nákupy tab.
- Per-supplier dashboard / supplier comparison. Data is collected (`SupplierName`); view comes later.
- Worker-side editing of past purchases. Workers submit; admin edits.
- Combined hours+purchase in one kiosk submission (V1.1 polish — Option 2 above).
- Push notification to manager on every purchase (V1.1 polish).
- Soft-delete on purchases (V1.1 polish).

## Open questions still worth asking the customer

1. **Single vs. dual entry point** — V1 ships both (pre-screen tile + in-šichta Location trigger). Customer's stated preference is the in-šichta combined flow. Once they live with both for a sprint, ask whether to drop the standalone tile. Until then, both stay.
2. **Worker self-view** — should workers see their own past purchases on the kiosk (a "Moje nákupy" tab next to "Moje hodiny"), or admin-only? V1 default = admin-only.
3. **Receipt photo enforcement** — block save without a receipt or just recommend? V1 default = recommend, with the Slovak hint above.
4. **Manager push on submit** — push to `vladosroka` when a purchase is recorded? Plumbing exists (`IPushNotificationService`). V1 default = no.
5. **Suppliers** — free text in V1, or a `Supplier` table from the start? V1 default = free text; `varchar(200)` migrates to FK later cleanly.
6. **Catalogue price update** — auto-update `Material.PricePerUnit` to the latest paid price when the admin promotes / reviews, or always require an explicit click? V1 default = explicit click only.

## What "done" looks like for V1

- Migration `AddMaterialPurchases` generated via CLI; PostgreSQL self-heal blocks for both tables in `Program.cs`.
- Two new EF entities, one new admin controller, one kiosk endpoint set, one new frontend service.
- Kiosk pre-screen with `Zaznamenať šichtu` / `Nákup materiálu` tiles. Existing šichta flow continues to work unchanged when the picked Location is not the trigger Location.
- In-šichta combined flow: when the worker picks the configured `MaterialPurchases:TriggerLocationId`, the Položky + Účtenka steps appear inline; one submit writes both a `TimeEntry` AND a linked `MaterialPurchase`.
- Standalone Nákup flow (from the pre-screen tile): Location picker (with `Nezadané`), line entry with catalogue search + `+ Nový materiál`, optional receipt photo, confirm screen. Produces a `MaterialPurchase` with `TimeEntryId = NULL`.
- Admin Materiál page reorganised into Katalóg / Nákupy / Neidentifikované / Spotreba tabs.
- Per-material drill-in works from any tab.
- `MaterialPurchasesExcelExportService` mirrors `MaterialExcelExportService` shape.
- `MaterialPurchases` feature flag wired across backend filter + frontend service + Account toggle row; default off in prod, on in dev once dev is green. With the flag off, both kiosk entry points fall back to the current behaviour (no pre-screen, no in-šichta capture); the Location named "Nákup materiálu" still works as a regular Location for clocking hours.
- `dotnet build` clean. `npx tsc --noEmit -p tsconfig.app.json` clean. Local Postgres dev DB starts cleanly with no `no such column` warnings.

## Notes for the implementation session

- All blocking design decisions have been answered. Feel free to start when ready; the open questions in the section above are V1.1 polish, not blockers.
- `WAREHOUSE_PLAN.md` Phase 1's `MaterialPurchase` section is superseded by the schema in this file. When this lands, leave a one-line "Implemented per `MATERIAL_PURCHASES_PLAN.md`" note in the older plan rather than deleting it.
- Older-worker UX rules (`NOTIFICATIONS_PLAN.md` §10) apply to every kiosk-facing string. No marketing copy. Plain Slovak. Big targets.
- Mobile-first layouts; match the patterns shipped in the 2026-05-01 tablet pass.
- The trigger Location ID belongs in `appsettings`, not hard-coded against a name. Use `MaterialPurchases:TriggerLocationId`. Falls back to skipping the in-šichta capture when missing or stale, never crashes the kiosk.
- "Firma" is the next location-as-workaround the customer wants to discuss. It is **not** in scope for this plan; it gets its own brief.

---

*End of plan.*
