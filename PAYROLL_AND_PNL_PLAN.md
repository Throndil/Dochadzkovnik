<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Payroll (Mzdy) + Per-Workplace P&L — Plan & Reference

> Created 2026-05-25 from the 2026-05-24 customer call.
> Status: **Not started. This file is the brief for the implementation session.**
>
> This file scopes three closely-coupled items from the 2026-05-24 batch in
> `BACKLOG.md`: Mzdy (payroll) view, hourly-wage foundation, and the
> per-workplace net-profit view (Náklady → Čistý zisk). It supersedes
> `WAREHOUSE_PLAN.md` Phase 3 (`Employee.HourlyWage` + `TimeEntry.WageAtTime`)
> and Phase 4 (revenue / per-location P&L) — the schema and UX below are the
> answers to those sketches, grounded in what the customer actually asked for
> on 2026-05-24. When this lands, leave a one-line "Implemented per
> `PAYROLL_AND_PNL_PLAN.md`" note in `WAREHOUSE_PLAN.md` rather than deleting
> those sections.

## Read first

1. `PROJECT_NOTES.md` — Core Data Models, Migration Safety Rules, the
   two-surface architecture (kiosk no-JWT vs. admin JWT). Wages and advances
   are admin-only — the kiosk surface MUST NOT expose them in any DTO,
   response, or log line.
2. `MATERIALS_PLAN.md` — V1.1 inflation-protection pattern
   (`MaterialUsage.UnitPriceAtTime`). The wage snapshot below is the same
   pattern applied to `TimeEntry`.
3. `MATERIAL_PURCHASES_PLAN.md` — feature-flag wiring, separate financial /
   statistics sub-site direction. The P&L view in this plan lives on the
   admin Location detail page in V1, but the read endpoints are shaped so a
   future sub-site can reuse them without rewrites.
4. `WAREHOUSE_PLAN.md` §Phase 3 + §Phase 4 — superseded by this plan; do not
   start from those sketches directly.
5. `BACKLOG.md` §Customer call (2026-05-24) — the three items being scoped
   here: "Mzdy view", "Per-workplace net profit view", and the parked
   "Hourly wage per employee" / "Historical wage snapshotting" /
   "Per-location P&L view" from the Financial management section.

## What the customer asked for

From the 2026-05-24 call notes (translated):

- **Mzdy view.** New admin page or sub-tab. One row per employee for the
  selected month with columns: `Meno | Hodiny v mesiaci | Hodinová sadzba |
  Zálohy | Výplata`. `Výplata = Hodiny × Sadzba − Zálohy`. Default month is
  the **previous** calendar month — the customer's phrasing was "vyjde
  apríl" = April ends, you open this and pay people for April. This is the
  customer's first concrete ask that unblocks the parked Financial
  Management items.

- **Per-workplace net profit view.** Full P&L per `Location`:
  `Príjem (contract value) − (Mzdové náklady + Materiálové náklady) =
  Čistý zisk`. Depends on the Mzdy foundation plus `MaterialPurchase` (in
  flight per `MATERIAL_PURCHASES_PLAN.md`) plus a new `Location.ContractValue`
  for the income side. If contract value is empty, show only the cost side
  and label profit as `—`.

- **Wage rises must not rewrite history.** Implicit in the customer's
  "payroll has to be correct" framing. Same snapshot pattern as
  `MaterialUsage.UnitPriceAtTime` — store the wage on each `TimeEntry`
  row at the moment it is logged.

## Design decisions locked in this session

- **(a) Snapshot wage at TimeEntry insert time.** New
  `TimeEntry.WageAtTime` column, populated from `Employee.HourlyWage` on
  every insert path (kiosk `log-hours`, kiosk `clock-out`, admin POST,
  admin PUT). If the employee's wage is NULL at insert time, snapshot 0
  and let the Mzdy view surface a Slovak warning "Sadzba nenastavená" so
  the manager knows to fix it. Editing a `TimeEntry` does **not** re-snapshot
  unless the caller explicitly sends `wageAtTime`. Mirrors `MaterialUsage`
  exactly (see V1.1 release notes in `PROJECT_NOTES.md`).

- **(b) Advances (`Zálohy`) are a separate table, not a column on
  TimeEntry.** A worker may receive multiple advances per month, on
  different dates, with different notes; some months have none. A separate
  `EmployeeAdvance` table is the only model that captures that without
  bending other tables.

- **(c) "Previous calendar month" default with manual override.** The Mzdy
  page opens on the previous month by default (so when the customer opens
  it on 2026-05-03 it shows April). A month-picker chip row lets them step
  back to older months. No date-range freedom in V1 — payroll is monthly.

