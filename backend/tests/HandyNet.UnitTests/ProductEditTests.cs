using HandyNet.Modules.Catalog.Domain;

namespace HandyNet.UnitTests;

/// <summary>ADR-0017: the creating organization changes a canonical product's data with the same rules as creating it.</summary>
public class ProductEditTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private static Product NewDisplay() => Product.Create(new NewProduct("Apple", "iPhone 13 Display", ProductCategory.Display,
        PartQuality.ServicePack, "661-1", "4006381333931", "iPhone 13", ["iPhone 13"]), Owner, DateTimeOffset.UtcNow).Value;

    [Fact]
    public void Update_normalizes_like_create_and_reports_a_change()
    {
        var product = NewDisplay();
        var result = product.Update(new NewProduct(" Apple ", " iPhone 13 Display OLED ", ProductCategory.Display, PartQuality.Original,
            " 661-22 ", "96385074", "iPhone 13", ["iPhone 13", "iphone 13", "iPhone 13 Pro", " "]));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value);
        Assert.Equal("Apple", product.Brand);
        Assert.Equal("iPhone 13 Display OLED", product.Name);
        Assert.Equal(PartQuality.Original, product.Quality);
        Assert.Equal("661-22", product.Mpn);
        Assert.Equal("66122", product.MpnKey);
        Assert.Equal("0000096385074", product.Ean); // GTIN-8 padded to 13
        Assert.Equal(["iPhone 13", "iPhone 13 Pro"], product.CompatibleModels);
        Assert.Equal(["IPHONE13", "IPHONE13PRO"], product.ModelKeys);
        Assert.Equal(Owner, product.CreatedByOrganizationId); // ownership never changes
    }

    [Fact]
    public void Update_with_the_same_data_is_no_change()
    {
        var product = NewDisplay();
        var result = product.Update(new NewProduct("Apple", "iPhone 13 Display", ProductCategory.Display, PartQuality.ServicePack, "661-1",
            "4006381333931", "iPhone 13", ["iPhone 13"]));
        Assert.True(result.IsSuccess);
        Assert.False(result.Value);
    }

    [Theory]
    [InlineData("", "Name", "661", null, "product.incomplete")]
    [InlineData("Apple", "Name", null, null, "product.identifier_required")]
    [InlineData("Apple", "Name", null, "4006381333932", "product.invalid_ean")]
    public void Update_validates_like_create(string brand, string name, string? mpn, string? ean, string code)
    {
        var product = NewDisplay();
        var result = product.Update(new NewProduct(brand, name, ProductCategory.Display, PartQuality.ServicePack, mpn, ean));
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal("iPhone 13 Display", product.Name); // unchanged on failure
    }

    [Theory]
    [InlineData(ProductCategory.Display, ProductCategory.Battery, null)]
    [InlineData(ProductCategory.Tablet, ProductCategory.Wearable, null)]
    [InlineData(ProductCategory.Display, ProductCategory.Smartphone, "product.category_stock_mode")]
    [InlineData(ProductCategory.Smartphone, ProductCategory.Tablet, "product.category_stock_mode")]
    [InlineData(ProductCategory.Tablet, ProductCategory.Accessory, "product.category_stock_mode")]
    public void Category_changes_must_keep_the_stock_mode(ProductCategory from, ProductCategory to, string? code)
    {
        var product = Product.Create(new NewProduct("Brand", "Item", from, PartQuality.NotApplicable, "X-1", null), Owner, DateTimeOffset.UtcNow).Value;
        var result = product.Update(new NewProduct("Brand", "Item", to, PartQuality.NotApplicable, "X-1", null));
        Assert.Equal(code, result.Error?.Code);
        Assert.Equal(code is null ? to : from, product.Category);
    }

    [Fact]
    public void Only_the_creating_organization_may_edit()
    {
        Assert.True(Product.CanEdit(Owner, Owner));
        Assert.False(Product.CanEdit(Owner, Guid.NewGuid()));
        Assert.False(Product.CanEdit(Owner, null));
        Assert.False(Product.CanEdit(null, null)); // platform products: not through the tenant endpoint
        Assert.False(Product.CanEdit(null, Owner));
    }
}
