using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace API.Services;

public interface IImageProcessingService
{
    /// <summary>
    /// Normalises any image stream to JPEG (quality 75), capping the longest edge at <paramref name="maxDimension"/> px.
    /// The returned stream is positioned at 0 and is ready for upload.
    /// </summary>
    Task<Stream> NormaliseToJpegAsync(Stream input, int maxDimension = 1600);
}

public class ImageProcessingService : IImageProcessingService
{
    public async Task<Stream> NormaliseToJpegAsync(Stream input, int maxDimension = 1600)
    {
        // Load — ImageSharp auto-detects format from the stream header
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
        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 75 });
        output.Position = 0;
        return output;
    }
}
