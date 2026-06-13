<!--
Writing style: this file is read by AI assistants. Write plainly. No emojis,
no "—" as rhetoric, no exclamation marks, no "super!" / "great!" / "perfect!" /
"absolutely right!", no enthusiastic openings, no padding sentences. Bold sparingly.
Slovak strings shown to managers must read like a normal person typed them,
not marketing copy. When in doubt, write less.
-->

# Invoice Scanning V1.1 — Staged implementation plan

> Companion to `INVOICE_SCANNING_CAMERA_PLAN.md` (long-form design).
> That file says **what** the camera-to-invoice feature does and **why**;
> this file says **in what order to ship it** so the customer sees value
> after stage 1 and again after every subsequent stage, instead of after
> one giant landing.
>
> Created 2026-05-28.

## Goal at the end of all stages

A manager holds a paper invoice. Opens `/admin/invoices` on a phone or
tablet, taps `Naskenovať mobilom`, photographs each page, taps `Hotovo`,
ends up on the same `/admin/invoices/{id}/review` screen that today's
PDF-upload flow lands on. The OCR result, the reconciliation rule, the
delivery-list auto-mapping, all of that is unchanged downstream — the
camera path is just a new way to **get bytes into** the existing
parser.

## Anchor invariant for every stage

Every stage must, by the end of its work, still produce **the same
`InvoiceDocument` shape** the existing PDF-upload path produces today.
The DEK acceptance test from `INVOICE_SCANNING_PLAN.md` (printed paper,
3 pages, 11 delivery lists, 31 lines, reconciles to `1 788,43 €`)
remains the regression guard. If a stage breaks that, the stage is
broken.

---

## Stage 1 — Backend accepts N images, combines them into one PDF, parses

**Goal:** server-side endpoint that takes 1..N image files and routes
the resulting bytes through the existing OCR pipeline.

**Scope:**

- New endpoint `POST /api/invoices/upload-photos` next to the existing
  `POST /api/invoices/upload`. Accepts multipart with field
  `files: IFormFile[]` (order matters — page 1 first).
- Server-side image processing:
  - Validate each file: MIME `image/*`, size cap (e.g. 10 MB per
    image), max 10 images total (sanity).
  - Use `SixLabors.ImageSharp` (already a dependency) to normalise
    each image: auto-rotate via EXIF, downscale long edge to 2200 px
    if larger, re-encode JPEG at q=82.
- PDF assembly: add `QuestPDF` (MIT, modern .NET API, smaller surface
  than PdfSharp) — one page per image, no margins, page sized to the
  image aspect ratio. Output as a `byte[]`.
- Feed the assembled PDF bytes through the **existing**
  `_ocr.ProcessAsync(bytes, "application/pdf", ct)` call. No change
  to the OCR client, parser, or reconciliation.
- The rest of the existing `Upload` handler (Cloudinary upload, dedup
  check, `InvoiceDocument` insertion, delivery-list creation, line
  population, auto-match) is shared logic with the PDF path. Refactor
  the existing `Upload` into a private `ProcessUploadedPdfAsync(byte[]
  bytes, string originalFileName)` helper and have both endpoints call
  it.
- Two-column provenance per V1.1 plan §Schema: set
  `ScanSource = "camera"`, `ScanPageCount = files.Length` on the new
  `InvoiceDocument`. (Adds two columns; same self-heal pattern as the
  others.)

**Done when:**

- POSTing 3 JPEG images of the printed DEK invoice to
  `/api/invoices/upload-photos` produces an `InvoiceDocument` with the
  same 11 delivery lists / 31 lines / `1 788,43 €` totals as the
  PDF-upload path on the same content.
- `dotnet build` clean. `ScanSource = 'camera'`, `ScanPageCount = 3`
  on the resulting row.
- The existing `POST /api/invoices/upload` PDF path produces an
  identical document. (Refactor regression check.)

