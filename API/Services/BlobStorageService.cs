using CloudinaryDotNet;
using CloudinaryDotNet.Actions;

namespace API.Services;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string folder);
    Task DeleteAsync(string url, string folder);
}

public class CloudinaryStorageService : IBlobStorageService
{
    private readonly Cloudinary _cloudinary;

    public CloudinaryStorageService(Cloudinary cloudinary)
    {
        _cloudinary = cloudinary;
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string folder)
    {
        var publicId = $"{folder}/{Guid.NewGuid()}";
        var uploadParams = new ImageUploadParams
        {
            File = new FileDescription(fileName, stream),
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
            if (uploadIndex < 0) return;

            var afterUpload = path[(uploadIndex + 8)..]; // v123/folder/id.ext
            // Skip version segment (v123/)
            var slashIndex = afterUpload.IndexOf('/');
            if (slashIndex < 0) return;

            var publicIdWithExt = afterUpload[(slashIndex + 1)..]; // folder/id.ext
            var publicId = Path.GetFileNameWithoutExtension(publicIdWithExt);
            var dirPart = Path.GetDirectoryName(publicIdWithExt)?.Replace('\\', '/');
            var fullPublicId = string.IsNullOrEmpty(dirPart) ? publicId : $"{dirPart}/{publicId}";

            await _cloudinary.DestroyAsync(new DeletionParams(fullPublicId));
        }
        catch
        {
            // Best-effort deletion; don't fail the request
        }
    }
}
