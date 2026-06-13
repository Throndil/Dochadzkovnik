<!--
Writing style: read by AI assistants. Write plainly. No emojis, no "—" as
rhetoric, no exclamation marks, no padding. Bold sparingly. Slovak strings must
read like a normal person typed them. When in doubt, write less.
-->

# Invoice Scanning V2 — context handoff

> Created 2026-06-05. Paste this into a new chat to continue the new-supplier
> invoice work without re-deriving the audit. Read the two referenced docs for
> full detail; this file is the short version plus the exact next step.

## What this work is

The V1 invoice scanner (`InvoiceScanning` feature flag, live) was built and
tuned against one DEK invoice, `FA_2600141367.pdf` (the binding master: 11
delivery lists, 31 lines, reconciles to 1 788,43 EUR). Four more invoices were
supplied to test other supplier formats. They were audited; two parse cleanly,
two need parser work. No production source files were changed in the audit.

## Files produced (already in the repo)

- `INVOICE_SCANNING_V2_NEW_SUPPLIERS.md` — full audit: per-invoice ground
  truth, the verified regex matrix, six recommended fixes.
- `API.Tests/API.Tests.csproj` + `API.Tests/InvoiceParserTextLayerTests.cs` —
  runnable text-layer tests. Fixtures in `API.Tests/fixtures/` are the
  pdftotext output of each invoice. Run: `dotnet test API.Tests/API.Tests.csproj`.
  Not yet added to `Dochadzkovnik.sln` and not yet compiled (see caveats).

## The four test invoices

| File | Real supplier | Format | Reconciles | Verdict |
|---|---|---|---|---|
| FA_2600150614 | Stavebniny DEK s.r.o. | DEK súhrnná | 657,16 € | Parses cleanly |
| FA_2600132372 | Stavebniny DEK s.r.o. | DEK súhrnná | 402,37 € | Parses cleanly |
| az_profistav…20260470 | **HEKTRANS s.r.o.** (AZ Profistav is the buyer) | single-table, € currency | 369,00 € | Header + totals fail on labels |
| SCAN0000 | **BAU-ARTICEL s.r.o.** | single-table, **image-only scan** | 183,49 € | OCR-dependent + same label gaps |

## Key findings

1. The two DEK invoices are clones of the master and need no work. They
   reconcile to the cent.
2. The two non-DEK suppliers break the same way: the parser's SK-specific text
   patterns are written for DEK's exact wording. A supplier that writes
   `Dátum vydania` (not `dátum vyhotovenia`), `Suma na úhradu … €` (not
   `spolu … EUR`), or `Základ DPH … €` (not `cena bez DPH … EUR`) gets null
   header/total fields and falls back entirely to Document AI's generic entities.
3. Latent bug found during replay: `TotalInclVatRx` does not match the text on
   the master or FA_2600132372, and matches the wrong number (`0,00` rounding
   line) on FA_2600150614. Masked in production only because Document AI's
   `total_amount` entity is preferred. The incl-VAT total has no working text
   fallback today.
4. The supplier-name text fallback (`ExtractSupplierFromBlock`) misses on DEK's
   own layout (name sits above the `dodávateľ` label); production relies on the
   Document AI `supplier_name` entity. It does work for HEKTRANS.

## Verified regex matrix (text-layer replay, empty entities)

| Regex | master | 150614 | 132372 | HEKTRANS |
|---|---|---|---|---|
| InvoiceNumberRx | y | y | y | **NO** |
| IcoRx / IcDphRx / IbanRx | y | y | y | y |
| IssueDateRx (vyhotovenia) | y | y | y | **NO** (vydania) |
| DueDateRx / HeaderDelDateRx | y | y | y | y |
| DeliveryListRx (za dodací list) | 13 | 9 | 8 | **0** |
| AkciaRx | y | y | y | **NO** |
| SubtotalRx | 14 | 9 | 9 | **0** |
| TotalExclVatRx (cena bez DPH…EUR) | y | y | y | **NO** |
| TotalInclVatRx (spolu/k úhrade…EUR) | **NO** | wrong(0,00) | **NO** | **NO** |
| LinePricesRx (5-col) | 24 | 24 | 21 | **0** |

SCAN0000 is image-only (no text layer); its row is n/a until Document AI OCR.

## Recommended fixes (additive, do not change DEK behaviour)

1. `IssueDateRx`: accept `dátum\s+(?:vyhotovenia|vydania)`.
2. `TotalExclVatRx`: add `základ\s+DPH` alternative and accept `€` as well as `EUR`.
3. `TotalInclVatRx`: add `suma\s+na\s+úhradu` and `celkom\s+k\s+úhrade`, accept `€`.
   (Also gives the DEK invoices the incl-VAT text fallback they currently lack.)
4. When no `za dodací list` subtotal exists, derive invoice totals from the
   header summary box instead of leaving them null.
5. Invoice-number regex safety net: first `\d{6,16}` near `Faktúra` or
   `Variab. symb.` when not adjacent.
6. Multi-page scan guard: ignore `line_item` entities on a page with no
   currency/price tokens (stops the SCAN0000 page-2 weighing ticket from
   injecting phantom lines).

Items 1-3 are one-line regex edits in `API/Services/InvoiceParser.cs` and are
enough to make HEKTRANS and BAU-ARTICEL headers/totals parse from text alone.
Auto-match algorithm does not need to change.

## Caveats for whoever picks this up

- Tests were validated by replaying the exact regexes in a sandbox; they were
  NOT compiled (no dotnet available there). Run `dotnet test` to confirm the
  project builds before trusting green/red.
- The text fixtures are pdftotext output, a proxy for Document AI's reflowed
  FullText. Line-item extraction needs real Document AI entities, so per-line
  reconciliation for the new suppliers is not unit-tested. To make the tests
  end-to-end, capture each invoice's live Document AI RawJson into
  `API.Tests/fixtures/` and feed it through the parser.
- Docker was failing in the V1 dev environment at the time of this audit;
  backend and frontend were running directly. Not needed for the audit.

## Suggested next step

Implement fixes 1-3 in `API/Services/InvoiceParser.cs`, then flip the
`Hektrans_DocumentsCurrentGaps_FlipWhenLabelsSupported` assertions in
`API.Tests/InvoiceParserTextLayerTests.cs` from `Null` to the expected values
(invoice number 20260470, issue date 31.5.2026, excl 300,00, incl 369,00).
Keep the changes surgical per `CLAUDE.md` §3.