**Why this is stage 1:** every later stage produces images that
ultimately hit this endpoint. Building it first unblocks **any**
upload mechanism on the client — including a one-line
`<input type="file" multiple accept="image/*">` form. The feature is
already usable on the lowest possible UI investment.

**Estimated effort:** 1 day.

---

## Stage 2 — `Naskenovať mobilom` button using the OS camera

**Goal:** manager has a working phone-scan path through the existing
admin UI, with zero new dependencies on the frontend.

**Scope:**

- New `InvoiceCameraScan` feature flag (frontend toggle only — the
  backend endpoint from stage 1 is already gated by the existing
  `InvoiceScanning` flag at controller level).
- `invoices.page.html`: second button `Naskenovať mobilom` next to the
  existing `Nahrať faktúru`, gated by `invoiceScanning() &&
  invoiceCameraScan()`. The button opens an `<input type="file"
  accept="image/*" capture="environment" multiple>` picker.
- On iOS/Android Chrome, `capture="environment" multiple` opens the
  OS camera with a "take photo / done" UX out of the box. The user
  photographs each page, taps Done, and the input fires `change` with
  a `FileList` of N images.
- `InvoiceService.uploadPhotos(files: File[])` (new method) — runs
  `normaliseFile` on each (HEIC → PNG via the existing util), posts
  multipart to the stage 1 endpoint. Reuses the existing success/
  failure modal flow from `onFile`.

**Done when:**

- Manager taps `Naskenovať mobilom` on a phone, photographs the
  printed DEK pages, gets routed to the review screen, sees the
  expected 11 delivery lists. Manual visual check on at least one
  iPhone + one Android Chrome.
- Toggling `InvoiceCameraScan` off in the superadmin Funkcie card
  hides the button. Toggling `InvoiceScanning` off hides both
  buttons.
- The `Nahrať faktúru` PDF-picker path still works identically.

**Why this is stage 2:** it gives the customer a **functioning
phone-scan feature** in one demoable button. Everything in stages
3–6 is UX polish over the same backend. If stages 3–6 never ship,
stages 1+2 alone are enough to satisfy the customer's stated need.

**Estimated effort:** half a day on top of stage 1.

---

## Stage 3 — Custom in-app camera component (live preview + retake)

**Goal:** replace the OS-camera handoff with a controlled in-app
experience: live preview, per-page review, retake, reorder, delete.

**Scope:**

- New standalone component `InvoiceCameraComponent` at
  `client/src/app/components/invoice-camera/invoice-camera.component.{ts,html}`.
- New route `/admin/invoices/scan` rendering it full-screen, guarded
  by `invoiceScanning() && invoiceCameraScan()`.
- The `Naskenovať mobilom` button from stage 2 now navigates to this
  route instead of triggering the file picker. The
  `capture="environment"` file picker stays available as a labelled
  fallback ("Vybrať z galérie") for managers who took photos earlier
  in the day.
- States: `idle` (permission prompt) → `streaming` (live preview +
  shutter) → `review-page` (frozen frame with accept/retake) →
  `pages-list` (thumbnail strip, drag-to-reorder, delete, add another)
  → `uploading` → `done` (router.navigate to the review page).
- Capture path: `navigator.mediaDevices.getUserMedia({ video: {
  facingMode: 'environment' }})` → render to a hidden canvas on
  shutter → `canvas.toBlob('image/jpeg', 0.82)` → push to `pages`
  signal.
- Upload: same `InvoiceService.uploadPhotos(files)` from stage 2, no
  backend change.

**Done when:**

- Manager can photograph 3 pages, reorder them by drag, delete page 2
  and retake it, then submit, and reach the review screen with the
  pages in the corrected order. Visual check on phone + tablet.
- Permissions denied → component renders an error card with a
  fallback link to the file picker.
- The OS-camera file picker fallback from stage 2 still works for
  managers who haven't granted camera permission.

**Why this is stage 3:** the OS camera from stage 2 has no reorder,
no per-page retake, and on some Android skins, no "done after N shots"
button at all. The in-app component fixes those without changing the
backend.

