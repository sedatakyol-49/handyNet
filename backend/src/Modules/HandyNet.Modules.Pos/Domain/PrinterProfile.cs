using System.Text.RegularExpressions;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// How the browser of a till reaches the receipt printer (ADR-0018). The server only stores the configuration; it never
/// connects to a printer or bridge.
/// </summary>
public enum PrinterConnection
{
    /// <summary>System / integrated printer through the browser's print dialog (works everywhere).</summary>
    Browser,

    /// <summary>Epson ePOS-Print: XML over HTTP(S) from the browser to the printer (<c>/cgi-bin/epos/service.cgi</c>).</summary>
    EpsonEpos,

    /// <summary>Star WebPRNT: XML over HTTP(S) from the browser to the printer (<c>/StarWebPRNT/SendMessage</c>).</summary>
    StarWebPrnt,

    /// <summary>Raw ESC/POS on TCP 9100 through a local "SEHA Print Bridge" (browsers cannot open TCP sockets).</summary>
    EscPosBridge,

    /// <summary>ESC/POS over WebUSB (Chromium only).</summary>
    WebUsb,

    /// <summary>ESC/POS over WebSerial (Chromium only; serial or virtual COM port printers).</summary>
    WebSerial,
}

/// <summary>Character table for ESC/POS text (both contain German umlauts; only CP858 has €).</summary>
public enum PrinterCodepage
{
    Cp858,
    Pc437,
}

/// <summary>The editable fields of a printer profile (create and update).</summary>
public sealed record PrinterProfileSettings(
    string Name,
    PrinterConnection Connection,
    int PaperWidthMm,
    int CharsPerLine,
    bool AutoPrint,
    bool OpenDrawerOnCash,
    int Copies,
    bool CutPaper,
    bool PrintQr,
    PrinterCodepage Codepage,
    string? Host,
    int? Port,
    bool UseHttps,
    string? EposDeviceId,
    string? BridgeUrl,
    int? UsbVendorId,
    int? UsbProductId,
    int? BaudRate);

/// <summary>
/// A receipt printer configuration of one register (Kasse). Tenant-owned (RLS). A register may have several profiles
/// (e.g. a network printer at the counter and a USB printer at the back desk); one of them is the register's default, and
/// each device can choose another one locally. Configuration only: deleting a profile is allowed (audited).
/// </summary>
public sealed partial class PrinterProfile : IAuditable
{
    public const int MaxNameLength = 100;
    public const int MaxHostLength = 253;
    public const int MaxDeviceIdLength = 64;
    public const int MaxBridgeUrlLength = 500;
    public const int MinCharsPerLine = 24;
    public const int MaxCharsPerLine = 64;
    public const int MaxCopies = 5;
    public const int MaxProfilesPerRegister = 20;
    public const string DefaultEposDeviceId = "local_printer";
    public static readonly int[] PaperWidths = [58, 80];
    public static readonly int[] BaudRates = [9600, 19200, 38400, 57600, 115200];

    private PrinterProfile() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public string Name { get; private set; } = null!;
    public PrinterConnection Connection { get; private set; }
    public int PaperWidthMm { get; private set; }
    public int CharsPerLine { get; private set; }
    public bool AutoPrint { get; private set; }
    public bool OpenDrawerOnCash { get; private set; }
    public int Copies { get; private set; }
    public bool CutPaper { get; private set; }
    public bool PrintQr { get; private set; }
    public PrinterCodepage Codepage { get; private set; }

    /// <summary>Printer IP/hostname (ePOS, WebPRNT, bridge target).</summary>
    public string? Host { get; private set; }

    /// <summary>Port: HTTP(S) port of the printer (ePOS/WebPRNT, null = 80/443) or the raw TCP port behind the bridge (default 9100).</summary>
    public int? Port { get; private set; }

    public bool UseHttps { get; private set; }
    public string? EposDeviceId { get; private set; }
    public string? BridgeUrl { get; private set; }

    /// <summary>Remembered USB ids of the paired device (WebUSB/WebSerial), to find it again on the device.</summary>
    public int? UsbVendorId { get; private set; }

