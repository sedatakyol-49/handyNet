namespace HandyNet.SharedKernel.Imaging;

/// <summary>Raster formats accepted from users and produced by <see cref="IImageCodec"/>.</summary>
public enum RasterFormat
{
    Jpeg,
    Png,
    Webp,
}

/// <summary>Identifies an upload by its magic bytes — never by file name or the client's Content-Type.</summary>
public static class ImageMagic
{
    public static RasterFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return RasterFormat.Jpeg;
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return RasterFormat.Png;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return RasterFormat.Webp;
        return null;
    }

    public static string ContentType(RasterFormat format) => format switch
    {
        RasterFormat.Jpeg => "image/jpeg",
        RasterFormat.Png => "image/png",
        _ => "image/webp",
    };
}

/// <summary>
/// Limits checked before an upload is decoded: file size, and the declared pixel size (decompression-bomb guard). The
/// error codes are the same as for product images (<c>image.empty</c>, <c>image.too_large</c>, <c>image.dimensions</c>,
/// <c>image.too_small</c>).
/// </summary>
public sealed record ImageDecodeLimits(long MaxBytes, int MaxSide = 12_000, long MaxPixels = 40_000_000, int MinSide = 1)
{
    public Error? CheckSize(long length)
    {
        if (length <= 0)
            return Error.Validation("image.empty", "The uploaded file is empty.");
        if (length > MaxBytes)
            return Error.Validation("image.too_large", $"Images are limited to {Megabytes(MaxBytes)} MB.");
        return null;
    }

    public Error? CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return Error.Validation("image.corrupt", "The image could not be read.");
        if (width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            return Error.Validation("image.dimensions", $"Images may have at most {MaxSide} pixels per side and {MaxPixels / 1_000_000} megapixels.");
        if (width < MinSide || height < MinSide)
            return Error.Validation("image.too_small", $"Images need at least {MinSide} pixels per side.");
        return null;
    }

    private static string Megabytes(long bytes) => (bytes / (1024d * 1024d)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// One output of <see cref="IImageCodec.Render"/>: scaled so that the longest side is at most <paramref name="MaxSide"/>
/// (never enlarged). <paramref name="FlattenOnWhite"/> draws transparent pixels onto white (always done for JPEG).
/// </summary>
public sealed record RenditionSpec(int MaxSide, RasterFormat Format, int Quality = 85, bool FlattenOnWhite = false);

public sealed record EncodedImage(byte[] Bytes, RasterFormat Format, int Width, int Height)
{
    public string ContentType => ImageMagic.ContentType(Format);
}

/// <param name="Images">One entry per <see cref="RenditionSpec"/>, in the same order.</param>
public sealed record ImageRenditions(RasterFormat OriginalFormat, int OriginalWidth, int OriginalHeight, IReadOnlyList<EncodedImage> Images);

/// <summary>
/// Decodes untrusted JPEG/PNG/WebP uploads and re-encodes them from raw pixels (ADR-0011, ADR-0014): the format is
/// identified by magic bytes, sizes are checked before decoding, the EXIF orientation is applied to the pixels, and
/// nothing of the original file survives (EXIF/GPS, XMP, ICC profiles, comments, trailing bytes). Implemented with
/// SkiaSharp in <c>HandyNet.FileStorage</c>; inject it wherever an upload or an image for a PDF has to be processed.
/// </summary>
public interface IImageCodec
{
    /// <summary>Decodes <paramref name="data"/> once and encodes every requested rendition.</summary>
    Result<ImageRenditions> Render(byte[] data, ImageDecodeLimits limits, IReadOnlyList<RenditionSpec> specs);

    /// <summary>
    /// Converts any accepted image into a baseline JPEG (transparency flattened onto white) for
    /// <see cref="Documents.SimplePdf.Image"/>, which embeds JPEG only. Example: a PNG signature from a signature pad.
    /// </summary>
    Result<EncodedImage> ToJpeg(byte[] data, ImageDecodeLimits limits, int maxSide = 1600, int quality = 90);
}
