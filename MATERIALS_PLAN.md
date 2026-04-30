<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Materials — Implementation Plan & Reference

> Last updated: 2026-04-26 (V1.1)
> Status: **Implemented (V1.1) — pending local verification on `localhost`.**

## Changelog

- **2026-04-26 V1.1** — Added `Material.PricePerUnit` and inflation-protected `MaterialUsage.UnitPriceAtTime` snapshot, cost calculations and grand totals across the panel + Excel export, smart "default to last day of filtered month" for the entry date, and an inline "+ Nový materiál" creator inside the slide-over panel. New migration: `dotnet ef migrations add AddMaterialPrice`.
- **2026-04-26 V1** — Initial release of per-location material tracking (catalogue + usages + slide-over panel + 2-sheet Excel export). Migration: `dotnet ef migrations add AddMaterialsAndUsage`.

---

## Goal

Track per-location material consumption (cement, voda, obklad, dlažba, piesok, omietka, …)
inside Šichtovnica, with a one-click Excel export so the customer keeps their familiar
artefact for the accountant / archive — but with the database as the single source of truth.

---

## Approach decision

**Approach B — DB as truth, Excel as report format.**

Approach A ("two-way sync with an Excel file on disk") was rejected because:
- Browsers cannot reliably "open" a local file; concurrency would corrupt the artefact;
- A single user editing the file in Excel would silently desync from server state;
- No querying / cross-location aggregation; no audit trail; mobile UX is a non-starter.

Approach B keeps the same **deliverable** (the .xlsx) but generates it on demand from
clean, queryable data, mirroring the rest of the app architecture (Locations, TimeEntries, WorkPhotos).

---

## V1 scope (what was built)

### Backend (`API/`)

| File | What it adds |
|---|---|
| `Models/Material.cs` | Catalogue entity (Name, Unit, IsActive) |
| `Models/MaterialUsage.cs` | Per-location consumption record (LocationId, MaterialId, Quantity, Date, EmployeeId?, Note?, PhotoUrl?) |
| `Models/Location.cs` | Added `MaterialUsages` navigation collection |
| `Data/AppDbContext.cs` | New `Materials` / `MaterialUsages` DbSets + entity configs (decimal precision 12,3 on Quantity, indexes on `(LocationId, Date)` and `(LocationId, MaterialId)`) + timestamp updates on save |
| `DTOs/Dtos.cs` | `MaterialDto`, `CreateMaterialDto`, `UpdateMaterialDto`, `MaterialUsageDto`, `Create/UpdateMaterialUsageDto`, `MaterialSummaryRowDto` |
| `Controllers/MaterialsController.cs` | Catalogue CRUD (`/api/materials`); duplicate-name guard (case-insensitive); soft delete if usage exists, hard delete otherwise |
| `Controllers/LocationsController.cs` | Per-location material endpoints (see Endpoints below) |
| `Services/MaterialExcelExportService.cs` | ClosedXML report builder — two sheets (Súhrn + Detailný záznam), amber header row, frozen panes, column autofit, photo hyperlinks |
| `Program.cs` | DI registration; SQLite + PostgreSQL self-heal blocks for both new tables; first-run seed of the catalogue (10 common Slovak items) |

`ClosedXML 0.102.3` was already on `API.csproj` — no new NuGet needed.

### Frontend (`client/`)

| File | What it adds |
|---|---|
| `services/material.service.ts` | Catalogue + per-location usage + Excel download helpers |
| `components/location-manage-panel/` | Slide-over right-hand panel — Süh, add/edit/delete entries, sticky footer with headline numbers and "Stiahnuť Excel" |
| `pages/materials/` | Admin catalogue page at `/admin/materials` — table + add/edit/toggle/delete |
| `pages/locations/locations.page.{ts,html}` | "Spravovať" button on every Lokácia card; mounts the slide-over panel |
| `app.routes.ts` | New `/admin/materials` route |
| `components/navbar/navbar.component.html` | "Materiál" link in desktop and mobile menus |

### UX details