    public int? UsbProductId { get; private set; }
    public int? BaudRate { get; private set; }
    public bool IsDefault { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static Result<PrinterProfile> Create(Guid organizationId, Guid registerId, PrinterProfileSettings settings, bool isDefault, DateTimeOffset now)
    {
        var profile = new PrinterProfile
        {
            Id = Guid.CreateVersion7(), OrganizationId = organizationId, RegisterId = registerId, IsDefault = isDefault, CreatedAt = now,
        };
        var applied = profile.Update(settings, now);
        return applied.IsSuccess ? profile : applied.Error!;
    }

    /// <summary>Validates and applies the settings; fields that do not belong to the connection type are cleared.</summary>
    public Result Update(PrinterProfileSettings s, DateTimeOffset now)
    {
        var name = s.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            return Error.Validation("pos.printer_name", $"A printer name (max {MaxNameLength} characters) is required.");
        if (!Enum.IsDefined(s.Connection))
            return Error.Validation("pos.printer_connection", "Unknown connection type.");
        if (!PaperWidths.Contains(s.PaperWidthMm))
            return Error.Validation("pos.printer_paper_width", "The paper width must be 58 or 80 mm.");
        if (s.CharsPerLine is < MinCharsPerLine or > MaxCharsPerLine)
            return Error.Validation("pos.printer_chars_per_line", $"Characters per line must be between {MinCharsPerLine} and {MaxCharsPerLine}.");
        if (s.Copies is < 1 or > MaxCopies)
            return Error.Validation("pos.printer_copies", $"Copies must be between 1 and {MaxCopies}.");
        if (!Enum.IsDefined(s.Codepage))
            return Error.Validation("pos.printer_codepage", "Unknown code page.");

        var network = s.Connection is PrinterConnection.EpsonEpos or PrinterConnection.StarWebPrnt or PrinterConnection.EscPosBridge;
        var usb = s.Connection is PrinterConnection.WebUsb or PrinterConnection.WebSerial;
        string? host = null;
        int? port = null;
        if (network)
        {
            host = s.Host?.Trim();
            if (string.IsNullOrEmpty(host) || !IsValidHost(host))
                return Error.Validation("pos.printer_host", "Enter the printer's IP address or host name (without http:// and path).");
            if (s.Port is { } p && p is < 1 or > 65535)
                return Error.Validation("pos.printer_port", "The port must be between 1 and 65535.");
            port = s.Port ?? (s.Connection == PrinterConnection.EscPosBridge ? 9100 : null);
        }
        string? bridgeUrl = null;
        if (s.Connection == PrinterConnection.EscPosBridge)
        {
            bridgeUrl = s.BridgeUrl?.Trim();
            if (string.IsNullOrEmpty(bridgeUrl) || !IsValidBridgeUrl(bridgeUrl))
                return Error.Validation("pos.printer_bridge_url", "The bridge URL must be an http(s) address such as http://localhost:9123/print.");
        }
        string? deviceId = null;
        if (s.Connection == PrinterConnection.EpsonEpos)
        {
            deviceId = string.IsNullOrWhiteSpace(s.EposDeviceId) ? DefaultEposDeviceId : s.EposDeviceId.Trim();
            if (deviceId.Length > MaxDeviceIdLength || !DeviceIdPattern().IsMatch(deviceId))
                return Error.Validation("pos.printer_device_id", "The ePOS device id may contain letters, digits and _ - . only.");
        }
        if (usb && (s.UsbVendorId is < 0 or > 0xFFFF || s.UsbProductId is < 0 or > 0xFFFF))
            return Error.Validation("pos.printer_usb_id", "USB vendor and product ids must be between 0 and 65535.");
        int? baud = null;
        if (s.Connection == PrinterConnection.WebSerial)
        {
            baud = s.BaudRate ?? 9600;
            if (!BaudRates.Contains(baud.Value))
                return Error.Validation("pos.printer_baud_rate", "Unsupported baud rate.");
        }

        Name = name;
        Connection = s.Connection;
        PaperWidthMm = s.PaperWidthMm;
        CharsPerLine = s.CharsPerLine;
        AutoPrint = s.AutoPrint;
        OpenDrawerOnCash = s.OpenDrawerOnCash;
        Copies = s.Copies;
        CutPaper = s.CutPaper;
        PrintQr = s.PrintQr;
        Codepage = s.Codepage;
        Host = host;
        Port = port;
        UseHttps = (s.Connection is PrinterConnection.EpsonEpos or PrinterConnection.StarWebPrnt) && s.UseHttps;
        EposDeviceId = deviceId;
        BridgeUrl = bridgeUrl;
        UsbVendorId = usb ? s.UsbVendorId : null;
        UsbProductId = usb ? s.UsbProductId : null;
        BaudRate = baud;
        UpdatedAt = now;
        return Result.Success();
    }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;

    /// <summary>IPv4, bracketed or bare IPv6, or a DNS host name; never a URL (no scheme, path, credentials or port).</summary>
    public static bool IsValidHost(string host) =>
        host.Length <= MaxHostLength && (HostNamePattern().IsMatch(host) || (Ipv6Pattern().IsMatch(host) && host.Count(c => c == ':') >= 2));

    public static bool IsValidBridgeUrl(string url) =>
        url.Length <= MaxBridgeUrlLength
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment);

    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,62}))*$")]
    private static partial Regex HostNamePattern();

    [GeneratedRegex(@"^\[?[0-9A-Fa-f:.]{2,45}\]?$")]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex DeviceIdPattern();
}
