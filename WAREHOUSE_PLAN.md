<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Warehouse + Financial Tracking — Plan & Handoff

> Created 2026-05-01 at the end of the materials / mobile-UX session.
> Status: **Not started.**
>
> **2026-05-06 update.** The customer reopened the purchases side of Phase 1
> with a kiosk-first flavour (workers select / create materials and upload
> receipts during clock-in, not via an admin page). The schema and UX for
> that work live in `MATERIAL_PURCHASES_PLAN.md` and supersede the
> `MaterialPurchase` section below. The plans / budget side
> (`MaterialPlan`) and the longer-term P&L direction in this file remain
> current. Read `MATERIAL_PURCHASES_PLAN.md` first if you are starting
> implementation.
>
> The customer asked for a "warehouse" concept, either a sibling of `Location` or
> directly tied to it. They want to plan how much material a site will need,
> log purchases, and over time grow this into a per-location financial picture
> (spending vs. revenue, profit per job). This document scopes that ask, lists
> the design choices, recommends a Phase 1, and flags the questions the customer
> still needs to answer before any code is written.

## Read first

1. `PROJECT_NOTES.md` — full stack context, especially:
   - "Core Data Models" (Material, MaterialUsage already exist).
   - "⚠️ CRITICAL: Migration Safety Rules" — every new table or column MUST go through `dotnet ef migrations add` AND a SQLite + PostgreSQL self-heal block in `Program.cs`. Recent self-heal scars (text-typed timestamps and decimals) are documented in `Program.cs` around the type-fix `DO $$ … END $$` block. Do not regress that.
2. `MATERIALS_PLAN.md` — the existing material-consumption design (V1 + V1.1). The new work extends that surface; do not duplicate it.
3. `BACKLOG.md` — the customer also wants per-employee `HourlyWage` parked there. That feeds into Phase 3 below; do not start it without bringing it up.

## What the customer actually said

Original ask, translated: a **warehouse** concept, either resembling `Location`
or **directly tied** to it, where the customer can:

- plan how much material will be used at each site (a budget / forecast).
- "buy more for it" (record purchases, top up stock).
- and longer term, see this app become a **financial management** tool: not
  only worker time, but spending and gain (revenue) per location, so that one
  day a manager can answer "did this job make us money?".

Two things to keep in mind while reading the rest of this file:

- The customer has **not** asked for full inventory tracking with bin
  locations, lot numbers, expiry dates, cycle counts, or barcode scans. Avoid
  importing the wrong mental model from generic ERP / WMS products.
- The customer's workforce is older / non-tech-savvy. Anything that ends up on
  a worker-facing surface (kiosk, photo upload) must be plain Slovak, big
  targets, no jargon. The admin surfaces have more latitude.

## The long-game picture (do not build this now)

The endpoint, after several iterations, is per-location P&L:

```
Location:  "Bazen Šmida Šamorin"
─────────────────────────────────────────────
Revenue (what we billed the customer)        45 000 €
─────────────────────────────────────────────
Material spend     (sum MaterialUsage)        9 200 €
Labour spend       (sum TimeEntry × wage)    18 700 €
Other spend        (subcontractors, etc)      2 100 €
─────────────────────────────────────────────
Profit                                       15 000 €     33 %
```

`MaterialUsage` already covers the material-spend line, with inflation-protected
`UnitPriceAtTime` snapshots. Everything else needs new schema. The plan below
chips at this picture from the easy end first and stops at any point if the
customer is satisfied — the customer is one step ahead of where we are; do not
sprint past them.

## Design choices for the warehouse concept

There are four mental models. Each has a different blast radius. Pick **one**
with the customer before writing code.

### A. Single central warehouse (one stock pool for the whole company)

A `Material` row gains a running `StockOnHand` (computed). Purchases add to
stock; usages subtract from it. There is no per-location allocation.

- Pro: one number per material, easy to teach, fast to build.
- Con: doesn't reflect "we ordered cement specifically for the Hlboká site";
  no visibility into "how much of this is reserved for which job".

### B. Per-location stock (each site is its own little warehouse)