**Estimated effort:** 2 days.

---

## Stage 4 — Quality gates per page

**Goal:** the component warns the manager when a captured page is
likely to OCR badly (blurry, too dark, page not fully framed), and
blocks `Hotovo — odoslať` until they retake or explicitly override.

**Scope:**

- On the `review-page` state of the camera component, run three
  checks on the frozen canvas:
  1. **Blur** — Laplacian variance on a 3×3 kernel over the grayscale
     image. Threshold tuned against ~10 hand-scanned pages of the DEK
     invoice (starting point: variance < 60).
  2. **Lighting** — mean grayscale luminance in `[35..220]`.
  3. **Coverage** — the largest dark-on-light contour covers ≥ 40%
     of the frame (proxy for "the page is actually in shot"; full
     edge detection arrives in stage 5).
- Failed checks render an amber warning per page with strings like
  `Rozmazané`, `Príliš tmavé`, `Príliš svetlé`, `Stránka nie je celá
  v zábere`.
- Per-page `Aj tak prijať` checkbox overrides any warning on that
  page. `Hotovo — odoslať` button is disabled while any page has
  an unresolved warning.

**Done when:**

- Deliberately taking a blurry shot of the DEK page triggers the
  `Rozmazané` warning and disables submit.
- Ticking `Aj tak prijať` enables submit for that page.
- A well-lit, in-focus photo trips zero warnings.
- Same DEK content under good lighting still produces the expected
  parse result downstream (i.e. quality gates only add friction, they
  never change the OCR payload).

**Why this is stage 4:** customer's brief is "must be perfect for
finances". Gates prevent the manager from spending Document AI
credits on a photo that's going to OCR wrong, and prevent the
downstream review screen from being polluted with garbage. Cheap
guard against the most common failure mode (sun glare, motion blur).

**Estimated effort:** 1 day.

---

## Stage 5 — Auto edge detection + manual perspective warp (jscanify)

**Goal:** each captured frame gets cropped to just the page,
straightened, and rotated upright before being sent. The manager can
nudge the four detected corners if the auto-detection is wrong.

**Scope:**

- Lazy-load `jscanify` and `opencv.js` from CDN on first camera-route
  visit. Show `Načítavam skener…` while loading (one-time per page
  load).
- On accepting a frozen frame, call `jscanify.findPaperContour(mat)`
  to get four corner points, render them as draggable handles
  overlaid on the page review screen.
- On `Prijať`, call `jscanify.extractPaper(mat, width, height, corners)`
  to produce a clean rectangular warped JPEG. That's what gets
  pushed into `pages`.
- Fall back gracefully if jscanify/OpenCV fails to load (offline,
  blocked) — the component renders an error card and offers the
  stage 2 file picker as a fallback.

**Done when:**

- A skewed phone photo of one DEK page is captured, corner handles
  appear on the four edges of the document automatically, the
  manager can drag any corner ±20 pixels, and on `Prijať` the warped
  output is a clean rectangle of just the page content with no
  background.
- Under good lighting on a flat surface, auto-detected corners are
  within 10 pixels of the real corners on at least 9 out of 10 test
  shots.
- DEK acceptance test still passes — the warped photo path
  reconciles to `1 788,43 €` end to end.

**Why this is stage 5:** without this, Document AI is processing
photos with curled paper, background clutter, and ~5-15° skew.
That's the difference between "OCR mostly works" and "OCR is the
finance source of truth". Per the customer's "must be perfect"
brief.

**Estimated effort:** 2 days.

---

## Stage 6 — Move PDF assembly to the client (pdf-lib)

**Goal:** the client assembles the PDF using `pdf-lib` and uploads one
PDF to the existing `/api/invoices/upload` endpoint instead of N
images to `/api/invoices/upload-photos`.

**Scope:**

