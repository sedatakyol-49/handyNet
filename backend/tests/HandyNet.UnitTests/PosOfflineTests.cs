using System.Security.Cryptography;
using System.Text;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;

namespace HandyNet.UnitTests;

/// <summary>Offline sales (ADR-0022): reference and time rules, receipt snapshot and request fingerprint.</summary>
public class PosOfflineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("OFF-AB12-7", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("OFF 1", false)]
    [InlineData("OFF_1", false)]
    [InlineData("OFF-ä", false)]
    [InlineData("0123456789012345678901234567890123456789", true)]   // 40
    [InlineData("01234567890123456789012345678901234567890", false)] // 41
    public void Reference_is_1_to_40_letters_digits_or_hyphens(string reference, bool valid)
    {
        Assert.Equal(valid, OfflineRecord.IsReference(reference));
        Assert.Equal(valid ? null : "pos.offline_reference", new OfflineRecord(Now, reference).Check(Now, 72).Error?.Code);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]            // clock drift of the device
    [InlineData(6, false)]           // further in the future
    [InlineData(-72 * 60, true)]     // exactly the maximum age
    [InlineData(-72 * 60 - 1, false)]
    public void Recording_time_must_be_plausible(int minutesFromNow, bool valid)
    {
        var result = new OfflineRecord(Now.AddMinutes(minutesFromNow), "OFF-1").Check(Now, 72);
        Assert.Equal(valid ? null : "pos.offline_time_invalid", result.Error?.Code);
        if (!valid)
            Assert.Equal(HandyNet.SharedKernel.ErrorKind.Validation, result.Error!.Kind);
    }

    [Fact]
    public void Receipt_keeps_the_booking_time_and_prints_the_offline_record()
    {
        var recorded = new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.FromHours(1)); // 09:30 in Berlin (CET)
        var header = PosReceiptTests.Header() with { Offline = new OfflineRecord(recorded, "OFF-AB12-7") };
        var sale = Receipt.CreateSale(header, [PosReceiptTests.Cable()], [new PaymentInput(PaymentMethod.Cash, 19_00)]).Value;
        Assert.Equal(header.Now, sale.CreatedAt);
        Assert.Equal((recorded.ToUniversalTime(), "OFF-AB12-7"), (sale.OfflineRecordedAt!.Value, sale.OfflineReference));

        var fiscal = new FiscalData(FiscalStatus.NotConnected, "Test", null, null, null, null, null, null);
        var document = ReceiptDocument.For(sale, new SellerInfo("Shop", "Shop GmbH", "Weg 1", "10115", "Berlin", "DE", null), "Kasse", null, fiscal);
        Assert.Contains("Offline erfasst am 11.11.2026 09:30 · Offline-Nr. OFF-AB12-7 · nachträglich gebucht und signiert", document.Notes);
        Assert.Equal(new DocumentOffline(recorded.ToUniversalTime(), "OFF-AB12-7"), document.Offline);

        // A normal sale carries nothing of it.
        var normal = Receipt.CreateSale(PosReceiptTests.Header(), [PosReceiptTests.Cable()], [new PaymentInput(PaymentMethod.Cash, 19_00)]).Value;
        Assert.Null(normal.Offline);
        var normalDocument = ReceiptDocument.For(normal, new SellerInfo("Shop", "Shop GmbH", "Weg 1", "10115", "Berlin", "DE", null), "Kasse", null, fiscal);
        Assert.Null(normalDocument.Offline);
        Assert.DoesNotContain(normalDocument.Notes, n => n.Contains("Offline"));
    }

    [Fact]
    public void Fingerprint_changes_only_when_the_offline_record_is_present()
    {
        var register = Guid.NewGuid();
        var product = Guid.NewGuid();
        var request = new CreateSaleRequest(register, [new SaleLineRequest(product, null, 2, 10_00, 1_00)], [new SalePaymentRequest(PaymentMethod.Cash, 20_00)]);
        var legacy = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"sale#{register:N}#{product:N}::2:1000:100#Cash:2000")));
        Assert.Equal(legacy, SaleService.HashRequest(request));

        var offline = request with { Offline = new OfflineSaleRequest(Now, "OFF-1") };
        var hash = SaleService.HashRequest(offline);
        Assert.NotEqual(legacy, hash);
        // Same instant in another offset is the same request; another time or reference is not.
        Assert.Equal(hash, SaleService.HashRequest(request with { Offline = new OfflineSaleRequest(Now.ToOffset(TimeSpan.FromHours(1)), "OFF-1") }));
        Assert.NotEqual(hash, SaleService.HashRequest(request with { Offline = new OfflineSaleRequest(Now.AddSeconds(1), "OFF-1") }));
        Assert.NotEqual(hash, SaleService.HashRequest(request with { Offline = new OfflineSaleRequest(Now, "OFF-2") }));
    }
}
