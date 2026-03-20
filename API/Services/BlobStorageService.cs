using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace API.Services;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string containerName);
    Task DeleteAsync(string blobName, string containerName);
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;

    public BlobStorageService(BlobServiceClient blobServiceClient)
    {
        _blobServiceClient = blobServiceClient;
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string containerName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

        var blobName = $"{Guid.NewGuid()}{Path.GetExtension(fileName)}";
        var blobClient = containerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(stream, new BlobHttpHeaders
        {
            ContentType = GetContentType(fileName)
        });

        return blobClient.Uri.ToString();
    }

    public async Task DeleteAsync(string blobUrl, string containerName)
    {
        var uri = new Uri(blobUrl);
        var blobName = Path.GetFileName(uri.LocalPath);
        var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
        await containerClient.DeleteBlobIfExistsAsync(blobName);
    }

    private static string GetContentType(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}