- Add `pdf-lib` to `client/package.json`.
- In the camera component's `Hotovo — odoslať` handler, replace the
  multi-image upload with: `PDFDocument.create()` → `embedJpg` each
  page → assemble → `pdf.save()` → POST as `application/pdf` to the
  existing upload endpoint.
- Remove or keep the `upload-photos` endpoint. Recommended: keep it
  in place as the documented fallback for non-JS clients and for the
  Stage 2 file-picker path. The two endpoints share the same private
  helper, so maintenance cost is one handler, not two.

**Done when:**

- Camera path produces a multi-page PDF on the client, posts it to
  `/api/invoices/upload`, and reaches the review screen.
- Server CPU on upload drops measurably (no QuestPDF assembly on
  every camera submission). Optional check — only worth measuring
  if the customer's traffic warrants it.
- DEK acceptance test still passes.

**Why this is stage 6 (last):** this is purely an optimisation.
Server-side PDF assembly from stage 1 works fine at the customer's
expected invoice volume. Moving it client-side is "nice", but doesn't
unlock any new behaviour, so it sits last in priority order. **Stage 6
can be skipped indefinitely** without harming the feature.

**Estimated effort:** half a day.

---

## Total picture

| Stage | Ships | What the customer sees                       | Effort |
|-------|-------|----------------------------------------------|--------|
| 1     | always | Nothing visible (backend only).             | 1 d    |
| 2     | always | `Naskenovať mobilom` button using OS camera. | 0.5 d  |
| 3     | likely | In-app camera with thumbnails + retake.      | 2 d    |
| 4     | likely | Quality warnings per page.                   | 1 d    |
| 5     | likely | Auto-crop + perspective correction.          | 2 d    |
| 6     | maybe  | (No visible change.)                         | 0.5 d  |

After stage 2, the camera feature is **functionally complete** for
the customer's stated need. Stages 3-5 raise it from "works on a
phone" to "feels like a real scanner app". Stage 6 is an internal
optimisation that can wait.

## Sequencing rules

- **Don't skip stage 1.** Every other stage depends on it.
- **Stage 2 can ship alone**, as a demoable feature. Ship it; collect
  feedback before committing to stage 3.
- **Stages 3 → 4 → 5 are ordered by user value, not strict
  dependency.** Quality gates (stage 4) work even without the warp
  from stage 5; the warp (stage 5) doesn't need quality gates to
  function. If field testing shows photos already OCR fine under
  good lighting, skip stage 4 and go straight to stage 5.
- **Stage 6 is optional.** Reassess after the others are in.
- After each stage, run the DEK paper-print test end to end. If it
  fails, fix the regression before starting the next stage.

## Cross-stage concerns

- **Feature flag (`InvoiceCameraScan`)** lives behind stages 1-6 the
  whole way. Default OFF in prod. Manager flips it on per
  environment once a stage is verified.
- **Both buttons coexist forever.** `Nahrať faktúru` (PDF picker)
  stays in the UI. A manager who already has a digital PDF uses it
  directly.
- **No schema change after stage 1.** The two columns added in stage
  1 (`ScanSource`, `ScanPageCount`) cover everything stages 2-6
  produce.
- **No change to OCR / parser / reconciliation in any stage.** They
  are the contract. If a stage's output breaks them, the stage is
  wrong; the parser is correct.
- **Mobile/tablet baseline** from `MOBILE_TABLET_HANDOFF.md` applies:
  `.min-h-dvh`, `.touch-target`, stacked cards below `md`. The
  camera component is portrait by default and reflows for tablet
  landscape.

## Out of scope at every stage

- Replacing the existing PDF-upload path. Both coexist.
- Receipt (blocek) capture from the kiosk side — see V1.0 out of
  scope.
- Multi-currency, foreign VAT — inherited from V1.0.
- Auto-shutter when document is in frame. Manager always presses
  shutter; adds back later only if asked.
- Persisting in-progress scans across an app refresh. Manager
  retries.
- Bulk upload of more than one invoice at a time.

---

*End of stages plan.*
