<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to workers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Invoice Scanning (Naskenovať faktúru) — Plan & Reference

> Created 2026-05-25 from the 2026-05-24 customer call + a real-world
> reference PDF supplied 2026-05-25 (`FA_2600141367.pdf`,
> Stavebniny DEK, EUR 1 788,43).
> Status: **Not started. This file is the brief for the implementation session.**
>
> This file scopes three items in `BACKLOG.md` §Customer call (2026-05-24)
> as one coherent feature: "Receipt (blocek) OCR", "PDF / multi-photo
> document scanning", and "Auto-fill current price from the most recent
> receipt". They are bundled because they share the same OCR pipeline and
> the same review/commit surface.

## Read first

1. `PROJECT_NOTES.md` — Core Data Models, Migration Safety Rules, the
   two-surface architecture (kiosk no-JWT vs. admin JWT). This feature is
   **admin-only** (JWT) — the kiosk never sees the upload UI or the parsed
   results. The admin Záznamy / Materiál layer stays unchanged.
2. `MATERIAL_PURCHASES_PLAN.md` — existing `MaterialPurchase` /
   `MaterialPurchaseLine` schema, the per-purchase Cloudinary photo
   pattern, and the feature-flag wiring template. This plan extends both
   tables (new columns) and adds one parent table for the source document.
3. `BACKLOG.md` §Customer call (2026-05-24) — the three items being
   scoped here.
4. The example invoice itself: `FA_2600141367.pdf` (3 pages, 11 delivery
   lists, EUR 1 788,43). **This document is the V1 acceptance test.** The
   numbers below are taken from it verbatim — any implementation that
   does not reproduce them to the cent is broken.

## What the customer asked for

From the 2026-05-25 conversation:

- Manager scans or uploads a supplier invoice (PDF or photo).
- System reads every line: material name, quantity, unit price, VAT,
  discount, line total.
- Each line shows a button to assign it to a `Location` (existing
  construction site) or to "Inventár / sklad" (general stock,
  `LocationId = NULL`).
- **Manager-only.**
- **Tool must be perfect — it will be used for finances.** The customer
  worded this as the binding accuracy constraint. Read this whole
  document with that in mind. Where speed and accuracy conflict in V1,
  pick accuracy.

## What the example invoice actually contains (grounding the design)

The supplied `FA_2600141367.pdf` is a `Súhrnná faktúra` (summary
invoice). Its structure dictates the schema and the review UX:

- **3 pages, 11 delivery lists.** Not one flat line-item table.
- **Each delivery list (`za dodací list DL-100-26-XXXXXX`) carries
  its own metadata header:** date, pickup person (`prevzal:`),
  pickup method (`spôsob dopravy:`), free-text `Pozn.DL:`, and most
  importantly `akcia: <site name>`. The `akcia` field IS the
  construction site — `Devinska`, `Alzbetin Dvor`, `Čierna Voda`,
  `Palenisko`, `uzbecka fontana`, `Bratislava`, `.` (one is a single
  dot meaning unassigned). **The supplier has already pre-categorized
  every line by site. Our OCR job is to keep that grouping intact,
  not to recover it.**
- **One scanned PDF → N MaterialPurchases.** One MaterialPurchase per
  delivery list. The 11 delivery lists in the example produce 11
  records (or 10, if the manager merges the `Alzbetin Dvor` lists
  manually — out of scope V1).
- **Each line has 9 fields:** supplier code (e.g. `1681011188`),
  description, quantity (`30,00000`), unit (`bal.` / `ks` / `doska` /
  `rol` / `x`), list price excl. VAT (`cena MJ cenník bez DPH`),
  discount % (`zľava`), discounted unit price excl. VAT
  (`cena MJ po zľave bez DPH`), discounted unit price incl. VAT
  (`cena MJ po zľave s DPH`), and line total excl. VAT
  (`cena spolu bez DPH`). Our existing `MaterialPurchaseLine` only
  has 5 of these.
- **VAT rate is per delivery-list, not per line.** Most delivery lists
  are 23%, but two on this invoice are mixed 23% + 0% reverse-charge
  (`** Prenesenie daňovej povinnosti`) for steel mesh products
  (`KR KH 20`). **This is tax-significant.** Our schema currently
  has no VAT field anywhere. We need `VatRate` and `IsReverseCharge`
  on each line.
