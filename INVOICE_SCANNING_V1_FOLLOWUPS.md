<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to managers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Invoice Scanning V1 — Follow-up polish items

> Created 2026-05-28 from manager feedback after V1 (PDF/file-picker
> upload + Document AI parse + admin review) shipped and started being
> used on real supplier invoices.
> Status: **Not started. This file is the brief for the next polish
> session.** It is independent of `INVOICE_SCANNING_CAMERA_PLAN.md`
> (V1.1, in-app camera capture) — the two can ship in either order or
> together.

## Read first

1. `INVOICE_SCANNING_PLAN.md` — the V1 brief that's now live. Schema,
   feature flag (`InvoiceScanning`), reconciliation rule, review UX
   are all unchanged by anything in this file.
2. `client/src/app/pages/location-detail/location-detail.page.{html,ts}`
   — host of the Spotreba materiálu card (item A below).
3. `API/Controllers/LocationsController.cs`
   `GET /api/locations/{id}/materials?from=...&to=...` and
   `GetMaterialSummary` already accept arbitrary date ranges; the
   client is artificially flattening to whole months. Item A is mostly
   a frontend change.
4. `API/Services/InvoiceParser.cs` — `AkciaRx`, `PrevzalRx`, `PoznDlRx`,
   `ParseDeliveryListMeta`, and the `DlMeta` record (line ~457). The
   parser fix for item B lives here.
5. `API/Controllers/InvoicesController.cs` `AutoMatchLocationsAsync`
   (line ~426) — already scans `AkciaName`, `PickedUpBy`,
   `DeliveryNote` candidates. Item B improves the *input* to this
   matcher; the matcher itself does not need to change.

---

## Item A — Pracovisko `Spotreba materiálu`: date-range filter + export

### What the manager reported

The `Spotreba materiálu` card on `/admin/locations/{id}` currently
groups everything by month. The whole row shows the actual delivery
date (`19.05.2026`, `21.05.2026`, etc.), which is correct, but the
filter at the top of the card only lets the manager pick a calendar
month. That makes it impossible to ask "what did this Pracovisko
consume between 19.05. and 22.05.?", and it forces the export (when
the manager wants one) to cover a whole month rather than the
specific window they care about.

### Current behaviour (verified in code)

- One signal `galleryMonth = signal(this.currentYearMonth())` in
  `location-detail.page.ts` drives **four** cards: Fotogaléria,
  Spotreba materiálu, Odpracované hodiny, Diaries.
- `loadMaterials()` (line ~133) parses `galleryMonth()` into
  `from = YYYY-MM-01`, `to = YYYY-MM-<lastDay>` and posts that range
  to `MaterialService.getUsages(id, from, to)`.
- The backend endpoint
  `GET /api/locations/{id}/materials?from=YYYY-MM-DD&to=YYYY-MM-DD`
  in `LocationsController.cs` already accepts any date range. No
  backend change required for the on-screen filter.
- There is **no export button** on the Spotreba card today.

### Change

**Decouple Spotreba's filter from the shared month picker.** The
Fotogaléria, Odpracované hodiny and Diaries cards keep their month
picker exactly as it is — the manager's mental model for those three
is calendar-month-based. Spotreba materiálu gets its own
date-range row.

Frontend (`location-detail.page.ts` + `.html`):

```
materialsRangeFrom = signal<string>(this.firstOfMonth());  // 'YYYY-MM-DD'
materialsRangeTo   = signal<string>(this.todayIso());      // 'YYYY-MM-DD'

loadMaterials() {
  const from = this.materialsRangeFrom();
  const to   = this.materialsRangeTo();
  if (!from || !to || from > to) return;
  this.materialsLoading.set(true);
  this.materialService.getUsages(this.id, from, to).subscribe({ ... });
}

// Existing `galleryMonth` chain still calls loadGallery, loadDiaries,
// loadHours. It NO LONGER calls loadMaterials. That call moves to the
// new range pickers' (ngModelChange) handlers.
```

Header row of the Spotreba card (`location-detail.page.html` around
line 78):

```
┌──────────────────────────────────────────────────────────────────┐
│ Spotreba materiálu                                                │
│                                                                   │
│ Od [ 2026-05-01 ▼ ]  Do [ 2026-05-28 ▼ ]   [ Exportovať .xlsx ]  │
└──────────────────────────────────────────────────────────────────┘
```

Use the same `bg-slate-100 dark:bg-slate-700` input styling already
used by the gallery month picker. Both date inputs are
`<input type="date">` — Slovak format renders natively from the
browser's locale.

