using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using API.Data;
using API.DTOs;
using API.Filters;
using API.Models;
using API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace API.Controllers;

/// <summary>
/// Admin invoice scanning controller — see INVOICE_SCANNING_PLAN.md.
/// Manager scans / uploads a supplier invoice; Document AI parses it; the
/// manager reviews + edits the result; commit creates N MaterialPurchases.
///
/// Finance-grade: the commit endpoint re-runs reconciliation server-side
/// even if the client UI already passed it. Any cent-level mismatch
/// blocks the commit.
/// </summary>
[ApiController]
[Route("api/invoices")]
[Authorize]
[RequireFeatureOrSuperAdmin("InvoiceScanning")]
public class InvoicesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBlobStorageService? _blob;
    private readonly IDocumentAiClient? _ocr;
    private readonly IInvoiceParser _parser;
    private readonly ILlmInvoiceExtractor _ai;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<InvoicesController> _log;

    private const string PdfFolderRoot = "invoices";

    public InvoicesController(
        AppDbContext db,
        IInvoiceParser parser,
        ILlmInvoiceExtractor ai,
        IHttpClientFactory httpFactory,
        ScanStatusService scanStatus,
        ILogger<InvoicesController> log,
        IBlobStorageService? blob = null,
        IDocumentAiClient? ocr = null)
    {
        _db = db;
        _parser = parser;
        _ai = ai;
        _httpFactory = httpFactory;
        _scanStatus = scanStatus;
        _log = log;
        _blob = blob;
        _ocr = ocr;
    }

    private readonly ScanStatusService _scanStatus;

    /// <summary>
    /// GET /api/invoices/scan-status — pipeline health for the client
    /// banners. Modes: "ok" (both extractors healthy), "fallback" (AI quota
    /// spent / AI down — Document AI carries, photos may parse worse),
    /// "ai-only" (Document AI outage — AI carries), "down" (both out).
    /// </summary>
    [HttpGet("scan-status")]
    public ActionResult<object> ScanStatus()
    {
        var aiOk = _ai.IsConfigured && !_scanStatus.AiExhausted && !_scanStatus.AiUnhealthy;
        var ocrOk = _ocr != null && !_scanStatus.OcrUnhealthy;
        var mode = aiOk && ocrOk ? "ok"
                 : aiOk          ? "ai-only"
                 : ocrOk         ? "fallback"
                 :                 "down";
        return Ok(new
        {
            mode,
            aiConfigured = _ai.IsConfigured,
            aiExhaustedUntil = _scanStatus.AiExhaustedUntilUtc
        });
    }

    /// <summary>
    /// In-memory version of the reconciliation gate: do the parsed lines
    /// (+ their VAT) land on the printed total? Same tolerance as
    /// RecomputeReconciliationAsync (5 cents + cash rounding headroom).
    /// Used to decide whether the AI fallback should run and whether its
    /// output is trustworthy enough to replace the deterministic parse.
    /// </summary>
    private static bool ParsedReconciles(ParsedInvoice p)
    {
        if (string.IsNullOrWhiteSpace(p.Header.InvoiceNumber)) return false;
        if (p.Header.TotalInclVat is not { } incl) return false;
        var lines = p.DeliveryLists.SelectMany(d => d.Lines).ToList();
        if (lines.Count == 0) return false;
        var excl = lines.Sum(l => l.LineTotalExclVat ?? 0m);
        var vat = lines.Sum(l => Round2((l.LineTotalExclVat ?? 0m) * l.VatRate / 100m));
        return Math.Abs(excl + vat - incl) <= 0.06m;
    }

    // ────────────────────────────────────────────────────────────────
    //  Upload + parse
    // ────────────────────────────────────────────────────────────────

    [HttpPost("upload")]
    [RequestSizeLimit(20 * 1024 * 1024)]   // 20 MB cap
    public async Task<ActionResult<InvoiceDocumentDto>> Upload(IFormFile file, bool photoScan = false)
    {
        if (_ocr == null)
            return StatusCode(503, "OCR nie je nakonfigurované. Skontrolujte Google Document AI nastavenia.");
        if (_blob == null)
            return StatusCode(503, "Úložisko súborov nie je nakonfigurované.");
        if (file == null || file.Length == 0)
            return BadRequest("Súbor je prázdny.");
        if (file.Length > 20 * 1024 * 1024)
            return BadRequest("Súbor je príliš veľký (limit 20 MB).");

        var mime = (file.ContentType ?? "").ToLowerInvariant();
        if (mime != "application/pdf"
            && !mime.StartsWith("image/", StringComparison.Ordinal))
            return BadRequest("Povolené sú iba PDF alebo obrázky.");

        // Buffer the file once — we need it both for Cloudinary upload and for
        // Document AI (no second stream from the same IFormFile).
        byte[] bytes;
        await using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        // Native digital PDF vs. image-derived (photo/scan)? This decides which
        // engine we trust when Gemini doesn't reconcile: a PDF's text layer
        // parses EXACTLY, so PDFs keep the Document AI + parser fallback; photos
        // don't, so on those Gemini wins. Camera uploads pass photoScan=true;
        // direct image uploads are flagged in the enhancement step below.
        var imageDerived = photoScan;

        // If the manager uploaded a PHOTO rather than a PDF, run it through the
        // same enhancement the camera path uses (auto-orient, ≤3000 px,
        // shadow-flatten + sharpen, 4:4:4 JPEG) so the model gets the best
        // raster no matter which upload button was used. PDFs pass through
        // untouched — rasterising a clean digital PDF would only degrade it.
        // Falls back to the original bytes on any decode error (e.g. HEIC).
        if (mime.StartsWith("image/", StringComparison.Ordinal))
        {
            imageDerived = true;
            var enhancedPdf = await EnhanceImageToPdfAsync(bytes);
            if (enhancedPdf != null)
            {
                bytes = enhancedPdf;
                mime = "application/pdf";
            }
        }

        // ── Extraction, Gemini-first ─────────────────────────────────
        // The vision model reads the document for ~$0.002; the Document AI
        // Invoice Parser costs $0.10 per document — and on phone photos the
        // vision read is stronger (it SEES the layout instead of reflowing
        // it). When Gemini's numbers pass the reconciliation cent-gate we
        // trust them and skip Document AI entirely; otherwise the
        // deterministic pipeline runs exactly as before and the acceptance
        // ladder below picks the better result.
        DocumentAiResult? ocr = null;
        ParsedInvoice? accepted = null;
        ParsedInvoice? aiRead = null;

        if (_ai.IsPrimary)
        {
            try
            {
                using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                aiRead = await _ai.ExtractAsync(bytes, mime, null, aiCts.Token);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[InvoiceScanning] Gemini primary read failed — falling back to Document AI.");
            }
            if (aiRead != null && ParsedReconciles(aiRead))
            {
                _log.LogInformation("[InvoiceScanning] Gemini primary reconciled (číslo={Num}, spolu={Total}) — Document AI skipped.",
                    aiRead.Header.InvoiceNumber, aiRead.Header.TotalInclVat);
                accepted = aiRead;
            }
            else if (imageDerived
                     && aiRead != null
                     && !string.IsNullOrWhiteSpace(aiRead.Header.InvoiceNumber)
                     && aiRead.Header.TotalInclVat != null)
            {
                // IMAGE-DERIVED input (photo/scan) whose Gemini read has číslo +
                // total but doesn't cent-reconcile: accept it FOR REVIEW and skip
                // Document AI — on photos the deterministic parser only produces
                // gibberish, so it cannot do better. Native digital PDFs are the
                // opposite (text layer parses exactly), so they fall through to
                // Document AI + parser below and keep that reconciling result.
                _log.LogInformation("[InvoiceScanning] Gemini primary usable but not reconciled on image scan (číslo={Num}, spolu={Total}) — accepted for review, Document AI skipped.",
                    aiRead.Header.InvoiceNumber, aiRead.Header.TotalInclVat);
                accepted = aiRead;
            }
        }

        if (accepted == null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                ocr = await _ocr.ProcessAsync(bytes, mime, cts.Token);
                _scanStatus.MarkOcrOk();
            }
            catch (Exception ex)
            {
                _scanStatus.MarkOcrFailure();
                _log.LogError(ex, "Document AI failed for upload");
                // At this point the AI read has ALSO failed or not reconciled
                // (it runs first) — this is a real outage for this document.
                return StatusCode(502, "Rozpoznávanie dokladov je momentálne nedostupné (výpadok služby). Fotky si nechajte a skúste ich nahrať o pár minút znova.");
            }
        }

        if (ocr != null)
        {
            // Diagnostic log: what entity types did Document AI return? This is
            // what tells us whether the parser is reading the right fields. Visible
            // in the dotnet watch console. Safe to log — entity TYPE names are
            // metadata (e.g. "line_item", "supplier_name"), no PII.
            var typeCounts = ocr.Entities
                .GroupBy(e => e.Type)
                .Select(g => $"{g.Key}({g.Count()})")
                .OrderBy(s => s);
            _log.LogInformation("[InvoiceScanning] Document AI returned {Count} entities, types: {Types}",
                ocr.Entities.Count, string.Join(", ", typeCounts));

            // If any line_item entities exist, log the property types of the FIRST
            // one so we can see what nested field names Document AI uses.
            var firstLine = ocr.Entities.FirstOrDefault(e => e.Type.Contains("line", StringComparison.OrdinalIgnoreCase));
            if (firstLine != null)
            {
                _log.LogInformation("[InvoiceScanning] First line-like entity '{Type}' mention='{Mention}' properties: {Props}",
                    firstLine.Type,
                    Truncate(firstLine.MentionText, 80),
                    string.Join(", ", firstLine.Properties.Select(p => $"{p.Type}='{Truncate(p.MentionText, 30)}'")));
            }
            else
            {
                _log.LogWarning("[InvoiceScanning] Document AI returned ZERO line-item-like entities. Parser will produce empty lines.");
            }
        }

        var parsed = accepted ?? _parser.Parse(ocr!);

        // Per-line diagnostic: dump what the parser produced for every row.
        // Lets us spot when text-based extraction failed (list/discount null)
        // or grabbed wrong values (e.g. list=0 on a non-credit row).
        foreach (var dl in parsed.DeliveryLists)
        {
            _log.LogInformation("[InvoiceScanning] DL={Ref} akcia={Akcia} sub={SubExcl}/{SubVat} lines={N}",
                dl.DeliveryNoteRef, dl.AkciaName, dl.SubtotalExclVat, dl.SubtotalVat, dl.Lines.Count);
            foreach (var l in dl.Lines)
            {
                _log.LogInformation("[InvoiceScanning]   line code={Code} desc='{Desc}' qty={Qty}{Unit} list={List} disc={Disc}% postExcl={PE} total={Tot}",
                    l.SupplierItemCode,
                    Truncate(l.Description, 50),
                    l.Quantity, l.Unit,
                    l.ListPriceExclVat, l.DiscountPercent, l.UnitPriceExclVat, l.LineTotalExclVat);
            }
        }

        // ── AI fallback (Gemini vision) ─────────────────────────────
        // The deterministic parse doesn't reconcile (or missed key fields)
        // → let a vision model read the page; photo scans that Document AI
        // reflows into nonsense are usually trivial for it. Its output
        // faces the SAME arithmetic gate — it can only replace the parse
        // when the lines land cent-exact on the printed total, so it can
        // never invent numbers into the books.
        if (accepted == null && _ai.IsConfigured && !ParsedReconciles(parsed))
        {
            try
            {
                // Reuse the primary Gemini read when it already ran; only the
                // deterministic-first configuration makes a fresh call here
                // (with the OCR text as a hint).
                if (aiRead == null)
                {
                    using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                    aiRead = await _ai.ExtractAsync(bytes, mime, ocr?.FullText, aiCts.Token);
                }
                var detHasBasics = !string.IsNullOrWhiteSpace(parsed.Header.InvoiceNumber)
                                   && parsed.Header.TotalInclVat != null;
                var aiHasBasics = aiRead != null
                                  && !string.IsNullOrWhiteSpace(aiRead.Header.InvoiceNumber)
                                  && aiRead.Header.TotalInclVat != null;
                if (aiRead != null && ParsedReconciles(aiRead))
                {
                    _log.LogInformation("[InvoiceScanning] AI read reconciled (číslo={Num}, spolu={Total}) — replacing the deterministic parse.",
                        aiRead.Header.InvoiceNumber, aiRead.Header.TotalInclVat);
                    parsed = aiRead;
                }
                else if (aiHasBasics && !detHasBasics)
                {
                    // The deterministic parse would be REJECTED outright
                    // (no number / no total). The AI's non-reconciling read
                    // is still far more useful: the document lands in review
                    // with the Nesedí banner for manual correction instead
                    // of bouncing the upload back to the customer.
                    _log.LogInformation("[InvoiceScanning] AI read didn't reconcile but rescued the basics (číslo={Num}, spolu={Total}) — accepting for review.",
                        aiRead!.Header.InvoiceNumber, aiRead.Header.TotalInclVat);
                    parsed = aiRead;
                }
                else
                {
                    _log.LogInformation("[InvoiceScanning] AI read did not improve on the deterministic parse — keeping it.");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[InvoiceScanning] AI fallback failed — continuing with the deterministic parse.");
            }
        }

        // Hard requirements before we even bother uploading the PDF — the
        // manager needs to see something coherent on review.
        if (string.IsNullOrWhiteSpace(parsed.Header.InvoiceNumber) || parsed.Header.TotalInclVat == null)
        {
            // Dump the text layer: rejected photo uploads leave no stored
            // RawOcrJson behind, so this is the only way to see what the OCR
            // produced and teach the parser the layout. Both to the console
            // and to rejected-scans/ next to the app (best-effort).
            _log.LogWarning("[InvoiceScanning] Upload rejected (číslo='{Num}', spolu={Total}). Text layer ({Len} chars):\n{Text}",
                parsed.Header.InvoiceNumber, parsed.Header.TotalInclVat, ocr?.FullText?.Length ?? 0, ocr?.FullText);
            try
            {
                var dumpDir = Path.Combine(Directory.GetCurrentDirectory(), "rejected-scans");
                Directory.CreateDirectory(dumpDir);
                var dumpName = $"{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileNameWithoutExtension(file.FileName)}.txt";
                await System.IO.File.WriteAllTextAsync(Path.Combine(dumpDir, dumpName), ocr?.FullText ?? "");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[InvoiceScanning] Could not write rejected-scan dump.");
            }
            // Both the deterministic parse AND the AI failed at this point —
            // the photo genuinely doesn't carry the information. Big, clear
            // instruction for the customer.
            return BadRequest("DOKLAD SA NEPODARILO PREČÍTAŤ — ani s pomocou AI. Odfoťte ho znova pri LEPŠOM OSVETLENÍ (zapnite blesk), zblízka a s celou stranou v zábere.");
        }

        // 2) Dedup check — invoice number + issue date. Supplier IČO is NOT
        // part of the key (it parses inconsistently across scan qualities —
        // a photo of KOVOUNIBA FV260492 once read the CUSTOMER as the
        // supplier, letting a true duplicate through), but the number alone
        // is too weak for cash receipts (č.bloku 815 / č.d. 145 are short
        // and two vendors can share one), so the date disambiguates.
        var effectiveIssueDate = parsed.Header.IssueDate ?? DateTime.UtcNow.Date;
        var dupExists = await _db.InvoiceDocuments.AnyAsync(d =>
            d.InvoiceNumber == parsed.Header.InvoiceNumber
            && d.IssueDate == effectiveIssueDate);
        if (dupExists)
            return Conflict($"Faktúra {parsed.Header.InvoiceNumber} z {effectiveIssueDate:d.M.yyyy} už bola nahraná.");

        // 3) Upload original to Cloudinary, partitioned by year-month.
        // PDFs go through the raw-upload path (byte-identical, no image
        // normalisation). Photo uploads (when a manager scans with their
        // phone instead of a PDF) go through the normal image path so
        // HEIC/PNG/WebP/BMP get JPEG-normalised like work photos.
        var ym = (parsed.Header.IssueDate ?? DateTime.UtcNow).ToString("yyyy-MM");
        var folder = $"{PdfFolderRoot}/{ym}";
        var isPdf = mime == "application/pdf";
        var ext = isPdf ? ".pdf" : Path.GetExtension(file.FileName ?? "") ?? "";
        if (string.IsNullOrEmpty(ext) && !isPdf) ext = ".jpg";
        var fileName = $"{parsed.Header.InvoiceNumber}-{Guid.NewGuid():N}{ext}";
        string pdfUrl;
        await using (var blobStream = new MemoryStream(bytes))
        {
            pdfUrl = isPdf
                ? await _blob.UploadRawAsync(blobStream, fileName, folder)
                : await _blob.UploadAsync(blobStream, fileName, folder);
        }

        // 4) Persist InvoiceDocument + draft MaterialPurchases + lines.
        // Defensive clipping: Document AI can return surprisingly long strings
        // (extra context glued to a field, OCR noise, etc.). Every string
        // below is clipped to its schema's varchar limit so a fluke OCR doesn't
        // crash the save with "value too long for type character varying(N)".
        var uploader = User.Identity?.Name ?? "unknown";
        var doc = new InvoiceDocument
        {
            InvoiceNumber       = Clip(parsed.Header.InvoiceNumber!, 100),
            SupplierName        = Clip(parsed.Header.SupplierName ?? "(neznámy dodávateľ)", 200),
            SupplierIco         = Clip(parsed.Header.SupplierIco, 50),
            SupplierIcDph       = Clip(parsed.Header.SupplierIcDph, 50),
            SupplierIban        = Clip(parsed.Header.SupplierIban, 50),
            IssueDate           = parsed.Header.IssueDate ?? DateTime.UtcNow.Date,
            DeliveryDate        = parsed.Header.DeliveryDate,
            DueDate             = parsed.Header.DueDate,
            PeriodFrom          = parsed.Header.PeriodFrom,
            PeriodTo            = parsed.Header.PeriodTo,
            Currency            = parsed.Header.Currency,
            TotalExclVat        = Round2(parsed.Header.TotalExclVat ?? 0m),
            TotalVat            = Round2(parsed.Header.TotalVat ?? 0m),
            TotalInclVat        = Round2(parsed.Header.TotalInclVat!.Value),
            PdfUrl              = pdfUrl,
            // Gemini-primary happy path skips Document AI — store a marker so
            // ocr-diagnostic shows the source instead of pretending OCR ran.
            RawOcrJson          = ocr?.RawJson ?? "{\"source\":\"gemini-primary\"}",
            Status              = "review",
            DocumentKind        = parsed.Header.IsReceipt ? "receipt" : "invoice",
            UploadedBy          = uploader,
            UploadedAt          = DateTime.UtcNow
        };
        _db.InvoiceDocuments.Add(doc);
        await _db.SaveChangesAsync();   // get the doc.Id

        var buildError = await BuildDraftPurchasesAsync(doc, parsed);
        if (buildError != null) return BadRequest(buildError);

        await _db.SaveChangesAsync();

        // Reconciliation: run it once now so the manager sees the result.
        await RecomputeReconciliationAsync(doc.Id);

        return await BuildDtoAsync(doc.Id);
    }

    /// <summary>
    /// Materialise a ParsedInvoice into draft MaterialPurchases + lines for
    /// the given document. Shared by the upload path and the AI re-parse.
    /// Returns a Slovak error message instead of throwing when the DB lacks
    /// prerequisites (no active employee).
    /// </summary>
    private async Task<string?> BuildDraftPurchasesAsync(InvoiceDocument doc, ParsedInvoice parsed)
    {
        // The "buyer" for these draft purchases is the uploading manager. We
        // don't know which Employee that is without a JWT→Employee mapping;
        // for V1 we attach to the first active admin Employee, or fail loudly
        // if none. Practical: there's always one in this customer's DB.
        var firstActiveEmp = await _db.Employees.FirstOrDefaultAsync(e => e.IsActive);
        if (firstActiveEmp == null)
            return "V databáze nie je žiadny aktívny zamestnanec, ku ktorému by sa dali priradiť nákupy.";

        // Build draft MaterialPurchase + MaterialPurchaseLine rows.
        var locationsByNormName = await _db.Locations
            .Where(l => l.IsActive)
            .ToDictionaryAsync(l => NormalizeForMatch(l.Name), l => l.Id);

        // Index active employees by normalised last name so we can map the
        // invoice's prevzal text (e.g. "p. Sroka") onto a real Employee.
        // Falling back to last-name avoids the "p." / "Mgr." prefix noise.
        var activeEmployees = await _db.Employees.Where(e => e.IsActive).ToListAsync();

        foreach (var dl in parsed.DeliveryLists)
        {
            // ── Match prevzal → Employee ────────────────────────────
            // Strip honorifics ("p.", "pán", "ing.", "mgr.") and split into
            // tokens, then look for an Employee whose normalised last name
            // appears in the candidate set. Unique match wins.
            var pickedUpEmpId = MatchEmployeeFromPickedUpBy(dl.PickedUpBy, activeEmployees);
            // Auto-map → Location by normalised name match against akcia first,
            // then prevzal / Pozn.DL as fallback. Some DEK delivery-note layouts
            // omit the "akcia:" label and crash the site name into the prevzal
            // continuation, so we have to scan all three candidates.
            int? autoLocationId = null;
            string[] rawCandidates = { dl.AkciaName ?? "", dl.PickedUpBy ?? "", dl.Note ?? "" };
            foreach (var raw in rawCandidates)
            {
                var n = NormalizeForMatch(raw);
                if (string.IsNullOrEmpty(n)) continue;
                if (locationsByNormName.TryGetValue(n, out var exact))
                {
                    autoLocationId = exact;
                    break;
                }
                var hits = locationsByNormName
                    .Where(kv => n.Contains(kv.Key))
                    .Select(kv => (int?)kv.Value)
                    .ToList();
                if (hits.Count == 1) { autoLocationId = hits[0]; break; }
            }

            var purchase = new MaterialPurchase
            {
                PurchaseDate        = dl.DeliveryDate ?? doc.IssueDate,
                EmployeeId          = pickedUpEmpId ?? firstActiveEmp.Id,
                LocationId          = autoLocationId,
                InvoiceDocumentId   = doc.Id,
                DeliveryNoteRef     = Clip(dl.DeliveryNoteRef, 100),
                PickedUpBy          = Clip(dl.PickedUpBy, 200),
                DeliveryNote        = Clip(dl.Note, 2000),
                AkciaName           = Clip(dl.AkciaName, 200),
                SupplierName        = doc.SupplierName,
                SubtotalExclVat     = dl.SubtotalExclVat.HasValue ? Round2(dl.SubtotalExclVat.Value) : (decimal?)null,
                SubtotalVat         = dl.SubtotalVat.HasValue     ? Round2(dl.SubtotalVat.Value)     : (decimal?)null
            };
            foreach (var l in dl.Lines)
            {
                var qty   = l.Quantity ?? 0m;
                var unit  = l.UnitPriceExclVat ?? 0m;
                var total = l.LineTotalExclVat ?? Round2(qty * unit);
                purchase.Lines.Add(new MaterialPurchaseLine
                {
                    MaterialNameRaw   = Clip(l.Description, 200) ?? "(bez popisu)",
                    Unit              = Clip(l.Unit, 50) ?? "ks",
                    Quantity          = qty,
                    UnitPrice         = unit,
                    LineTotal         = total,
                    SupplierItemCode  = Clip(l.SupplierItemCode, 50),
                    ListPriceExclVat  = l.ListPriceExclVat,
                    DiscountPercent   = l.DiscountPercent,
                    UnitPriceInclVat  = l.UnitPriceInclVat,
                    VatRate           = l.VatRate,
                    IsReverseCharge   = l.IsReverseCharge,
                    IsService         = l.IsService
                });
            }
            purchase.TotalCost = purchase.Lines.Sum(l => l.LineTotal);
            _db.MaterialPurchases.Add(purchase);
        }
        return null;
    }

    /// <summary>
    /// Camera-scan upload: accepts N image files (one per page of a paper
    /// invoice), normalises them via ImageSharp (EXIF auto-rotate + downscale
    /// long edge to 2200 px + JPEG q=82), assembles into a single PDF with
    /// the in-house JPEG writer (one image per page, page sized to image
    /// aspect — see <see cref="BuildJpegPdf"/>), then delegates to the
    /// existing <see cref="Upload"/> path so the downstream
    /// OCR / parser / persistence is identical. After the upload returns,
    /// stamps the resulting <c>InvoiceDocument</c> with
    /// <c>ScanSource = "camera"</c> and <c>ScanPageCount = files.Count</c>.
    ///
    /// Stage 1 of INVOICE_SCANNING_CAMERA_STAGES.md. The new endpoint is
    /// what every later stage's frontend code ultimately posts to.
    /// </summary>
    [HttpPost("upload-photos")]
    [RequestSizeLimit(40 * 1024 * 1024)]   // 40 MB cap across all photos
    public async Task<ActionResult<InvoiceDocumentDto>> UploadPhotos(List<IFormFile> files)
    {
        if (_ocr == null)
            return StatusCode(503, "OCR nie je nakonfigurované. Skontrolujte Google Document AI nastavenia.");
        if (_blob == null)
            return StatusCode(503, "Úložisko súborov nie je nakonfigurované.");
        if (files == null || files.Count == 0)
            return BadRequest("Nahrajte aspoň jednu fotku.");
        if (files.Count > 10)
            return BadRequest("Maximálne 10 fotiek na jednu faktúru.");

        foreach (var f in files)
        {
            var imgMime = (f.ContentType ?? "").ToLowerInvariant();
            if (!imgMime.StartsWith("image/", StringComparison.Ordinal))
                return BadRequest("Povolené sú iba obrázky (image/*).");
            if (f.Length == 0)
                return BadRequest("Niektorá fotka je prázdna.");
            if (f.Length > 10 * 1024 * 1024)
                return BadRequest("Fotka je príliš veľká (limit 10 MB na fotku).");
        }

        // Single photo: skip the PDF assembly entirely — Document AI accepts
        // images directly (the picker image-upload path has always worked
        // this way), so one less moving part. Multi-page invoices still get
        // assembled below so all pages reach OCR as ONE document.
        if (files.Count == 1)
        {
            var singleResult = await Upload(files[0], photoScan: true);
            if (singleResult.Result is OkObjectResult okSingle && okSingle.Value is InvoiceDocumentDto okSingleDto)
            {
                await StampScanProvenanceAsync(okSingleDto.Id, 1);
                return await BuildDtoAsync(okSingleDto.Id);
            }
            if (singleResult.Value is InvoiceDocumentDto singleDto)
            {
                await StampScanProvenanceAsync(singleDto.Id, 1);
                return await BuildDtoAsync(singleDto.Id);
            }
            return singleResult;
        }

        // 1) Build the PDF on the server. CombineImagesToPdfAsync handles
        //    EXIF rotation, downscaling, and JPEG re-encoding per image.
        byte[] pdfBytes;
        try
        {
            pdfBytes = await CombineImagesToPdfAsync(files);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[InvoiceCameraScan] Failed to combine {N} photos into PDF", files.Count);
            return StatusCode(500, "Nepodarilo sa zostaviť PDF z fotiek. Skúste znova.");
        }

        _log.LogInformation("[InvoiceCameraScan] Combined {N} photos into a {Bytes}-byte PDF",
            files.Count, pdfBytes.Length);

        // 2) Wrap the assembled PDF as an IFormFile and reuse the existing
        //    Upload path verbatim. Anything that worked for PDF-picker
        //    uploads — Document AI call, dedup, Cloudinary upload, draft
        //    MaterialPurchase creation, reconciliation — works here too.
        await using var pdfStream = new MemoryStream(pdfBytes);
        var pdfFormFile = new FormFile(pdfStream, 0, pdfBytes.Length,
            name: "file",
            fileName: $"camera-scan-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };
        var result = await Upload(pdfFormFile, photoScan: true);

        // 3) Stamp scan provenance on the newly-created InvoiceDocument so
        //    the list page can render a different icon for camera-scanned
        //    invoices and so analytics can correlate parse quality with
        //    page count down the line.
        if (result.Result is OkObjectResult ok && ok.Value is InvoiceDocumentDto okDto)
        {
            await StampScanProvenanceAsync(okDto.Id, files.Count);
            return await BuildDtoAsync(okDto.Id);
        }
        if (result.Value is InvoiceDocumentDto dto)
        {
            await StampScanProvenanceAsync(dto.Id, files.Count);
            return await BuildDtoAsync(dto.Id);
        }
        // Pass through any non-success result from Upload (validation,
        // 502 from Document AI, 409 dedup, etc.) — same wire shape as the
        // file-picker path.
        return result;
    }

    private async Task StampScanProvenanceAsync(int invoiceDocumentId, int pageCount)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(invoiceDocumentId);
        if (doc == null) return;
        doc.ScanSource = "camera";
        doc.ScanPageCount = pageCount;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Normalises each uploaded image (EXIF auto-rotate, downscale long
    /// edge to 3000 px if larger, JPEG re-encode at quality 90) and
    /// embeds the result one-per-page into a PDF. Page size matches each
    /// image's aspect ratio so Document AI gets the cleanest possible
    /// rasterisation. Returns the PDF bytes.
    ///
    /// The PDF itself is written by hand (BuildJpegPdf) — PdfSharpCore 1.3.x
    /// is binary-incompatible with ImageSharp 3.x (its XImage.FromStream
    /// calls the removed Image.Load(Stream, out IImageFormat) overload and
    /// dies with MissingMethodException), and embedding JPEGs one-per-page
    /// is all we need from a PDF library anyway.
    /// </summary>
    private static async Task<byte[]> CombineImagesToPdfAsync(IReadOnlyList<IFormFile> files)
    {
        // Matches the client scanner's output band (≤3000 px, q≥0.9) — the
        // server must not undo the capture-quality work by recompressing.
        const int maxLongEdge = 3000;
        const int jpegQuality = 90;

        var pages = new List<(byte[] Jpeg, int Width, int Height)>(files.Count);

        foreach (var file in files)
        {
            // ImageSharp loads almost anything (JPEG/PNG/WebP/BMP/HEIC via plugins,
            // but we keep just the built-in formats; client-side normaliseFile
            // converts HEIC → PNG before sending).
            await using var inStream = file.OpenReadStream();
            using var image = await Image.LoadAsync(inStream);

            // Ensure the page is upright before anything else — a sideways photo
            // is much harder for the model to read. AutoOrient applies the EXIF
            // orientation and clears it; it's a harmless no-op if the decoder
            // already rotated the pixels.
            image.Mutate(x => x.AutoOrient());

            // Downscale only if the long edge exceeds the cap.
            var longEdge = Math.Max(image.Width, image.Height);
            if (longEdge > maxLongEdge)
            {
                var scale = (double)maxLongEdge / longEdge;
                image.Mutate(x => x.Resize(
                    (int)Math.Round(image.Width  * scale),
                    (int)Math.Round(image.Height * scale)));
            }

            // OCR enhancement — server-side on purpose. This used to run in
            // the browser (OpenCV wasm) and its full-page buffers kept
            // exhausting phone browsers; here memory is a non-issue and every
            // device gets the identical pipeline:
            //  1. shadow flattening (divide by the low-frequency background),
            //     gated on measured unevenness so clean scans pass untouched;
            //  2. a mild sharpen for the soft video-pipeline captures.
            EnhanceForOcr(image);

            // Re-encode as JPEG — the bytes go into the PDF verbatim
            // (DCTDecode), so this is the exact raster the model will see.
            // Full 4:4:4 chroma (no subsampling) keeps fine text edges crisp for
            // the vision model. Still 3-component YCbCr, so the PDF's /DeviceRGB
            // colour space stays correct, even for grayscale sources.
            using var jpegStream = new MemoryStream();
            await image.SaveAsJpegAsync(jpegStream, new JpegEncoder
            {
                Quality = jpegQuality,
                ColorType = JpegEncodingColor.YCbCrRatio444
            });
            pages.Add((jpegStream.ToArray(), image.Width, image.Height));
        }

        return BuildJpegPdf(pages);
    }

    /// <summary>
    /// Enhance a single uploaded photo (buffered bytes) into a 1-page JPEG PDF,
    /// applying the same pipeline as the camera path (auto-orient, ≤3000 px,
    /// shadow-flatten + sharpen, 4:4:4 JPEG). Returns null when the bytes aren't
    /// a decodable raster (e.g. HEIC without a plugin) so the caller can fall
    /// back to sending the original file untouched.
    /// </summary>
    private static async Task<byte[]?> EnhanceImageToPdfAsync(byte[] imageBytes)
    {
        const int maxLongEdge = 3000;
        const int jpegQuality = 90;
        try
        {
            await using var ms = new MemoryStream(imageBytes);
            using var image = await Image.LoadAsync(ms);

            image.Mutate(x => x.AutoOrient());
            var longEdge = Math.Max(image.Width, image.Height);
            if (longEdge > maxLongEdge)
            {
                var scale = (double)maxLongEdge / longEdge;
                image.Mutate(x => x.Resize(
                    (int)Math.Round(image.Width  * scale),
                    (int)Math.Round(image.Height * scale)));
            }
            EnhanceForOcr(image);

            using var jpegStream = new MemoryStream();
            await image.SaveAsJpegAsync(jpegStream, new JpegEncoder
            {
                Quality = jpegQuality,
                ColorType = JpegEncodingColor.YCbCrRatio444
            });
            return BuildJpegPdf(new[] { (jpegStream.ToArray(), image.Width, image.Height) });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// In-place OCR enhancement for photographed pages. Shadow bands across
    /// a page (window light, phone shadow — see the HEKTRANS photo scan)
    /// wreck Document AI far more than mild blur does: the text under the
    /// band loses contrast against its local background. Dividing each pixel
    /// by the heavily-blurred background evens the illumination while text
    /// edges survive. Gated: when the background is already uniform
    /// (relative σ &lt; 0.10) the image passes through with only the sharpen.
    /// </summary>
    private static void EnhanceForOcr(Image image)
    {
        try
        {
            using var rgb = image.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgb24>();

            // Background = heavy blur of a 1/8-scale luminance copy, scaled
            // back up. Cheap and smooth enough for illumination estimation.
            var bgW = Math.Max(1, rgb.Width / 8);
            var bgH = Math.Max(1, rgb.Height / 8);
            using var bg = rgb.CloneAs<SixLabors.ImageSharp.PixelFormats.L8>();
            bg.Mutate(x => x.Resize(bgW, bgH).GaussianBlur(12f).Resize(rgb.Width, rgb.Height));

            // Unevenness gate: relative stddev of the background.
            double sum = 0, sumSq = 0;
            long n = 0;
            bg.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < rows.Height; y += 4)          // sample every 4th row
                {
                    var row = rows.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x += 4)
                    {
                        double v = row[x].PackedValue;
                        sum += v;
                        sumSq += v * v;
                        n++;
                    }
                }
            });
            if (n == 0) return;
            var mean = sum / n;
            var std = Math.Sqrt(Math.Max(0, sumSq / n - mean * mean));
            var uneven = mean > 1 && std / mean >= 0.10;

            if (uneven)
            {
                // out = clamp(pixel × 235 / background) per channel.
                rgb.ProcessPixelRows(bg, (srcRows, bgRows) =>
                {
                    for (var y = 0; y < srcRows.Height; y++)
                    {
                        var src = srcRows.GetRowSpan(y);
                        var bgr = bgRows.GetRowSpan(y);
                        for (var x = 0; x < src.Length; x++)
                        {
                            var d = bgr[x].PackedValue + 1;        // +1 avoids ÷0
                            var p = src[x];
                            src[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(
                                (byte)Math.Min(255, p.R * 235 / d),
                                (byte)Math.Min(255, p.G * 235 / d),
                                (byte)Math.Min(255, p.B * 235 / d));
                        }
                    }
                });
                // Copy the flattened pixels back into the caller's image.
                image.Mutate(x => x.DrawImage(rgb, 1f));
            }

            // Mild unsharp for the soft phone-video captures.
            image.Mutate(x => x.GaussianSharpen(1.2f));
        }
        catch
        {
            // Enhancement is best-effort — the un-enhanced page still scans.
        }
    }

    /// <summary>
    /// Minimal dependency-free PDF writer: one baseline JPEG per page,
    /// embedded verbatim as an Image XObject with the DCTDecode filter.
    /// Page size = image size in points (1 px = 1 pt — absolute scale is
    /// irrelevant to Document AI, only the aspect ratio matters).
    /// Object layout: 1 = Catalog, 2 = Pages, then per page i (0-based):
    /// 3+3i = Page, 4+3i = Contents, 5+3i = Image.
    /// </summary>
    private static byte[] BuildJpegPdf(IReadOnlyList<(byte[] Jpeg, int Width, int Height)> pages)
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();   // byte offset of object k+1 at index k

        void WriteAscii(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }
        void BeginObj(int num)
        {
            offsets.Add(ms.Position);
            WriteAscii($"{num} 0 obj\n");
        }

        WriteAscii("%PDF-1.4\n");

        var n = pages.Count;
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{3 + 3 * i} 0 R"));

        BeginObj(1);
        WriteAscii("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObj(2);
        WriteAscii($"<< /Type /Pages /Kids [{kids}] /Count {n} >>\nendobj\n");

        for (var i = 0; i < n; i++)
        {
            var (jpeg, w, h) = pages[i];
            var pageObj    = 3 + 3 * i;
            var contentObj = 4 + 3 * i;
            var imageObj   = 5 + 3 * i;

            BeginObj(pageObj);
            WriteAscii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
                       $"/Resources << /XObject << /Im{i} {imageObj} 0 R >> >> " +
                       $"/Contents {contentObj} 0 R >>\nendobj\n");

            // Content stream: scale the unit-square image XObject to fill the page.
            var content = Encoding.ASCII.GetBytes($"q\n{w} 0 0 {h} 0 0 cm\n/Im{i} Do\nQ\n");
            BeginObj(contentObj);
            WriteAscii($"<< /Length {content.Length} >>\nstream\n");
            ms.Write(content, 0, content.Length);
            WriteAscii("endstream\nendobj\n");

            BeginObj(imageObj);
            WriteAscii($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
                       "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode " +
                       $"/Length {jpeg.Length} >>\nstream\n");
            ms.Write(jpeg, 0, jpeg.Length);
            WriteAscii("\nendstream\nendobj\n");
        }

        // Cross-reference table: entries are exactly 20 bytes each.
        var xrefPos = ms.Position;
        WriteAscii($"xref\n0 {offsets.Count + 1}\n");
        WriteAscii("0000000000 65535 f \n");
        foreach (var off in offsets)
            WriteAscii($"{off:D10} 00000 n \n");
        WriteAscii($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");

        return ms.ToArray();
    }

    // ────────────────────────────────────────────────────────────────
    //  Read
    // ────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<List<InvoiceDocumentDto>>> List(
        [FromQuery] string? status,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? supplier)
    {
        var q = _db.InvoiceDocuments.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(d => d.Status == status);
        if (from.HasValue) q = q.Where(d => d.IssueDate >= from.Value.Date);
        if (to.HasValue)   q = q.Where(d => d.IssueDate <  to.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(supplier))
        {
            var sLower = supplier.Trim().ToLower();
            q = q.Where(d => d.SupplierName.ToLower().Contains(sLower));
        }

        var rows = await q
            .Include(d => d.Purchases).ThenInclude(p => p.Location)
            .Include(d => d.Purchases).ThenInclude(p => p.Lines).ThenInclude(l => l.Location)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();
        return rows.Select(BuildSummaryDto).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<InvoiceDocumentDto>> Get(int id)
    {
        var exists = await _db.InvoiceDocuments.AnyAsync(d => d.Id == id);
        if (!exists) return NotFound();
        // First-time visit after the AkciaName column was added: backfill the
        // akcia text from the stored OCR JSON so the auto-matcher below has
        // something to work with on invoices that were uploaded before the
        // column existed. No-op once every purchase has AkciaName populated.
        await BackfillAkciaNamesIfNeededAsync(id);
        // Opportunistic auto-match: any delivery list whose Location is still
        // null gets re-checked against the current Pracovisko list. Useful
        // when the manager created the matching Pracovisko AFTER uploading
        // the invoice — they shouldn't have to manually assign each one.
        // Never touches a delivery list that already has a LocationId — the
        // manager's explicit choice is preserved.
        await AutoMatchLocationsAsync(id);
        return await BuildDtoAsync(id);
    }

    /// <summary>
    /// Re-derive AkciaName for delivery lists where it's NULL by re-running
    /// the parser's text-extraction pass against the stored RawOcrJson.
    /// Cheap (regex over a stored string) and idempotent — once a row has
    /// AkciaName the work is skipped on subsequent calls.
    /// </summary>
    private async Task BackfillAkciaNamesIfNeededAsync(int invoiceId)
    {
        var doc = await _db.InvoiceDocuments
            .Include(d => d.Purchases)
            .FirstOrDefaultAsync(d => d.Id == invoiceId);
        if (doc == null) return;

        var needsBackfill = doc.Purchases
            .Where(p => string.IsNullOrEmpty(p.AkciaName) && !string.IsNullOrEmpty(p.DeliveryNoteRef))
            .ToList();
        _log.LogInformation("[InvoiceScanning] Backfill: invoice {Id}, {Total} purchases, {Need} need backfill.",
            invoiceId, doc.Purchases.Count, needsBackfill.Count);
        if (needsBackfill.Count == 0) return;
        if (string.IsNullOrEmpty(doc.RawOcrJson))
        {
            _log.LogWarning("[InvoiceScanning] Backfill needed but RawOcrJson is empty.");
            return;
        }

        string text;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(doc.RawOcrJson);
            text = json.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[InvoiceScanning] Failed to parse RawOcrJson.");
            return;
        }
        if (text.Length == 0)
        {
            _log.LogWarning("[InvoiceScanning] RawOcrJson has empty 'text' property.");
            return;
        }

        var dummy = new DocumentAiResult("", Array.Empty<DocumentAiEntity>(), text);
        var reparsed = _parser.Parse(dummy);

        var akciaByRef = reparsed.DeliveryLists
            .Where(dl => !string.IsNullOrEmpty(dl.DeliveryNoteRef) && !string.IsNullOrEmpty(dl.AkciaName))
            .ToDictionary(dl => dl.DeliveryNoteRef!, dl => dl.AkciaName!);
        _log.LogInformation("[InvoiceScanning] Backfill: extracted {N} ref→akcia mappings: {Map}",
            akciaByRef.Count, string.Join(", ", akciaByRef.Select(kv => $"{kv.Key}={kv.Value}")));

        var changed = 0;
        foreach (var p in needsBackfill)
        {
            if (akciaByRef.TryGetValue(p.DeliveryNoteRef!, out var akcia))
            {
                p.AkciaName = Clip(akcia, 200);
                _log.LogInformation("[InvoiceScanning]   DL={Ref} → akcia '{Akcia}'", p.DeliveryNoteRef, p.AkciaName);
                changed++;
            }
            else
            {
                _log.LogWarning("[InvoiceScanning]   DL={Ref} → no akcia found in text", p.DeliveryNoteRef);
            }
        }
        if (changed > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogInformation("[InvoiceScanning] Backfill saved {N} rows.", changed);
        }
    }

    /// <summary>
    /// Fill in any null LocationId on this invoice's delivery lists by
    /// matching the stored akcia text against active Pracovisko names
    /// (case-insensitive, diacritics-stripped, both directions). No-op
    /// when no nulls exist or no matches are found.
    /// </summary>
    private async Task AutoMatchLocationsAsync(int invoiceId)
    {
        // Include rows where AkciaName is null but PickedUpBy/DeliveryNote
        // could still carry the site name. DEK's "26DN..." delivery-note
        // layout sometimes drops the "akcia:" label and crams the site name
        // into the prevzal continuation (e.g. "p. Sroka - stavba Bratislava
        // - p. Sorka 0902 099 999"). The fallback below scans those fields.
        var purchases = await _db.MaterialPurchases
            .Where(p => p.InvoiceDocumentId == invoiceId
                     && p.LocationId == null)
            .ToListAsync();
        _log.LogInformation("[InvoiceScanning] AutoMatch: invoice {Id}, {N} purchases need matching.",
            invoiceId, purchases.Count);
        if (purchases.Count == 0) return;

        var locations = await _db.Locations.Where(l => l.IsActive).ToListAsync();
        // GroupBy + ToDictionary (taking first) is safer than ToDictionary
        // directly — two Pracoviská with the same diacritics-stripped name
        // would otherwise crash with "An item with the same key has been added".
        var locationsByNormName = locations
            .GroupBy(l => NormalizeForMatch(l.Name))
            .ToDictionary(g => g.Key, g => g.First().Id);
        _log.LogInformation("[InvoiceScanning] AutoMatch: {N} active Pracoviská: [{Names}]",
            locations.Count, string.Join(", ", locations.Select(l => $"'{l.Name}'→'{NormalizeForMatch(l.Name)}'")));
        if (locationsByNormName.Count == 0) return;

        var changed = 0;
        foreach (var p in purchases)
        {
            // Candidates in priority order: akcia (cleanest), then prevzal +
            // delivery-note as a fallback when akcia is missing. Stop at the
            // first one that yields a unique location.
            var candidates = new[]
            {
                ("akcia",        p.AkciaName),
                ("prevzal",      p.PickedUpBy),
                ("deliveryNote", p.DeliveryNote)
            };

            int? matchedId = null;
            string matchedSource = "";
            string matchedNorm = "";
            string matchedHow = "";

            foreach (var (label, raw) in candidates)
            {
                var n = NormalizeForMatch(raw);
                if (string.IsNullOrEmpty(n)) continue;

                if (locationsByNormName.TryGetValue(n, out var exactId))
                {
                    matchedId = exactId;
                    matchedSource = label;
                    matchedNorm = n;
                    matchedHow = "exact";
                    break;
                }

                // Substring match — but only when the Pracovisko name appears
                // INSIDE the candidate text (the inverse direction risks
                // matching "Bratislava" against an akcia text of "BA" etc.).
                // Need a unique winner across all locations or we abstain.
                var hits = locationsByNormName
                    .Where(kv => n.Contains(kv.Key))
                    .ToList();
                if (hits.Count == 1)
                {
                    matchedId = hits[0].Value;
                    matchedSource = label;
                    matchedNorm = n;
                    matchedHow = $"contains '{hits[0].Key}'";
                    break;
                }
                if (hits.Count > 1)
                {
                    _log.LogInformation("[InvoiceScanning]   DL={Ref} {Label}='{Raw}' (norm='{Norm}') → ambiguous ({N} candidates)",
                        p.DeliveryNoteRef, label, raw, n, hits.Count);
                }
            }

            if (matchedId.HasValue)
            {
                p.LocationId = matchedId.Value;
                _log.LogInformation("[InvoiceScanning]   DL={Ref} matched via {Source}='{Norm}' ({How}) → Location#{Id}",
                    p.DeliveryNoteRef, matchedSource, matchedNorm, matchedHow, matchedId.Value);
                changed++;
            }
            else
            {
                _log.LogInformation("[InvoiceScanning]   DL={Ref} no match (akcia='{Akcia}' prevzal='{Prev}' pozn='{Pozn}')",
                    p.DeliveryNoteRef, p.AkciaName, p.PickedUpBy, p.DeliveryNote);
            }
        }
        if (changed > 0)
        {
            await _db.SaveChangesAsync();
            _log.LogInformation("[InvoiceScanning] AutoMatch updated {N} purchases.", changed);
        }
    }

    /// <summary>
    /// Diagnostic endpoint: returns a summary of what Document AI extracted for
    /// this invoice, so we can see whether expected entities like "line_item"
    /// are present + what their property names look like. Helps tune the
    /// parser when the standard Invoice Parser shape doesn't match an unusual
    /// document layout (the SK construction summary-invoice is one such case).
    /// </summary>
    [HttpGet("{id}/ocr-diagnostic")]
    public async Task<ActionResult<object>> OcrDiagnostic(int id)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        if (string.IsNullOrWhiteSpace(doc.RawOcrJson))
            return BadRequest("Žiadne uložené OCR dáta.");

        // Re-parse the raw JSON to count entity types + sample line items.
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(doc.RawOcrJson);
            var root = json.RootElement;
            var entities = root.TryGetProperty("entities", out var e) ? e : default;

            var typeCounts = new Dictionary<string, int>();
            var samples    = new List<object>();
            if (entities.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var ent in entities.EnumerateArray())
                {
                    var t = ent.TryGetProperty("type", out var tv) ? tv.GetString() ?? "?" : "?";
                    typeCounts[t] = typeCounts.TryGetValue(t, out var c) ? c + 1 : 1;

                    // Sample first 3 of any "line_item"-like entity for inspection.
                    if (t.Contains("line", StringComparison.OrdinalIgnoreCase) && samples.Count < 6)
                    {
                        var mention = ent.TryGetProperty("mentionText", out var m) ? m.GetString() : null;
                        var props   = new List<object>();
                        if (ent.TryGetProperty("properties", out var pp) && pp.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var p in pp.EnumerateArray())
                            {
                                props.Add(new {
                                    type = p.TryGetProperty("type", out var pt) ? pt.GetString() : null,
                                    text = p.TryGetProperty("mentionText", out var pm) ? pm.GetString() : null
                                });
                            }
                        }
                        samples.Add(new { type = t, mentionText = mention, properties = props });
                    }
                }
            }

            var fullText = root.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "") : "";
            return Ok(new {
                totalEntities = typeCounts.Values.Sum(),
                entityTypes   = typeCounts.OrderByDescending(kv => kv.Value).Select(kv => new { type = kv.Key, count = kv.Value }),
                textLength    = fullText.Length,
                // Full OCR text — needed to build supplier-specific text parsers
                // for layouts where Document AI's structured entities are poor.
                text          = fullText,
                lineLikeSamples = samples
            });
        }
        catch (Exception ex)
        {
            return BadRequest($"Diagnostic parse zlyhal: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Edit (line + delivery list)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Delete a single line during review. Photo scans regularly produce
    /// phantom rows (OCR junk fragments become "lines") — the manager prunes
    /// them here instead of editing zeros into them. Parent purchase totals
    /// and the reconciliation are recomputed; locked after commit like every
    /// other line edit.
    /// </summary>
    [HttpDelete("{id}/lines/{lineId}")]
    public async Task<ActionResult<InvoiceDocumentDto>> DeleteLine(int id, int lineId)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        if (doc.Status != "review") return BadRequest("Faktúru nemožno upravovať po uložení / zahodení.");

        var line = await _db.MaterialPurchaseLines
            .Include(l => l.Purchase)
            .FirstOrDefaultAsync(l => l.Id == lineId && l.Purchase.InvoiceDocumentId == id);
        if (line == null) return NotFound();

        var purchaseId = line.PurchaseId;
        _db.MaterialPurchaseLines.Remove(line);
        await _db.SaveChangesAsync();

        // Recompute parent purchase totals from the remaining lines.
        var purchase = await _db.MaterialPurchases
            .Include(p => p.Lines)
            .FirstAsync(p => p.Id == purchaseId);
        purchase.TotalCost       = purchase.Lines.Sum(l => l.LineTotal);
        purchase.SubtotalExclVat = Round2(purchase.Lines.Sum(l => l.LineTotal));
        purchase.SubtotalVat     = Round2(purchase.Lines.Sum(l => Round2(l.LineTotal * l.VatRate / 100m)));
        await _db.SaveChangesAsync();

        await RecomputeReconciliationAsync(id);
        return await BuildDtoAsync(id);
    }

    [HttpPut("{id}/lines/{lineId}")]
    public async Task<ActionResult<InvoiceDocumentDto>> UpdateLine(int id, int lineId, [FromBody] UpdateInvoiceLineDto dto)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        // After commit only the per-row Pracovisko stays editable — the same
        // rule the delivery-list endpoint applies; the numbers are financial
        // history. Discarded documents are fully read-only.
        var locationOnly = dto.LocationId.HasValue
                        && dto.SupplierItemCode == null && dto.MaterialNameRaw == null
                        && dto.Unit == null && dto.Quantity == null && dto.UnitPrice == null
                        && dto.LineTotal == null && dto.VatRate == null
                        && dto.DiscountPercent == null && dto.IsReverseCharge == null
                        && dto.IsService == null;
        if (doc.Status != "review" && !(doc.Status == "committed" && locationOnly))
            return BadRequest("Faktúru nemožno upravovať po uložení / zahodení.");

        var line = await _db.MaterialPurchaseLines
            .Include(l => l.Purchase)
            .FirstOrDefaultAsync(l => l.Id == lineId && l.Purchase.InvoiceDocumentId == id);
        if (line == null) return NotFound();

        var editor = User.Identity?.Name ?? "unknown";
        var edits = DeserializeHistory(line.LineEditHistory);

        TryEdit(edits, editor, "supplierItemCode", line.SupplierItemCode, dto.SupplierItemCode, v => line.SupplierItemCode = v);
        TryEdit(edits, editor, "materialNameRaw",  line.MaterialNameRaw, dto.MaterialNameRaw, v => { if (v != null) line.MaterialNameRaw = v; });
        TryEdit(edits, editor, "unit",             line.Unit,            dto.Unit,            v => { if (v != null) line.Unit = v; });
        TryEditDecimal(edits, editor, "quantity",  line.Quantity, dto.Quantity, v => line.Quantity = v);
        TryEditDecimal(edits, editor, "unitPrice", line.UnitPrice, dto.UnitPrice, v => line.UnitPrice = v);
        TryEditDecimal(edits, editor, "lineTotal", line.LineTotal, dto.LineTotal, v => line.LineTotal = v);
        TryEditDecimal(edits, editor, "vatRate",   line.VatRate,   dto.VatRate,   v => line.VatRate = v);

        // Zľava % — informational (doesn't recompute the total; the receipt's
        // printed prices are already discounted). 0 or negative clears it.
        if (dto.DiscountPercent.HasValue)
        {
            var newDisc = dto.DiscountPercent.Value > 0m ? dto.DiscountPercent.Value : (decimal?)null;
            if (line.DiscountPercent != newDisc)
            {
                edits.Add(new EditRecord("discountPercent",
                    line.DiscountPercent?.ToString(CultureInfo.InvariantCulture),
                    newDisc?.ToString(CultureInfo.InvariantCulture),
                    editor, DateTime.UtcNow));
                line.DiscountPercent = newDisc;
            }
        }
        TryEditBool(edits, editor, "isReverseCharge", line.IsReverseCharge, dto.IsReverseCharge, v => line.IsReverseCharge = v);
        TryEditBool(edits, editor, "isService",       line.IsService,       dto.IsService,       v => line.IsService = v);

        // Per-line site override. A positive Location.Id assigns this row to a
        // different site than its delivery list; -1 / 0 clears the override so
        // the row follows the delivery list again. Null = field not sent.
        var previousLineLocationId = line.LocationId;
        if (dto.LocationId.HasValue)
        {
            int? newLoc = dto.LocationId.Value > 0 ? dto.LocationId.Value : (int?)null;
            if (newLoc.HasValue)
            {
                var loc = await _db.Locations.FindAsync(newLoc.Value);
                if (loc == null || !loc.IsActive) return BadRequest("Neplatné pracovisko.");
            }
            if (line.LocationId != newLoc)
            {
                edits.Add(new EditRecord("locationId",
                    line.LocationId?.ToString(CultureInfo.InvariantCulture),
                    newLoc?.ToString(CultureInfo.InvariantCulture),
                    editor, DateTime.UtcNow));
                line.LocationId = newLoc;
            }
        }

        // Committed invoice: the usage row minted at commit time must follow
        // the row's new EFFECTIVE site (override ?? delivery list) — the same
        // semantics as moving a whole delivery list in UpdateDeliveryList.
        if (doc.Status == "committed")
        {
            var effOld = previousLineLocationId ?? line.Purchase.LocationId;
            var effNew = line.LocationId ?? line.Purchase.LocationId;
            if (effOld != effNew)
            {
                var usages = await _db.MaterialUsages
                    .Where(u => u.SourceMaterialPurchaseLineId == line.Id)
                    .ToListAsync();
                if (effNew == null)
                {
                    // Back to Sklad — per-site usage rows disappear.
                    _db.MaterialUsages.RemoveRange(usages);
                }
                else if (usages.Count > 0)
                {
                    foreach (var u in usages) u.LocationId = effNew.Value;
                }
                else if (line.Quantity > 0 && !string.IsNullOrWhiteSpace(line.MaterialNameRaw))
                {
                    // The row sat on Sklad at commit time → no usage exists
                    // yet. Mint it now (mirrors the delivery-list move path).
                    var catalogue = await _db.Materials.ToListAsync();
                    var material = catalogue.FirstOrDefault(m => m.Id == (line.MaterialId ?? -1))
                                ?? catalogue.FirstOrDefault(m =>
                                       string.Equals(m.Name, line.MaterialNameRaw.Trim(), StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(m.Unit, (line.Unit ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                    if (material == null)
                    {
                        material = new Material
                        {
                            Name         = Truncate(line.MaterialNameRaw.Trim(), 200),
                            Unit         = Truncate((line.Unit ?? "").Trim(), 50),
                            PricePerUnit = line.UnitPrice,
                            IsActive     = true,
                        };
                        _db.Materials.Add(material);
                        await _db.SaveChangesAsync();
                    }
                    line.MaterialId = material.Id;

                    var unitPriceForUsage = line.Quantity > 0
                        ? Round2(line.LineTotal / line.Quantity)
                        : line.UnitPrice;
                    _db.MaterialUsages.Add(new MaterialUsage
                    {
                        LocationId                   = effNew.Value,
                        MaterialId                   = material.Id,
                        EmployeeId                   = line.Purchase.EmployeeId,
                        Quantity                     = line.Quantity,
                        UnitPriceAtTime              = unitPriceForUsage,
                        Date                         = line.Purchase.PurchaseDate.Date,
                        Note                         = string.IsNullOrWhiteSpace(line.Purchase.DeliveryNoteRef)
                                                           ? $"Faktúra #{doc.InvoiceNumber}"
                                                           : $"Faktúra #{doc.InvoiceNumber} / {line.Purchase.DeliveryNoteRef}",
                        SourceMaterialPurchaseLineId = line.Id,
                        IsService                    = line.IsService,
                    });
                }
            }
        }

        // If quantity or unitPrice changed but lineTotal wasn't explicitly set,
        // recompute it. Finance-grade: never let the displayed total drift from
        // the inputs unless the operator overrode it on purpose.
        if (dto.LineTotal == null && (dto.Quantity.HasValue || dto.UnitPrice.HasValue))
        {
            var recomputed = Round2(line.Quantity * line.UnitPrice);
            if (line.LineTotal != recomputed)
            {
                edits.Add(new EditRecord("lineTotal", line.LineTotal.ToString(CultureInfo.InvariantCulture), recomputed.ToString(CultureInfo.InvariantCulture), editor, DateTime.UtcNow, AutoCalc: true));
                line.LineTotal = recomputed;
            }
        }

        line.LineEditHistory = SerializeHistory(edits);

        // Recompute parent purchase totals.
        var purchase = await _db.MaterialPurchases
            .Include(p => p.Lines)
            .FirstAsync(p => p.Id == line.PurchaseId);
        purchase.TotalCost       = purchase.Lines.Sum(l => l.LineTotal);
        purchase.SubtotalExclVat = Round2(purchase.Lines.Sum(l => l.LineTotal));
        purchase.SubtotalVat     = Round2(purchase.Lines.Sum(l => Round2(l.LineTotal * l.VatRate / 100m)));

        await _db.SaveChangesAsync();
        await RecomputeReconciliationAsync(id);
        return await BuildDtoAsync(id);
    }

    /// <summary>
    /// POST /api/invoices/{id}/delivery-lists/{purchaseId}/lines
    /// Photo scans sometimes miss a printed row entirely — the manager adds
    /// it by hand during review instead of re-photographing. Review-only,
    /// same rule as every other number edit. The new line lands in the audit
    /// trail as a manual addition.
    /// </summary>
    [HttpPost("{id}/delivery-lists/{purchaseId}/lines")]
    public async Task<ActionResult<InvoiceDocumentDto>> AddLine(int id, int purchaseId, [FromBody] AddInvoiceLineDto dto)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        if (doc.Status != "review") return BadRequest("Riadky možno pridávať len počas kontroly.");

        var purchase = await _db.MaterialPurchases
            .FirstOrDefaultAsync(p => p.Id == purchaseId && p.InvoiceDocumentId == id);
        if (purchase == null) return NotFound();

        var name = (dto.MaterialNameRaw ?? "").Trim();
        if (name.Length == 0) return BadRequest("Zadajte názov položky.");

        var qty = dto.Quantity is > 0m ? dto.Quantity.Value : 1m;
        var unitPrice = dto.UnitPrice ?? 0m;
        var lineTotal = dto.LineTotal ?? Round2(qty * unitPrice);
        // Total given but unit price not → derive it (receipts print totals).
        if (dto.UnitPrice == null && dto.LineTotal is { } lt && qty > 0m)
            unitPrice = Round2(lt / qty);

        var editor = User.Identity?.Name ?? "unknown";
        var line = new MaterialPurchaseLine
        {
            PurchaseId       = purchase.Id,
            SupplierItemCode = string.IsNullOrWhiteSpace(dto.SupplierItemCode) ? null : dto.SupplierItemCode.Trim(),
            MaterialNameRaw  = Truncate(name, 200),
            Unit             = Truncate(string.IsNullOrWhiteSpace(dto.Unit) ? "ks" : dto.Unit!.Trim(), 50),
            Quantity         = qty,
            UnitPrice        = unitPrice,
            LineTotal        = lineTotal,
            VatRate          = dto.VatRate ?? 23m,
            DiscountPercent  = dto.DiscountPercent is > 0m ? dto.DiscountPercent : null,
            IsReverseCharge  = false,
            IsService        = false,
            LineEditHistory  = SerializeHistory([new EditRecord("manualAdd", null, name, editor, DateTime.UtcNow)])
        };
        _db.MaterialPurchaseLines.Add(line);
        await _db.SaveChangesAsync();

        // Recompute parent purchase totals from the persisted lines.
        var fresh = await _db.MaterialPurchases
            .Include(p => p.Lines)
            .FirstAsync(p => p.Id == purchase.Id);
        fresh.TotalCost       = fresh.Lines.Sum(l => l.LineTotal);
        fresh.SubtotalExclVat = Round2(fresh.Lines.Sum(l => l.LineTotal));
        fresh.SubtotalVat     = Round2(fresh.Lines.Sum(l => Round2(l.LineTotal * l.VatRate / 100m)));
        await _db.SaveChangesAsync();

        await RecomputeReconciliationAsync(id);
        return await BuildDtoAsync(id);
    }

    public sealed class AddInvoiceLineDto
    {
        public string? SupplierItemCode { get; set; }
        public string? MaterialNameRaw { get; set; }
        public string? Unit { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? LineTotal { get; set; }
        public decimal? VatRate { get; set; }
        public decimal? DiscountPercent { get; set; }
    }

    [HttpPut("{id}/delivery-lists/{purchaseId}")]
    public async Task<ActionResult<InvoiceDocumentDto>> UpdateDeliveryList(int id, int purchaseId, [FromBody] UpdateInvoiceDeliveryListDto dto)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        // Location reassignment (Pracovisko) is allowed even after commit —
        // the manager might realise a delivery list went to the wrong site
        // only after the invoice is in their books. Line-item numbers stay
        // locked (see UpdateLine which requires status='review'). Only the
        // 'discarded' state blocks all edits.
        if (doc.Status == "discarded") return BadRequest("Zahodenú faktúru nemožno upraviť.");

        var purchase = await _db.MaterialPurchases
            .FirstOrDefaultAsync(p => p.Id == purchaseId && p.InvoiceDocumentId == id);
        if (purchase == null) return NotFound();

        var previousLocationId = purchase.LocationId;

        if (dto.LocationId.HasValue)
        {
            // Null is allowed via dto.LocationId = -1 sentinel below; here LocationId.HasValue + actual int
            if (dto.LocationId.Value > 0)
            {
                var loc = await _db.Locations.FindAsync(dto.LocationId.Value);
                if (loc == null || !loc.IsActive) return BadRequest("Neplatné pracovisko.");
                purchase.LocationId = dto.LocationId.Value;
            }
            else
            {
                // -1 / 0 sentinel = clear (Sklad / Inventár).
                purchase.LocationId = null;
            }
        }
        if (dto.PickedUpBy != null)   purchase.PickedUpBy   = string.IsNullOrWhiteSpace(dto.PickedUpBy)   ? null : dto.PickedUpBy.Trim();
        if (dto.DeliveryNote != null) purchase.DeliveryNote = string.IsNullOrWhiteSpace(dto.DeliveryNote) ? null : dto.DeliveryNote.Trim();

        // If the manager just moved the delivery list to a different Pracovisko
        // on a committed invoice, the MaterialUsage rows that Option A minted
        // at commit time are still pointing at the OLD location — they need to
        // follow the move, otherwise the material shows under both sites.
        // When the new LocationId is null (back to Sklad), the usages are
        // deleted: Sklad lines stay catalogue-only.
        if (doc.Status == "committed" && purchase.LocationId != previousLocationId)
        {
            // Only lines that INHERIT the delivery list's site follow this move.
            // Lines with their own LocationId override are pinned to their site
            // and must not be dragged along when the delivery list is reassigned.
            var lineIds = await _db.MaterialPurchaseLines
                .Where(l => l.PurchaseId == purchase.Id && l.LocationId == null)
                .Select(l => l.Id)
                .ToListAsync();
            var usages = await _db.MaterialUsages
                .Where(u => u.SourceMaterialPurchaseLineId != null
                         && lineIds.Contains(u.SourceMaterialPurchaseLineId.Value))
                .ToListAsync();

            if (purchase.LocationId == null)
            {
                // Moving to Sklad — drop the per-site usage rows.
                _db.MaterialUsages.RemoveRange(usages);
            }
            else if (usages.Count > 0)
            {
                // Move existing usages to the new location.
                foreach (var u in usages) u.LocationId = purchase.LocationId.Value;
            }
            else
            {
                // No usages existed (e.g. the delivery list was Sklad at commit
                // time). Mint them now for the lines that have qty > 0.
                var purchaseWithLines = await _db.MaterialPurchases
                    .Include(p => p.Lines)
                    .FirstAsync(p => p.Id == purchase.Id);
                var catalogue = await _db.Materials.ToListAsync();
                foreach (var line in purchaseWithLines.Lines)
                {
                    // Overridden lines are pinned to their own site — the
                    // delivery-list move doesn't create usages for them here.
                    if (line.LocationId != null) continue;
                    // Services no longer skipped here either — Item C of
                    // INVOICE_SCANNING_V1_FOLLOWUPS.md. Matches the
                    // commit-time path in AutoPromoteAndCreateUsagesAsync.
                    if (line.Quantity <= 0) continue;
                    if (string.IsNullOrWhiteSpace(line.MaterialNameRaw)) continue;

                    var material = catalogue.FirstOrDefault(m => m.Id == (line.MaterialId ?? -1))
                                ?? catalogue.FirstOrDefault(m =>
                                       string.Equals(m.Name, line.MaterialNameRaw.Trim(), StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(m.Unit, (line.Unit ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                    if (material == null)
                    {
                        material = new Material
                        {
                            Name         = Truncate(line.MaterialNameRaw.Trim(), 200),
                            Unit         = Truncate((line.Unit ?? "").Trim(), 50),
                            PricePerUnit = line.UnitPrice,
                            IsActive     = true,
                        };
                        _db.Materials.Add(material);
                        await _db.SaveChangesAsync();
                        catalogue.Add(material);
                    }
                    line.MaterialId = material.Id;

                    var unitPriceForUsage = line.Quantity > 0
                        ? Round2(line.LineTotal / line.Quantity)
                        : line.UnitPrice;

                    _db.MaterialUsages.Add(new MaterialUsage
                    {
                        LocationId                   = purchase.LocationId.Value,
                        MaterialId                   = material.Id,
                        EmployeeId                   = purchase.EmployeeId,
                        Quantity                     = line.Quantity,
                        UnitPriceAtTime              = unitPriceForUsage,
                        Date                         = purchase.PurchaseDate.Date,
                        Note                         = string.IsNullOrWhiteSpace(purchase.DeliveryNoteRef)
                                                           ? $"Faktúra #{doc.InvoiceNumber}"
                                                           : $"Faktúra #{doc.InvoiceNumber} / {purchase.DeliveryNoteRef}",
                        SourceMaterialPurchaseLineId = line.Id,
                        IsService                    = line.IsService,
                    });
                }
            }
        }

        await _db.SaveChangesAsync();
        return await BuildDtoAsync(id);
    }

    /// <summary>
    /// PUT /api/invoices/{id}/printed-total
    /// Lets the manager correct the invoice's printed grand total (incl. VAT)
    /// when the parser misread it — e.g. it grabbed a weighbridge "TOTAL n t"
    /// from an extra scanned page. Re-runs reconciliation so the review screen
    /// reflects the fix immediately.
    /// </summary>
    [HttpPut("{id}/printed-total")]
    public async Task<ActionResult<InvoiceDocumentDto>> UpdatePrintedTotal(int id, [FromBody] UpdatePrintedTotalDto dto)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        if (doc.Status != "review") return BadRequest("Faktúru nemožno upravovať po uložení / zahodení.");
        if (dto.TotalInclVat < 0) return BadRequest("Suma musí byť kladná.");

        doc.TotalInclVat = Round2(dto.TotalInclVat);
        await _db.SaveChangesAsync();
        await RecomputeReconciliationAsync(id);
        return await BuildDtoAsync(id);
    }

    public sealed class UpdatePrintedTotalDto
    {
        public decimal TotalInclVat { get; set; }
    }

    /// <summary>
    /// Manual correction of the issue date ("dátum vyhotovenia"). Photo scans
    /// sometimes defeat every date heuristic and the document then lands in
    /// the wrong month on the Financie overview — the manager fixes it here
    /// without re-uploading. Allowed on review and committed documents.
    ///
    /// The correction follows through to the money views: purchases that
    /// INHERITED the header date (their PurchaseDate equals the old issue
    /// date — no own delivery-list date was parsed) move with it, and so do
    /// their already-minted MaterialUsages. Purchases carrying a real
    /// delivery-list date stay untouched.
    /// </summary>
    [HttpPut("{id}/issue-date")]
    public async Task<ActionResult<InvoiceDocumentDto>> UpdateIssueDate(int id, [FromBody] UpdateIssueDateDto dto)
    {
        var doc = await _db.InvoiceDocuments
            .Include(d => d.Purchases).ThenInclude(p => p.Lines)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null) return NotFound();
        if (doc.Status == "discarded") return BadRequest("Zahodenú faktúru nemožno upravovať.");

        var oldDate = doc.IssueDate.Date;
        var newDate = dto.IssueDate.Date;
        if (newDate == oldDate) return await BuildDtoAsync(id);

        doc.IssueDate = newDate;

        var moved = doc.Purchases.Where(p => p.PurchaseDate.Date == oldDate).ToList();
        foreach (var p in moved) p.PurchaseDate = newDate;

        if (moved.Count > 0)
        {
            var movedLineIds = moved.SelectMany(p => p.Lines).Select(l => l.Id).ToList();
            var usages = await _db.MaterialUsages
                .Where(u => u.SourceMaterialPurchaseLineId != null
                            && movedLineIds.Contains(u.SourceMaterialPurchaseLineId.Value)
                            && u.Date == oldDate)
                .ToListAsync();
            foreach (var u in usages) u.Date = newDate;

            _log.LogInformation(
                "[InvoiceScanning] Issue date of invoice {Id} moved {Old} → {New}; {P} purchases and {U} usages followed.",
                id, oldDate.ToString("d.M.yyyy"), newDate.ToString("d.M.yyyy"), moved.Count, usages.Count);
        }

        await _db.SaveChangesAsync();
        return await BuildDtoAsync(id);
    }

    public sealed class UpdateIssueDateDto
    {
        public DateTime IssueDate { get; set; }
    }

    /// <summary>
    /// POST /api/invoices/{id}/ai-reparse — the manual "Skúsiť AI" button on
    /// review. Downloads the stored original, runs the vision-LLM extraction
    /// and REPLACES the draft header + purchases with its result; the
    /// reconciliation banner then reports honestly whether the new numbers
    /// add up. Review-only (committed numbers are history) and requires the
    /// AI number + total to be present — otherwise the document is untouched.
    /// </summary>
    [HttpPost("{id}/ai-reparse")]
    public async Task<ActionResult<InvoiceDocumentDto>> AiReparse(int id)
    {
        if (!_ai.IsConfigured)
            return StatusCode(503, "AI rozpoznávanie nie je nakonfigurované (Gemini API kľúč chýba).");

        var doc = await _db.InvoiceDocuments
            .Include(d => d.Purchases)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null) return NotFound();
        if (doc.Status != "review") return BadRequest("AI rozpoznanie je možné len počas kontroly.");

        byte[] bytes;
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(30);
            bytes = await http.GetByteArrayAsync(doc.PdfUrl);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[InvoiceScanning] ai-reparse: stored file download failed for doc {Id}", id);
            return StatusCode(502, "Uložený dokument sa nepodarilo stiahnuť.");
        }

        // The Document AI text layer is a useful hint even when scrambled.
        string? ocrText = null;
        try
        {
            using var raw = JsonDocument.Parse(doc.RawOcrJson ?? "{}");
            if (raw.RootElement.TryGetProperty("text", out var t)) ocrText = t.GetString();
        }
        catch { /* the hint is optional */ }

        var mime = doc.PdfUrl.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? "application/pdf"
            : "image/jpeg";

        ParsedInvoice? ai;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            ai = await _ai.ExtractAsync(bytes, mime, ocrText, cts.Token);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[InvoiceScanning] ai-reparse failed for doc {Id}", id);
            ai = null;
        }
        if (ai == null)
            return StatusCode(502, "AI rozpoznanie zlyhalo. Skúste znova o chvíľu.");
        if (string.IsNullOrWhiteSpace(ai.Header.InvoiceNumber) || ai.Header.TotalInclVat == null)
            return BadRequest("AI nerozpoznalo číslo faktúry alebo celkovú sumu — dokument ostáva bez zmeny.");

        // Dedup guard (same rule as upload), excluding this document.
        var newNumber = Clip(ai.Header.InvoiceNumber!, 100)!;
        var newIssue = ai.Header.IssueDate ?? doc.IssueDate;
        var dup = await _db.InvoiceDocuments.AnyAsync(d =>
            d.Id != id && d.InvoiceNumber == newNumber && d.IssueDate == newIssue);
        if (dup) return Conflict($"Faktúra {newNumber} z {newIssue:d.M.yyyy} už existuje.");

        // Replace the draft. Review documents have no usage rows yet, so
        // removing the purchases (lines cascade) is safe.
        doc.InvoiceNumber = newNumber;
        doc.SupplierName  = Clip(ai.Header.SupplierName, 200) ?? doc.SupplierName;
        doc.SupplierIco   = Clip(ai.Header.SupplierIco, 50) ?? doc.SupplierIco;
        doc.SupplierIcDph = Clip(ai.Header.SupplierIcDph, 50) ?? doc.SupplierIcDph;
        doc.IssueDate     = newIssue;
        doc.DeliveryDate  = ai.Header.DeliveryDate ?? doc.DeliveryDate;
        doc.DueDate       = ai.Header.DueDate ?? doc.DueDate;
        doc.TotalExclVat  = Round2(ai.Header.TotalExclVat ?? 0m);
        doc.TotalVat      = Round2(ai.Header.TotalVat ?? 0m);
        doc.TotalInclVat  = Round2(ai.Header.TotalInclVat.Value);
        if (ai.Header.IsReceipt) doc.DocumentKind = "receipt";

        _db.MaterialPurchases.RemoveRange(doc.Purchases);
        await _db.SaveChangesAsync();

        var rebuildError = await BuildDraftPurchasesAsync(doc, ai);
        if (rebuildError != null) return BadRequest(rebuildError);
        await _db.SaveChangesAsync();

        _log.LogInformation("[InvoiceScanning] Doc {Id} re-parsed via AI (číslo={Num}, spolu={Total}).",
            id, newNumber, doc.TotalInclVat);
        await RecomputeReconciliationAsync(id);
        return await BuildDtoAsync(id);
    }

    /// <summary>
    /// Manual correction of the supplier name — photo scans occasionally OCR
    /// a logo or stamp instead of the printed company name. Allowed on review
    /// and committed documents (mirrors the issue-date rule); only the
    /// document header changes, purchases stay linked by id.
    /// </summary>
    [HttpPut("{id}/supplier-name")]
    public async Task<ActionResult<InvoiceDocumentDto>> UpdateSupplierName(int id, [FromBody] UpdateSupplierNameDto dto)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        if (doc.Status == "discarded") return BadRequest("Zahodenú faktúru nemožno upravovať.");

        var name = (dto.SupplierName ?? "").Trim();
        if (name.Length == 0) return BadRequest("Meno dodávateľa nemôže byť prázdne.");

        doc.SupplierName = Truncate(name, 300);
        await _db.SaveChangesAsync();
        return await BuildDtoAsync(id);
    }

    public sealed class UpdateSupplierNameDto
    {
        public string? SupplierName { get; set; }
    }

    // ────────────────────────────────────────────────────────────────
    //  Commit / discard
    // ────────────────────────────────────────────────────────────────

    [HttpPost("{id}/commit")]
    public async Task<ActionResult<InvoiceDocumentDto>> Commit(int id, [FromQuery] bool force = false)
    {
        var doc = await _db.InvoiceDocuments.FindAsync(id);
        if (doc == null) return NotFound();
        if (doc.Status == "committed") return Conflict("Faktúra je už uložená.");
        if (doc.Status != "review") return BadRequest("Faktúru nemožno uložiť v aktuálnom stave.");

        // Server-authoritative reconciliation. Normally this is a binding gate;
        // but the manager can explicitly override it (force=true) after reading
        // the warning — e.g. when an odd supplier layout legitimately won't
        // reconcile. The override is recorded in the note for audit.
        var ok = await RecomputeReconciliationAsync(id);
        if (!ok && !force)
            return BadRequest($"Súčet riadkov sa nezhoduje s vytlačenou sumou. {doc.ReconciliationNote}");

        doc.Status      = "committed";
        doc.CommittedBy = User.Identity?.Name ?? "unknown";
        doc.CommittedAt = DateTime.UtcNow;
        if (!ok)
            doc.ReconciliationNote = Clip(
                $"{doc.ReconciliationNote} Uložené napriek nezhode používateľom {doc.CommittedBy}.", 500);
        await _db.SaveChangesAsync();

        // Option A: promote each line to the Material catalogue (find-or-create
        // by name) and mint MaterialUsage rows for the lines whose delivery list
        // has a Pracovisko assigned. Sklad / Inventár (LocationId == null)
        // stays out of the per-site consumption view by design.
        await AutoPromoteAndCreateUsagesAsync(id);

        return await BuildDtoAsync(id);
    }

    /// <summary>
    /// Option A flow (INVOICE_SCANNING_PLAN.md): after Commit succeeds,
    ///   1. For every line, find-or-create a Material catalogue row keyed by
    ///      normalised MaterialNameRaw + Unit. Set MaterialPurchaseLine.MaterialId.
    ///   2. For every line whose parent MaterialPurchase has a LocationId
    ///      (i.e. not Sklad), create a MaterialUsage tagged with
    ///      SourceMaterialPurchaseLineId so a future Discard cascades it away.
    ///
    /// Services (<see cref="MaterialPurchaseLine.IsService"/>) are skipped —
    /// they don't represent physical inventory. Non-positive quantities are
    /// skipped (credit / "Zľava z prenájmu" rows have qty &lt;= 0).
    ///
    /// Idempotent on re-commit: if a usage already exists for a line
    /// (SourceMaterialPurchaseLineId match), skip — never duplicate.
    /// </summary>
    private async Task AutoPromoteAndCreateUsagesAsync(int invoiceDocumentId)
    {
        var doc = await _db.InvoiceDocuments
            .Include(d => d.Purchases).ThenInclude(p => p.Lines)
            .FirstAsync(d => d.Id == invoiceDocumentId);

        // Pull existing source-line ids so re-commits (shouldn't happen, but the
        // method is idempotent anyway) don't double up.
        var lineIds = doc.Purchases.SelectMany(p => p.Lines).Select(l => l.Id).ToList();
        var alreadyTagged = await _db.MaterialUsages
            .Where(u => u.SourceMaterialPurchaseLineId != null
                     && lineIds.Contains(u.SourceMaterialPurchaseLineId!.Value))
            .Select(u => u.SourceMaterialPurchaseLineId!.Value)
            .ToListAsync();
        var alreadyTaggedSet = new HashSet<int>(alreadyTagged);

        // Snapshot the catalogue once so we don't re-query inside the loop.
        var catalogue = await _db.Materials.ToListAsync();

        foreach (var purchase in doc.Purchases)
        {
            foreach (var line in purchase.Lines)
            {
                // Services (rentals) used to be skipped here on the
                // grounds that they don't represent physical inventory.
                // Per INVOICE_SCANNING_V1_FOLLOWUPS.md item C, rentals
                // now flow into MaterialUsage with IsService=true so the
                // Pracovisko Spotreba can show them with a distinct
                // badge. Credit-note rows (qty<=0) and empty-name rows
                // still skipped — those are data-quality guards.
                if (line.Quantity <= 0) continue;
                if (string.IsNullOrWhiteSpace(line.MaterialNameRaw)) continue;

                // 1) Find-or-create the Material catalogue row.
                var material = line.Material;
                if (material == null && line.MaterialId.HasValue)
                {
                    material = catalogue.FirstOrDefault(m => m.Id == line.MaterialId.Value);
                }
                if (material == null)
                {
                    var nameKey = line.MaterialNameRaw.Trim();
                    var unitKey = (line.Unit ?? "").Trim();
                    material = catalogue.FirstOrDefault(m =>
                        string.Equals(m.Name, nameKey, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Unit, unitKey, StringComparison.OrdinalIgnoreCase));
                    if (material == null)
                    {
                        material = new Material
                        {
                            Name         = Truncate(nameKey, 200),
                            Unit         = Truncate(unitKey, 50),
                            PricePerUnit = line.UnitPrice,
                            IsActive     = true,
                        };
                        _db.Materials.Add(material);
                        await _db.SaveChangesAsync();   // need the Id before we tag the line
                        catalogue.Add(material);
                    }
                    line.MaterialId = material.Id;
                }

                // 2) Create the per-site MaterialUsage at the line's EFFECTIVE
                //    site — its own LocationId override when set, otherwise the
                //    delivery list's. Lets one delivery list split across sites.
                //    Sklad lines (effective site null) stay catalogue-only.
                var effectiveLocationId = line.LocationId ?? purchase.LocationId;
                if (effectiveLocationId == null) continue;
                if (alreadyTaggedSet.Contains(line.Id)) continue;

                // The Pracovisko view computes LineCost as Quantity × UnitPriceAtTime,
                // so the snapshotted unit price must reconcile to the printed
                // Spolu. Some scanned lines have UnitPrice != LineTotal/Quantity
                // (parser sometimes stores the list price in UnitPrice when the
                // discount cell is blank). Derive from LineTotal so the per-site
                // cost matches the invoice exactly.
                var unitPriceForUsage = line.Quantity > 0
                    ? Round2(line.LineTotal / line.Quantity)
                    : line.UnitPrice;

                var usage = new MaterialUsage
                {
                    LocationId                   = effectiveLocationId.Value,
                    MaterialId                   = material.Id,
                    EmployeeId                   = purchase.EmployeeId,
                    Quantity                     = line.Quantity,
                    UnitPriceAtTime              = unitPriceForUsage,
                    Date                         = purchase.PurchaseDate.Date,
                    Note                         = string.IsNullOrWhiteSpace(purchase.DeliveryNoteRef)
                                                       ? $"Faktúra #{doc.InvoiceNumber}"
                                                       : $"Faktúra #{doc.InvoiceNumber} / {purchase.DeliveryNoteRef}",
                    SourceMaterialPurchaseLineId = line.Id,
                    IsService                    = line.IsService,
                };
                _db.MaterialUsages.Add(usage);
                alreadyTaggedSet.Add(line.Id);
            }
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// One-shot backfill of MaterialUsages for service lines that committed
    /// before Item C (INVOICE_SCANNING_V1_FOLLOWUPS.md) shipped. Iterates every
    /// committed InvoiceDocument and re-runs the usage-minting pass.
    /// AutoPromoteAndCreateUsagesAsync is already idempotent — it tracks the
    /// existing SourceMaterialPurchaseLineId set and skips lines whose usage
    /// already exists — so this is safe to call repeatedly and only mints
    /// usages that weren't there before. Superadmin only; runs synchronously.
    /// </summary>
    [HttpPost("backfill-service-usages")]
    public async Task<IActionResult> BackfillServiceUsages()
    {
        if (!User.HasClaim("isSuperAdmin", "true")) return Forbid();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var beforeCount = await _db.MaterialUsages.CountAsync();

        var committedIds = await _db.InvoiceDocuments
            .Where(d => d.Status == "committed")
            .Select(d => d.Id)
            .ToListAsync();

        foreach (var id in committedIds)
        {
            await AutoPromoteAndCreateUsagesAsync(id);
        }

        var afterCount = await _db.MaterialUsages.CountAsync();
        sw.Stop();

        _log.LogInformation(
            "[InvoiceScanning] Backfill: processed {N} invoices, minted {Created} usages in {Ms}ms",
            committedIds.Count, afterCount - beforeCount, sw.ElapsedMilliseconds);

        return Ok(new
        {
            invoicesProcessed = committedIds.Count,
            usagesCreated     = afterCount - beforeCount,
            durationMs        = sw.ElapsedMilliseconds
        });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Discard(int id)
    {
        var doc = await _db.InvoiceDocuments
            .Include(d => d.Purchases).ThenInclude(p => p.Lines)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null) return NotFound();

        // Hard-delete the InvoiceDocument and its MaterialPurchases. Allowed
        // for every status — including committed (the manager may want to
        // erase an erroneous record from their books). The Cloudinary PDF
        // remains as audit — we never delete uploaded PDFs in V1.
        // Frontend asks for stronger confirmation on committed invoices.
        foreach (var p in doc.Purchases)
        {
            _db.MaterialPurchaseLines.RemoveRange(p.Lines);
        }
        _db.MaterialPurchases.RemoveRange(doc.Purchases);
        _db.InvoiceDocuments.Remove(doc);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ────────────────────────────────────────────────────────────────
    //  Reconciliation
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Server-authoritative reconciliation: sum of all line totals + sum of VAT
    /// amounts must equal InvoiceDocument.TotalInclVat to the cent. Updates
    /// ReconciliationOk and ReconciliationNote on the document. Returns true
    /// when the document reconciles.
    /// </summary>
    private async Task<bool> RecomputeReconciliationAsync(int invoiceDocumentId)
    {
        var doc = await _db.InvoiceDocuments
            .Include(d => d.Purchases).ThenInclude(p => p.Lines)
            .FirstAsync(d => d.Id == invoiceDocumentId);

        var lineSum = doc.Purchases.SelectMany(p => p.Lines).Sum(l => l.LineTotal);
        var vatSum  = doc.Purchases.SelectMany(p => p.Lines).Sum(l => Round2(l.LineTotal * l.VatRate / 100m));
        var grand   = Round2(lineSum + vatSum);
        var printed = Round2(doc.TotalInclVat);

        var diff = Math.Abs(grand - printed);
        // 5-cent tolerance: cash receipts round the paid total (zaokrúhlenie,
        // typically ±0,01–0,02) and per-line VAT rounding on many-line
        // receipts drifts a few more cents against the printed recap.
        var ok = diff <= 0.05m;

        doc.ReconciliationOk = ok;
        doc.ReconciliationNote = ok
            ? $"Riadky {Money(lineSum)} + DPH {Money(vatSum)} = {Money(grand)} sedí s vytlačeným {Money(printed)}."
            : $"Riadky {Money(lineSum)} + DPH {Money(vatSum)} = {Money(grand)} sa nezhoduje s vytlačeným {Money(printed)} (rozdiel {Money(diff)}).";
        await _db.SaveChangesAsync();
        return ok;
    }

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    private static string Money(decimal v) => SlovakNumberHelper.FormatMoney(v) + " €";
    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    /// <summary>
    /// Clip a string to a varchar limit before persisting. Returns null when
    /// the input is null (preserves NULL semantics for nullable columns).
    /// Used everywhere to keep OCR-derived strings from overflowing the
    /// schema's varchar(N) and crashing the save.
    /// </summary>
    private static string? Clip(string? s, int max)
    {
        if (s == null) return null;
        var t = s.Trim();
        if (t.Length == 0) return null;
        return t.Length <= max ? t : t[..max];
    }

    // ────────────────────────────────────────────────────────────────
    //  DTO builders
    // ────────────────────────────────────────────────────────────────

    private async Task<InvoiceDocumentDto> BuildDtoAsync(int id)
    {
        var doc = await _db.InvoiceDocuments
            .Include(d => d.Purchases).ThenInclude(p => p.Location)
            .Include(d => d.Purchases).ThenInclude(p => p.Lines).ThenInclude(l => l.Location)
            .FirstAsync(d => d.Id == id);

        var dto = BuildSummaryDto(doc);
        dto.DeliveryLists = doc.Purchases
            .OrderBy(p => p.Id)
            .Select(p => new InvoiceDeliveryListDto
            {
                Id               = p.Id,
                DeliveryNoteRef  = p.DeliveryNoteRef,
                PurchaseDate     = p.PurchaseDate,
                PickedUpBy       = p.PickedUpBy,
                DeliveryNote     = p.DeliveryNote,
                LocationId       = p.LocationId,
                LocationName     = p.Location?.Name,
                SubtotalExclVat  = p.SubtotalExclVat,
                SubtotalVat      = p.SubtotalVat,
                Lines = p.Lines
                    .OrderBy(l => l.Id)
                    .Select(l => new InvoiceLineDto
                    {
                        Id               = l.Id,
                        PurchaseId       = l.PurchaseId,
                        SupplierItemCode = l.SupplierItemCode,
                        MaterialNameRaw  = l.MaterialNameRaw,
                        Unit             = l.Unit,
                        Quantity         = l.Quantity,
                        UnitPrice        = l.UnitPrice,
                        LineTotal        = l.LineTotal,
                        ListPriceExclVat = l.ListPriceExclVat,
                        DiscountPercent  = l.DiscountPercent,
                        UnitPriceInclVat = l.UnitPriceInclVat,
                        VatRate          = l.VatRate,
                        IsReverseCharge  = l.IsReverseCharge,
                        IsService        = l.IsService,
                        LocationId       = l.LocationId,
                        LocationName     = l.Location?.Name
                    })
                    .ToList()
            })
            .ToList();
        return dto;
    }

    private static InvoiceDocumentDto BuildSummaryDto(InvoiceDocument d) => new()
    {
        Id                 = d.Id,
        InvoiceNumber      = d.InvoiceNumber,
        SupplierName       = d.SupplierName,
        SupplierIco        = d.SupplierIco,
        SupplierIcDph      = d.SupplierIcDph,
        SupplierIban       = d.SupplierIban,
        IssueDate          = d.IssueDate,
        DeliveryDate       = d.DeliveryDate,
        DueDate            = d.DueDate,
        PeriodFrom         = d.PeriodFrom,
        PeriodTo           = d.PeriodTo,
        Currency           = d.Currency,
        TotalExclVat       = d.TotalExclVat,
        TotalVat           = d.TotalVat,
        TotalInclVat       = d.TotalInclVat,
        PdfUrl             = d.PdfUrl,
        Status             = d.Status,
        DocumentKind       = d.DocumentKind,
        ReconciliationOk   = d.ReconciliationOk,
        ReconciliationNote = d.ReconciliationNote,
        UploadedBy         = d.UploadedBy,
        UploadedAt         = d.UploadedAt,
        CommittedBy        = d.CommittedBy,
        CommittedAt        = d.CommittedAt,
        Note               = d.Note,
        ScanSource         = d.ScanSource,
        ScanPageCount      = d.ScanPageCount,
        // Effective locations this document's lines were assigned to (line
        // override ?? delivery list). Shown as chips on the Faktúry list so
        // the manager sees at a glance where the money went. Empty when the
        // navigations weren't loaded by the caller.
        LocationNames = d.Purchases
            .SelectMany(p => p.Lines.Select(l => l.Location?.Name ?? p.Location?.Name))
            .Where(n => n != null)
            .Select(n => n!)
            .Distinct()
            .OrderBy(n => n)
            .ToList()
    };

    // ────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Try to map an invoice's <c>prevzal:</c> text onto an existing
    /// Employee. Strips Slovak honorifics ("p.", "pán", "ing.", "mgr."),
    /// splits into tokens, diacritics-stripped + lowercased. The Employee
    /// matches when their last name (and optionally first name) appears as
    /// a token in the prevzal text. Returns the Employee id only when the
    /// match is UNIQUE — ambiguity stays on the uploader so we never silently
    /// pin a purchase to the wrong worker.
    /// </summary>
    private static int? MatchEmployeeFromPickedUpBy(string? prevzal, IEnumerable<Models.Employee> employees)
    {
        if (string.IsNullOrWhiteSpace(prevzal)) return null;

        // Drop the common honorifics + punctuation, then tokenise on
        // whitespace / dashes / dots / commas.
        var cleaned = Regex.Replace(
            NormalizeForMatch(prevzal),
            @"\b(p|pan|ing|mgr|bc|dr|phdr|mudr|rndr)\.?\b",
            " ",
            RegexOptions.IgnoreCase);
        var tokens = cleaned.Split(new[] { ' ', '-', '.', ',', '/', ';' },
                                   StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .ToHashSet();
        if (tokens.Count == 0) return null;

        var hits = new List<int>();
        foreach (var emp in employees)
        {
            var first = NormalizeForMatch(emp.FirstName);
            var last  = NormalizeForMatch(emp.LastName);
            // Last name is the strong signal — match on that. If both names
            // appear in the prevzal text it's a stronger hit, but a single
            // last-name hit is enough.
            if (!string.IsNullOrEmpty(last) && tokens.Contains(last))
            {
                hits.Add(emp.Id);
            }
        }
        return hits.Count == 1 ? hits[0] : (int?)null;
    }

    /// <summary>Strip diacritics + lowercase for fuzzy Location matching.</summary>
    private static string NormalizeForMatch(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant().Trim();
    }

    // ─── Audit trail ───────────────────────────────────────────────

    private sealed record EditRecord(string Field, string? OldValue, string? NewValue, string EditedBy, DateTime EditedAt, bool AutoCalc = false);

    private static List<EditRecord> DeserializeHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<EditRecord>>(json) ?? []; }
        catch { return []; }
    }

    private static string SerializeHistory(List<EditRecord> edits)
        => JsonSerializer.Serialize(edits);

    private static void TryEdit(List<EditRecord> edits, string editor, string field, string? current, string? incoming, Action<string?> apply)
    {
        if (incoming == null) return;
        var normalized = string.IsNullOrWhiteSpace(incoming) ? null : incoming.Trim();
        if (current == normalized) return;
        edits.Add(new EditRecord(field, current, normalized, editor, DateTime.UtcNow));
        apply(normalized);
    }

    private static void TryEditDecimal(List<EditRecord> edits, string editor, string field, decimal current, decimal? incoming, Action<decimal> apply)
    {
        if (incoming == null) return;
        var rounded = Round2(incoming.Value);
        if (current == rounded) return;
        edits.Add(new EditRecord(field, current.ToString(CultureInfo.InvariantCulture), rounded.ToString(CultureInfo.InvariantCulture), editor, DateTime.UtcNow));
        apply(rounded);
    }

    private static void TryEditBool(List<EditRecord> edits, string editor, string field, bool current, bool? incoming, Action<bool> apply)
    {
        if (incoming == null) return;
        if (current == incoming.Value) return;
        edits.Add(new EditRecord(field, current.ToString(), incoming.Value.ToString(), editor, DateTime.UtcNow));
        apply(incoming.Value);
    }
}