- **Special line types exist:**
  - Rentals (`PN10010 Prenájom - Vibračná doska ...`) with `qty × days`
    layout (`14,00 x 22,00` = 14 units × EUR 22/day).
  - Negative-quantity credit notes (`PS01090 Zľava z prenájmu` with
    `-1,00 x 0,00 ... -175,00`).
  - Atypical / non-returnable items (`*` marker).
  - Reverse-charge items (`**` marker, 0% VAT).
- **Slovak number format:** comma decimal (`40,39`), space thousands
  separator (`1 788,43`), percent with comma (`38,00 %`). Document AI
  returns numbers in en-US by default; the backend must normalize.
- **The grand total reconciles to the cent:**
  - `cena bez DPH` = `1 507,63 EUR`
  - `základ DPH 23%` = `1 220,75 EUR`
  - `DPH 23%` = `280,80 EUR`
  - `základ DPH 0% **` = `286,88 EUR`
  - `DPH 0%` = `0,00 EUR`
  - `spolu` = `1 788,43 EUR`
  - **`1 220,75 + 280,80 + 286,88 + 0,00 = 1 788,43`** — the
    reconciliation invariant.
  - Sum of all 11 delivery-list `cena spolu bez DPH` totals also
    equals `1 507,63`.

## Design decisions locked in this session

These are answered. Do not relitigate during implementation.

- **(a) OCR provider: Google Document AI, Invoice Parser processor.**
  Pre-trained for invoices, native Slovak support, returns line items +
  VAT + totals with per-field confidence scores. ~EUR 0.07/page,
  EU residency available. Service account JSON stored in Railway env as
  `Google__DocumentAi__CredentialsJson` (single line, escaped) +
  `Google__DocumentAi__ProcessorId` + `Google__DocumentAi__Location`.
  Backend uses the official `Google.Cloud.DocumentAI.V1` NuGet.

- **(b) Reconciliation is mandatory and blocking.** The save button on
  the review page is disabled until the sum of extracted line totals +
  extracted VAT equals the printed `spolu` to the cent. The manager
  edits failing lines until it balances. No "I acknowledge" escape
  hatch in V1. Per the customer's "must be perfect for finances"
  instruction, this is the single most important guardrail.

- **(c) Manager-only access.** Any user with a JWT (i.e. `vladosroka`
  or `admin` superadmin in the current seed). The kiosk surface is
  completely untouched by this feature.

- **(d) One PDF → N MaterialPurchases.** Each `dodací list` block in
  the PDF maps to one MaterialPurchase. Delivery-list metadata
  (`prevzal`, `Pozn.DL`, delivery date) is captured on the
  MaterialPurchase. The `akcia` field is auto-mapped to a Location by
  case-insensitive name contains, with the manager confirming the
  mapping on the review page before commit.

- **(e) Original PDF is preserved immutably.** Uploaded to Cloudinary
  under `invoices/{year-month}/{invoiceNumber}-{guid}.pdf` and the URL
  is stored on the new `InvoiceDocument` row. The OCR result (raw JSON
  from Document AI) is also stored alongside, so a future re-parse or
  audit can replay the source.

- **(f) New schema columns** to carry the data the existing tables
  lose: see §Schema below.

- **(g) Feature flag `InvoiceScanning`.** Same pattern as
  `Notifications` / `CommanderIntegration` / `MaterialPurchases` /
  `ProofOfWorkChoices` / `PayrollAndPnL`. Default OFF in prod.
  Superadmin flips it on per environment after demo.

- **(h) Slovak number normalization on the backend.** Document AI
  returns en-US numerics (`40.39` / `1234.5`). A `SlovakNumberHelper`
  parses both formats; outbound numbers go through a Slovak culture
  formatter when surfaced to the manager.

- **(i) Audit trail on every edit.** Each PUT on a line stores
  `EditedBy / EditedAt / OldValue / NewValue` in a JSON column on the
  line row. No separate audit table in V1 — keeps the migration small.
  The original OCR JSON is the canonical "what the document said".

