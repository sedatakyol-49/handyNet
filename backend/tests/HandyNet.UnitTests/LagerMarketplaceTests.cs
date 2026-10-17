using HandyNet.FileStorage;
using HandyNet.Modules.Catalog.Domain;
using HandyNet.Modules.Catalog.Infrastructure;
using HandyNet.Modules.Imports.Application;
using HandyNet.Modules.Imports.Domain;
using HandyNet.Modules.Marketplace.Application;
using HandyNet.SharedKernel.Storage;
using HandyNet.Tests.Support;
using SkiaSharp;

namespace HandyNet.UnitTests;

public class ImageRulesTests
{
    [Fact]
    public void Formats_are_detected_by_magic_bytes_only()
    {
        Assert.Equal(ImageFormat.Jpeg, ImageSniffer.Detect(TestImages.Jpeg(10, 10)));
        Assert.Equal(ImageFormat.Png, ImageSniffer.Detect(TestImages.Png(10, 10)));
        Assert.Equal(ImageFormat.Webp, ImageSniffer.Detect(TestImages.Webp(10, 10)));
        Assert.Null(ImageSniffer.Detect("GIF89a......"u8));
        Assert.Null(ImageSniffer.Detect("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.Null(ImageSniffer.Detect("RIFF\0\0\0\0WAVE"u8)); // RIFF, but not WebP
        Assert.Null(ImageSniffer.Detect([0xFF, 0xD8])); // too short
    }

    [Theory]
    [InlineData(0, "image.empty")]
    [InlineData(ImageLimits.MaxUploadBytes + 1, "image.too_large")]
    public void Upload_size_is_limited(long length, string code) => Assert.Equal(code, ImageLimits.CheckUpload(length)?.Code);

    [Fact]
    public void Exactly_five_megabytes_is_allowed() => Assert.Null(ImageLimits.CheckUpload(ImageLimits.MaxUploadBytes));

    [Theory]
    [InlineData(12_001, 10, "image.dimensions")]
    [InlineData(8000, 6000, "image.dimensions")] // 48 MP
    [InlineData(0, 10, "image.corrupt")]
    [InlineData(6000, 6000, null)]
    public void Pixel_limits_guard_against_decompression_bombs(int w, int h, string? code) =>
        Assert.Equal(code, ImageLimits.CheckDimensions(w, h)?.Code);

    [Theory]
    [InlineData(4000, 3000, 2048, 2048, 1536)]
    [InlineData(300, 4000, 320, 24, 320)]
    [InlineData(100, 50, 320, 100, 50)] // never enlarged
    public void Fit_within_keeps_aspect_ratio(int w, int h, int max, int ew, int eh) =>
        Assert.Equal((ew, eh), ImageLimits.FitWithin(w, h, max));

    private static ProductImage Image(Guid product, int position) =>
        ProductImage.Create(Guid.NewGuid(), product, position, new ImageRendition("k" + Guid.NewGuid(), "image/webp", 10, 10, 1),
            new ImageRendition("t" + Guid.NewGuid(), "image/webp", 5, 5, 1), new string('a', 64), ImageFormat.Png, 10, Guid.NewGuid(),
            Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Fact]
    public void Only_the_creating_organization_or_a_platform_admin_without_tenant_may_manage_images()
    {
        var owner = Guid.NewGuid();
        Assert.True(ProductImageRules.CanManage(owner, owner, false).IsSuccess);
        Assert.Equal("image.not_product_owner", ProductImageRules.CanManage(owner, Guid.NewGuid(), false).Error?.Code);
        Assert.Equal("image.not_product_owner", ProductImageRules.CanManage(null, Guid.NewGuid(), false).Error?.Code); // platform product
        Assert.Equal("image.not_product_owner", ProductImageRules.CanManage(owner, null, false).Error?.Code);
        Assert.True(ProductImageRules.CanManage(owner, null, true).IsSuccess);
        Assert.Equal("image.admin_with_tenant", ProductImageRules.CanManage(owner, owner, true).Error?.Code);
    }

    [Fact]
    public void At_most_eight_images_per_product()
    {
        var product = Guid.NewGuid();
        var images = Enumerable.Range(0, 7).Select(i => Image(product, i)).ToList();
        Assert.Equal(7, ProductImageRules.NextPosition(images).Value);
        images.Add(Image(product, 7));
        Assert.Equal("image.limit_reached", ProductImageRules.NextPosition(images).Error?.Code);
    }

    [Fact]
    public void Removing_the_primary_image_promotes_the_next_and_closes_the_gap()
    {
        var product = Guid.NewGuid();
        var images = Enumerable.Range(0, 3).Select(i => Image(product, i)).ToList();
        Assert.True(ProductImageRules.Remove(images, images[0].Id, Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess);
        Assert.Null(images[0].Position);
        Assert.NotNull(images[0].RemovedAt);
        Assert.Equal(0, images[1].Position);
        Assert.Equal(1, images[2].Position);
        Assert.Equal("image.not_found", ProductImageRules.Remove(images, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow).Error?.Code);
    }

    [Fact]
    public void Reorder_must_list_every_image_exactly_once()
    {
        var product = Guid.NewGuid();
        var images = Enumerable.Range(0, 3).Select(i => Image(product, i)).ToList();
        Assert.Equal("image.order_mismatch", ProductImageRules.Reorder(images, [images[0].Id, images[1].Id]).Error?.Code);
        Assert.Equal("image.order_mismatch", ProductImageRules.Reorder(images, [images[0].Id, images[0].Id, images[1].Id]).Error?.Code);
        Assert.Equal("image.order_mismatch", ProductImageRules.Reorder(images, [images[0].Id, images[1].Id, Guid.NewGuid()]).Error?.Code);
        Assert.True(ProductImageRules.Reorder(images, [images[2].Id, images[0].Id, images[1].Id]).IsSuccess);
        Assert.Equal([1, 2, 0], images.Select(i => i.Position!.Value));
    }
}

public class SkiaImageProcessorTests
{
    private readonly SkiaImageProcessor _processor = new();

    [Fact]
    public void Re_encoding_strips_exif_applies_orientation_and_makes_a_thumbnail()
    {
        var upload = TestImages.Jpeg(800, 400, exifOrientation: 6); // 6 = rotate 90° clockwise to display
        Assert.True(TestImages.Contains(upload, TestImages.ExifSecret));
        Assert.True(TestImages.Contains(upload, "Exif"));

        var result = _processor.Process(upload);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var image = result.Value;
        Assert.Equal(ImageFormat.Jpeg, image.OriginalFormat);
        Assert.Equal((400, 800), (image.FullWidth, image.FullHeight)); // rotated
        Assert.Equal((160, 320), (image.ThumbnailWidth, image.ThumbnailHeight));
        foreach (var output in new[] { image.Full, image.Thumbnail })
        {
            Assert.Equal(ImageFormat.Webp, ImageSniffer.Detect(output));
            Assert.False(TestImages.Contains(output, TestImages.ExifSecret));
            Assert.False(TestImages.Contains(output, "Exif"));
            Assert.False(TestImages.Contains(output, "EXIF"));
        }
        using var decoded = SKBitmap.Decode(image.Full);
        Assert.Equal(400, decoded.Width);
    }

    [Fact]
    public void Large_images_are_scaled_down_to_2048_px()
    {
        var result = _processor.Process(TestImages.Png(3000, 1500));
        Assert.True(result.IsSuccess);
        Assert.Equal((2048, 1024), (result.Value.FullWidth, result.Value.FullHeight));
    }

    [Fact]
    public void Wrong_magic_bytes_are_rejected() =>
        Assert.Equal("image.unsupported_type", _processor.Process("not an image at all, just text"u8.ToArray()).Error?.Code);

    [Fact]
    public void A_truncated_or_fake_jpeg_is_rejected()
    {
        byte[] fake = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 1, 2, 3, 4, 5, 6, 7];
        Assert.Equal("image.corrupt", _processor.Process(fake).Error?.Code);
    }

    [Fact]
    public void A_png_renamed_to_webp_is_still_read_by_its_content()
    {
        var result = _processor.Process(TestImages.Png(50, 40));
        Assert.Equal(ImageFormat.Png, result.Value.OriginalFormat);
    }
}

public class CatalogEntryRulesTests
{
    [Fact]
    public void Model_keys_normalize_model_name_and_compatible_models()
    {
        var product = Product.Create(new NewProduct("Apple", "Display", ProductCategory.Display, PartQuality.ServicePack, "661-1", null,
            "iPhone 13", ["iPhone 13", "iphone-13 Pro", " "]), null, DateTimeOffset.UtcNow).Value;
        Assert.Equal(["iPhone 13", "iphone-13 Pro"], product.CompatibleModels);
        Assert.Equal(["IPHONE13", "IPHONE13PRO"], product.ModelKeys);
    }

    [Fact]
    public void Product_fields_are_length_checked_in_the_domain() =>
        Assert.Equal("product.field_too_long", Product.Create(new NewProduct("Apple", new string('x', 301), ProductCategory.Display,
            PartQuality.Original, "1", null), null, DateTimeOffset.UtcNow).Error?.Code);

    [Theory]
    [InlineData(ProductCategory.Smartphone, true, true)]
    [InlineData(ProductCategory.Tablet, false, true)]
    [InlineData(ProductCategory.Wearable, false, true)]
    [InlineData(ProductCategory.Display, false, false)]
    [InlineData(ProductCategory.Accessory, false, false)]
    public void Serialized_categories(ProductCategory category, bool requires, bool allows)
    {
        Assert.Equal(requires, SerializedStock.RequiresImei(category));
        Assert.Equal(allows, SerializedStock.AllowsImei(category));
    }

    [Theory]
    [InlineData(ProductCategory.Smartphone, 3, 0, false, false, "entry.imei_required")]
    [InlineData(ProductCategory.Smartphone, null, 2, false, false, null)]
    [InlineData(ProductCategory.Smartphone, 2, 2, false, false, null)]
    [InlineData(ProductCategory.Smartphone, 3, 2, false, false, "entry.quantity_mismatch")]
    [InlineData(ProductCategory.Smartphone, null, 1, true, false, "entry.serialized_offer_unsupported")]
    [InlineData(ProductCategory.Smartphone, 0, 0, true, false, null)] // offer without stock
    [InlineData(ProductCategory.Display, 5, 0, true, false, null)]
    [InlineData(ProductCategory.Display, null, 1, false, false, "entry.imei_not_allowed")]
    [InlineData(ProductCategory.Tablet, 4, 0, false, false, null)] // Wi-Fi tablets have no IMEI
    [InlineData(ProductCategory.Tablet, 4, 0, false, true, "entry.imei_required")] // already stocked by IMEI
    [InlineData(ProductCategory.Tablet, 0, 0, true, true, "entry.serialized_offer_unsupported")]
    public void Stock_rules(ProductCategory category, int? quantity, int imeis, bool offer, bool serializedStock, string? code) =>
        Assert.Equal(code, ManualEntryRules.CheckStock(category, quantity, imeis, offer, serializedStock)?.Code);
}

public class ManualEntryValidationTests
{
    private const string ValidImei = "490154203237518";
    private readonly ManualEntryValidator _validator = new();

    private static ManualEntryRequest NewProduct(string category = "Display", string? ean = "4006381333931") => new(null,
        new ManualEntryProductInput("Apple", "iPhone 13 Display", category, "ServicePack", ean, null, "iPhone 13", ["iPhone 13"]),
        null, 5, null, 12.5m, new ManualEntryOfferInput("SKU-1", 49.9m));

    [Fact]
    public void A_complete_request_is_valid() => Assert.True(_validator.Validate(NewProduct()).IsValid);

    [Fact]
    public void Either_product_id_or_new_product()
    {
        Assert.False(_validator.Validate(NewProduct() with { ProductId = Guid.NewGuid() }).IsValid);
        Assert.False(_validator.Validate(NewProduct() with { NewProduct = null }).IsValid);
        Assert.True(_validator.Validate(NewProduct() with { ProductId = Guid.NewGuid(), NewProduct = null }).IsValid);
    }

    [Fact]
    public void Invalid_ean_and_missing_identifier_are_rejected()
    {
        Assert.False(_validator.Validate(NewProduct(ean: "4006381333932")).IsValid); // check digit
        Assert.False(_validator.Validate(NewProduct(ean: null)).IsValid); // no EAN and no MPN
    }

    [Fact]
    public void Sub_cent_prices_are_rejected_not_rounded()
    {
        Assert.False(_validator.Validate(NewProduct() with { PurchasePrice = 1.005m }).IsValid);
        Assert.False(_validator.Validate(NewProduct() with { Offer = new ManualEntryOfferInput("S", 9.999m) }).IsValid);
        Assert.True(_validator.Validate(NewProduct() with { PurchasePrice = 1.50m }).IsValid);
    }

    [Fact]
    public void Imeis_need_a_valid_check_digit_and_must_be_distinct()
    {
        var phone = NewProduct("Smartphone") with { Quantity = null, Offer = null };
        Assert.True(_validator.Validate(phone with { Imeis = [ValidImei] }).IsValid);
        Assert.False(_validator.Validate(phone with { Imeis = ["490154203237519"] }).IsValid);
        Assert.False(_validator.Validate(phone with { Imeis = [ValidImei, "49-015420-323751-8"] }).IsValid); // same device
    }

    [Fact]
    public void Request_hash_identifies_the_request()
    {
        var a = ManualEntryService.HashRequest(NewProduct());
        Assert.Equal(a, ManualEntryService.HashRequest(NewProduct()));
        Assert.NotEqual(a, ManualEntryService.HashRequest(NewProduct() with { Quantity = 6 }));
        Assert.Equal(64, a.Length);
    }
}

public class BrowseQueryValidationTests
{
    private static BrowseQuery Query(decimal? min = null, decimal? max = null, int? days = null) =>
        new(null, null, null, null, null, null, null, null, null, null, min, max, days, null, null, null);

    [Fact]
    public void Price_filters_reject_sub_cent_values_and_inverted_ranges()
    {
        var validator = new BrowseQueryValidator();
        Assert.True(validator.Validate(Query(10.5m, 20m, 5)).IsValid);
        Assert.False(validator.Validate(Query(min: 10.555m)).IsValid);
        Assert.False(validator.Validate(Query(30m, 20m)).IsValid);
        Assert.False(validator.Validate(Query(days: 61)).IsValid);
        Assert.False(validator.Validate(Query(min: -1m)).IsValid);
    }
}

public sealed class FileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "handynet-unit-files", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("products/a/b/full.webp", true)]
    [InlineData("../secret.txt", false)]
    [InlineData("products/../../x", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("products\\x", false)]
    [InlineData("products//x", false)]
    [InlineData("C:/windows/x", false)]
    [InlineData("", false)]
    public void Keys_are_restricted_to_safe_relative_paths(string key, bool valid) => Assert.Equal(valid, StorageKey.IsValid(key));

    [Fact]
    public async Task Local_disk_round_trip_and_idempotent_delete()
    {
        var storage = new LocalDiskFileStorage(_root);
        await storage.PutAsync("products/p1/i1/full.webp", new MemoryStream([1, 2, 3]), "image/webp", CancellationToken.None);

        await using (var file = await storage.OpenReadAsync("products/p1/i1/full.webp", CancellationToken.None))
        {
            Assert.NotNull(file);
            Assert.Equal("image/webp", file.ContentType);
            Assert.Equal(3, file.Length);
        }

        await storage.DeleteAsync("products/p1/i1/full.webp", CancellationToken.None);
        await storage.DeleteAsync("products/p1/i1/full.webp", CancellationToken.None);
        Assert.Null(await storage.OpenReadAsync("products/p1/i1/full.webp", CancellationToken.None));
    }

    [Fact]
    public async Task Local_disk_refuses_path_traversal() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new LocalDiskFileStorage(_root).PutAsync("../outside.txt", new MemoryStream([1]), "text/plain", CancellationToken.None));

    [Fact]
    public void S3_configuration_is_validated()
    {
        Assert.NotEmpty(new S3StorageOptions().Problems());
        Assert.NotEmpty(new S3StorageOptions { Bucket = "b", ServiceUrl = "https://s3.example", AccessKey = "only-key" }.Problems());
        Assert.Empty(new S3StorageOptions { Bucket = "b", ServiceUrl = "https://s3.example" }.Problems()); // default credential chain
        Assert.Empty(new S3StorageOptions { Bucket = "b", Region = "eu-central-1", AccessKey = "k", SecretKey = "s" }.Problems());

        var config = S3FileStorage.CreateConfig(new S3StorageOptions { Bucket = "b", ServiceUrl = "https://fsn1.example.com", Region = "fsn1" });
        Assert.True(config.ForcePathStyle);
        Assert.StartsWith("https://fsn1.example.com", config.ServiceURL);
        Assert.Equal("fsn1", config.AuthenticationRegion);
    }
}