- Panel opens with a 300ms slide-in from the right (Tailwind `translate-x-full → translate-x-0`).
- Backdrop click, Esc, or the ✕ button all dismiss.
- `<body>` gets `overflow-hidden` while the panel is open (mobile-friendly).
- Date filter defaults to the current calendar month with a "Tento mesiac" pill that re-snaps after manual changes.
- Quick-quantity chips `[1, 5, 10, 20, 50]` mirror the hour-presets pattern from the kiosk hours form.
- Detailed log shows friendly relative dates ("dnes" / "včera" / "pred 3 dňami") for entries within the last week.
- Sticky footer in the panel always shows the top 2 material totals plus the "Stiahnuť Excel" button.

---

## Endpoints

### Catalogue (`/api/materials`)

```
GET    /api/materials?activeOnly=true|false
GET    /api/materials/{id}
POST   /api/materials                       { name, unit }
PUT    /api/materials/{id}                  { name, unit, isActive }
PATCH  /api/materials/{id}/toggle-active
DELETE /api/materials/{id}                  → soft (sets IsActive=false) if any usage exists; hard otherwise
```

### Per-location usage (`/api/locations/{id}/materials`)

```
GET    /api/locations/{id}/materials?from=YYYY-MM-DD&to=YYYY-MM-DD
GET    /api/locations/{id}/materials/summary?from=&to=
POST   /api/locations/{id}/materials                  { materialId, quantity, date, employeeId?, note? }
PUT    /api/locations/{id}/materials/{usageId}        same shape
DELETE /api/locations/{id}/materials/{usageId}
POST   /api/locations/{id}/materials/{usageId}/photo  multipart/form-data
DELETE /api/locations/{id}/materials/{usageId}/photo
GET    /api/locations/{id}/materials/export?from=&to=  → application/vnd.openxmlformats-officedocument.spreadsheetml.sheet
```

All endpoints are JWT-protected; same auth model as the rest of the admin surface.

---

## Excel report format

Filename: `Spotreba_{LocationName}_{from}_{to}.xlsx` (diacritics stripped from the location name; spaces → `_`).

**Sheet 1 — "Súhrn"**
Materiál | Jednotka | Spolu množstvo | Počet záznamov

**Sheet 2 — "Detailný záznam"**
Dátum | Materiál | Množstvo | Jednotka | Zamestnanec | Poznámka | Foto (hyperlink to Cloudinary if present)

Both sheets have an amber header row, frozen top, autofit columns. Quantities round-trip as numbers (so the customer can sum/sort in Excel); dates are real Excel dates.

---

## Migration

> ⚠️ Per the **Migration Safety Rules** in `PROJECT_NOTES.md`:
> the migration file MUST be generated via the EF CLI to get the `.Designer.cs` snapshot.
> The self-heal blocks in `Program.cs` exist as a backstop in case the migration is skipped on a deployed environment.

From the `API/` directory on dev:

```bash
cd API
dotnet ef migrations add AddMaterialsAndUsage
```

This produces:
- `Migrations/<timestamp>_AddMaterialsAndUsage.cs`
- `Migrations/<timestamp>_AddMaterialsAndUsage.Designer.cs`
- updated `AppDbContextModelSnapshot.cs`

Then run the API locally — `MigrateAsync()` applies the migration on startup. The SQLite self-heal is idempotent and will quietly do nothing if the migration created the tables first; same on PostgreSQL.

To verify locally:

```bash
dotnet ef migrations list   # the new migration should appear as pending until first run
dotnet run                  # tables are created, catalogue is seeded with 10 entries
```

---

## Seeded catalogue

On first run only (when `Materials` table is empty), Program.cs inserts:

| Name | Unit |
|---|---|
| Cement | vrece |
| Voda | l |
| Piesok | kg |
| Štrk | kg |
| Obklad | m² |
| Dlažba | m² |
| Omietka | kg |
| Lepidlo | vrece |
| Sadrokartón | ks |
| Skrutky | ks |

Customer can edit / add / deactivate any of these from `/admin/materials`.

---

## Out of scope (V2 candidates)

1. **Kiosk flow** — let workers log material usage from the tablet after clocking out.
2. **Excel import** — accept a customer-provided .xlsx and seed `MaterialUsages` from it.
3. **Per-unit cost / invoicing** — add `Material.PricePerUnit`, surface "spend" totals in the panel.
4. **Stock / inventory** — current model is consumption only, not warehouse stock. Confirm with customer before building.
5. **Cross-location dashboard** — "How much cement did all sites use in March?" report.
6. **Photo retention policy for material photos** — same open question as work photos.