- **(j) Catalogue mapping is admin-driven, not auto.** Supplier code
  and raw description are kept verbatim on the line
  (`SupplierItemCode`, `MaterialNameRaw`). Linking the line to a
  `Material` catalogue row happens later, via the existing admin
  "Neidentifikované" promotion flow scoped in
  `MATERIAL_PURCHASES_PLAN.md`. This plan does not extend that flow.

- **(k) Rentals and discounts are MaterialPurchaseLines with type
  markers.** `IsService` boolean (true for `Prenájom`), negative
  `Quantity` allowed for credit-note rows. No separate `RentalLine`
  table in V1.

## Schema

Generate the migration via the CLI per Migration Safety Rule 1:

```
cd API
dotnet ef migrations add AddInvoiceScanning
```

PostgreSQL self-heal blocks for the new table AND each new column in
`Program.cs` per Rule 3.

### `InvoiceDocument` — one scanned PDF, header data, OCR result, reconciliation state

```
Id                  int            PK
InvoiceNumber       varchar(100)   NOT NULL    -- supplier's invoice number (e.g. "2600141367")
SupplierName        varchar(200)   NOT NULL
SupplierIco         varchar(50)    NULL        -- e.g. "43821103"
SupplierIcDph       varchar(50)    NULL        -- e.g. "SK2022484849"
SupplierIban        varchar(50)    NULL
IssueDate           date           NOT NULL    -- dátum vyhotovenia
DeliveryDate        date           NULL        -- dátum dodania (header-level)
DueDate             date           NULL        -- dátum splatnosti
PeriodFrom          date           NULL        -- obdobie plnenia od
PeriodTo            date           NULL        -- obdobie plnenia do
Currency            varchar(3)     NOT NULL  DEFAULT 'EUR'
TotalExclVat        decimal(14,2)  NOT NULL    -- cena bez DPH; reconcile invariant
TotalVat            decimal(14,2)  NOT NULL    -- sum of VAT amounts
TotalInclVat        decimal(14,2)  NOT NULL    -- spolu / k úhrade; reconcile invariant
PdfUrl              varchar(1000)  NOT NULL    -- Cloudinary URL of the original PDF
RawOcrJson          text           NOT NULL    -- full Document AI response, preserved verbatim
Status              varchar(30)    NOT NULL    -- 'parsing' | 'review' | 'committed' | 'discarded'
ReconciliationOk    bool           NOT NULL  DEFAULT FALSE
ReconciliationNote  varchar(500)   NULL        -- e.g. "lines 1 220,75 + VAT 280,80 + base 0% 286,88 = 1 788,43, matches printed 1 788,43"
UploadedBy          varchar(100)   NOT NULL    -- username from JWT
UploadedAt          timestamp      NOT NULL
CommittedBy         varchar(100)   NULL
CommittedAt         timestamp      NULL
Note                varchar(2000)  NULL        -- free text the manager can add

UNIQUE  (InvoiceNumber, SupplierIco)   -- dedup: same invoice twice = error
INDEX   (Status)
INDEX   (UploadedAt)
INDEX   (SupplierName, IssueDate)
```

### `MaterialPurchase` — new columns

```
InvoiceDocumentId   int?           FK -> InvoiceDocuments(Id) ON DELETE SetNull
                                   -- non-null when this purchase was produced by an invoice scan
DeliveryNoteRef     varchar(100)   NULL        -- e.g. "DL-100-26-015474"
PickedUpBy          varchar(200)   NULL        -- prevzal: free-text from the PDF
DeliveryNote        varchar(2000)  NULL        -- Pozn.DL: free-text from the PDF
SubtotalExclVat     decimal(14,2)  NULL        -- this delivery list's cena spolu bez DPH
SubtotalVat         decimal(14,2)  NULL        -- this delivery list's DPH (across rates)

INDEX (InvoiceDocumentId)
```

### `MaterialPurchaseLine` — new columns

