using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Imaging;
using SkiaSharp;

namespace HandyNet.FileStorage.Imaging;

/// <summary>
/// <see cref="IImageCodec"/> with SkiaSharp (MIT): the same pipeline as the product images (ADR-0011) — magic bytes must
/// match the decoder's format, declared size checked before decoding, EXIF orientation applied to the pixels, output
/// re-encoded from raw pixels so no metadata or trailing payload of the upload survives.
/// </summary>
public sealed class SkiaImageCodec : IImageCodec
{
    public Result<ImageRenditions> Render(byte[] data, ImageDecodeLimits limits, IReadOnlyList<RenditionSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (specs.Count == 0)
            throw new ArgumentException("At least one rendition is required.", nameof(specs));

        var decoded = Decode(data, limits);
        if (!decoded.IsSuccess)
            return decoded.Error!;
        var (format, oriented) = decoded.Value;
        using (oriented)
        {
            var images = new List<EncodedImage>(specs.Count);
            foreach (var spec in specs)
            {
                var encoded = Encode(oriented, spec);
                if (encoded is null)
                    return Corrupt();
                images.Add(encoded);
            }
            return new ImageRenditions(format, oriented.Width, oriented.Height, images);
        }
    }

    public Result<EncodedImage> ToJpeg(byte[] data, ImageDecodeLimits limits, int maxSide = 1600, int quality = 90)
    {
        var rendered = Render(data, limits, [new RenditionSpec(maxSide, RasterFormat.Jpeg, quality, FlattenOnWhite: true)]);
        return rendered.IsSuccess ? rendered.Value.Images[0] : rendered.Error!;
    }

    private static Result<(RasterFormat Format, SKBitmap Oriented)> Decode(byte[] data, ImageDecodeLimits limits)
    {
        if (limits.CheckSize(data.LongLength) is { } sizeError)
            return sizeError;
        if (ImageMagic.Detect(data) is not { } format)
            return Error.Validation("image.unsupported_type", "Only JPEG, PNG and WebP images are accepted.");

        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec is null || !Matches(codec.EncodedFormat, format))
            return Corrupt();
        if (limits.CheckDimensions(codec.Info.Width, codec.Info.Height) is { } dimensionError)
            return dimensionError;

        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul, srgb);
        using var pixels = new SKBitmap(info);
        var result = codec.GetPixels(info, pixels.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            return Corrupt();
        return (format, Orient(pixels, codec.EncodedOrigin));
    }

    private static EncodedImage? Encode(SKBitmap source, RenditionSpec spec)
    {
        var (width, height) = FitWithin(source.Width, source.Height, spec.MaxSide);
        var flatten = spec.FlattenOnWhite || spec.Format == RasterFormat.Jpeg;
        using var target = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888,
            flatten ? SKAlphaType.Opaque : SKAlphaType.Premul, source.ColorSpace));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(flatten ? SKColors.White : SKColors.Transparent);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var encodedImage = SKImage.FromBitmap(target);
        var skFormat = spec.Format switch
        {
            RasterFormat.Jpeg => SKEncodedImageFormat.Jpeg,
            RasterFormat.Png => SKEncodedImageFormat.Png,
            _ => SKEncodedImageFormat.Webp,
        };
        using var data = encodedImage.Encode(skFormat, Math.Clamp(spec.Quality, 1, 100));
        return data is null ? null : new EncodedImage(data.ToArray(), spec.Format, width, height);
    }

    /// <summary>Scales (width, height) so the longest side is at most <paramref name="maxSide"/>; never enlarges.</summary>
    public static (int Width, int Height) FitWithin(int width, int height, int maxSide)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxSide)
            return (width, height);
        var scale = (double)maxSide / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static bool Matches(SKEncodedImageFormat encoded, RasterFormat sniffed) => (encoded, sniffed) switch
    {
        (SKEncodedImageFormat.Jpeg, RasterFormat.Jpeg) => true,
        (SKEncodedImageFormat.Png, RasterFormat.Png) => true,
        (SKEncodedImageFormat.Webp, RasterFormat.Webp) => true,
        _ => false,
    };

    /// <summary>Applies an EXIF orientation by drawing the pixels through the matching affine transform.</summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
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

    private static Error Corrupt() => Error.Validation("image.corrupt", "The image could not be read.");
}
