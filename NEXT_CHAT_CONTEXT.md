# NEXT_CHAT_CONTEXT.md — handoff for a fresh session (2026-07-14)

Šichtovnica/Dochádzkovník — .NET 9 API + Angular 20 client (signals, @if/@for, Tailwind,
dark mode) for construction company **AZ Profistav, s.r.o.** (IČO 47208368 — always the
CUSTOMER on documents, never the supplier). Slovak UI. Invoice/receipt scanning via
Google Document AI + Gemini.

## Environments & workflow rules (IMPORTANT)
- Branches: `dev` (Vercel preview + Railway dev API `dochadzkovnik-dev.up.railway.app`),
  `master` (production: `dochadzkovnik.vercel.app` + `dochadzkovnik-production.up.railway.app`).
- **The user builds, commits and pushes THEMSELVES.** Never run `git add/commit/push`
  from the sandbox — a sandbox commit once captured TRUNCATED files (stale FUSE mount)
  and broke the build (commit 249efd1, fixed by re-committing from Windows).
- The Linux sandbox mount can serve stale/truncated views of recently edited files.
  **Read/Edit/Write tools are authoritative**; use bash only for git *reads* and Python
  simulations, and distrust it right after heavy editing.
- Local testing: `http://localhost:4200` via Chrome MCP (admin login; Bearer token in
  localStorage). Never test against live sites.
- Rejected photo uploads dump their OCR text to `API/rejected-scans/*.txt` (gitignored,
  like `API/scans/`). Accepted docs: `GET /api/invoices/{id}/ocr-diagnostic`.
- Parser philosophy: **the printed total is ground truth** — any extraction (regex or AI)
  must reconcile lines+VAT to the printed total (cent-gates, ±0.05/0.06 cash tolerance)
  or it is not trusted. Tests replicate real OCR fixtures verbatim (`API.Tests/fixtures/`).
- CLAUDE.md applies: surgical diffs, no speculative features, simplicity first.

## Invoice scanning architecture (current)
Upload pipeline (`API/Controllers/InvoicesController.cs` → `Upload`):
1. **Gemini primary** (`ILlmInvoiceExtractor` / `GeminiInvoiceExtractor`): vision read of
   the original file, strict JSON schema, temp 0. If it passes `ParsedReconciles` →
   Document AI is SKIPPED (~$0.002 vs $0.10/doc). `RawOcrJson` then stores
   `{"source":"gemini-primary"}`.
2. Else **Document AI + deterministic parser** (`API/Services/InvoiceParser.cs`) +
   acceptance ladder: AI-reconciles → AI wins; AI-has-number+total while parser doesn't →
   AI accepted for review (Nesedí banner); else parser kept.
3. Both failed → BIG Slovak reject message (retake with flash / fill frame).
- Config: `Gemini__ApiKey` (free tier, resets midnight Pacific), default model
  `gemini-3.1-flash-lite`, `Gemini__Model` override, `Gemini__Primary=false` kill switch.
  429/503 → retry + model fallback chain; per-day 429 recorded in **ScanStatusService**
  → `GET /api/invoices/scan-status` → client banners (fallback/ai-only/down modes) on
  invoices + camera pages. Quota exhaustion degrades to DocAI silently (still works).
- Manual **"✨ Skúsiť AI rozpoznanie"** button on Nesedí review docs → `POST {id}/ai-reparse`
  (review-only, replaces draft, dedup-checked).
- Photo pages get server-side `EnhanceForOcr` (ImageSharp: gated shadow-flattening +
  sharpen) inside `CombineImagesToPdfAsync` (3000px/q90).
- Parser recovery formats (all fixture-tested): DEK multi-DL, Hilti, KOVOUNI snap,
  A-Z STAV, receipts (MPL qty-shuffle repair, PRESPOR orphan-row pairing incl. Cyrillic
  с, HORNBACH per-item "Základ" rebuild), receipt gross→net, zľava variants.
  DEK repair gated by text marker `"za dodací list"` (adopted from master at merge).