```
SupplierItemCode    varchar(50)    NULL        -- e.g. "1681011188"
ListPriceExclVat    decimal(12,4)  NULL        -- cena MJ cenník bez DPH (pre-discount)
DiscountPercent     decimal(5,2)   NULL        -- zľava (e.g. 38.00)
UnitPriceInclVat    decimal(12,4)  NULL        -- cena MJ po zľave s DPH (denormalised for display)
VatRate             decimal(5,2)   NOT NULL  DEFAULT 23.00   -- 23.00 | 0.00 etc.
IsReverseCharge     bool           NOT NULL  DEFAULT FALSE   -- ** prenesenie daňovej povinnosti
IsService           bool           NOT NULL  DEFAULT FALSE   -- rentals + non-material services
LineEditHistory     text           NULL        -- JSON array of edit records:
                                                -- [{ field, oldValue, newValue, editedBy, editedAt }]
```

Existing `Quantity` precision (12,3) and existing `UnitPrice` /
`LineTotal` precisions stay as they are — the example invoice's
five-decimal `30,00000` is overprecision (display only), not data.

## Feature flag wiring

Identical to the other five flags already shipped.

- Backend:
  - Add `"InvoiceScanning"` to the `knownFlags` array in `Program.cs`.
  - Apply `[RequireFeatureOrSuperAdmin("InvoiceScanning")]` at the
    class level on the new `InvoicesController`.
- Frontend:
  - `feature-flag.service.ts` — add `invoiceScanning: Signal<boolean>`.
  - `app.routes.ts` — new `/admin/invoices` route + guard.
  - `navbar.component.html` — "Faktúry" link gated by flag-or-superadmin.
  - `account.page.html` — sixth toggle row in the Funkcie superadmin
    card.
- Default state: prod boots off; superadmin flips it on once dev is
  green AND a Google Cloud project + Document AI processor is
  provisioned.

## OCR pipeline (backend)

### Upload + parse

```
POST   /api/invoices/upload                multipart: file (PDF or image)
       Manager only. Stores PDF on Cloudinary under
       invoices/{year-month}/. Creates an InvoiceDocument row with
       Status='parsing'. Returns { id, status }.

       The parse itself runs synchronously in the upload handler in V1:
       call Document AI Invoice Parser, normalize numbers, persist
       RawOcrJson, persist parsed MaterialPurchase + MaterialPurchaseLine
       rows in 'review' status (not yet committed — committed records
       only appear in the admin Nákupy tab once the manager confirms).

       Synchronous is fine in V1: Document AI on a 3-page PDF returns
       in ~3-5 seconds. If invoices grow much larger, promote to a
       BackgroundService later.

GET    /api/invoices                       list, with Status filter
GET    /api/invoices/{id}                  full parsed result with confidence
                                           per field, reconciliation state,
                                           and the PDF URL for inline view
DELETE /api/invoices/{id}                  pre-commit only; deletes the
                                           InvoiceDocument and its draft
                                           MaterialPurchases. Cloudinary
                                           PDF stays for audit (we never
                                           delete uploaded PDFs in V1).
```

### Review edits

```
PUT    /api/invoices/{id}/lines/{lineId}   edit one line. Body: any of
       { description, quantity, unitPrice, lineTotal, vatRate,
         isReverseCharge, isService, supplierItemCode, discountPercent }.
       Server appends an edit record to LineEditHistory and recomputes
       reconciliation. Returns the new reconciliation state.

PUT    /api/invoices/{id}/delivery-lists/{purchaseId}
       Body: { locationId | null, pickedUpBy?, deliveryNote? }.
       Sets the Location for a delivery list. NULL = "Sklad / Inventár".

PUT    /api/invoices/{id}                  edit header (rare —
                                           InvoiceNumber, SupplierIco,
                                           IssueDate). Audit-logged via
                                           a Note field append.
```

### Commit

```
POST   /api/invoices/{id}/commit
       Pre-flight: ReconciliationOk MUST be true (server re-checks; the
       client's UI gate is not trusted). All delivery lists MUST have a
       Location decision (id or explicit "null = sklad").
       On success: Status flips to 'committed', CommittedBy/At stamped,
       the draft MaterialPurchases become real records visible on the
       admin /admin/material-purchases page (which is gated by the
       MaterialPurchases flag — note for the customer: enable both flags
       to see results end-to-end).
       Idempotent: a re-POST on an already-committed invoice is a 409.
```

### Reconciliation rule (server-side, authoritative)