Default range: first of current month → today. Manager edits both
ends with native date pickers; on every change, `loadMaterials()`
re-runs. The existing `materials()` table, `materialsTotalCost()`
computed, and per-row `Dátum` column all stay untouched — they were
already showing per-row dates correctly; this change just lets the
manager constrain which rows are shown.

The empty-state copy needs a tweak (it currently reads "V tomto
mesiaci nebol zaznamenaný žiadny materiál."):

```
"V zvolenom období nebol zaznamenaný žiadny materiál."
```

### Export

New `Exportovať .xlsx` button next to the date inputs. The export uses
the same XLSX path the other admin reports use (ClosedXML, server-side
generation, presigned URL or direct download). New endpoint:

```
GET /api/locations/{id}/materials/export?from=YYYY-MM-DD&to=YYYY-MM-DD
    → application/vnd.openxmlformats-officedocument.spreadsheetml.sheet
    → Content-Disposition: attachment;
       filename="spotreba-{location-slug}-{from}-{to}.xlsx"
```

Columns in the workbook, in the same order as the on-screen table:
`Materiál | Mn. | MJ | Náklady (€) | Dátum | Zdroj`, plus a `Spolu`
row at the bottom. `Zdroj` is the same three-value enum the UI shows
(`Z nákupu` / `Faktúra` / `Ručne`).

Manager-only, same JWT gate as the rest of `/admin`. No new feature
flag — this is a refinement of an already-shipped page.

### What "done" looks like for item A

- Spotreba card has from/to date inputs and a working
  `Exportovať .xlsx` button.
- Picking a 4-day range loads only the materials with `Dátum` in that
  range. The footer `Spolu` matches `SUM(Náklady)` over the visible
  rows.
- The exported XLSX has the same rows as the on-screen table,
  including the per-row date and zdroj badge value.
- Fotogaléria / Odpracované hodiny / Diaries are unchanged — they
  still react to the existing month picker.
- `dotnet build` and `npx tsc --noEmit -p tsconfig.app.json` are
  clean.

---

## Item B — Auto-match: also extract `stavba <name>` from `Pozn.DL`

### What the manager reported

A MAPEI delivery list arrived with this metadata footer:

```
za dodací list 26DN008151 | dodávateľa MAPEI SK, s.r.o. |
prevzal: p. Sroka | miesto dodania: p. Sroka, osobný odber |
akcia: Bratislava |
dátum dodania tovaru alebo služby: 22.05.2026 |
Pozn.DL: - stavba Bratislava - p. Sorka 0902 099 999
```

The site is mentioned **twice** in the document (`akcia: Bratislava`
and `stavba Bratislava` in `Pozn.DL`), but the review screen still
showed `— Sklad / Inventár —` as the picked Pracovisko. The manager
had to set it manually.

### Why the current matcher didn't pick it

Reading `AutoMatchLocationsAsync` (lines 426–524 of
`InvoicesController.cs`), the candidates are: `AkciaName`,
`PickedUpBy`, `DeliveryNote`. Match is exact-on-normalised-string
first, then "Location name appears inside the candidate text".

The likely failure mode in this specific case is one of:

1. **No active Location named `Bratislava`** in the customer's DB.
   The Locations are project names like `Devinska`, `Alzbetin Dvor`,
   `Palenisko`, `Čierna Voda`. "Bratislava" is the city for many of
   them, not a project. The exact match misses; the substring check
   misses too because `n.Contains(locName)` requires the Location
   name to be a substring of the candidate, and the Location name
   `Devinska` is not in the text `bratislava`.
2. **`AkciaName` came in null** because the OCR layer split the
   metadata block such that `akcia:` ended up out of the per-DL
   body the parser scans. The fallback would then be `PickedUpBy`
   ("p. Sroka") and `DeliveryNote` ("- stavba Bratislava - p. Sorka
   0902 099 999"). The latter contains "bratislava" as a substring,
   but again only matches if a `Bratislava`-named Location exists.

Either way, the fix is the same: the parser should treat
`stavba <name>` in `Pozn.DL` as a **first-class site-name signal**
alongside `akcia: <name>`, not just leave it as raw note text. That
gives the matcher a clean, scoped candidate (the literal name after
`stavba`), and it makes the audit-log "matched via stavba='Bratislava'"
human-readable.

This plan does **not** change the matcher's algorithm (exact then
substring). Loosening the substring direction so that `akcia=Bratislava`
matches `Location=Bratislava - Petržalka` is tempting but risky in a
finance-critical feature: out of scope. The customer will continue to
override on the review screen when no Location matches.

### Change

In `API/Services/InvoiceParser.cs`:

1. Add a new compiled regex next to `AkciaRx`:

   ```csharp
   private static readonly Regex StavbaRx = new(
       @"stavba\s+([^\-\|\r\n]+?)\s*(?=-|\||\r|\n|$)",
       RegexOptions.Compiled | RegexOptions.IgnoreCase);
   ```

   The pattern captures the name after `stavba `, stopping at the
   next `-`, `|`, newline, or end-of-string. We deliberately stop at
   `-` because the real-world Pozn.DL uses `-` as the field
   separator (`- stavba X - p. Sorka 0902 ...`).

2. In `ParseDeliveryListMeta` (line ~462), after the existing
   `akcia = AkciaRx.Match(body)...` line, add:

   ```csharp
   // Fallback: some suppliers (MAPEI SK) put the site name in
   // Pozn.DL as "stavba <name>" instead of in the akcia: field.
   // Only used when akcia is empty/sentinel — never overwrites a
   // populated akcia.
   if (string.IsNullOrEmpty(akcia))
   {
       var stavba = StavbaRx.Match(body).Groups[1].Value.OrNull()?.Trim();
       if (!string.IsNullOrEmpty(stavba) && stavba != "." && stavba != "-")
           akcia = stavba;
   }
   ```

3. No DTO change. The extracted name flows through `meta.Akcia` into
   `ParsedDeliveryList.AkciaName` (line ~385) exactly as the existing
   `akcia:` value does, which means:

   - The audit/log lines that show "akcia=..." still surface it.
   - The auto-matcher picks it up as the first candidate
     (`("akcia", p.AkciaName)`).
   - The manager-facing review screen shows it in the same place as
     a real `akcia:`. We **do not** label it differently — the
     manager doesn't need to know which regex won.

### Per-supplier behaviour after the change

| Supplier   | `akcia:` field | Result before | Result after          |
|------------|----------------|---------------|-----------------------|
| DEK        | populated      | matched       | matched (no change)   |
| MAPEI SK   | sometimes null | unmatched     | matched via Pozn.DL   |
| OBI / Hornbach (TBD) | varies | varies   | unchanged (no `stavba` in their notes) |

If a future supplier uses yet another label (e.g. `objekt: <name>`,
`zákazka: <name>`), the same one-line `if (string.IsNullOrEmpty(akcia))`
chain extends naturally with another regex. We add suppliers as we
see them, not speculatively.

### What "done" looks like for item B

- Re-uploading the MAPEI invoice that prompted this feedback produces
  a `MaterialPurchase` with `AkciaName = "Bratislava"` (assuming a
  Location named "Bratislava" exists; if not, behaviour is unchanged
  but the auto-match log line now reads "matched via akcia"-flavoured
  text, making the no-match cause clearer in logs).
- The DEK acceptance test from `INVOICE_SCANNING_PLAN.md` still
  produces the same 11 delivery lists / 31 lines and reconciles to
  `1 788,43 €`. (Existing `akcia:` extraction must not regress.)
- Unit test added next to wherever `InvoiceParser` is tested today
  (or a new one if there isn't one): given a synthetic per-DL body
  with `Pozn.DL: - stavba TestSite - p. X`, the returned `DlMeta`
  has `Akcia == "TestSite"`. Given a body with both `akcia:` and
  `stavba`, `Akcia` equals the `akcia:` value (no overwrite).
- `dotnet build` clean.

---

## Item C — Services (rentals) show up in Pracovisko `Spotreba materiálu`

### What the manager reported

A MAPEI invoice contained the rental line `PN10010 Prenájom -
Iskrový detekčný prístroj` (1 × 85,00 € = 85,00 €). The manager set
that delivery list's Pracovisko to `Bratislava` on the review screen
and committed the invoice. The MAPEI Keraflex line on a different
delivery list flowed through to Bratislava's `Spotreba materiálu`
correctly; the rental did not. The manager expected both.

### Why it didn't show up

`AutoPromoteAndCreateUsagesAsync` in `InvoicesController.cs` (line
~818) explicitly skips service lines when minting `MaterialUsage`
rows on commit:

```csharp
foreach (var line in purchase.Lines)
{
    if (line.IsService) continue;          // ← skip rentals
    if (line.Quantity <= 0) continue;      // ← skip credit notes
    if (string.IsNullOrWhiteSpace(line.MaterialNameRaw)) continue;
    ...
    var usage = new MaterialUsage { ... };
}
```

The comment above the method states the design rationale: *"Services
are skipped — they don't represent physical inventory."* That
rationale stood for V1; the open question #6 in
`INVOICE_SCANNING_PLAN.md` flagged it for customer follow-up. The
customer follow-up is this item.

### Change

Two coordinated changes:

**1. Stop skipping services on commit.** In
`AutoPromoteAndCreateUsagesAsync` (line ~841), drop the
`if (line.IsService) continue;` guard. Keep the other two guards
(`Quantity <= 0` for credit notes, empty `MaterialNameRaw`) — those
are about data quality, not about the service/material split.

The find-or-create Material catalogue step underneath then creates a
catalogue row for `Prenájom - Iskrový detekčný prístroj` exactly the
way it creates one for `MAPEI Keraflex Quick S1 23kg šedý`. This is
fine — a "service catalogue row" is the same record shape with a
made-up unit (`x`, `ks`, whatever the invoice printed). The
catalogue stays clean because the same find-or-create dedups across
invoices.

**1b. Also drop the service skip from the post-commit Pracovisko-
change handler.** The PUT `/api/invoices/{id}/delivery-lists/{purchaseId}`
endpoint (around line 689 of `InvoicesController.cs`) handles three
cases when the manager moves a committed delivery list:

- **Move to Sklad** (LocationId → null): deletes existing usages.
  No change needed — service usages get cleaned up like material
  usages.
- **Move to a Pracovisko and usages already exist**: updates
  `LocationId` on the existing rows. No change needed — once Item C
  is live, service usages exist and move like material usages.
- **Move to a Pracovisko and no usages exist** (typical when the
  delivery list was Sklad at commit time): the handler mints fresh
  usages from the lines. **This branch has its own copy of the
  service skip** (line ~720):

  ```csharp
  foreach (var line in purchaseWithLines.Lines)
  {
      if (line.IsService || line.Quantity <= 0) continue;   // ← drop IsService
      ...
  }
  ```

  Drop `line.IsService` from that condition too. Keep
  `line.Quantity <= 0` as before. Both code paths now have the same
  shape, which is what we want: a service line behaves like a
  material line for the purpose of `MaterialUsage` lifecycle —
  created on assignment, moved on re-assignment, deleted on
  un-assignment back to Sklad.

The manager's described behaviour ("take it away from any Pracoviska
that had it assigned previously and add it to the newly assigned
Pracovisko") matches the existing move-existing-usages branch
exactly — that branch already does this for materials. Dropping the
service skip in the mint-fresh branch is what makes it work the
same way for services that were assigned to Sklad at commit time and
re-routed later.

**2. Surface `IsService` on the read side so the UI can badge it.**
Add one column to `MaterialUsage`:

```
IsService           bool           NOT NULL  DEFAULT FALSE
```

Self-heal block in `Program.cs` per Migration Safety Rule 3. Set it
on creation inside `AutoPromoteAndCreateUsagesAsync` from
`line.IsService`. All historical usages remain `false` (correct;
those were materials, not services). Manual kiosk-nákup pseudo-rows
also stay `false`.

The DTO carries it through:

```csharp
public sealed class MaterialUsageDto
{
    ...
    public bool FromPurchase   { get; set; }
    public int? PurchaseId     { get; set; }
    public bool IsService      { get; set; }   // ← new
}
```

In `BuildUnifiedMaterialEntriesAsync` (line ~508):

- The `usageRows` projection sets `IsService = u.IsService`.
- The synthesised `purchaseRows` projection sets
  `IsService = l.IsService` (the join already includes `Lines`).

### Frontend (`location-detail.page.html`)

Extend the badge block around line 114 by adding a fourth branch
**before** the `Faktúra` branch (more specific test wins):

```html
@if (m.fromPurchase) {
  <span class="inline-block px-2 py-0.5 rounded-full bg-blue-100 dark:bg-blue-900/40 text-blue-700 dark:text-blue-300">Z nákupu</span>
} @else if (m.isService) {
  <span class="inline-block px-2 py-0.5 rounded-full bg-purple-100 dark:bg-purple-900/40 text-purple-700 dark:text-purple-300">Faktúra (služba)</span>
} @else if (m.note?.startsWith('Faktúra')) {
  <span class="inline-block px-2 py-0.5 rounded-full bg-amber-100 dark:bg-amber-900/40 text-amber-700 dark:text-amber-300">Faktúra</span>
} @else {
  <span class="inline-block px-2 py-0.5 rounded-full bg-slate-100 dark:bg-slate-700 text-slate-600 dark:text-slate-300">Ručne</span>
}
```

The TS-side `MaterialUsage` interface in `material.service.ts` gains
`isService?: boolean`. Default-undefined treats older API responses
as material, which is the safe fallback during the deploy window.

Mn. column note: the rental in the example screenshot shows
`Mn. 1 ks` with `Cena 0,00` and `Spolu 85,00`. The Spotreba table's
`formatQty(m.quantity) {{ m.unit }}` will render `1 x` (the unit was
`x`). That's correct — it reflects what the invoice said. We do not
re-engineer the rental display in this follow-up; if it confuses the
manager later, treat it as a separate polish item.

### Backfill for already-committed invoices

`AutoPromoteAndCreateUsagesAsync` is already idempotent: it tracks
`alreadyTaggedSet` of `SourceMaterialPurchaseLineId` values and
skips lines whose usage already exists. After the
`if (line.IsService) continue;` line is removed, simply re-running
the method against every committed invoice will pick up the
previously-skipped service lines and leave material lines alone.

Add a one-shot superadmin endpoint:

```
POST /api/invoices/backfill-service-usages
     [Authorize] + [RequireSuperAdmin]
     Iterates every InvoiceDocument with Status == "committed",
     calls AutoPromoteAndCreateUsagesAsync(doc.Id), returns
     { invoicesProcessed, usagesCreated, durationMs }.
```

Run once after the deploy lands; the response tells the operator
how many usages were back-created. Idempotent — running it twice is
a no-op on the second call. Lives in `InvoicesController.cs` next
to the existing commit handler. No background queue, no scheduling
— the customer's full dataset is small enough that a sync call
finishes well inside the 60s request limit.

After the backfill confirms, the endpoint can stay in the codebase
or be deleted in a later cleanup. Leaving it doesn't cost anything;
it's superadmin-gated.

### What "done" looks like for item C

- The MAPEI rental on the offending invoice shows up in Bratislava's
  Spotreba materiálu with a `Faktúra (služba)` purple badge,
  alongside the MAPEI Keraflex with its existing `Faktúra` amber
  badge.
- Moving the rental's delivery list from Bratislava to a different
  Pracovisko (Devinska, say) on the review screen removes it from
  Bratislava's Spotreba and adds it to Devinska's. Moving it back to
  Sklad (the `— Sklad / Inventár —` option) deletes the usage; the
  rental still appears on the invoice itself (and on the admin
  Nákupy page if that flag is on) but vanishes from any Pracovisko's
  Spotreba.
- DEK acceptance test from `INVOICE_SCANNING_PLAN.md` still
  reconciles to `1 788,43 €`. No material lines from DEK accidentally
  get the service badge.
- `MaterialUsage.IsService` column exists after migration; self-heal
  block fires once on first boot in dev with no warning on the
  second boot.
- Superadmin `POST /api/invoices/backfill-service-usages` returns
  `usagesCreated > 0` on first run against the live DB (assuming at
  least one rental was committed pre-fix). Returns
  `usagesCreated == 0` on a second run.
- Manual kiosk-nákup entries (the "Ručne" badge case) still render
  as before — `IsService` is `false` for those.
- `dotnet build` and `npx tsc --noEmit -p tsconfig.app.json` clean.

---

## Item D — Archive (soft delete) + permanent delete for committed invoices

### What the manager reported

The current `DELETE /api/invoices/{id}` button hard-removes the
`InvoiceDocument`, its `MaterialPurchases`, and its `Lines`, even on
committed invoices. The Cloudinary PDF stays. That's too destructive
as a default — a manager who clicks Delete on a wrong invoice loses
the parsed data permanently and has to re-upload the PDF and
re-confirm every line.

The customer wants a two-action lifecycle:

- **Archivovať** (Archive, soft delete): the invoice is removed from
  Spotreba materiálu and Nákupy, but stays viewable in `/admin/invoices`
  under an `archived` status, with the Cloudinary PDF intact. Used
  for "this was committed by mistake" or "we no longer need this in
  active reports". Recoverable.
- **Vymazať natrvalo** (Permanent delete): hard-removes everything
  including the Cloudinary PDF. Used rarely, when Cloudinary storage
  costs grow and the manager wants to clean up old archived
  invoices. Not recoverable.

### Current behaviour (verified in code)

`InvoicesController.cs` line ~910:

```csharp
[HttpDelete("{id}")]
public async Task<IActionResult> Discard(int id)
{
    ...
    foreach (var p in doc.Purchases)
    {
        _db.MaterialPurchaseLines.RemoveRange(p.Lines);
    }
    _db.MaterialPurchases.RemoveRange(doc.Purchases);
    _db.InvoiceDocuments.Remove(doc);
    await _db.SaveChangesAsync();
    return NoContent();
}
```

Allowed for every status. Cloudinary PDF untouched. **Does not touch
`MaterialUsages`** — for committed invoices this is technically a
dangling-FK bug (the `SourceMaterialPurchaseLineId` on usage rows
points at lines that no longer exist). Item D fixes that incidentally
on the hard-delete path.

`IBlobStorageService.DeleteAsync(string url, string folder)` already
exists (`BlobStorageService.cs` line 22). The hard-delete path uses it.

### Schema

Two additive bool columns. Generate via CLI per Migration Safety
Rule 1:

```
cd API
dotnet ef migrations add AddInvoiceArchiveFlags
```

Self-heal blocks in `Program.cs` for both columns per Rule 3.

```
MaterialPurchase
  IsArchived          bool           NOT NULL  DEFAULT FALSE
  INDEX (IsArchived) WHERE IsArchived = TRUE   -- small partial index

MaterialUsage
  IsArchived          bool           NOT NULL  DEFAULT FALSE
  INDEX (IsArchived) WHERE IsArchived = TRUE
```

`InvoiceDocument` already has a `Status` column with values
`parsing | review | committed | discarded`. **Add a fifth value:
`archived`.** No new column on `InvoiceDocument`; the status enum
carries it. The existing `discarded` value stays unused after this
ships (pre-archive drafts that were never committed will now skip
straight to permanent delete from the review page — same UX).
Decision: leave `discarded` in the enum for back-compat with rows
that already have it; new writes never produce it.

### Lifecycle

```
review  ──Commit──▶  committed  ──Archive──▶  archived  ──Delete──▶ (gone)
                          ▲                       │
                          └──────── Restore ──────┘
```

- `review` → `archived` is **not allowed**. Drafts that haven't been
  committed go through the existing `Discard` path (now renamed
  "Vymazať" in the UI; same hard-delete behaviour, since there's no
  child data worth preserving on a draft).
- `committed` → `archived` is the new soft-delete.
- `archived` → `committed` is restore.
- `archived` → gone is the permanent-delete escape hatch.
- `committed` → gone (directly) is **not allowed**. Manager must
  archive first. Two clicks beat one regret.

### Endpoints

Replace the single `[HttpDelete("{id}")]` with three actions. Keep
the path stable for the draft case so the existing frontend
review-page Discard button keeps working.

```
POST   /api/invoices/{id}/archive
       Manager only. Allowed only when Status == 'committed'.
       1) Find all MaterialPurchases for this InvoiceDocument; set
          IsArchived = true on each.
       2) Find all MaterialUsages whose SourceMaterialPurchaseLineId
          belongs to any line of those purchases; set IsArchived = true.
       3) Set InvoiceDocument.Status = 'archived',
          ArchivedBy = User.Identity?.Name, ArchivedAt = UtcNow.
       Idempotent: re-archiving is a 409 with "Faktúra je už
       archivovaná." (frontend hides the button when archived).

POST   /api/invoices/{id}/restore
       Manager only. Allowed only when Status == 'archived'.
       1) Flip IsArchived = false on all MaterialPurchases and
          MaterialUsages tied to this InvoiceDocument.
       2) Set InvoiceDocument.Status = 'committed', clear ArchivedBy
          and ArchivedAt.
       Idempotent: restoring a committed invoice is a 409.

DELETE /api/invoices/{id}
       Manager only. Behaviour now depends on Status:
       - 'review' (draft):  hard-delete (existing behaviour, no
                            Cloudinary touch — drafts never minted
                            usages, and we kept the PDF for audit
                            even on drafts in V1). UI confirmation
                            wording stays as-is.
       - 'archived':        hard-delete + remove the Cloudinary PDF
                            via _blob.DeleteAsync(doc.PdfUrl,
                            "invoices/{year-month}"). Cascade-remove
                            MaterialUsages tied to this invoice's
                            line ids, then MaterialPurchaseLines,
                            MaterialPurchases, InvoiceDocument
                            (mirroring the current Discard but with
                            the usage cleanup added). Stronger UI
                            confirmation ("Naozaj zmazať natrvalo?
                            Súbor sa odstráni aj z Cloudinary.").
       - 'committed':       409 with "Najprv archivujte faktúru."
                            Forces the two-step.
```

`ArchivedBy varchar(100) NULL` and `ArchivedAt timestamp NULL` are
two more additive columns on `InvoiceDocument` (mirror the existing
`UploadedBy/At` and `CommittedBy/At` pattern). Same migration as the
other two flags.

### Read-side filtering

Three places need to filter on `IsArchived`:

1. **`LocationsController.BuildUnifiedMaterialEntriesAsync`** (item
   A change is in the same area):

   ```csharp
   var uq = _db.MaterialUsages
       .Include(u => u.Material)
       .Include(u => u.Employee)
       .Where(u => u.LocationId == locationId
                && !u.IsArchived);          // ← new

   var pq = _db.MaterialPurchases
       .Include(p => p.Employee)
       .Include(p => p.Lines).ThenInclude(l => l.Material)
       .Where(p => p.LocationId == locationId
                && !p.IsArchived);          // ← new
   ```

2. **`MaterialPurchasesController`** Nákupy list. Find every place
   that queries `MaterialPurchases` for the admin Nákupy view and
   add `&& !p.IsArchived`. Don't change the controller's edit
   endpoints — those operate by id on archived rows too, because
   restoring an archived invoice needs the children to still be
   editable through their normal endpoints. (Pre-restore edits to an
   archived purchase aren't a thing the UI can trigger, since the
   Nákupy page doesn't show them.)

3. **`/admin/invoices` list** — keep showing all statuses by default;
   the existing status pill already differentiates `archived` from
   `committed`. The filter dropdown gets `archived` as a fifth
   option. The list is the only place archived invoices are visible
   on purpose.

`InvoicesController` GET endpoints (`list`, `{id}`, the review
screen) **do not** filter on archived status — managers need to see
archived invoices to restore them or delete them permanently. The
review page renders read-only when `Status == 'archived'` (same flag
the review page already checks for `committed`).

### Frontend

`invoices.page.html` list page:

- Status pill `Archivovaná` (grey-purple, e.g.
  `bg-purple-200 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300`).
- Filter dropdown gains `Archivovaná` option.
- Per-row actions on archived invoices: `Obnoviť` (restore) and
  `Vymazať natrvalo` (permanent delete).

`invoice-review.page.html`:

- When `Status == 'committed'`: existing `[Zahodiť]` button changes
  copy to `[Archivovať]`. Same red-confirm modal, different wording
  ("Archivovať faktúru? Materiál sa stratí zo Spotreby a Nákupov,
   ale faktúra bude stále viditeľná v Faktúrach.").
- When `Status == 'archived'`: editable form goes read-only. Sticky
  footer shows two buttons: `[Obnoviť]` and `[Vymazať natrvalo]`.
  The permanent-delete modal copy: "Naozaj zmazať natrvalo? Faktúra
  aj jej PDF sa odstránia. Nedá sa vrátiť späť."
- When `Status == 'review'`: existing draft behaviour unchanged.
  Button stays `[Zahodiť]`, hard-delete on confirm.

Slovak strings (manager-facing):

```
"Archivovať"
"Obnoviť"
"Vymazať natrvalo"
"Archivovaná"
"Archivovať faktúru? Materiál sa stratí zo Spotreby a Nákupov, ale faktúra bude stále viditeľná v Faktúrach."
"Naozaj zmazať natrvalo? Faktúra aj jej PDF sa odstránia. Nedá sa vrátiť späť."
"Najprv archivujte faktúru."
"Faktúra je už archivovaná."
```

### Restore correctness

Restore flips the `IsArchived` flags back and the `Status` back to
`committed`. The rows themselves were not touched during archive —
they were merely hidden — so:

- `Spotreba materiálu` and `Nákupy` see the rows again exactly as
  before.
- `MaterialUsage` records carry their original
  `SourceMaterialPurchaseLineId`, `LocationId`, and totals.
- No re-parse from `RawOcrJson` is required (that path is more
  complex and risks dropping the manager's edits — avoided by the
  soft-delete approach).
- Reconciliation isn't recomputed; it was passing when the invoice
  was committed and nothing changed.

If the manager renamed a `Material` (e.g. promoted "Cemnt" → "Cement")
while the invoice was archived, the restored usages point at the
canonical material via `MaterialId` and pick up the new name on read
— no special handling needed.

If the manager deactivated the `Location` while the invoice was
archived (`Location.IsActive = false`), restored `MaterialUsage`
rows still reference it by id; the Pracovisko detail page is a
read-by-id call that doesn't filter by `IsActive`, so the data is
visible. The Pracovisko's own visibility in the admin Pracoviská
list is governed by `IsActive`, unchanged.

### What "done" looks like for item D

- Migration `AddInvoiceArchiveFlags` adds two `IsArchived` columns
  and two new `InvoiceDocument` columns (`ArchivedBy`, `ArchivedAt`);
  self-heal blocks fire once and stay quiet thereafter.
- A committed invoice:
  - `POST /api/invoices/{id}/archive` → 204; invoice's
    `MaterialPurchases`, `Lines`, and `MaterialUsages` no longer
    appear in any Pracovisko's Spotreba materiálu or in the Nákupy
    list. Invoice still appears in `/admin/invoices` with the
    `Archivovaná` pill.
  - `POST /api/invoices/{id}/archive` again → 409 ("Faktúra je už
    archivovaná").
  - `POST /api/invoices/{id}/restore` → 204; same Spotreba and
    Nákupy rows reappear unchanged. Status flips back to
    `committed`.
- An archived invoice:
  - `DELETE /api/invoices/{id}` → 204; Cloudinary PDF is gone (HEAD
    of the stored URL returns 404), database row and all children
    are gone.
- A committed invoice:
  - `DELETE /api/invoices/{id}` → 409 ("Najprv archivujte faktúru.").
- A draft (review) invoice:
  - `DELETE /api/invoices/{id}` → 204; behaviour identical to today.
- DEK acceptance test from `INVOICE_SCANNING_PLAN.md` still produces
  the same 11 delivery lists / 31 lines / `1 788,43 €` after the
  schema change with no archiving in play.
- `dotnet build` clean. `npx tsc --noEmit -p tsconfig.app.json` clean.

---

## Out of scope of this follow-up

- Loosening the substring direction in `AutoMatchLocationsAsync`
  (matching `akcia=Bratislava` to `Location=Bratislava - Petržalka`).
  Considered and rejected above.
- A "create new Location from this akcia text" affordance on the
  review screen. Useful but a bigger UX change; capture as a separate
  backlog item if the manager keeps hitting it.
- Extending the date-range pattern from Spotreba to Fotogaléria /
  Odpracované hodiny / Diaries. Wait for explicit feedback before
  touching those.
- Renaming `AkciaName` to something supplier-neutral
  (`SiteName`, `ProjectName`). Schema-touch with no visible benefit;
  not worth it.
- Saving the Spotreba range in URL query params for shareable links.
  Nice to have; cheap to add later if asked.
- Splitting services into their own dedicated card on Pracovisko
  detail (was option 2 in the picking-the-direction conversation;
  customer chose to mix in with Spotreba materiálu via badge).
- A separate "rental summary by site" report. Worth doing once the
  customer has more rental history to look at; not in this pass.
- Bulk-archive or bulk-delete from the `/admin/invoices` list. One
  invoice at a time is fine for the current volume. Add if the
  customer ever asks to clear out a whole month.
- An "audit log of who archived what when" report. The
  `ArchivedBy/At` columns capture it; surfacing it as a report can
  wait.
- Replacing the `discarded` status leftover from V1 with `archived`
  retroactively. Old `discarded` rows can stay as-is; nothing reads
  them.

## Notes for the implementation session

- Item A is one Angular page + one C# controller endpoint + one XLSX
  generation method. Under 200 lines net.
- Item B is roughly 12 lines of regex + 5 lines of conditional + one
  unit test. The smallest meaningful change; resist the urge to also
  "clean up" the existing AkciaRx while you're in the file. Per
  `CLAUDE.md` §3 (Surgical Changes).
- Item C is one schema column + one self-heal block + dropping one
  `if` guard + threading `IsService` through one DTO + one HTML
  branch + one backfill endpoint. The backfill endpoint is the
  riskiest piece — make sure it's idempotent and superadmin-gated
  before merging.
- Item D is the biggest of the four: four new columns (two
  `IsArchived` bools + two `Archived*` audit columns), two new
  POST endpoints, a rewritten DELETE handler with three branches,
  and read-side filtering in two controllers. Still a single PR's
  worth of work, but think through the integration with Item C
  before merging — Item D's read-side filter must skip archived
  service usages too, otherwise Item C's badge would render on rows
  that should be hidden.
- All four items can ship independently. Order doesn't matter;
  do whichever the customer is loudest about first. Items B, C and
  D all touch invoice scanning and share little code, so they can
  share a single PR if the implementation session has spare cycles.
  Item A is unrelated and can ship by itself.
- After landing, add a one-line entry to `BACKLOG.md` under
  `Customer call (2026-05-24)` noting the V1 follow-up shipped, with
  a pointer to this file.

---

*End of follow-ups doc.*
