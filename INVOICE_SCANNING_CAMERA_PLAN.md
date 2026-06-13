<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to managers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Invoice Scanning V1.1 — In-app camera capture

> Created 2026-05-28 as the V1.1 follow-up to `INVOICE_SCANNING_PLAN.md`,
> after V1 (file-picker upload of a supplier PDF) shipped and parses the
> reference `FA_2600141367.pdf` correctly end-to-end.
> Status: **Not started. This file is the brief for the next implementation session.**
>
> V1 already accepts a phone photo via the existing file picker
> (`<input type="file" accept="application/pdf,image/*">` on
> `/admin/invoices`). The backend handler at
> `POST /api/invoices/upload` already buffers the bytes, calls Document AI
> with the correct MIME, uploads through the image path (HEIC/PNG/JPEG)
> instead of the raw PDF path, and writes the parsed delivery lists.
> **That path is not what this plan replaces.** It stays as the fallback
> for managers who already have a PDF in hand. This plan adds a second
> entry point: **a real in-app multi-page scanner for the case where the
> manager is standing in front of a paper invoice with only their phone
> or tablet.**

## Read first

1. `INVOICE_SCANNING_PLAN.md` — the V1 plan that's already shipped. The
   schema, the feature flag pattern, the Document AI wiring, the
   reconciliation rule, the review UX, and the binding acceptance test
   on `FA_2600141367.pdf` all stand. **This plan does not relitigate
   any of those decisions.** It only extends the upload entry point.
2. `client/src/app/pages/invoices/invoices.page.{html,ts}` — the existing
   list page with the file-picker upload. The new camera flow lives next
   to the existing `Nahrať faktúru` button, not on top of it.
3. `client/src/app/utils/image-utils.ts` — `normaliseFile()` (HEIC → PNG),
   `compressImage()` (long-edge resize + JPEG re-encode), and
   `fileToDataUrl()`. Reuse these. Do not duplicate.
4. `client/src/app/pages/kiosk/kiosk.page.html` lines 717, 1344, 1526 and
   `client/src/app/components/nakup-flow/nakup-flow.component.html` line
   275 — the existing `capture="environment"` pattern in the kiosk and
   nákup flows. The new camera flow is **not** that pattern (the OS
   camera returns one photo and forgets it). It's a custom
   in-app component that takes N photos and stitches them into one
   upload.
5. `API/Controllers/InvoicesController.cs` `[HttpPost("upload")]` —
   already accepts `application/pdf` and `image/*`. The new flow uploads
   a multi-page PDF assembled client-side, so on the server side **no
   logic change is required**; the existing PDF branch handles it.
6. `MATERIAL_PURCHASES_PLAN.md` mobile/tablet section — the
   `.min-h-dvh`, `.touch-target`, stacked-card patterns to match. Manager
   may be using a tablet outdoors in sun; design for that.

## What the customer asked for

From the 2026-05-28 conversation, after V1 was demoed working on the DEK
PDF: the manager wants to **scan a paper invoice by hand with a phone or
tablet camera**, not have to email or AirDrop a PDF to themselves first.
The supplier hands over a stapled multi-page invoice on site; the manager
should be able to open `/admin/invoices`, tap a new button, photograph
each page, and arrive at the same review screen they already use.

The accuracy bar is unchanged from V1: **"Tool must be perfect — it will
be used for finances."** A camera path that produces a worse OCR result
than a PDF upload of the same invoice is broken. The binding acceptance
test is therefore: **photograph `FA_2600141367.pdf` (printed on paper, 3
pages) under realistic site conditions, run it through the camera path,
and reach the same 11 delivery lists / 31 lines / reconciled
`1 788,43 €` that V1 produces from the digital PDF.** If the camera path
produces fewer lines, wrong line totals, or fails reconciliation, it is
not done.

## What the camera path actually has to do (grounding the design)

A camera scan is not "a photo upload with extra steps". It's an OCR
pre-processing pipeline that the manager runs on-device before any byte
reaches the server. The constraints:

- **Document AI Invoice Parser charges per page and degrades on
  low-quality photos.** Skewed pages with curled paper, glare, motion
  blur, or a partial page visible drop line-item recall sharply. The
  client must give the document a fair shot before spending money on
  parsing it.
- **A real supplier invoice is multi-page.** The DEK example is 3
  printed pages. Document AI expects one document per request, so the
  client must combine N photos into one PDF (or one multi-page TIFF)
  before posting to `/api/invoices/upload`.
- **The manager is outdoors.** Bright sun on white paper produces blown
  highlights; an awning produces deep shadow. Either kills OCR. The
  capture screen needs in-the-moment quality feedback, not "we'll find
  out after upload".
- **Phones disagree about photo orientation.** iOS sets EXIF rotation
  flags that survive canvas rendering inconsistently; Android Chrome
  sometimes drops them. Every page must be rotated to portrait upright
  before being written to the PDF, otherwise Document AI reads the page
  sideways.
- **HEIC is the iPhone default.** The existing `normaliseFile` helper
  handles HEIC → PNG already; reuse it on any path that produces a File,
  not on the in-memory canvas path (canvas output is already PNG/JPEG).
- **Tablet vs. phone.** A tablet is heavier and the camera is worse,
  but the screen is bigger so the corner-adjustment UI is easier. The
  capture component must work in portrait and landscape on both.

## Design decisions locked in this session

Answered. Do not relitigate during implementation.

- **(a) New entry point, not a replacement.** `/admin/invoices` gets a
  second top-of-list button: `Naskenovať mobilom`. The existing
  `Nahrať faktúru` button and its `<input type="file">` stay exactly as
  they are. A manager with a PDF picks the file. A manager with paper
  picks the camera. Both end at the same `/admin/invoices/{id}/review`
  screen.

- **(b) Custom in-app capture, not the OS camera intent.** The OS
  camera (`capture="environment"`) returns a single full-resolution
  photo with no opportunity to retake, crop, or chain a second shot.
  V1.1 needs multi-page + per-page quality feedback + retake, so we
  build a real component on `navigator.mediaDevices.getUserMedia`.
  The capture component is its own standalone Angular component,
  reusable later for receipts and proof-of-work photos if desired (out
  of scope V1.1).

- **(c) Multi-page is required.** Capture flow ends only when the
  manager taps `Hotovo`. Until then they can keep tapping `Ďalšia
  strana`. The pages list is reorderable (drag/long-press to move) and
  each page has a delete and a retake button.

- **(d) Client-side perspective correction is required.** Each captured
  page is shown with four draggable corner handles overlaid on the
  detected document edges. The manager can nudge them. On `Prijať` the
  client warps the image to a clean rectangle and discards the
  background. The corner detector runs once on capture; the manual
  adjustment is the safety net for the cases auto-detection gets wrong.

- **(e) PDF is assembled on the client.** Each accepted page becomes
  one page of a PDF (one warped + rotated + compressed JPEG per page,
  embedded in a PDF via `pdf-lib`). The whole PDF is posted to the
  existing `POST /api/invoices/upload` endpoint with content-type
  `application/pdf`. **No backend change to the upload endpoint.**

- **(f) Light backend additions only.** Two new optional columns on
  `InvoiceDocument` to record provenance and per-page metadata. No
  changes to `MaterialPurchase`, `MaterialPurchaseLine`, or the
  reconciliation rule. See §Schema.

- **(g) Library choice: jscanify + pdf-lib + heic2any.**
  - `jscanify` (~50KB) wraps OpenCV.js for the 4-corner detection and
    the perspective warp. OpenCV.js itself is ~8 MB; we load it
    lazily, only when the camera component mounts, with a `Načítavam…`
    spinner. Off the V1 file-picker path it costs nothing.
  - `pdf-lib` (~340KB) assembles the warped pages into one PDF.
    Better-maintained than jsPDF and renders JPEG embeddings without
    surprise canvas re-encoding.
  - `heic2any` is already a dependency for the kiosk image flow; reuse
    for any path that takes a File rather than a live MediaStream.