Stock is tracked per `(LocationId, MaterialId)`. Purchases land at a site;
usages debit that site's stock; an explicit transfer moves stock between sites.

- Pro: reflects how the firm actually thinks about jobs ("the Hlboká site has
  20 bags of cement left").
- Con: doubles the bookkeeping (every purchase needs a destination location);
  transfers between sites add a third movement type; needs more UI.

### C. Warehouse(s) as first-class entity, Locations consume from them

`Warehouse` is a new entity, peer of `Location`. Stock lives in warehouses;
"Issue from warehouse to location" creates a movement that decrements warehouse
stock and creates a `MaterialUsage` against the location. Multi-warehouse is
naturally supported (e.g. central depot + truck stock).

- Pro: closest to reality and to standard ERP language; future-proof.
- Con: most code to write; risk of over-engineering for a small construction
  firm. Probably overkill unless the customer says they do have multiple
  physical storage points already.

### D. Plans + Purchases, no stock at all (budget vs. actuals only)

No physical stock model. Two new lightweight concepts:

- **Plan** — per-location, per-material, planned quantity (and optionally
  planned unit price). "We expect to use 40 bags of cement at Hlboká."
- **Purchase** — when stock arrives (invoice, supplier, total cost), with
  optional allocation to a location. Just a log of money out.

Reports compute:

- Plan vs. Actual usage per location (over/underspent).
- Total cost of material across all sites in a date range (purchases sum).

- Pro: minimum schema, fastest to ship, answers the customer's literal asks
  ("plan how much will be used", "buy more for it"). No counting, no transfers,
  no audit headaches.
- Con: no live "how much do we have left"; can't warn before a stockout.

### Recommendation

**Start with D. Add A or B later if the customer asks.** Reasons:

- D maps 1:1 to what the customer literally asked for ("plan" + "buy more").
- The complexity gap from D to B/C is significant; jumping there before
  validating that the customer cares about live stock counts risks building
  the wrong thing.
- D is additive: it does not invalidate adopting A/B/C later. The `Purchase`
  log from D becomes the inbound side of A/B/C; the `MaterialPlan` from D is
  the same thing in both worlds.
- D unlocks the financial vision (purchases sum = material cost line on the
  P&L) without committing to a stock model.

Do not propose D as "the final design". Frame it to the customer as Phase 1
and explain the upgrade path.

## Phase 1 — Plans + Purchases

Schema (all new). Generate the migration via the CLI (Migration Safety Rule 1):

```
cd API
dotnet ef migrations add AddWarehousePhase1
```

Both SQLite and PostgreSQL self-heal blocks must follow the same `IF NOT EXISTS`
pattern that the other tables in `Program.cs` use. Do not skip the self-heals;
production has been bitten twice this week by drift between the model and the
physical schema.

### `MaterialPlan` (per-location, per-material budget)

```
Id                int    PK
LocationId        int    FK -> Locations(Id)        ON DELETE Cascade
MaterialId        int    FK -> Materials(Id)        ON DELETE Restrict
PlannedQuantity   decimal(12,3)  NOT NULL
PlannedUnitPrice  decimal(12,4)  NULL    -- snapshot at plan time; falls back to Material.PricePerUnit if NULL
Note              varchar(500)   NULL
CreatedAt         timestamp NOT NULL
UpdatedAt         timestamp NOT NULL

UNIQUE (LocationId, MaterialId)   -- one plan row per (site, material)
INDEX  (LocationId)
```

UI: extend `LocationManagePanelComponent` with a second tab or a section above
the existing usage list. "Plán materiálu" — a small grid where the manager can
add rows and tweak quantities. Live "Plán: 40, Použité: 12, Zostáva: 28"
calculation per row, comparing against the existing `MaterialUsage` sum for
the same `(Location, Material)`. Cost variance shown alongside.

### `MaterialPurchase` (a purchase event, optionally allocated to a location)

```
Id              int      PK
MaterialId      int      FK -> Materials(Id)        ON DELETE Restrict
LocationId      int?     FK -> Locations(Id)        ON DELETE SetNull
                          -- nullable on purpose: a purchase may be "general stock"
                          -- not yet earmarked for a job. Only used by reports;
                          -- never affects MaterialUsage.UnitPriceAtTime.
SupplierName    varchar(200)  NULL
InvoiceRef      varchar(100)  NULL
PurchaseDate    date     NOT NULL
Quantity        decimal(12,3) NOT NULL
UnitPrice       decimal(12,4) NOT NULL  -- price actually paid; can differ from catalogue
TotalCost       decimal(14,4) NOT NULL  -- denormalised for sums; = Quantity * UnitPrice + maybe extras
PhotoUrl        varchar(1000) NULL      -- delivery slip / invoice scan
Note            varchar(500)  NULL
EmployeeId      int?     FK -> Employees(Id) ON DELETE SetNull   -- who logged it
CreatedAt       timestamp NOT NULL
UpdatedAt       timestamp NOT NULL

INDEX (MaterialId, PurchaseDate)
INDEX (LocationId)
```

UI: a new admin page `/admin/material-purchases` (Slovak label "Nákupy
materiálu"). List + filter by date range / location / material; "Pridať nákup"
form; per-row Cloudinary photo upload (re-use the existing `BlobService`).

### Reports

Two new reads, both server-side aggregates:

- `GET /api/locations/{id}/material-budget` returns a single object:

  ```json
  {
    "rows": [
      { "materialId": 7, "materialName": "Cement", "unit": "vrece",
        "plannedQty": 40, "plannedCost": 200,
        "actualQty": 12, "actualCost": 60,
        "variance": -140 }
    ],
    "totalPlanned": 1500,
    "totalActual":  900,
    "variance":     -600
  }
  ```

- `GET /api/material-purchases/summary?from=...&to=...&locationId=...` returns
  total spent grouped by material (with grand total) so an Excel export can be
  produced from a single query, mirroring `MaterialExcelExportService`.

### Out of scope for Phase 1

Do not add any of the following without explicit customer sign-off:

- Live `StockOnHand` calculation.
- Multi-warehouse, transfers, locations-of-storage.
- Reorder thresholds, low-stock alerts, push notifications when stock dips.
- Per-supplier price history / supplier comparison.
- Returns-to-supplier flow.

Each is reasonable. None is asked for yet.

## Phase 2 — Live cost line on the location detail page

Trivial extension once Phase 1 is in. Add a small "Náklady" card to
`/admin/locations/:id` showing:

- Material spend so far: sum of `MaterialUsage.Quantity * UnitPriceAtTime`.
- Material purchased for this site: sum of `MaterialPurchase.TotalCost` where
  `LocationId = this`.
- Plan vs. Actual variance (from the Phase 1 endpoint).
- Optional: link to a per-location Excel export combining usage + purchases.

No schema change. Pure UI + a new endpoint.

## Phase 3 — Labour cost line (depends on `Employee.HourlyWage`)

`BACKLOG.md` already parks `HourlyWage`. Adding it unlocks:

- Per-location labour cost = `sum(TimeEntry.HoursWorked * Employee.HourlyWage)`,
  with a snapshot pattern identical to `MaterialUsage.UnitPriceAtTime` so wage
  rises do not retroactively rewrite past totals (`TimeEntry.WageAtTime`).
- Migration: `Employee.HourlyWage` (decimal 12,4 EUR/h, nullable),
  `TimeEntry.WageAtTime` (decimal 12,4, nullable). Both with self-heal blocks.

Bring this up with the customer before starting. The wage rate is sensitive
data; confirm that managers (the `vladosroka` admin) are the only role that
should see it. The kiosk (no JWT) must never expose it.

## Phase 4 — Revenue per location (the harder ask)

This is the bottom half of the long-game P&L. Needs new schema:

- `LocationContract` (or `Quote`) — quoted total + currency + status; per
  location.
- `LocationInvoice` (or `Payment`) — actual money in.

Two flavours:

- Single-quote sites: one contract, one or many invoices billed against it.
- Hourly sites: revenue accrues with `TimeEntry.HoursWorked × rate`, so an
  `HourlyRate` lives on `LocationContract` rather than the worker.

The customer needs to describe how they actually bill before this is built.
Do not guess. Possible answer: "we don't bill from this app, we just want to
type a single number per job." If so, schema collapses to one nullable decimal
on `Location` and the rest of the analytics fall out for free.

## Schema migration order across all phases

1. `AddWarehousePhase1` — `MaterialPlan`, `MaterialPurchase`.
2. `AddEmployeeHourlyWage` — `Employee.HourlyWage`, `TimeEntry.WageAtTime`.
3. `AddLocationFinance` — revenue side per the customer's billing flow.

Each migration is its own commit, gated by a customer demo before the next
starts. Do not bundle.

## Open questions for the customer (ask before writing code)

1. Confirm the mental model. Read back: "We will let you (a) plan how many
   bags of cement, etc., a job will need, and (b) log purchases when stock
   arrives. We will NOT track live stock counts in version one. Is that what
   you mean by 'warehouse'?"
2. Multiple physical warehouses, or just one place where stock arrives?
   (Drives whether to ever leave Phase 1 in the D direction.)
3. Should a purchase always be tied to a specific site, or are some purchases
   "general" (van stock, shop floor)? This is what makes `Purchase.LocationId`
   nullable above; confirm.
4. Plan editability: once a plan row is set, is it locked, or can it be
   adjusted as the job evolves? (Locking adds a tiny audit table; not locking
   is simpler but loses the original budget.)
5. Who logs purchases? The admin (`vladosroka`), or also field workers via the
   kiosk? If the latter, the kiosk needs a new tile and the older-worker UX
   rules in `NOTIFICATIONS_PLAN.md` §10 apply.
6. Currency: EUR only, or do any suppliers invoice in CZK / USD? V1 should be
   EUR-only; bringing in FX is a multi-day project.
7. For the longer-term P&L: how is revenue actually billed? Per quote, per
   hour, per site, per month? See Phase 4.
8. Per-employee `HourlyWage` (Phase 3) — does the customer want this and is
   it acceptable for the `admin` superadmin and `vladosroka` to see worker
   wages, but no one else? Confirm before starting.

## What "done" looks like for Phase 1

- Migration generated via CLI, with self-heal blocks for both DBs in
  `Program.cs`.
- Two new EF entities, two new controllers, two new services on the frontend.
- "Plán materiálu" tab in `LocationManagePanelComponent` showing plan vs.
  actual + variance with a Slovak EUR formatter.
- New `/admin/material-purchases` admin page with list + add + photo upload.
- One new Excel export: `MaterialPurchasesExcelExportService` mirroring the
  shape of `MaterialExcelExportService` so the accountant gets a familiar
  artefact.
- `dotnet build` clean. `npx tsc --noEmit -p tsconfig.app.json` clean. Local
  Postgres dev DB starts cleanly with no `no such column` warnings.
- A short pre-flight check on the dev DB after redeploy: confirm the new
  tables exist and the `text` self-heal block did not flag any newly
  text-typed columns.

## Notes for the next session

- Do **not** start Phase 1 without explicit answers to questions 1–5 above.
  The customer prefers reviewing the running implementation to answering
  questionnaires up-front (per `COMMANDER_PLAN.md` §2 history), so it is
  acceptable to pre-answer them as a working draft and ship behind a feature
  flag (`WarehousePhase1`) — the same pattern Commander used. Default the
  flag to **off** so the customer never sees half-finished work.
- The dev branch has an unmerged delta against master (the materials feature
  itself isn't on master yet). Before adding more migrations, confirm with
  the developer where this is going to land. If it's still all dev-only, the
  migration order is simpler.
- The mobile/tablet UX pass from this session is now the standard. Any new
  page must use `min-h-dvh`, the `.touch-target` utility, and a stacked-card
  fallback below `md`. Match the patterns shipped in the recently-updated
  `commander.page.html`, `materials.page.html`, and `kiosk.page.html` for
  consistency.

---

*End of plan.*
