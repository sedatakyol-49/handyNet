using HandyNet.Modules.Catalog.Application;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.SharedKernel;
using SkiaSharp;

namespace HandyNet.Modules.Catalog.Infrastructure;

/// <summary>
/// Decodes an upload with SkiaSharp (MIT) and re-encodes it from raw pixels, so nothing of the original file survives:
/// EXIF (incl. GPS), XMP, ICC profiles, comments and any trailing bytes are dropped. The EXIF orientation is applied to
/// the pixels before it is discarded. Outputs WebP: a "full" rendition (longest side ≤ 2048 px) and a thumbnail
/// (≤ 320 px). The declared size is checked before decoding (decompression-bomb guard).
/// </summary>
internal sealed class SkiaImageProcessor : IImageProcessor
{
    private const int FullQuality = 85;
    private const int ThumbnailQuality = 80;

    public Result<ProcessedImage> Process(byte[] data)
    {
        if (ImageLimits.CheckUpload(data.LongLength) is { } sizeError)
            return sizeError;
        if (ImageSniffer.Detect(data) is not { } format)
            return Unsupported();

        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec is null || !Matches(codec.EncodedFormat, format))
            return Corrupt();
        if (ImageLimits.CheckDimensions(codec.Info.Width, codec.Info.Height) is { } dimensionError)
            return dimensionError;

        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return Corrupt();

        var (fullWidth, fullHeight) = ImageLimits.FitWithin(info.Width, info.Height, ImageLimits.FullMaxSide);
        using var scaled = Resize(decoded, fullWidth, fullHeight);
        using var full = Orient(scaled, codec.EncodedOrigin);
        var (thumbWidth, thumbHeight) = ImageLimits.FitWithin(full.Width, full.Height, ImageLimits.ThumbnailMaxSide);
        using var thumbnail = Resize(full, thumbWidth, thumbHeight);

        var fullBytes = Encode(full, FullQuality);
        var thumbBytes = Encode(thumbnail, ThumbnailQuality);
        if (fullBytes is null || thumbBytes is null)
            return Corrupt();
        return new ProcessedImage(format, fullBytes, full.Width, full.Height, thumbBytes, thumbnail.Width, thumbnail.Height);
    }

    private static bool Matches(SKEncodedImageFormat encoded, ImageFormat sniffed) => (encoded, sniffed) switch
    {
        (SKEncodedImageFormat.Jpeg, ImageFormat.Jpeg) => true,
        (SKEncodedImageFormat.Png, ImageFormat.Png) => true,
        (SKEncodedImageFormat.Webp, ImageFormat.Webp) => true,
        _ => false,
    };

    private static SKBitmap Resize(SKBitmap source, int width, int height)
    {
        var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, source.ColorSpace));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return target;
    }

    /// <summary>Applies an EXIF orientation by drawing the pixels through the matching affine transform.</summary>
    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        float w = source.Width, h = source.Height;
        // x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY
        var (matrix, swap) = origin switch
        {
            SKEncodedOrigin.TopRight => (new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1), false),
            SKEncodedOrigin.BottomRight => (new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.BottomLeft => (new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.LeftTop => (new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightTop => (new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightBottom => (new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), true),
            SKEncodedOrigin.LeftBottom => (new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1), true),
            _ => (SKMatrix.Identity, false),
        };
        var target = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul, source.ColorSpace));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

    private static byte[]? Encode(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, quality);
        return data?.ToArray();
    }

    private static Error Unsupported() => Error.Validation("image.unsupported_type", "Only JPEG, PNG and WebP images are accepted.");
    private static Error Corrupt() => Error.Validation("image.corrupt", "The image could not be read.");
}