- **(d) EUR only.** Same call as `MATERIAL_PURCHASES_PLAN.md`. FX is a
  multi-day project; do not introduce a currency field on any of the new
  tables.

- **(e) Wages are admin-only data. Kiosk endpoints MUST NOT expose them.**
  No `WageAtTime`, `HourlyWage`, `EmployeeAdvance`, or computed wage total
  in any `/api/kiosk/*` response DTO. The split-DTO pattern from V1.3.2
  (kiosk `EmployeeMissingDaysDto` vs. admin `EmployeeMissingDaysAdminDto`)
  is the precedent — apply it here too. The type system enforces the
  boundary; reviewer scans `/api/kiosk/*` DTOs for the word "Wage" before
  any deploy.

- **(f) Contract value lives on `Location`, not on a separate `Contract`
  table.** V1 collects one number per site, optional. If the customer
  later asks for multiple contracts per site, billing milestones, or
  partial invoicing, promote `Location.ContractValue` to a real
  `LocationContract` table — same Y-shape upgrade path described in
  `MATERIAL_PURCHASES_PLAN.md` for suppliers.

- **(g) Feature flag.** `PayrollAndPnL`, same pattern as `Notifications` /
  `CommanderIntegration` / `MaterialPurchases`. Default OFF in prod.
  Superadmin flips it on per environment after demo. The P&L card on
  Location detail only renders the material-spend line when
  `MaterialPurchases` is **also** on; if it is off, the card hides the
  material-spend row and the profit total, surfacing a Slovak note
  "Nákupy materiálu nie sú aktivované". Independent flags, separate
  toggles, cleaner story for the customer.

## Schema

Generate the migration via the CLI per Migration Safety Rule 1:

```
cd API
dotnet ef migrations add AddPayrollAndPnL
```

PostgreSQL self-heal blocks for both new columns and the new table in
`Program.cs` per Rule 3.

### `Employee.HourlyWage` (new column)

```
HourlyWage   decimal(12,4)  NULL   -- EUR per hour. NULL = not set yet;
                                    -- Mzdy view shows "Sadzba nenastavená"
                                    -- in amber for that row.
```

NULLABLE on purpose: existing rows must not be back-filled with a guessed
value. The admin sets it explicitly per employee via the existing
`/admin/employees/:id` detail page (new field in the form).

### `TimeEntry.WageAtTime` (new column)

```
WageAtTime   decimal(12,4)  NOT NULL  DEFAULT 0  -- EUR/h snapshot at
                                                  -- insert time. Equivalent
                                                  -- of MaterialUsage.UnitPriceAtTime.
```

`NOT NULL DEFAULT 0` because every existing row pre-migration needs a
value, and 0 is the only honest answer for entries logged before wages
existed. The Mzdy view filters those rows with a warning (see UX below).

### `EmployeeAdvance` (new table)

```
Id           int            PK
EmployeeId   int            FK -> Employees(Id) ON DELETE Restrict   NOT NULL
Date         date           NOT NULL    -- when the advance was paid out
Amount       decimal(12,2)  NOT NULL    -- EUR; positive only in V1
Note         varchar(500)   NULL        -- e.g. "Záloha na nákup obkladu"
CreatedAt    timestamp      NOT NULL
UpdatedAt    timestamp      NOT NULL
CreatedBy    varchar(100)   NULL        -- admin username; audit only

INDEX (EmployeeId, Date)
```

Negative amounts are out of scope in V1. If the customer asks for refunds /
clawbacks later, allow negative `Amount` or add a `Type` enum then.

### `Location.ContractValue` (new column)

```
ContractValue   decimal(14,2)  NULL    -- EUR. NULL = no contract recorded;
                                        -- P&L card shows revenue row as "—"
                                        -- and hides the profit total.
```

Editable from the existing `/admin/locations/:id` detail page (new field
in the form). Audit log of changes is out of scope in V1; promote to a
`LocationContractHistory` table if the customer asks.

## Feature flag wiring

Identical to `Notifications` / `CommanderIntegration` / `MaterialPurchases`.

- Backend:
  - Add `"PayrollAndPnL"` to the `knownFlags` array in `Program.cs` so the
    row is seeded `Enabled = false` on first boot.
  - Apply `[RequireFeatureOrSuperAdmin("PayrollAndPnL")]` at the class
    level on the new `PayrollController` and `EmployeeAdvancesController`,
    AND on any new actions added to `LocationsController` for the P&L
    read (use the attribute at action level for those — do not flag the
    whole controller because it owns existing public-ish actions).