```
sum(MaterialPurchase.SubtotalExclVat)
  + sum(MaterialPurchase.SubtotalVat)
  ?= InvoiceDocument.TotalInclVat

AND every line: round(Quantity * UnitPrice, 2) == LineTotal (allowing
±0.01 for cumulative rounding across the doc).
```

Both checks must pass for `ReconciliationOk = true`. The exact rule
and the actual numbers it computed live in
`InvoiceDocument.ReconciliationNote` so the manager can see what
happened.

## Admin review UX

Route: `/admin/invoices` (list) and `/admin/invoices/{id}/review`
(per-invoice review). Both gated by the `InvoiceScanning` flag.

### `/admin/invoices` — list

Filter by `Status` (default: `review`), date range, supplier. Per row:
invoice number, supplier, issue date, total, status pill
(`Spracováva sa...` / `Na kontrolu` / `Uložená` / `Zahodená`),
reconciliation pill (`Zhoduje sa` / `Nezhoduje sa`).

Button at the top: `Nahrať faktúru` → file picker → upload.

### `/admin/invoices/{id}/review` — the heart of the feature

Two-column layout on `md+`:

- **Left:** PDF preview (PDF.js inline embed of the original Cloudinary URL).
- **Right:** the parsed result, organized by delivery list.

Each delivery list section:

```
┌───────────────────────────────────────────────────────────────┐
│ Dodací list DL-100-26-015474                                  │
│ 19.05.2026 · prevzal Sroka Vladimír                           │
│                                                               │
│ Pre pracovisko: [ Devinska ▾ ]   ← Location picker, auto-     │
│                                    selected from "akcia"      │
│                                                               │
│ ┌──────┬─────────────────────────┬──────┬──────┬──────┬─────┐│
│ │ Kód  │ Popis                   │ Mn.  │ Cena │ DPH  │Spolu││
│ ├──────┼─────────────────────────┼──────┼──────┼──────┼─────┤│
│ │22351…│ DB Polyuretánový tmel…  │ 1 ks │ 5,42 │ 23%  │5,42 ││
│ │22355…│ DB Mamut Glue CRYSTAL…  │ 1 ks │ 8,08 │ 23%  │8,08 ││
│ │ ...  │                         │      │      │      │     ││
│ ├──────┴─────────────────────────┴──────┴──────┴──────┴─────┤│
│ │ Súčet: 62,57 €  DPH: 14,39 €  Spolu s DPH: 76,96 €        ││
│ └───────────────────────────────────────────────────────────┘│
└───────────────────────────────────────────────────────────────┘
```

Per cell behaviour:
- **Editable inline.** Click → input. Blur → PUT → server returns the
  new reconciliation state.
- **Low-confidence highlight.** Document AI confidence < 0.8 → amber
  border on the cell with a small tooltip "OCR si nebol istý".
- **DPH column** is a small dropdown (23 / 20 / 10 / 0). Picking 0 +
  the reverse-charge checkbox stamps `IsReverseCharge = true`.
- **Row delete** (X button) — only allowed pre-commit. Audit-logged.
- **Add row** at the bottom of each delivery list — for the edge case
  where OCR missed one. Audit-logged.

Sticky footer:

```
┌────────────────────────────────────────────────────────────────┐
│ Vytlačené spolu: 1 788,43 €   Naše súčty: 1 788,43 €   ✓ Sedí │
│                                                                │
│ [Zahodiť]                                       [Uložiť všetko]│
└────────────────────────────────────────────────────────────────┘
```

When mismatched: `Naše súčty: 1 786,02 €   ✗ Nesedí (rozdiel 2,41 €)`
in red. `Uložiť všetko` disabled.

### Mobile

Same content stacked. PDF preview moves to a "Pozrieť PDF" button that
opens it inline. Editable rows become cards instead of table rows.
Match the 2026-05-01 tablet pass.

## Site mapping (`akcia` → `Location`)

Auto-mapping on parse:

1. Extract every `akcia: <name>` substring per delivery-list header.
2. For each, do `Location.Name.ToLower().Contains(akciaLower)` and
   reverse (akcia contains location name). Case-insensitive. Diacritics
   stripped on both sides via a `NormalizeForMatch` helper.
3. If exactly one active `Location` matches → auto-select.
4. If multiple or none → leave NULL. The review screen forces the
   manager to pick.