- **(h) Image quality gate on each page.** Before a page is accepted
  into the list, the client checks: (1) detected document covers ≥40%
  of the frame, (2) Laplacian variance ≥ a tuned threshold (blur), and
  (3) mean luminance in `[35..220]` (not blown out, not too dark). If
  any check fails, the page is marked amber with a tooltip ("Príliš
  tmavé", "Rozmazané", "Stránka nie je celá v zábere") and the
  `Hotovo` button is disabled until the manager either retakes or
  explicitly accepts the warning via a per-page checkbox. Per
  "must be perfect for finances", quality friction stays in.

- **(i) Compression target: long-edge 2200 px, JPEG q=0.82.** Slightly
  larger than the kiosk photo helper's 1200/0.72 — Document AI's
  text-recall improves measurably above 2000px long edge on supplier
  invoices, and a 3-page PDF at this setting comes in around 1.5–2 MB,
  well under the 20 MB upload cap.

- **(j) Feature sub-flag `InvoiceCameraScan`.** Default OFF in all
  environments at first deploy. The customer flips it on after the V1
  PDF path has been stable for at least one billing cycle. If the
  camera path turns out flaky in the field, the flag goes off and
  V1 file-picker remains live. Same wiring pattern as the other
  flags.

- **(k) Manager-only.** Same JWT gate as V1. The kiosk surface is
  untouched. The camera button is hidden when the camera flag is off.
  Both the V1 flag (`InvoiceScanning`) AND the new
  (`InvoiceCameraScan`) flag must be on to see the camera entry
  point.

- **(l) Orientation handled by the warp.** The 4-corner warp picks the
  "top" edge as the shortest one closer to the camera-top in the
  source frame and rotates to portrait. No EXIF reading required —
  the warp output is authoritative. Manager can rotate 90° from the
  review thumbnail if the auto-pick is wrong.

- **(m) No on-device OCR.** All OCR still happens server-side via
  Document AI. The client does layout cleanup, not text extraction.
  This keeps the cost model and the accuracy model identical between
  the two upload paths.

## Schema

One small, additive migration. Generate via CLI per Migration Safety Rule 1:

```
cd API
dotnet ef migrations add AddInvoiceScanSource
```

PostgreSQL self-heal blocks for both new columns in `Program.cs` per
Rule 3.

### `InvoiceDocument` — two new columns

```
ScanSource          varchar(20)    NOT NULL  DEFAULT 'file'
                                   -- 'file' | 'camera'  — provenance,
                                   -- read on /admin/invoices for an
                                   -- icon and in analytics later.
ScanPageCount       int            NULL
                                   -- camera: number of photos the
                                   -- manager took. file: NULL (the PDF
                                   -- already knows). Lets us correlate
                                   -- low-quality scans with page count
                                   -- when triaging support cases.
```

No changes to `MaterialPurchase`, `MaterialPurchaseLine`, or the
reconciliation logic. The PDF the server receives is already structured
correctly; nothing downstream cares whether the manager scanned it or
exported it.

## Feature flag wiring

Identical to the existing five flags.

- Backend:
  - Add `"InvoiceCameraScan"` to the `knownFlags` array in
    `Program.cs` (currently `Notifications`, `CommanderIntegration`,
    `MaterialPurchases`, `ProofOfWorkChoices`, `InvoiceScanning`).
  - **No new controller and no controller-level
    `[RequireFeatureOrSuperAdmin]` decorator** — the camera path
    posts to the same `[HttpPost("upload")]` as V1, which is already
    gated by `[RequireFeatureOrSuperAdmin("InvoiceScanning")]`. The
    camera flag is a frontend-only gate for the new button + screen.
- Frontend:
  - `feature-flag.service.ts` — add
    `invoiceCameraScan: Signal<boolean>`.
  - `app.routes.ts` — new `/admin/invoices/scan` route, guarded by
    both `InvoiceScanning` AND `InvoiceCameraScan`.
  - `invoices.page.html` — second button `Naskenovať mobilom`, shown
    only when `invoiceCameraScan() && invoiceScanning()`.
  - `account.page.html` — a row inside the existing Funkcie
    superadmin card. Nest it visually under the
    `InvoiceScanning` row to make the dependency obvious.

## Capture component

Path: `client/src/app/components/invoice-camera/invoice-camera.component.{ts,html}`.
Standalone, signal-based, no service required (the only network
interaction is the eventual call to `InvoiceService.upload()` on
`Hotovo`, which already exists from V1).

### States

```
idle           — permissions not granted yet, "Povoliť kameru" button
streaming      — live <video> from getUserMedia, shutter overlay visible
review-page    — frozen captured frame, 4-corner overlay, accept/retake
pages-list     — thumbnails of accepted pages, "Ďalšia strana" + "Hotovo"
uploading      — pdf-lib assembly + InvoiceService.upload() in flight
done           — navigate to /admin/invoices/{newId}/review
error          — render the error in the same red box V1 uses; one
                  "Skúsiť znova" button that drops back to idle
```

The five live states cycle until the manager taps `Hotovo`. There is no
back button from `uploading`; the manager waits. The
`InvoiceService.upload()` returns the parsed `InvoiceDocument`, after
which the component navigates with `this.router.navigate(['/admin/invoices', created.id])`,
mirroring the existing `onOpenUploaded` behaviour in `invoices.page.ts`.

### Layout (portrait, phone)

```
┌─────────────────────────┐
│ ✕                   ⚙  │  ← close + camera switch (front/back)
│                         │
│   ┌─────────────────┐  │
│   │                  │  │
│   │   live preview   │  │  ← <video autoplay muted playsinline>
│   │                  │  │
│   │   green corner   │  │  ← detected document outline overlay
│   │   guide overlay  │  │     (updates ~3x/sec, jscanify)
│   │                  │  │
│   └─────────────────┘  │
│                         │
│  Stránka 1 z ?          │  ← page count badge
│                         │
│         ⬤ shutter       │  ← large, centered, touch-target
│                         │
│  [Pridať stranu] [Hotovo]│  ← shown after first accepted page
└─────────────────────────┘
```

In landscape (typical for a tablet held over a flat invoice), the
preview stays centered and the controls move to the right side. Match
the existing tablet pass's `.min-h-dvh` / `.touch-target` utilities.

### Per-page review screen (after shutter)

```
┌─────────────────────────┐
│ Stránka 1                │
│                         │
│   ┌─────────────────┐  │
│   │  frozen photo    │  │
│   │  with 4 draggable │  │  ← jscanify-detected corners,
│   │  corner handles   │  │     manager can nudge each
│   │                  │  │
│   └─────────────────┘  │
│                         │
│  ⚠ Príliš tmavé          │  ← only when a quality gate trips
│  [ ] Aj tak prijať       │  ← per-page override checkbox
│                         │
│  [Odfotiť znova] [Prijať]│
└─────────────────────────┘
```

`Prijať` warps the cropped quad to a clean rectangle, compresses to JPEG
per §design decision (i), and appends to the `acceptedPages` signal.
`Odfotiť znova` discards and returns to `streaming`. If a quality gate
trips, `Prijať` is disabled until either retake or the `Aj tak prijať`
checkbox.

### Pages-list screen (between captures)

```
┌─────────────────────────┐
│ Naskenované strany       │
│                         │
│ ┌──┐ ┌──┐ ┌──┐         │  ← thumbnails, drag to reorder
│ │1 │ │2 │ │3 │         │     X to delete, tap to retake
│ └──┘ └──┘ └──┘         │
│                         │
│ [Pridať ďalšiu stranu]   │
│ [Hotovo — odoslať]       │
└─────────────────────────┘
```

`Hotovo — odoslať` is disabled while any page has an unresolved quality
warning, mirroring the per-page gate.

### Strings (Slovak, all in component template, no i18n in V1.1)

```
"Povoliť prístup ku kamere"
"Bez prístupu ku kamere sa faktúra nedá naskenovať."
"Stránka 1 z ?"
"Pridať stranu"
"Hotovo"
"Hotovo — odoslať"
"Odfotiť znova"
"Prijať"
"Aj tak prijať"
"Príliš tmavé"
"Príliš svetlé"
"Rozmazané"
"Stránka nie je celá v zábere"
"Posúvam rohy…"   (a11y label on each corner handle)
"Spracovávam…"     (uploading state)
"Skúsiť znova"
"Skenovanie zlyhalo. Skontrolujte pripojenie a skúste znova."
```

## Quality gates (client-side, blocking unless overridden)

Run on the frozen-frame canvas at review time, never on the live
preview (too slow).

1. **Document coverage** — jscanify's detected quad area ≥ 40% of the
   canvas area. If less, label `Stránka nie je celá v zábere`.
2. **Blur** — Laplacian variance via OpenCV.js: take the warped output
   converted to grayscale, run a 3×3 Laplacian, compute the variance.
   Threshold to tune in the implementation session; suggested starting
   point `< 60.0` triggers `Rozmazané`. Tune against ~10 hand-scanned
   pages of `FA_2600141367.pdf` on at least one iPhone and one Android.
3. **Luminance** — mean of grayscale pixels of the warped output. If
   `< 35` → `Príliš tmavé`; if `> 220` → `Príliš svetlé`.

Each failing gate writes a string into the page's
`qualityWarnings: string[]`. UI shows them stacked above the buttons.

A per-page `acceptedWithWarnings: boolean` flag exists for the
`Aj tak prijať` override. The pages-list `Hotovo — odoslať` button is
disabled if **any** page has unresolved warnings (i.e. warnings exist
AND override not checked). No global override. Per "must be perfect
for finances", we make the manager click through each warning
individually.

## Client-side PDF assembly

After `Hotovo — odoslať`:

```
import { PDFDocument } from 'pdf-lib';

const pdf = await PDFDocument.create();
for (const page of acceptedPages()) {
  // page.jpegBlob is the warped, compressed, oriented JPEG
  const bytes = await page.jpegBlob.arrayBuffer();
  const image = await pdf.embedJpg(bytes);
  const p = pdf.addPage([image.width, image.height]);
  p.drawImage(image, { x: 0, y: 0, width: image.width, height: image.height });
}
const pdfBytes = await pdf.save();
const pdfFile = new File([pdfBytes], `scan-${Date.now()}.pdf`, { type: 'application/pdf' });
await this.invoiceService.upload(pdfFile);
```

`InvoiceService.upload(file: File)` exists from V1; no change there.
The server sees the same `IFormFile` shape it already handles, takes
the `application/pdf` branch (`isPdf = true`), uploads via
`_blob.UploadRawAsync`, and the rest of the V1 pipeline runs unchanged.

The only server-side addition: when the upload arrives with the new
header `X-Scan-Source: camera` (or query param — implementer's call),
set `ScanSource = "camera"` and `ScanPageCount = <header value>` on the
new `InvoiceDocument`. Default stays `'file'`. This is a 3-line change
in `[HttpPost("upload")]`.

## OpenCV.js lazy-load

OpenCV.js is ~8 MB. We do **not** want it in the main bundle.

Approach:
- Add `opencv.js` and `jscanify` as runtime CDN imports, not as npm
  dependencies. The capture component's `ngOnInit` injects a `<script
  src="…opencv.js">` tag if not present, awaits its `onRuntimeInitialized`
  event, then loads jscanify and resolves `ready = true`. While loading,
  the component shows `Načítavam skener…` with the existing
  `SpinnerComponent`.
- Cache control: pin the OpenCV.js version in the script URL and serve
  from `https://docs.opencv.org/4.10.0/opencv.js` (or wherever the
  implementer decides — confirm CSP allows it; the current site has no
  strict CSP).
- Fallback: if OpenCV.js fails to load (offline, blocked), the camera
  component renders an error card with `Skener sa nepodarilo načítať.
  Použite ‘Nahrať faktúru’.` and a button that closes the component.
  V1 path stays usable.

## Permissions handling

- The component on first mount has no MediaStream. It renders a single
  `Povoliť prístup ku kamere` button. Tapping it calls
  `navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } })`.
- iOS Safari only allows `getUserMedia` on a user-gesture call on HTTPS.
  Both conditions hold in our production setup (Railway → HTTPS, button
  click triggers it). On localhost HTTP, iOS will refuse — dev guidance
  goes into `PROJECT_NOTES.md`.
- If `getUserMedia` rejects (denied, no camera, in-use by another tab),
  render an error card explaining the denial and offering to fall back
  to the file picker.

## Routes

```
/admin/invoices              (existing) list page, gains a second button
/admin/invoices/scan         (new)     full-screen camera component
/admin/invoices/{id}/review  (existing) unchanged
```

Both new entries are guarded by:

```
canActivate: [adminGuard, featureFlagGuard('InvoiceScanning'), featureFlagGuard('InvoiceCameraScan')]
```

On a successful upload the camera component navigates with
`router.navigate(['/admin/invoices', created.id])`, **not** to the list
— same behaviour as the existing `onOpenUploaded`.

## What "done" looks like for V1.1

Binding acceptance test:

1. Print `FA_2600141367.pdf` on a normal office printer. 3 sheets.
2. On an iPhone with iOS 17+ and on an Android phone with Chrome,
   open `/admin/invoices` → `Naskenovať mobilom`.
3. Photograph all three pages under three lighting conditions:
   - Indoor, even fluorescent.
   - Outdoor, partial shade.
   - Outdoor, direct sun on the paper.
4. For each run, expect:
   - The capture component detects four corners on each page within
     1 second of the shutter.
   - At most one of the three pages per run trips a quality warning;
     none in the indoor case.
   - Manager can drag corners and the warped output stays rectangular.
   - On `Hotovo — odoslať`, the assembled PDF posts successfully,
     Document AI returns within 5 seconds, and the resulting
     `InvoiceDocument` has the same totals as V1 produces from the
     digital PDF: `TotalExclVat = 1 507.63`, `TotalVat = 280.80`,
     `TotalInclVat = 1 788.43`.
   - 11 draft `MaterialPurchases`, 31 `MaterialPurchaseLines`, VAT
     rates correct, reconciliation passes.
5. `ScanSource = 'camera'` and `ScanPageCount = 3` on the new
   `InvoiceDocument`.
6. `dotnet build` clean. `npx tsc --noEmit -p tsconfig.app.json` clean.
7. Pre-flight on the dev DB after redeploy confirms the two new columns
   exist; no self-heal warnings fired.
8. Both flags can be flipped independently in the Funkcie card; with
   only `InvoiceScanning` on, the camera button is hidden but the
   file-picker upload still works exactly as it does today.

A camera scan that produces fewer than 11 delivery lists or fails
reconciliation on the printed-then-scanned DEK invoice **under indoor
lighting** is broken. Outdoor failure modes are acceptable in V1.1 as
long as the quality gate flags them and the manager is told why; the
"sun glare on glossy paper" case is genuinely hard and may require a
V1.2 polish pass.

## Out of scope (V1.1)

- On-device OCR. Document AI does all parsing. We only do layout
  cleanup client-side.
- Auto-shutter when the document is in frame. Manager taps the
  shutter. Cheap to add later if it turns out to be annoying.
- Live edge-detection overlay on the streaming preview. Costs CPU and
  battery for marginal UX gain over post-shutter detection.
- Receipt (blocek) capture from the kiosk side. The kiosk
  capture-receipt flow already uses `capture="environment"` and stays
  as-is. Migrating it to this component is a future polish, not V1.1.
- Multi-currency, foreign-VAT, or non-supplier-invoice documents.
  Inherited from V1's scope.
- Background queue for camera uploads. Synchronous upload + parse
  is still fine at 3-page sizes.
- A "scan again" button on the review screen that re-shoots without
  losing the current `InvoiceDocument` draft. Manager would have to
  `Zahodiť` and start over. Cheap to add later if requested.
- Saving the in-progress scan locally if the network drops mid-upload.
  Manager retries. We do not persist pre-upload state.
- Replacing the file-picker upload. Both entry points coexist.

## Open questions still worth asking the customer

1. **Tablet form factor.** Manager has both a phone and a tablet.
   V1.1 supports both, but if the customer only ever uses the phone
   in the field, we can drop the landscape layout. Confirm before
   investing the UX polish.
2. **Glossy paper / thermal paper from another supplier.** The DEK
   invoice is matte A4 office paper. If the customer also wants to
   scan thermal-printed receipts from Hornbach / OBI, the quality
   gates need re-tuning (thermal paper has low contrast). Confirm
   the supplier list V1.1 must handle.
3. **Saved scans recoverable across an app refresh.** If the manager
   takes 5 photos, the tab reloads, and the photos are gone — is that
   acceptable, or do we need IndexedDB persistence of in-progress
   scans? V1.1 default = not persisted.
4. **Per-page rotation control.** Auto-orientation gets it right most
   of the time, but a manual 90° / 180° rotate button on the pages-list
   thumbnails is one extra control. Worth shipping in V1.1?
5. **Bandwidth cost on cellular.** A 3-page PDF at the chosen
   compression settings is ~1.5–2 MB. On a flat-rate Slovak mobile
   plan this is nothing; on roaming it isn't. Default = no cellular
   warning. Confirm.
6. **Privacy-by-policy review.** No worker PII enters the camera
   pipeline (the camera only sees a supplier invoice), but the
   superadmin's IT person may still want this noted. Confirm if a
   one-paragraph addendum to whatever policy they keep is needed.

## Migration / deploy order

1. Code lands behind both flags off in prod.
2. Self-heal blocks add the two columns on first boot.
3. Superadmin flips `InvoiceCameraScan` on in dev → run the binding
   acceptance test against printed `FA_2600141367.pdf` on at least
   one iPhone and one Android phone, plus one Android tablet.
4. Promote to prod with the camera flag off. Superadmin flips it on
   after the customer signs off in dev.

## Notes for the implementation session

- The capture component is the only meaningful piece of new client
  code. It's a single `.ts` file ~400 lines + a single `.html` file
  ~150 lines. Resist the urge to extract sub-components or services
  before there's a second caller — per `CLAUDE.md` §2 (Simplicity
  First).
- jscanify's API is small: `JscanifyApi.findPaperContour(mat)` and
  `JscanifyApi.extractPaper(mat, w, h, contour)`. Wrap each in a
  thin helper so the rest of the component never imports the global
  `cv` or `Jscanify` objects directly.
- The CDN load of OpenCV.js is the one piece that's easy to get
  wrong. Test it cold-cache, with and without network throttling
  (DevTools `Slow 3G`).
- Don't add a "scan source = camera" badge to the
  `/admin/material-purchases` list. The provenance ends at the
  `InvoiceDocument`; the resulting purchases are indistinguishable
  from any other once committed.
- Don't move the V1 file-picker upload code into the new component.
  Keep both paths cleanly separated. Surgical changes, per
  `CLAUDE.md` §3.
- The reconciliation rule and the review screen are V1 territory and
  must not be touched in V1.1. If the camera path exposes a parser
  bug at review time, fix the parser in a separate PR; do not patch
  the review screen for it.
- "Must be perfect for finances" — same brief as V1. Where a V1.1
  polish shortcut and accuracy conflict, pick accuracy and defer the
  shortcut to V1.2.

---

*End of plan.*