- Frontend:
  - `feature-flag.service.ts` — extend the typed map with
    `payrollAndPnL: Signal<boolean>` next to the existing flags.
  - `app.routes.ts` — new `/admin/mzdy` route, gated by the same
    flag-or-superadmin guard pattern as `/admin/notifikacie`.
  - `navbar.component.html` — "Mzdy" link gated by
    `flags.payrollAndPnL() || auth.isSuperAdmin()`. Desktop + mobile menus.
  - `account.page.html` — fourth toggle row in the Funkcie superadmin card.
  - `/admin/locations/:id` — the new P&L card is rendered conditionally on
    `flags.payrollAndPnL() || auth.isSuperAdmin()`; the material-spend row
    inside it is further gated on `flags.materialPurchases() ||
    auth.isSuperAdmin()`.
- Default state: prod boots with the flag off; customer never sees Mzdy
  link, the P&L card on Location, the new wage / advance / contract input
  fields on the Employee / Location detail forms, or anything else added
  by this plan. Dev superadmin flips it on once dev is green.

## Endpoints

### Admin (JWT, behind the `PayrollAndPnL` flag)

```
GET  /api/payroll/monthly?month=2026-04
     returns [{
       employeeId, firstName, lastName, isActive,
       hoursWorked,           -- sum of TimeEntry.HoursWorked in month
       hourlyWageSnapshotAvg, -- weighted avg of TimeEntry.WageAtTime; if
                                 all rows are 0, returns null and the row
                                 carries a "wageMissing": true flag
       hourlyWageCurrent,     -- Employee.HourlyWage at read time, for
                                 the editor column (separate from snapshot)
       advancesTotal,         -- sum of EmployeeAdvance.Amount in month
       payout                 -- hoursWorked * hourlyWageSnapshotAvg - advancesTotal
     }]
     plus grand-total footer { hoursWorked, gross, advances, payout }.

GET  /api/payroll/monthly/export?month=2026-04
     application/vnd.openxmlformats-officedocument.spreadsheetml.sheet
     Single sheet "Mzdy". Same shape as the table above. Slovak headers,
     #,##0.00 € formatting on EUR columns. Filename
     "Mzdy_{yyyy-MM}.xlsx".

GET  /api/employee-advances?from=&to=&employeeId=
POST /api/employee-advances    body { employeeId, date, amount, note? }
PUT  /api/employee-advances/{id}    body { date, amount, note? }
DELETE /api/employee-advances/{id}

GET  /api/locations/{id}/pnl?from=&to=
     returns {
       location: { id, name, contractValue },
       labour:   { hoursWorked, cost, breakdownByEmployee: [...] },
       material: { cost, breakdownByMaterial: [...] }  // null if flag off
       revenue:  contractValue OR null,
       profit:   revenue - labour.cost - material.cost OR null
     }

PUT  /api/locations/{id}/contract-value    body { contractValue: decimal? }
```

The `hourlyWageSnapshotAvg` is computed server-side as
`sum(WageAtTime * HoursWorked) / sum(HoursWorked)` over the month so the
Mzdy view's `Výplata` matches what the worker actually earned given any
mid-month rises. Don't naïvely multiply hours by current wage on the read
path — that defeats the whole snapshot pattern.

### Wage-snapshot stamping (no new endpoint; existing inserts only)

Every existing insert path is amended to stamp `WageAtTime`:

- `KioskController.LogHours` and `KioskController.ClockOut` —
  `entry.WageAtTime = employee.HourlyWage ?? 0m;` before save.
- `TimeEntriesController.Create` and `TimeEntriesController.Update` —
  same. Update only re-snapshots if the caller explicitly sets
  `wageAtTime` in the request body (mirrors `unitPriceAtTime` behaviour
  on `MaterialUsage` PUT).

## Admin UX

### `/admin/mzdy` — new admin page

Single page, top-anchored month picker, big single table.

- **Header.** Month-picker chip row: `← Apríl 2026 →`. Default to the
  previous calendar month on first load. Persist last-picked month in
  `localStorage` so re-opening jumps back to the same month, not the
  default.
- **Table.** One row per `Employee` with at least one `TimeEntry` in the
  selected month OR at least one `EmployeeAdvance` in the selected month.
  Inactive employees with historical activity in the month still show
  (greyed out, with a small "neaktívny" badge). Columns:
  - Meno (sortable, click → employee detail)
  - Hodiny v mesiaci (right-aligned, tabular-nums, format `34,5 h`)
  - Hodinová sadzba (right-aligned, inline-editable; clicking the value
    opens a small numpad input that PUTs `/api/employees/{id}` with the
    new `hourlyWage`; the column shows both the *snapshot average* used
    for this month's payout AND the *current* rate if they differ, with
    a tiny hint `(katalóg: 5,80 €/h)`)
  - Zálohy (right-aligned; clicking opens an inline drawer listing the
    month's advances for that employee with add / edit / delete; sum is
    shown in the cell)
  - Výplata (right-aligned, bold, format `#,##0.00 €`; if any row has
    `wageMissing: true`, render the cell in amber and replace the number
    with "Sadzba nenastavená")
