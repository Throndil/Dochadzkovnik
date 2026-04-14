using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Microsoft.Extensions.Logging;

namespace API.Services;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string folder);
    Task DeleteAsync(string url, string folder);
}

public class CloudinaryStorageService : IBlobStorageService
{
    private readonly Cloudinary _cloudinary;
    private readonly IImageProcessingService _imageProcessor;
    private readonly ILogger<CloudinaryStorageService> _logger;

    public CloudinaryStorageService(Cloudinary cloudinary, IImageProcessingService imageProcessor,
        ILogger<CloudinaryStorageService> logger)
    {
        _cloudinary = cloudinary;
        _imageProcessor = imageProcessor;
        _logger = logger;
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string folder)
    {
        // Normalise to JPEG before uploading — handles HEIC, PNG, WebP, BMP, etc.
        await using var jpeg = await _imageProcessor.NormaliseToJpegAsync(stream);
        var jpegFileName = Path.GetFileNameWithoutExtension(fileName) + ".jpg";

        var publicId = $"{folder}/{Guid.NewGuid()}";
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
