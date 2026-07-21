using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Logging;

namespace API.Services;

public interface IBlobStorageService
{
    /// <summary>
    /// Upload an image. The stream is normalised to JPEG before upload —
    /// handles HEIC, PNG, WebP, BMP, etc. Use for photos.
    /// </summary>
    Task<string> UploadAsync(Stream stream, string fileName, string folder);

    /// <summary>
    /// Upload a non-image file as-is (PDF, ZIP, etc.). No normalisation.
    /// Used by the invoice-scanning flow to preserve the original PDF
    /// immutably for audit. Cloudinary stores it as resource_type=raw.
    /// </summary>
    Task<string> UploadRawAsync(Stream stream, string fileName, string folder);

    Task DeleteAsync(string url, string folder);
}

public class CloudinaryStorageService : IBlobStorageService
{
    private readonly Cloudinary _cloudinary;
    private readonly IImageProcessingService _imageProcessor;
    private readonly ILogger<CloudinaryStorageService> _logger;

    /// <summary>
    /// Per-customer top-level folder every asset lives under, isolating this
    /// customer from anyone else sharing the Cloudinary account. Set one unique
    /// value per customer deployment via <c>Cloudinary:ProjectFolder</c>
    /// (env <c>Cloudinary__ProjectFolder</c>); when a customer gets their own
    /// database they must also get their own folder here. Falls back to
    /// "profistav" (the first customer) so the original install keeps working.
    /// </summary>
    private const string DefaultRoot = "profistav";
    private readonly string _root;

    public CloudinaryStorageService(Cloudinary cloudinary, IImageProcessingService imageProcessor,
        ILogger<CloudinaryStorageService> logger, IConfiguration config)
    {
        _cloudinary = cloudinary;
        _imageProcessor = imageProcessor;
        _logger = logger;

        // Never allow an empty root — an empty prefix would dump every asset at
        // the account root and mix customers together. Empty/whitespace/unset
        // all fall back to the default, with a loud warning so a new customer
        // deployment that forgot to set its own folder is easy to spot in logs.
        var configured = config["Cloudinary:ProjectFolder"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            _root = DefaultRoot;
            _logger.LogWarning(
                "Cloudinary:ProjectFolder is not set — using default root '{Root}'. " +
                "Set a UNIQUE value per customer (own DB → own folder) so assets stay isolated.", _root);
        }
        else
        {
            _root = configured.Trim().Trim('/');
            if (string.IsNullOrEmpty(_root)) _root = DefaultRoot;
            _logger.LogInformation("Cloudinary root folder: {Root}", _root);
        }
    }

    /// <summary>Prepend the project root to a relative folder path.</summary>
    private string Rooted(string folder) => string.IsNullOrEmpty(_root) ? folder : $"{_root}/{folder}";

    public async Task<string> UploadAsync(Stream stream, string fileName, string folder)
    {
        // Normalise to JPEG before uploading — handles HEIC, PNG, WebP, BMP, etc.
        await using var jpeg = await _imageProcessor.NormaliseToJpegAsync(stream);
        var jpegFileName = Path.GetFileNameWithoutExtension(fileName) + ".jpg";

        var publicId = $"{Rooted(folder)}/{Guid.NewGuid()}";
        var uploadParams = new ImageUploadParams
        {
            File = new FileDescription(jpegFileName, jpeg),
            PublicId = publicId,
            Overwrite = true
        };

        var result = await _cloudinary.UploadAsync(uploadParams);
        if (result.Error != null)
            throw new Exception($"Cloudinary upload failed: {result.Error.Message}");

        return result.SecureUrl.ToString();
    }

    public async Task<string> UploadRawAsync(Stream stream, string fileName, string folder)
    {
        // Raw upload: no image normalisation, file goes up byte-identical.
        // Cloudinary stores it under resource_type=raw and serves it at
        // /raw/upload/... in the URL.
        var publicId = $"{Rooted(folder)}/{Guid.NewGuid()}{Path.GetExtension(fileName)}";
        var uploadParams = new RawUploadParams
        {
            File = new FileDescription(fileName, stream),
            PublicId = publicId,
            Overwrite = true
        };

        var result = await _cloudinary.UploadAsync(uploadParams);
        if (result.Error != null)
            throw new Exception($"Cloudinary raw upload failed: {result.Error.Message}");

        return result.SecureUrl.ToString();
    }

    public async Task DeleteAsync(string url, string folder)
    {
        // Extract public ID from the Cloudinary URL
        // URL format: https://res.cloudinary.com/{cloud}/image/upload/v123/{folder}/{id}.ext
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath; // /image/upload/v123/folder/id.ext
            var uploadIndex = path.IndexOf("/upload/", StringComparison.Ordinal);
            if (uploadIndex < 0)
            {
                _logger.LogWarning("Cloudinary delete skipped — could not find /upload/ in URL: {Url}", url);
                return;
            }

            var afterUpload = path[(uploadIndex + 8)..]; // v123/folder/id.ext
            // Skip version segment (v123/)
            var slashIndex = afterUpload.IndexOf('/');
            if (slashIndex < 0)
            {
                _logger.LogWarning("Cloudinary delete skipped — unexpected URL format (no version segment): {Url}", url);
                return;
            }

            var publicIdWithExt = afterUpload[(slashIndex + 1)..]; // folder/id.ext
            var publicId = Path.GetFileNameWithoutExtension(publicIdWithExt);
            var dirPart = Path.GetDirectoryName(publicIdWithExt)?.Replace('\\', '/');
            var fullPublicId = string.IsNullOrEmpty(dirPart) ? publicId : $"{dirPart}/{publicId}";

            var result = await _cloudinary.DestroyAsync(new DeletionParams(fullPublicId));
            if (result.Error != null)
                _logger.LogWarning("Cloudinary delete failed for public ID {PublicId}: {Error}", fullPublicId, result.Error.Message);
        }
        catch (Exception ex)
        {
            // Best-effort deletion — log but don't fail the request
            _logger.LogError(ex, "Cloudinary delete threw an exception for URL: {Url}", url);
        }
    }
}