- **Footer.** Grand total row across all employees: hodiny, hrubá mzda,
  zálohy, výplata. `Stiahnuť Excel` green button (same shape as the
  material exports). Sticky on mobile.
- **No date-range freedom.** The customer wants one number per worker per
  month; per-day breakdowns are noise here. Workers' daily hours stay
  visible on the existing Záznamy dochádzky page.

### `/admin/locations/:id` — new "Náklady a zisk" card

Lives below the existing Material slide-over button. Defaults to the
current calendar month with month / week / custom toggle (reuse the same
`Mesiac / Týždeň` pattern from Záznamy dochádzky and the `LocationManagePanel`).

Table layout:

```
Príjem (zmluvná hodnota)          5 000,00 €
─────────────────────────────────────────────
Mzdové náklady                    1 240,00 €
  Janko Mrkvička   8,5 h × 6,50 €    55,25 €
  Peter Kováč    180,0 h × 6,80 € 1 224,00 €
  ...
Materiálové náklady                 320,00 €    (only when MaterialPurchases flag on)
  Cement      40 × 4,80 €            192,00 €
  Voda         8 × 16,00 €           128,00 €
  ...
─────────────────────────────────────────────
Čistý zisk                        3 440,00 €    68 % marža
```

- Inline-editable `Zmluvná hodnota` field above the table; saves via PUT
  `/api/locations/{id}/contract-value`. Empty → revenue row shows "—",
  profit row hidden.
- `Mzdové náklady` and `Materiálové náklady` are collapsible. Default
  collapsed on mobile, expanded on `md+`.
- When `MaterialPurchases` flag is off, the Materiálové náklady row is
  replaced with a small Slovak note "Pre kompletný výpočet aktivujte
  Nákupy materiálu vo Funkcie." (visible only to superadmin) — for
  regular admin users the row is simply hidden and the profit total
  carries on without it. The note is a development reminder, not customer
  copy.
- "Stiahnuť Excel" button on the card produces a per-location two-sheet
  workbook (Súhrn + Detail) matching the shape of
  `MaterialExcelExportService` for visual consistency.

### `/admin/employees/:id` — new wage field

Existing form gains a `Hodinová sadzba (€/h)` input near the bottom.
Numeric, decimal, NULL allowed. When NULL, show amber hint `Bez sadzby —
nový záznam dochádzky uloží 0 €/h.` so the manager understands the
consequence.

### Mobile / tablet

Match the patterns shipped in the 2026-05-01 tablet pass: `.min-h-dvh`,
`.touch-target`, stacked-card layouts below `md`. Mzdy table collapses to
cards on narrow viewports (one card per employee). Reference
`materials.page.html`, `commander.page.html`, `kiosk.page.html`.

## Out of scope (V1)

- Multi-currency. EUR only.
- Tax / odvody calculations. `Výplata = Hodiny × Sadzba − Zálohy`. Gross
  number; the customer pays through their accountant. Net wages, tax
  brackets, social fund deductions all live outside this app.
- Wage history table. `Employee.HourlyWage` is overwritten on edit; only
  the per-`TimeEntry` snapshot is preserved. A `WageHistory` table is
  cheap to add later if the customer wants an audit of "when did Janko's
  wage change".
- Negative advances / clawbacks. V1 enforces `Amount > 0`.
- Multiple contracts per location, billing milestones, partial invoicing.
- Subcontractor / other-spend rows on the P&L. The long-game P&L sketch
  in `WAREHOUSE_PLAN.md` lists "Other spend" — V1 ships labour + material
  only.
- Per-employee P&L (across all locations they worked at). Mzdy already
  gives the labour side; cross-location aggregation is a future report.
- Payslip PDF generation. Excel export only.
- Worker self-view of their own wages / advances on the kiosk. Admin-only
  data per (e) above.
- Push / WhatsApp notification when a wage is changed, an advance is
  added, or a contract value is updated.

## Open questions still worth asking the customer

1. **Hours definition.** Are `hoursWorked` always wall-clock entry hours,
   or should overtime / weekend hours be priced at a multiplier? V1
   default = wall-clock × single rate. Promote to a `WageMultiplier` per
   `TimeEntry` later if needed.