## Camera page (client/src/app/pages/invoice-camera/)
- **Primary flow = native camera app** via `<input capture="environment">` ("Odfotiť
  stranu") — no getUserMedia, no permission prompts, full-res stills, native flash/night
  mode (flash hint text: set ⚡ On, not Auto). Pages get ≤3000px shrink when >4MB + 480px
  grid thumbs (full-res thumbs used to exhaust WebKit canvas memory → black frames/crashes).
- Secondary: in-app live viewfinder (1080p, burst-of-2 sharpest, WYSIWYG 3:4 crop, torch,
  zoom, low-light hint, degradation watchdog, camera released during review). All
  browser-side OpenCV was REMOVED (document-scan.ts deleted) after repeated phone crashes.
- Upload cap 40MB; dedup = invoice number + issue date.

## Recent completed (this session stream)
- Editable after commit: supplier name, issue date (cascades), per-ROW pracovisko
  (usage rows follow; mint/move/remove). Manual add-row during review
  (`POST {id}/delivery-lists/{purchaseId}/lines`). Per-row delete, editable S DPH/zľava.
- Financie report: rows = ALL locations with activity in range (inactive tagged
  "neaktívne"), synthetic "Sklad / Nepriradené" row, Faktúry column allocates PRINTED
  totals (residual→largest share) so it matches the card to the cent; zero-activity rows
  hidden client-side.
- Locations page: active grid + collapsed "Neaktívne pracoviská (N)" (auto-expands on
  deactivate). Materials Katalóg: grouped by jednotka, inactive collapsed.
- **Admin QOL redesign** (audit → kit → all 18 pages): shared
  `components/alert/AlertComponent`, `components/empty-state/EmptyStateComponent`,
  `services/api-error.service.ts (ApiErrorService.friendly(e, context))`. Fixed silent
  failures (employee-detail save/PIN/photo, car-detail, location-detail, dashboard/
  reports/finance loads, notifications fire-now). Danger ModalComponent replaces browser
  confirm() for destructive cascades. Unified export green + busy states, back links on
  detail pages, focus rings, empty states.
- Upload-another: success modal → "Nahrať ďalšiu"/"Naskenovať mobilom"; committed review
  page → next-document shortcuts.
- Mobile: invoice-review lines render as cards under `lg`.

## OPEN ISSUES (priority order)
1. **PRODUCTION 500 on POST /api/invoices/upload** — reported from
   `dochadzkovnik.vercel.app` → production Railway. The CORS error in console is a RED
   HERRING (ASP.NET 500s lack CORS headers). **Waiting on Railway production logs**
   (stack trace) from the user. Suspects: production deployed a partial master state;
   parser edge case on a specific doc; check whether prod master includes matching
   Program.cs registrations for controller ctor deps (ILlmInvoiceExtractor,
   IHttpClientFactory, ScanStatusService). Production does NOT need Gemini key (graceful).
2. **Uncommitted working tree** — the whole QOL round + pnl/locations/materials changes
   + new kit files are local only; user must commit+push (they know). `dotnet test`
   should be run (parser gate change) before pushing master.
3. Customer-device testing of the native camera flow + poor-light path pending.
4. Minor cleanups: unused PdfSharpCore reference in API.csproj; dead recovery-email code
   in account.page.ts; unused ModalComponent import in mzdy.page.ts; NG8113 warning for
   ModalComponent in MzdyPage template.

## Key files
- Parser: `API/Services/InvoiceParser.cs` (+`IInvoiceParser.cs` records)
- Extractor: `API/Services/GeminiInvoiceExtractor.cs`; health: `API/Services/ScanStatusService.cs`
- Upload/review endpoints: `API/Controllers/InvoicesController.cs`
- PnL/report: `API/Controllers/LocationsController.cs` (`GetPnlSummary`)
- Camera: `client/src/app/pages/invoice-camera/*`; review: `.../invoice-review/*`
- Kit: `client/src/app/components/{alert,empty-state}/`, `client/src/app/services/api-error.service.ts`
- Tests+fixtures: `API.Tests/InvoiceParserTextLayerTests.cs`, `API.Tests/fixtures/*`
- Migrations pending on prod DB if not auto-applied: AddInvoiceLineLocation,
  AddInvoiceDocumentKind.
