<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no enthusiastic openings, no padding.
Bold sparingly. Slovak strings shown to managers must read like a normal person
typed them. When in doubt, write less.
-->

# Invoice Scanning V2 — new supplier formats audit

> Created 2026-06-05. Four new invoices were run against the V1 parser
> (`API/Services/InvoiceParser.cs`) to see which formats the DEK-tuned
> pipeline handles and which need work. The binding master remains
> `FA_2600141367.pdf` (DEK, 11 delivery lists, reconciles to 1 788,43 EUR).
>
> Method: one sub-agent per invoice produced a verified ground-truth
> extraction; then the exact `InvoiceParser` text-layer regexes were replayed
> against each invoice's real text. Line-item extraction was not re-tested
> offline because it depends on Google Document AI entities, which require the
> live pipeline. See `API.Tests/` for the runnable text-layer tests.

## Result at a glance

| Invoice | Supplier | Format | Reconciles | Parser verdict |
|---|---|---|---|---|
| FA_2600141367 (master) | Stavebniny DEK s.r.o. | DEK súhrnná | 1 788,43 € | Reference, parses fully |
| FA_2600150614 | Stavebniny DEK s.r.o. | DEK súhrnná | 657,16 € | **Parses cleanly** (clone of master) |
| FA_2600132372 | Stavebniny DEK s.r.o. | DEK súhrnná | 402,37 € | **Parses cleanly** (clone of master) |
| az_profistav…20260470 | **HEKTRANS s.r.o.** | single-table (Doklado/pdfmake) | 369,00 € | **Header + totals fail** on labels; needs fix |
| SCAN0000 | **BAU-ARTICEL s.r.o.** | single-table, **image-only scan** | 183,49 € | OCR-dependent; same label gaps + scan risk |

Two of the four are standard DEK invoices and need no work. Two are different
suppliers with a different invoice layout, and they expose the same root issue:
the parser's SK-specific text patterns are written for DEK's exact wording, so a
supplier that writes `Dátum vydania` instead of `dátum vyhotovenia`, or
`Suma na úhradu … €` instead of `spolu … EUR`, gets null header/total fields and
falls back entirely to whatever Document AI's generic Invoice Parser returns.

## Verified regex matrix

Each cell is the result of replaying the real `InvoiceParser` regex against the
invoice's actual text (empty Document AI entities). "n/a" = image-only scan,
no text layer until Document AI OCR runs.

| Regex | master | 150614 | 132372 | HEKTRANS | SCAN0000 |
|---|---|---|---|---|---|
| InvoiceNumberRx | match | match | match | **NO** | n/a |
| IcoRx | match | match | match | match | n/a |
| IcDphRx | match | match | match | match | n/a |
| IbanRx | match | match | match | match | n/a |
| IssueDateRx (vyhotovenia) | match | match | match | **NO** (vydania) | n/a |
| DueDateRx (splatnosti) | match | match | match | match | n/a |
| HeaderDelDateRx (dodania) | match | match | match | match | n/a |
| PeriodRx | match | match | match | **NO** (none) | n/a |
| DeliveryListRx (za dodací list) | 13 | 9 | 8 | **0** | n/a |
| AkciaRx | match | match | match | **NO** | n/a |
| SubtotalRx (základ DPH…EUR) | 14 | 9 | 9 | **0** | n/a |
| TotalExclVatRx (cena bez DPH…EUR) | match | match | match | **NO** | n/a |
| TotalInclVatRx (spolu/k úhrade…EUR) | **NO** | wrong (0,00) | **NO** | **NO** | n/a |
| LinePricesRx (5-col) | 24 | 24 | 21 | **0** | n/a |

Two things to note even on the DEK invoices:

1. **`TotalInclVatRx` is unreliable.** On the master and FA_2600132372 it did
   not match the text layer at all; on FA_2600150614 it matched the wrong
   number (`0,00`, the rounding line). In production this is masked because the
   parser prefers Document AI's `total_amount` entity. It still means the
   incl-VAT grand total has no working text fallback. Low priority, but real.
2. The DEK clones reconcile to the cent at line level (verified by the
   sub-agents): FA_2600150614 → 559,50 excl + 97,66 VAT = 657,16; FA_2600132372
   → 329,33 excl + 73,04 VAT = 402,37.

## Per-invoice detail