2. **Mid-month rate change.** The snapshot pattern handles it correctly,
   but confirm with the customer: "If you raise Janko from 6,50 to 6,80
   on the 15th, his April payout is the 1–14 hours at 6,50 plus the
   15–30 hours at 6,80 — is that what you expect?" V1 default = yes.
3. **What counts as an advance?** Cash advance, reimbursed receipts,
   tools? V1 default = anything the manager records via `EmployeeAdvance`
   is subtracted from payout; semantics are the manager's call.
4. **Contract value mid-month.** If the customer raises a site's contract
   value mid-month, does the P&L view need a historical view or just
   show the current number? V1 default = current number, no history.
5. **Who can see wages?** Currently any user with `vladosroka` or
   `admin` JWT — that is two people. Confirm before shipping.
6. **Inactive employees with no advances or hours in a month** — should
   they appear in the Mzdy table at all? V1 default = no, only employees
   with activity in the month.

## What "done" looks like for V1

- Migration `AddPayrollAndPnL` generated via CLI; PostgreSQL self-heal
  blocks for `Employee.HourlyWage`, `TimeEntry.WageAtTime`,
  `Location.ContractValue`, and the `EmployeeAdvance` table in
  `Program.cs`.
- Three new EF entity edits (`Employee`, `TimeEntry`, `Location`) plus
  one new entity (`EmployeeAdvance`). One new admin controller
  (`PayrollController`), one extension to `LocationsController`
  (`/{id}/pnl` + `/{id}/contract-value`), one new
  `EmployeeAdvancesController`, one new frontend service
  (`PayrollService`), one new Excel exporter
  (`PayrollExcelExportService`).
- Every insert path stamps `TimeEntry.WageAtTime` from
  `Employee.HourlyWage ?? 0m`. Verified by reviewer scan of
  `KioskController.LogHours`, `KioskController.ClockOut`,
  `TimeEntriesController.Create`, `TimeEntriesController.Update`.
- `/admin/mzdy` page renders the monthly table with month picker, inline
  wage edit, advances drawer, sticky footer, Excel export.
- `/admin/locations/:id` shows the Náklady a zisk card with collapsible
  labour and material breakdowns; revenue / profit hide when contract
  value is empty; material row hides when `MaterialPurchases` flag is off.
- `/admin/employees/:id` form has the `Hodinová sadzba` input with the
  Slovak hint when NULL.
- No `/api/kiosk/*` endpoint references `WageAtTime`, `HourlyWage`,
  `EmployeeAdvance`, or any wage total. Reviewer greps the kiosk DTOs
  and controllers for the substrings `Wage`, `Advance`, `Záloh`, `Výplata`
  before merge.
- `PayrollAndPnL` feature flag wired across backend filter + frontend
  service + Account toggle row; default off in prod, on in dev once dev
  is green.
- `dotnet build` clean. `npx tsc --noEmit -p tsconfig.app.json` clean.
  Local Postgres dev DB starts cleanly with no `no such column` warnings.
- A short pre-flight check on the dev DB after redeploy: confirm the
  three new columns and the new table exist and the self-heal block did
  not flag anything.

## Migration order across the financial track

For reference; do not bundle.

1. `AddMaterialPurchases` (already in flight per `MATERIAL_PURCHASES_PLAN.md`).
2. `AddPayrollAndPnL` (this plan): `Employee.HourlyWage`,
   `TimeEntry.WageAtTime`, `EmployeeAdvance`, `Location.ContractValue`.
3. Future: `AddLocationContracts` (multi-contract per location) if the
   customer's billing model needs it. Out of scope until they ask.

## Notes for the implementation session

- All blocking design decisions have been answered. Feel free to start
  when ready; the open questions above are V1.1 polish, not blockers.
- The kiosk DTO leak guard (item (e) above) is non-negotiable. The
  V1.3.2 split-DTO precedent is the template — do not let convenience
  drift this plan into "just one shared DTO with optional fields".
- Older-worker UX rules (`NOTIFICATIONS_PLAN.md` §10) only apply to the
  kiosk surface. The admin Mzdy / P&L pages are manager-facing and can
  use denser layouts, but the mobile / tablet pass standard still
  applies (`.min-h-dvh`, `.touch-target`, stacked-card fallback below
  `md`).
- `WAREHOUSE_PLAN.md` §Phase 3 and §Phase 4 are superseded by the schema
  and views in this plan. When this lands, leave a one-line
  "Implemented per `PAYROLL_AND_PNL_PLAN.md`" note in the older plan
  rather than deleting those sections.

---

*End of plan.*
