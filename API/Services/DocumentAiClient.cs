using Google.Api.Gax.Grpc;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.DocumentAI.V1;
using Google.Protobuf;
using Grpc.Auth;

namespace API.Services;

/// <summary>
/// Production Document AI client. Reads credentials + processor from
/// configuration. Two ways to supply the service-account credentials:
///   - <c>Google:DocumentAi:CredentialsPath</c> — absolute path to the JSON
///     key file on disk. **Preferred for local dev.** Avoids the pain of
///     escaping multi-line JSON inside appsettings.Local.json.
///   - <c>Google:DocumentAi:CredentialsJson</c> — full service-account JSON
///     inline. Used in Railway prod where the env var UI accepts multi-line
///     values without escaping. Internal quotes don't need backslashing.
///
/// Plus three always-required settings:
///   - <c>Google:DocumentAi:ProjectId</c>     (Cloud project id)
///   - <c>Google:DocumentAi:Location</c>      ("eu" recommended for SK invoices)
///   - <c>Google:DocumentAi:ProcessorId</c>   (the Invoice Parser processor)
///
/// Throws at construction if any required setting is missing — failing loud
/// is correct for a finance-grade dependency. The InvoicesController will
/// catch and surface a Slovak error before the manager hits the OCR call.
///
/// See INVOICE_SCANNING_PLAN.md §"Operator setup" for one-time GCP setup.
/// </summary>
public sealed class DocumentAiClient : IDocumentAiClient
{
    private readonly DocumentProcessorServiceClient _client;
    private readonly string _processorName;
    private readonly ILogger<DocumentAiClient> _log;

    public DocumentAiClient(IConfiguration cfg, ILogger<DocumentAiClient> log)
    {
        _log = log;

        // Resolve credentials: file path takes precedence (local dev pattern),
        // inline JSON is the Railway env var pattern.
        var credentialsPath = cfg["Google:DocumentAi:CredentialsPath"];
        var credentialsJson = cfg["Google:DocumentAi:CredentialsJson"];
        if (!string.IsNullOrWhiteSpace(credentialsPath))
        {
            if (!File.Exists(credentialsPath))
                throw new InvalidOperationException($"Google:DocumentAi:CredentialsPath points to '{credentialsPath}' but no file exists there.");
            credentialsJson = File.ReadAllText(credentialsPath);
        }
        if (string.IsNullOrWhiteSpace(credentialsJson))
            throw new InvalidOperationException("Google:DocumentAi credentials are not configured. Set either CredentialsPath (file) or CredentialsJson (inline). See SECRETS.md.");

        var projectId   = cfg["Google:DocumentAi:ProjectId"];
        var location    = cfg["Google:DocumentAi:Location"];
        var processorId = cfg["Google:DocumentAi:ProcessorId"];

        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("Google:DocumentAi:ProjectId is not configured.");
        if (string.IsNullOrWhiteSpace(location))
            throw new InvalidOperationException("Google:DocumentAi:Location is not configured (use 'eu' for SK invoices).");
        if (string.IsNullOrWhiteSpace(processorId))
            throw new InvalidOperationException("Google:DocumentAi:ProcessorId is not configured.");

        // EU region needs an explicit regional endpoint; the global default
        // doesn't accept eu processor IDs.
        var endpoint = string.Equals(location, "eu", StringComparison.OrdinalIgnoreCase)
            ? "eu-documentai.googleapis.com"
            : $"{location}-documentai.googleapis.com";

        var credential = GoogleCredential
            .FromJson(credentialsJson)
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");

        _client = new DocumentProcessorServiceClientBuilder
        {
            Endpoint = endpoint,
            ChannelCredentials = credential.ToChannelCredentials()
        }.Build();

        _processorName = $"projects/{projectId}/locations/{location}/processors/{processorId}";
    }

    public async Task<DocumentAiResult> ProcessAsync(byte[] content, string mimeType, CancellationToken ct = default)
    {
        if (content == null || content.Length == 0)
            throw new ArgumentException("Empty document payload.", nameof(content));

        var request = new ProcessRequest
        {
            Name = _processorName,
            RawDocument = new RawDocument
            {
                Content = ByteString.CopyFrom(content),
                MimeType = mimeType
            }
        };

        ProcessResponse response;
        try
        {
            response = await _client.ProcessDocumentAsync(request, CallSettings.FromCancellationToken(ct));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Document AI ProcessDocument failed");
            throw;
        }

        var doc = response.Document;
        // Serialize the full Document message verbatim so we can audit / re-parse later.
        var rawJson = Google.Protobuf.JsonFormatter.Default.Format(doc);

        // Project entities into a Protobuf-free shape so the rest of the
        // codebase doesn't depend on Google.Cloud.DocumentAI.V1 types.
        var entities = doc.Entities.Select(MapEntity).ToList();

        return new DocumentAiResult(rawJson, entities, doc.Text ?? string.Empty);
    }

    private static DocumentAiEntity MapEntity(Google.Cloud.DocumentAI.V1.Document.Types.Entity e)
    {
        return new DocumentAiEntity(
            Type: e.Type,
            MentionText: e.MentionText,
            NormalizedValue: e.NormalizedValue?.Text,
            Confidence: e.Confidence,
            Properties: e.Properties.Select(MapEntity).ToList());
    }
}