`akcia = "."` → leave NULL.

The manager can always override the auto-pick on the review screen.
The picker also has a "Sklad / Inventár" option that maps to
`LocationId = NULL` (matches the existing `MaterialPurchase.LocationId`
nullable semantics).

## Catalogue mapping (out of this plan)

The supplier item code (`1681011188`) and the description are kept
verbatim on the line. Linking the line to our `Material` catalogue
row is the **existing** flow on the admin
`/admin/materials/neidentifikovane` tab from
`MATERIAL_PURCHASES_PLAN.md`. This plan does not extend that flow.
After commit, scanned lines appear in the Neidentifikované tab same
as kiosk-entered Nákup lines.

## Out of scope (V1)

- Handwritten receipts. V1 targets printed supplier invoices like
  Stavebniny DEK / OBI / Hornbach. Mobile phone photo of a printed
  receipt is fine; handwritten "blocek" is not.
- Multi-currency. EUR only. Matches `MaterialPurchase`.
- Auto-creating Material catalogue rows from extracted lines. Manager
  promotes in the Neidentifikované tab afterwards.
- Confidence-based auto-accept. Every scan goes through manager review
  in V1.
- Re-parsing a previously-uploaded PDF. Each upload is independent.
  Future polish: a `Re-parse` button on the review screen that calls
  Document AI again with the stored PDF (cheap experiment).
- Editing or deleting after commit. Once committed, the records live
  on the admin `/admin/material-purchases` page and are edited there.
  Mistakes after commit = soft-delete + re-scan.
- Push notification when an invoice finishes parsing. Synchronous
  parse in V1 means the upload handler doesn't return until parsing is
  done; the manager is already on the screen.
- Background sweeper for old `parsing`-state rows (V1 = synchronous;
  no abandoned-parse cleanup needed).
- Bulk upload (more than one PDF at once). One-at-a-time in V1.
- Per-supplier OCR templates. Document AI's pre-trained Invoice
  Parser handles supplier variation. If a specific supplier
  consistently fails, the customer asks and we add a per-supplier
  rule.

## Open questions still worth asking the customer

1. **Photo of a receipt vs PDF — both?** V1 default = both. Document AI
   takes both. Confirm that's what they want (vs. PDF-only).
2. **Who can scan?** V1 = any JWT admin (`vladosroka` or `admin`).
   Confirm whether a future "accountant" sub-role should be carved
   out.
3. **"Sklad / Inventár" semantics.** Right now `LocationId = NULL` on
   `MaterialPurchase` means "general stock" per
   `MATERIAL_PURCHASES_PLAN.md`. Confirm the customer wants the same
   semantics here — not a separate `Warehouse` table.
4. **Catalogue promotion timing.** Should the manager be able to
   promote unidentified lines to the catalogue **during** invoice
   review, or only afterwards on the Neidentifikované tab? V1 default
   = afterwards, per `MATERIAL_PURCHASES_PLAN.md`.
5. **VAT rate set.** V1 supports `{0, 10, 20, 23}`. Confirm those are
   the only rates the customer encounters in SK / EU invoices for the
   moment.
