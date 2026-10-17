using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace HandyNet.Tests.Support;

/// <summary>Builds real image files for upload tests, optionally with an EXIF block carrying a secret marker.</summary>
public static class TestImages
{
    /// <summary>Text placed inside the EXIF block; it must never appear in a processed image.</summary>
    public const string ExifSecret = "GPS-SECRET-48.1372N-11.5755E";

    public static byte[] Jpeg(int width, int height, ushort? exifOrientation = null)
    {
        var jpeg = Encode(width, height, SKEncodedImageFormat.Jpeg);
        return exifOrientation is { } orientation ? InsertExif(jpeg, orientation) : jpeg;
    }

    public static byte[] Png(int width, int height) => Encode(width, height, SKEncodedImageFormat.Png);

    public static byte[] Webp(int width, int height) => Encode(width, height, SKEncodedImageFormat.Webp);

    public static bool Contains(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            using var paint = new SKPaint { Color = SKColors.Orange };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>Inserts an APP1 "Exif" segment (big-endian TIFF, one Orientation entry) right after the JPEG SOI marker.</summary>
    private static byte[] InsertExif(byte[] jpeg, ushort orientation)
    {
        var tiff = new byte[26];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1); // one IFD entry
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), 0x0112); // Orientation
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3); // SHORT
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        // next IFD offset (bytes 22..25) = 0

        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).Concat(Encoding.ASCII.GetBytes(ExifSecret)).ToArray();
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return jpeg[..2].Concat(segment).Concat(jpeg[2..]).ToArray();
    }
}