### FA_2600150614 — DEK, parses cleanly
9 delivery lists, 25 product lines, one 0% reverse-charge list (`** KR KH`,
`Prenesenie daňovej povinnosti`). Worksites carried by `akcia:` (Matúšova, Byt
Kalisova, Trnavka, Madej, Rievky). Same column layout as the master. Only
practical exposure: on lists where `akcia:` ends a physical line and the
worksite value starts the next line, `AkciaRx` can capture blank on raw text;
Document AI's reflowed FullText normally merges these, so it usually survives.

### FA_2600132372 — DEK, parses cleanly
8 delivery lists, 21 lines, one mixed 0%+23% subtotal block (DL-110-26-011649).
Simpler than the master: no rentals, no credit lines, no repeated product codes,
so the parser's trickiest paths (`FindNextOffset` duplicate disambiguation,
`InferDuplicateLineTotalsFromSubtotal`) are not even exercised. Reconciles
exactly.

### az_profistav…20260470 — HEKTRANS s.r.o., needs fix
Naming trap: the file is named for AZ Profistav, but **AZ Profistav is the
customer (odberateľ); the supplier is HEKTRANS s.r.o.** The supplier-name block
scan still lands on HEKTRANS correctly because the block is scoped between
`dodávateľ` and `odberateľ`.

This is a single-table invoice (one transport service line, 369,00 € incl).
There is no delivery-list grouping, no `akcia:`, no `základ DPH … EUR | DPH …
EUR` subtotal, no 5-column price block. The parser falls back to one synthetic
segment with no lines, and the following header fields come back null because
the labels differ: invoice number, issue date, total excl VAT, total incl VAT.
Today only Document AI's generic entities would fill those. There is no
worksite, so auto-match would correctly route to Sklad.

### SCAN0000 — BAU-ARTICEL s.r.o., image-only scan
Two-page scanned PDF with no text layer. Page 1 is a clean vector-quality
invoice (2 aggregate lines: štrk 0/4 and makadam 4-8, 183,49 € incl); page 2 is
a faded weighing ticket with no invoice data. Reconciles to the cent. Same
label gaps as HEKTRANS (`Vyhotovenie`/`Splatnosť`/`Dodanie`, `Základ`/`Celkom`,
invoice number top-right), so the SK regexes mostly miss and the parser depends
on Document AI entities. Two extra risks specific to scans: (a) Document AI must
OCR page 1 well — it is clean, so this should be fine; (b) the page-2 weighing
ticket can inject phantom `line_item` entities that `IsLikelyJunkLine` would not
catch (it filters subtotal/amount-only rows, not arbitrary noise).

## Recommended changes (additive, surgical)

All of these are additions to existing regexes. None change DEK behaviour. Per
`CLAUDE.md` §3, resist reworking the existing patterns while editing.

1. **Issue date label.** `IssueDateRx`: accept `dátum\s+(?:vyhotovenia|vydania)`.
2. **Grand total excl VAT.** `TotalExclVatRx`: add a `základ\s+DPH` alternative
   and accept `€` as well as `EUR`.
3. **Grand total incl VAT.** `TotalInclVatRx`: add `suma\s+na\s+úhradu` and
   `celkom\s+k\s+úhrade`, accept `€`. This also gives the DEK invoices a working
   text fallback they currently lack.
4. **Single-rate subtotal / header-total fallback.** When no `za dodací list`
   subtotal exists, derive the invoice totals from the header summary box
   (`Základ DPH` / `Výška DPH` / `Suma na úhradu`) instead of leaving them null.
5. **Invoice number safety net.** Keep relying on Document AI `invoice_id`; as a
   regex fallback for top-right / detached numbers, grab the first `\d{6,16}`
   within N characters of `Faktúra` or `Variab. symb.`.
6. **Scan noise guard.** For multi-page scans, ignore `line_item` entities that
   fall on a page whose text has no currency/price tokens, to stop page-2
   weighing-ticket rows from becoming phantom lines.

Items 1-3 are one-line regex edits each and are enough to make the HEKTRANS and
BAU-ARTICEL headers and totals parse from text alone. Items 4-6 are the
follow-on robustness work. None of this requires touching the auto-match
algorithm.

## What was NOT done here

- No source files were modified. This is an audit plus a test scaffold.
- Line-item reconciliation for the new suppliers was not unit-tested offline
  because it needs Document AI entities. Capture the live processor's RawJson
  for each invoice into `API.Tests/fixtures/` to make the tests end-to-end.
- The image-only scan has no text fixture; only the live OCR path can exercise
  it. The fixtures folder has a note placeholder for it.