6. **Service vs material split.** V1 stores rentals as
   MaterialPurchaseLines with `IsService = true`. The customer may
   want a separate report later ("how much did rentals cost this
   month"). Confirm before that report is in scope.
7. **Duplicate-invoice handling.** UNIQUE `(InvoiceNumber, SupplierIco)`
   in V1 hard-rejects re-uploads. Should re-upload instead overwrite
   the draft (if Status != 'committed')?
8. **Multiple Alzbetin Dvor delivery lists in the example.** Should
   the manager be able to merge two delivery lists for the same site
   into one MaterialPurchase on the review screen, or always keep them
   separate as the PDF has them? V1 default = always separate.

## What "done" looks like for V1

The example DEK invoice is the binding acceptance test:

- Migration `AddInvoiceScanning` generated via CLI; PostgreSQL
  self-heal blocks for the `InvoiceDocuments` table and all 9 new
  columns in `Program.cs`.
- One new EF entity, one new admin controller, two new services on
  the frontend (`InvoiceService` + a tiny `OcrConfidence` helper).
- Google Document AI client wired (`API/Services/IDocumentAiClient.cs`
  + `DocumentAiClient.cs`). Credentials from Railway env per
  `SECRETS.md`. Per-call timeout (60s) and one retry on transient
  failure.
- `/admin/invoices` list page + `/admin/invoices/{id}/review` page
  shipped behind the `InvoiceScanning` flag.
- Uploading `FA_2600141367.pdf` produces:
  - 1 `InvoiceDocument` with `InvoiceNumber = "2600141367"`,
    `SupplierName = "Stavebniny DEK s.r.o."`,
    `SupplierIco = "43821103"`,
    `TotalExclVat = 1 507.63`, `TotalVat = 280.80`,
    `TotalInclVat = 1 788.43`.
  - **11 draft MaterialPurchases**, one per delivery list, with the
    correct `DeliveryNoteRef`, `PickedUpBy`, and `akcia` → `Location`
    auto-mapping where a Location with a matching name exists.
  - **31 MaterialPurchaseLines** across the 11 purchases, with all
    9 fields populated and VAT rates correct (most 23%, two `KR KH 20`
    rows at 0% with `IsReverseCharge = true`).
  - Reconciliation check passes: sum of line totals = `1 507.63`;
    sum of line totals + sum of VAT amounts = `1 788.43`.
- Manager can edit any cell, picker the Location for each delivery
  list, and commit only when reconciliation passes.
- Committed invoice's MaterialPurchases appear on the admin
  `/admin/material-purchases` page (assuming the MaterialPurchases
  flag is also on).
- Original PDF stays on Cloudinary; the URL is reachable from the
  review page; `DELETE` of a draft does not delete the PDF blob.
- `dotnet build` clean. `npx tsc --noEmit -p tsconfig.app.json` clean.
- A pre-flight check on the dev DB after redeploy confirms the new
  table + columns exist and no self-heal warnings fired.

## Migration / deploy order

1. Provision Google Cloud project + Document AI processor (one-time
   operator task — see "Operator setup" below).
2. Add env vars to Railway (`Google__DocumentAi__*`).
3. Deploy with `InvoiceScanning` flag off by default. Self-heal blocks
   add the schema on first boot.
4. Superadmin flips the flag on in dev → run the binding acceptance
   test on `FA_2600141367.pdf` → confirm.
5. Promote to prod with the flag off → superadmin flips on per
   environment after customer sign-off.

## Operator setup (one-time, before first deploy)

1. Create a Google Cloud project (or reuse an existing one — `dek-test`
   or `dochadzkovnik-prod`).
2. Enable the Document AI API.
3. Create a processor: type = `Invoice Parser`, region = `EU`.
4. Copy the processor ID.
5. Create a service account with role `Document AI API User`. Download
   the JSON key.
6. In Railway, set:
   - `Google__DocumentAi__CredentialsJson` = full JSON (single-line,
     escaped — `SECRETS.md` style).
   - `Google__DocumentAi__ProcessorId` = the processor ID from step 4.
   - `Google__DocumentAi__Location` = `eu`.
7. Update `SECRETS.md` with the new env-var slots.

## Notes for the implementation session

- The binding acceptance test in §"What done looks like" is the
  contract. Any line not present, any total off by a cent, any wrong
  VAT rate = broken. Run the test against the real PDF before merge.
- Older-worker UX rules do not apply (admin-only feature).
- Mobile/tablet still — manager may use a tablet to scan + review.
  Match the 2026-05-01 patterns (`.min-h-dvh`, `.touch-target`,
  stacked-card fallback below `md`).
- The reconciliation rule is the single most important code path.
  Server-side authoritative; client UI gate is hint-only.
- Original PDF is immutable. Never overwrite. Never delete.
- Audit trail per line via `LineEditHistory` JSON. No GDPR concern in
  V1 (no PII beyond the supplier's invoice fields).
- `MATERIAL_PURCHASES_PLAN.md` is intentionally untouched. This plan
  extends `MaterialPurchase` + `MaterialPurchaseLine` schemas
  additively; the kiosk-side Nákup flow is unaffected.
- The customer's instruction "must be perfect for finances" is the
  brief. Where a V1 shortcut and finance accuracy conflict, pick
  accuracy and defer the shortcut to V1.1.

---

*End of plan.*
