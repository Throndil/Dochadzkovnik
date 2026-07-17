using System.Text;
using System.Text.Json;

namespace API.Services;

/// <summary>
/// Claude (Anthropic Messages API) invoice extraction — the PRIMARY
/// extractor (~2–4¢/doc, ~€1–2/month at real volume). Gemini's free tier
/// is the fallback when this is unconfigured or down.
///
/// Uses the SAME prompt rules and wire DTOs as GeminiInvoiceExtractor
/// (single source of truth), so a document gets identical instructions no
/// matter which provider reads it. Claude reads PDFs natively — it gets
/// both the text layer and the rendered pages.
///
/// Configure with <c>Anthropic:ApiKey</c> in appsettings.Local.json
/// (+ optional <c>Anthropic:Model</c>, default claude-sonnet-5).
/// </summary>
public sealed class AnthropicInvoiceExtractor
{
    private readonly HttpClient _http;
    private readonly ILogger<AnthropicInvoiceExtractor> _log;
    private readonly string? _apiKey;
    private readonly string _model;

    public AnthropicInvoiceExtractor(HttpClient http, IConfiguration cfg, ILogger<AnthropicInvoiceExtractor> log)
    {
        _http = http;
        _log = log;
        _apiKey = cfg["Anthropic:ApiKey"];
        _model = string.IsNullOrWhiteSpace(cfg["Anthropic:Model"]) ? "claude-sonnet-5" : cfg["Anthropic:Model"]!;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<ParsedInvoice?> ExtractAsync(byte[] fileBytes, string mimeType, string? ocrText, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            // No responseSchema equivalent in the Messages API — the JSON
            // shape is spelled out in the prompt instead, and the output is
            // parsed with the same DTOs as Gemini's.
            var prompt = GeminiInvoiceExtractor.BuildPrompt(ocrText) +
                "\nReturn ONLY a single JSON object — no markdown fences, no commentary — with exactly this shape (camelCase keys, null when unknown):\n" +
                "{\"supplierName\":str|null,\"supplierIco\":str|null,\"supplierIcDph\":str|null,\"invoiceNumber\":str|null," +
                "\"issueDate\":\"YYYY-MM-DD\"|null,\"deliveryDate\":\"YYYY-MM-DD\"|null,\"dueDate\":\"YYYY-MM-DD\"|null," +
                "\"totalExclVat\":num|null,\"totalVat\":num|null,\"totalInclVat\":num|null,\"isReceipt\":bool," +
                "\"deliveryLists\":[{\"deliveryNoteRef\":str|null,\"siteName\":str|null,\"deliveryDate\":str|null," +
                "\"subtotalExclVat\":num|null,\"subtotalVat\":num|null," +
                "\"lines\":[{\"code\":str|null,\"description\":str,\"quantity\":num|null,\"unit\":str|null," +
                "\"unitPriceExclVat\":num|null,\"lineTotalExclVat\":num|null,\"vatRatePercent\":num|null,\"discountPercent\":num|null}]}]}";

            // PDFs go in as a document block (text layer + rendered pages);
            // images as an image block.
            var isPdf = mimeType == "application/pdf";
            var fileBlock = isPdf
                ? (object)new { type = "document", source = new { type = "base64", media_type = "application/pdf", data = Convert.ToBase64String(fileBytes) } }
                : new { type = "image", source = new { type = "base64", media_type = mimeType, data = Convert.ToBase64String(fileBytes) } };

            // No temperature: claude-sonnet-5 rejects the parameter
            // ("`temperature` is deprecated for this model") — determinism
            // comes from the schema-shaped prompt instead.
            var body = new
            {
                model = _model,
                max_tokens = 16000,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[] { fileBlock, new { type = "text", text = prompt } }
                    }
                }
            };
            var requestJson = JsonSerializer.Serialize(body);

            // One retry on transient overload (429/529); anything else is
            // terminal for this document — the caller keeps the Gemini read.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-api-key", _apiKey);
                req.Headers.Add("anthropic-version", "2023-06-01");

                using var resp = await _http.SendAsync(req, ct);
                var respText = await resp.Content.ReadAsStringAsync(ct);

                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(respText);
                    string? json = null;
                    foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var t) && t.GetString() == "text")
                        {
                            json = block.GetProperty("text").GetString();
                            break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(json)) return null;

                    // Defensive: strip markdown fences if the model added them anyway.
                    json = json.Trim();
                    if (json.StartsWith("```", StringComparison.Ordinal))
                    {
                        var start = json.IndexOf('{');
                        var end = json.LastIndexOf('}');
                        if (start < 0 || end <= start) return null;
                        json = json[start..(end + 1)];
                    }

                    _log.LogInformation("[InvoiceScanning] Claude raw JSON ({Len} chars): {Json}",
                        json.Length, GeminiInvoiceExtractor.Truncate(json, 12000));
                    var dto = JsonSerializer.Deserialize<GeminiInvoiceExtractor.LlmInvoice>(json, GeminiInvoiceExtractor.JsonOpts);
                    return dto == null ? null : GeminiInvoiceExtractor.Map(dto);
                }

                var overloaded = (int)resp.StatusCode is 429 or 529;
                _log.LogWarning("[InvoiceScanning] Claude {Model} returned {Status} (attempt {Attempt}): {Body}",
                    _model, (int)resp.StatusCode, attempt + 1, GeminiInvoiceExtractor.Truncate(respText, 300));
                if (!overloaded) return null;
                if (attempt == 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[InvoiceScanning] Claude extraction failed.");
            return null;
        }
    }
}
