using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;

namespace API.Services;

public interface IImageProcessingService
{
    /// <summary>
    /// Normalises any image stream to PNG, capping the longest edge at <paramref name="maxDimension"/> px.
    /// The returned stream is positioned at 0 and is ready for upload.
    /// </summary>
    Task<Stream> NormaliseToPngAsync(Stream input, int maxDimension = 2048);
}

public class ImageProcessingService : IImageProcessingService
{
    public async Task<Stream> NormaliseToPngAsync(Stream input, int maxDimension = 2048)
    {
        // Load — ImageSharp detects format from stream header
        using var image = await Image.LoadAsync(input);

        // Resize if either dimension exceeds the cap
        if (image.Width > maxDimension || image.Height > maxDimension)
        {
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(maxDimension, maxDimension),
                Mode = ResizeMode.Max   // maintains aspect ratio, never upscales
            }));
        }

        var output = new MemoryStream();
        await image.SaveAsPngAsync(output, new PngEncoder { CompressionLevel = PngCompressionLevel.DefaultCompression });
        output.Position = 0;
        return output;
    }
}
