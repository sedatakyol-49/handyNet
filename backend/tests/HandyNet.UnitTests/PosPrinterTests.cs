using HandyNet.Modules.Pos.Domain;

namespace HandyNet.UnitTests;

/// <summary>Printer profile rules (ADR-0018): hosts are never URLs, bridge URLs are plain http(s), type-specific fields.</summary>
public sealed class PosPrinterTests
{
    private static PrinterProfileSettings Settings(PrinterConnection connection, string? host = null, string? bridgeUrl = null, int? port = null,
        int paper = 80, int chars = 48, int copies = 1, int? baud = null, string? deviceId = null) =>
        new("Drucker", connection, paper, chars, true, true, copies, true, true, PrinterCodepage.Cp858, host, port, true, deviceId, bridgeUrl, 1208, 514, baud);

    [Theory]
    [InlineData("192.168.1.50", true)]
    [InlineData("printer.local", true)]
    [InlineData("tm-m30", true)]
    [InlineData("fe80::1", true)]
    [InlineData("[2001:db8::7]", true)]
    [InlineData("http://192.168.1.50", false)]
    [InlineData("192.168.1.50/cgi-bin", false)]
    [InlineData("192.168.1.50:8008", false)]
    [InlineData("user@printer", false)]
    [InlineData("printer local", false)]
    [InlineData("-printer", false)]
    public void Host_is_an_address_not_a_url(string host, bool valid) => Assert.Equal(valid, PrinterProfile.IsValidHost(host));

    [Theory]
    [InlineData("http://localhost:9123/print", true)]
    [InlineData("https://bridge.shop.lan/print", true)]
    [InlineData("ftp://localhost/print", false)]
    [InlineData("http://user:pw@localhost:9123/print", false)]
    [InlineData("localhost:9123", false)]
    [InlineData("http://localhost:9123/print#x", false)]
    public void Bridge_url_is_plain_http(string url, bool valid) => Assert.Equal(valid, PrinterProfile.IsValidBridgeUrl(url));

    [Fact]
    public void Fields_of_other_connection_types_are_cleared()
    {
        var now = DateTimeOffset.UtcNow;
        var browser = PrinterProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Settings(PrinterConnection.Browser, "192.168.1.5", "http://localhost:9123/print", 80), true, now).Value;
        Assert.Null(browser.Host);
        Assert.Null(browser.Port);
        Assert.Null(browser.BridgeUrl);
        Assert.Null(browser.UsbVendorId);
        Assert.False(browser.UseHttps);

        var bridge = PrinterProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Settings(PrinterConnection.EscPosBridge, "10.0.0.9", "http://localhost:9123/print"), false, now).Value;
        Assert.Equal(9100, bridge.Port);
        Assert.False(bridge.UseHttps);
        Assert.Null(bridge.EposDeviceId);

        var epson = PrinterProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Settings(PrinterConnection.EpsonEpos, "10.0.0.9"), false, now).Value;
        Assert.Equal(PrinterProfile.DefaultEposDeviceId, epson.EposDeviceId);
        Assert.Null(epson.Port);
        Assert.True(epson.UseHttps);

        var serial = PrinterProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Settings(PrinterConnection.WebSerial), false, now).Value;
        Assert.Equal(9600, serial.BaudRate);
        Assert.Equal(1208, serial.UsbVendorId);
        Assert.Null(serial.Host);
    }

    [Theory]
    [InlineData(PrinterConnection.Browser, 76, 48, 1, null, null, "pos.printer_paper_width")]
    [InlineData(PrinterConnection.Browser, 80, 23, 1, null, null, "pos.printer_chars_per_line")]
    [InlineData(PrinterConnection.Browser, 80, 48, 6, null, null, "pos.printer_copies")]
    [InlineData(PrinterConnection.WebSerial, 80, 48, 1, 4800, null, "pos.printer_baud_rate")]
    [InlineData(PrinterConnection.EpsonEpos, 80, 48, 1, null, "bad id!", "pos.printer_device_id")]
    public void Invalid_settings_are_rejected(PrinterConnection connection, int paper, int chars, int copies, int? baud, string? deviceId, string code)
    {
        var host = connection == PrinterConnection.EpsonEpos ? "10.0.0.9" : null;
        var result = PrinterProfile.Create(Guid.NewGuid(), Guid.NewGuid(), Settings(connection, host, paper: paper, chars: chars, copies: copies, baud: baud,
            deviceId: deviceId), false, DateTimeOffset.UtcNow);
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
    }
}
