using HandyNet.SharedKernel;

namespace HandyNet.Modules.Catalog.Domain;

public enum ImageFormat
{
    Jpeg,
    Png,
    Webp,
}

/// <summary>Identifies an upload by its magic bytes — never by file name or the client's Content-Type.</summary>
public static class ImageSniffer
{
    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
            return ImageFormat.Jpeg;
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return ImageFormat.Png;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            return ImageFormat.Webp;
        return null;
    }
}

/// <summary>Size and pixel limits for product image uploads.</summary>
public static class ImageLimits
{
    public const long MaxUploadBytes = 5 * 1024 * 1024;

    /// <summary>Decompression-bomb guard: images are rejected before decoding if they declare more pixels.</summary>
    public const long MaxPixels = 40_000_000;
    public const int MaxSide = 12_000;

    /// <summary>The stored "full" image is scaled down to this longest side (never up).</summary>
    public const int FullMaxSide = 2048;
    public const int ThumbnailMaxSide = 320;

    public static Error? CheckUpload(long length)
    {
        if (length <= 0)
            return Error.Validation("image.empty", "The uploaded file is empty.");
        if (length > MaxUploadBytes)
            return Error.Validation("image.too_large", "Images are limited to 5 MB.");
        return null;
    }

    public static Error? CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return Error.Validation("image.corrupt", "The image could not be read.");
        if (width > MaxSide || height > MaxSide || (long)width * height > MaxPixels)
            return Error.Validation("image.dimensions", $"Images may have at most {MaxSide} pixels per side and {MaxPixels / 1_000_000} megapixels.");
        return null;
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
}

/// <summary>A re-encoded rendition as stored (metadata stripped).</summary>
public sealed record ImageRendition(string StorageKey, string ContentType, int Width, int Height, long SizeBytes);

/// <summary>
/// Canonical image of a catalog product (shared by all tenants, like the product itself). Uploaded by the organization
/// that created the product, or by a platform administrator. Position 0 is the primary image. Removing an image keeps
/// the row (history) with no position; the database allows at most <see cref="MaxPerProduct"/> active images and
/// unique positions per product.
/// </summary>
public sealed class ProductImage : IAuditable
{
    public const int MaxPerProduct = 8;

    private ProductImage() { }

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }

    /// <summary>0 = primary. Null once removed.</summary>
    public int? Position { get; private set; }
    public string StorageKey { get; private set; } = null!;
    public string ContentType { get; private set; } = null!;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public long SizeBytes { get; private set; }
    public string ThumbnailStorageKey { get; private set; } = null!;
    public int ThumbnailWidth { get; private set; }
    public int ThumbnailHeight { get; private set; }
    public long ThumbnailSizeBytes { get; private set; }

    /// <summary>SHA-256 (hex) of the stored full rendition; used as ETag.</summary>
    public string Sha256 { get; private set; } = null!;
    public ImageFormat OriginalFormat { get; private set; }
    public long OriginalSizeBytes { get; private set; }

    /// <summary>Null when a platform administrator uploaded it.</summary>
    public Guid? UploadedByOrganizationId { get; private set; }
    public Guid UploadedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public Guid? RemovedByUserId { get; private set; }
    public uint Version { get; private set; }

    public bool IsActive => RemovedAt is null;

    public static ProductImage Create(Guid id, Guid productId, int position, ImageRendition full, ImageRendition thumbnail, string sha256,
        ImageFormat originalFormat, long originalSizeBytes, Guid? organizationId, Guid userId, DateTimeOffset now) => new()
    {
        Id = id,
        ProductId = productId,
        Position = position,
        StorageKey = full.StorageKey,
        ContentType = full.ContentType,
        Width = full.Width,
        Height = full.Height,
        SizeBytes = full.SizeBytes,
        ThumbnailStorageKey = thumbnail.StorageKey,
        ThumbnailWidth = thumbnail.Width,
        ThumbnailHeight = thumbnail.Height,
        ThumbnailSizeBytes = thumbnail.SizeBytes,
        Sha256 = sha256,
        OriginalFormat = originalFormat,
        OriginalSizeBytes = originalSizeBytes,
        UploadedByOrganizationId = organizationId,
        UploadedByUserId = userId,
        CreatedAt = now,
    };

    public static string KeyFor(Guid productId, Guid imageId, string variant) => $"products/{productId:N}/{imageId:N}/{variant}.webp";

    internal void MoveTo(int position) => Position = position;

    internal void MarkRemoved(Guid userId, DateTimeOffset now)
    {
        Position = null;
        RemovedAt = now;
        RemovedByUserId = userId;
    }
}

/// <summary>Who may change a product's images, and how positions change. Pure rules; the caller persists.</summary>
public static class ProductImageRules
{
    /// <summary>The organization that created the product, or a platform administrator acting without a tenant.</summary>
    public static Result CanManage(Guid? productCreatedBy, Guid? actingOrganizationId, bool actingAsPlatformAdmin)
    {
        if (actingAsPlatformAdmin)
            return actingOrganizationId is null
                ? Result.Success()
                : Error.Validation("image.admin_with_tenant", "Platform administration of images runs without X-Organization-Id.");
        if (actingOrganizationId is null || productCreatedBy != actingOrganizationId)
            return Error.Forbidden("image.not_product_owner", "Only the organization that created the product (or the platform) can change its images.");
        return Result.Success();
    }

    public static Result<int> NextPosition(IReadOnlyCollection<ProductImage> active) =>
        active.Count >= ProductImage.MaxPerProduct
            ? Error.Conflict("image.limit_reached", $"A product can have at most {ProductImage.MaxPerProduct} images.")
            : active.Count;

    /// <summary>Removes the image and closes the gap, so the next image becomes primary when the primary is removed.</summary>
    public static Result Remove(IReadOnlyList<ProductImage> active, Guid imageId, Guid userId, DateTimeOffset now)
    {
        var image = active.FirstOrDefault(i => i.Id == imageId);
        if (image is null)
            return Error.NotFound("image.not_found", "Image not found.");
        image.MarkRemoved(userId, now);
        var position = 0;
        foreach (var remaining in active.Where(i => i.IsActive).OrderBy(i => i.Position))
            remaining.MoveTo(position++);
        return Result.Success();
    }

    /// <summary>Applies a new order; the request must list every active image exactly once. The first becomes primary.</summary>
    public static Result Reorder(IReadOnlyList<ProductImage> active, IReadOnlyList<Guid> orderedIds)
    {
        if (orderedIds.Count != active.Count || orderedIds.Distinct().Count() != orderedIds.Count
            || !orderedIds.All(id => active.Any(i => i.Id == id)))
            return Error.Validation("image.order_mismatch", "List every current image of the product exactly once.");
        for (var i = 0; i < orderedIds.Count; i++)
            active.First(x => x.Id == orderedIds[i]).MoveTo(i);
        return Result.Success();
    }
}
