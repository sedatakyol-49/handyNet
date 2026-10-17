using System.Security.Cryptography;
using HandyNet.Modules.Catalog.Contracts;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Storage;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HandyNet.Modules.Catalog.Application;

/// <summary>Decoded and re-encoded upload: WebP full rendition + thumbnail, metadata stripped.</summary>
internal sealed record ProcessedImage(ImageFormat OriginalFormat, byte[] Full, int FullWidth, int FullHeight, byte[] Thumbnail,
    int ThumbnailWidth, int ThumbnailHeight);

internal interface IImageProcessor
{
    Result<ProcessedImage> Process(byte[] data);
}

public sealed record ReorderImagesRequest(IReadOnlyList<Guid> ImageIds);

public enum ImageVariant
{
    Thumbnail,
    Full,
}

internal sealed record ImageContent(StoredFile File, string ETag);

/// <summary>
/// Canonical product images. Files are written to <see cref="IFileStorage"/> before the database row is committed and
/// removed again if the commit fails; removal keeps the row (no DELETE on business data) and deletes the files after
/// the commit. Image rows of one product are serialized with a row lock on the product.
/// </summary>
internal sealed class ProductImageService(
    HandyNetDbContext db,
    IFileStorage storage,
    IImageProcessor processor,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<ProductImageService> logger)
{
    public const string WebpContentType = "image/webp";

    public async Task<Result<IReadOnlyList<ProductImageInfo>>> ListAsync(Guid productId, CancellationToken ct)
    {
        if (!await db.Set<Product>().AnyAsync(p => p.Id == productId, ct))
            return Error.NotFound("product.not_found", "Product not found.");
        return (await ActiveAsync(productId, tracked: false, ct)).Select(ToInfo).ToList();
    }

    public async Task<Result<ProductImageInfo>> UploadAsync(Guid productId, byte[] data, bool asAdmin, CancellationToken ct)
    {
        if (await AuthorizeAsync(productId, asAdmin, ct) is { } denied)
            return denied;
        if (ImageLimits.CheckUpload(data.LongLength) is { } sizeError)
            return sizeError;
        if (ImageSniffer.Detect(data) is null)
            return Error.Validation("image.unsupported_type", "Only JPEG, PNG and WebP images are accepted.");
        if (ProductImageRules.NextPosition(await ActiveAsync(productId, tracked: false, ct)) is { IsSuccess: false } full)
            return full.Error!; // fail fast before the CPU work; checked again under the lock

        var processed = processor.Process(data);
        if (!processed.IsSuccess)
            return processed.Error!;
        var image = processed.Value;

        var imageId = Guid.CreateVersion7();
        var fullRendition = new ImageRendition(ProductImage.KeyFor(productId, imageId, "full"), WebpContentType, image.FullWidth,
            image.FullHeight, image.Full.LongLength);
        var thumbRendition = new ImageRendition(ProductImage.KeyFor(productId, imageId, "thumb"), WebpContentType, image.ThumbnailWidth,
            image.ThumbnailHeight, image.Thumbnail.LongLength);

        using (var fullStream = new MemoryStream(image.Full, writable: false))
            await storage.PutAsync(fullRendition.StorageKey, fullStream, WebpContentType, ct);
        using (var thumbStream = new MemoryStream(image.Thumbnail, writable: false))
            await storage.PutAsync(thumbRendition.StorageKey, thumbStream, WebpContentType, ct);

        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockProductAsync(productId, ct);
            var position = ProductImageRules.NextPosition(await ActiveAsync(productId, tracked: true, ct));
            if (!position.IsSuccess)
            {
                await DeleteFilesAsync(fullRendition.StorageKey, thumbRendition.StorageKey);
                return position.Error!;
            }

            var entity = ProductImage.Create(imageId, productId, position.Value, fullRendition, thumbRendition,
                Convert.ToHexStringLower(SHA256.HashData(image.Full)), image.OriginalFormat, data.LongLength,
                asAdmin ? null : tenant.OrganizationId, currentUser.UserId, clock.UtcNow);
            db.Add(entity);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToInfo(entity);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.PostgresException)
        {
            logger.LogWarning(ex, "Storing image {ImageId} for product {ProductId} failed; removing its files", imageId, productId);
            await DeleteFilesAsync(fullRendition.StorageKey, thumbRendition.StorageKey);
            return Error.Conflict("image.concurrent", "The images of this product were changed concurrently. Try again.");
        }
    }

    public async Task<Result> RemoveAsync(Guid productId, Guid imageId, bool asAdmin, CancellationToken ct)
    {
        if (await AuthorizeAsync(productId, asAdmin, ct) is { } denied)
            return denied;

        ProductImage? removed;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockProductAsync(productId, ct);
            var active = await ActiveAsync(productId, tracked: true, ct);
            removed = active.FirstOrDefault(i => i.Id == imageId);
            var result = ProductImageRules.Remove(active, imageId, currentUser.UserId, clock.UtcNow);
            if (!result.IsSuccess)
                return result;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.PostgresException)
        {
            logger.LogWarning(ex, "Removing image {ImageId} failed", imageId);
            return Error.Conflict("image.concurrent", "The images of this product were changed concurrently. Try again.");
        }

        // The row stays (history); the files go. A failure here leaves an orphan file, never a broken image row.
        await DeleteFilesAsync(removed!.StorageKey, removed.ThumbnailStorageKey);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<ProductImageInfo>>> ReorderAsync(Guid productId, ReorderImagesRequest request, bool asAdmin,
        CancellationToken ct)
    {
        if (await AuthorizeAsync(productId, asAdmin, ct) is { } denied)
            return denied;
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockProductAsync(productId, ct);
            var active = await ActiveAsync(productId, tracked: true, ct);
            var result = ProductImageRules.Reorder(active, request.ImageIds ?? []);
            if (!result.IsSuccess)
                return result.Error!;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct); // the unique (product, position) constraint is checked here (deferred)
            return active.OrderBy(i => i.Position).Select(ToInfo).ToList();
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.PostgresException)
        {
            logger.LogWarning(ex, "Reordering images of product {ProductId} failed", productId);
            return Error.Conflict("image.concurrent", "The images of this product were changed concurrently. Try again.");
        }
    }

    public async Task<ImageContent?> OpenAsync(Guid imageId, ImageVariant variant, CancellationToken ct)
    {
        var image = await db.Set<ProductImage>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId && i.RemovedAt == null, ct);
        if (image is null)
            return null;
        var file = await storage.OpenReadAsync(variant == ImageVariant.Full ? image.StorageKey : image.ThumbnailStorageKey, ct);
        if (file is null)
        {
            logger.LogError("Image {ImageId} has no stored {Variant} file", imageId, variant);
            return null;
        }
        // Content behind an image id never changes, so the id + variant is a strong validator.
        return new ImageContent(file, $"\"{image.Id:N}-{(variant == ImageVariant.Full ? "f" : "t")}\"");
    }

    internal static ProductImageInfo ToInfo(ProductImage i) => new(i.Id, i.Position ?? -1, i.Position == 0, i.Width, i.Height,
        i.ThumbnailWidth, i.ThumbnailHeight, ProductImageUrls.Thumbnail(i.Id), ProductImageUrls.Full(i.Id));

    private async Task<Error?> AuthorizeAsync(Guid productId, bool asAdmin, CancellationToken ct)
    {
        var product = await db.Set<Product>().AsNoTracking().Where(p => p.Id == productId)
            .Select(p => new { p.CreatedByOrganizationId }).FirstOrDefaultAsync(ct);
        if (product is null)
            return Error.NotFound("product.not_found", "Product not found.");
        if (asAdmin && !currentUser.IsPlatformAdmin)
            return Error.Forbidden("permission.denied", "Platform administrators only.");
        var allowed = ProductImageRules.CanManage(product.CreatedByOrganizationId, tenant.OrganizationId, asAdmin);
        return allowed.IsSuccess ? null : allowed.Error;
    }

    private Task LockProductAsync(Guid productId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM catalog.products WHERE id = {productId} FOR UPDATE", ct);

    private async Task<List<ProductImage>> ActiveAsync(Guid productId, bool tracked, CancellationToken ct)
    {
        var query = db.Set<ProductImage>().Where(i => i.ProductId == productId && i.RemovedAt == null);
        if (!tracked)
            query = query.AsNoTracking();
        return await query.OrderBy(i => i.Position).ToListAsync(ct);
    }

    private async Task DeleteFilesAsync(params string[] keys)
    {
        foreach (var key in keys)
        {
            try
            {
                await storage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete stored file {Key}", key);
            }
        }
    }
}
