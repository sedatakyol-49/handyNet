using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// How an organization's (or a register's) receipts are signed (KassenSichV technical security device). Stored as text —
/// never rename members without a data migration.
/// </summary>
public enum FiscalMode
{
    /// <summary>No TSE: receipts are marked "TSE nicht angebunden – Testbetrieb". Never in Production.</summary>
    Disabled,

    /// <summary>
    /// HandyNet's simulated cloud TSE for tests and demos (real ECDSA signatures with a key that is not certified). Every
    /// receipt says "Test-TSE – nicht für den Echtbetrieb". Refused in Production unless explicitly allowed for staging.
    /// </summary>
    SimulatedTse,

    /// <summary>fiskaly SIGN DE v2 cloud TSE (sandbox or production). Adapter not verified against the real service.</summary>
    FiskalyCloudTse,
}

/// <summary>Formats of KassenSichV / DSFinV-K (Anhang I "QR-Code", Kassenbeleg-V1 process data). Pure functions.</summary>
public static class KassenSichV
{
    public const string ProcessTypeReceipt = "Kassenbeleg-V1";
    public const string ProcessTypeOther = "SonstigerVorgang";
    public const string QrVersion = "V0";
    public const string TimeFormatUnix = "unixTime";
    public const string AlgorithmP256 = "ecdsa-plain-SHA256";
    public const string AlgorithmP384 = "ecdsa-plain-SHA384";
    public const string OutageNotice = "Sicherheitseinrichtung ausgefallen";
    public const string TestTseNotice = "Test-TSE – nicht für den Echtbetrieb";
    public const string ReceiptType = "Beleg";

    public static readonly IReadOnlyList<string> Algorithms = [AlgorithmP256, AlgorithmP384];

    /// <summary>
    /// The five VAT buckets of Kassenbeleg-V1, in this order: 19 %, 7 %, 10.7 % (§24(1) Nr. 3), 5.5 % (§24(1) Nr. 1), 0 %.
    /// §25a margin lines are taxed at the standard rate (on the margin) and count into the 19 % bucket with their gross.
    /// </summary>
    public static int VatBucket(TaxTreatment treatment, int rateBasisPoints) => treatment == TaxTreatment.Margin25a
        ? 0
        : rateBasisPoints switch
        {
            1900 => 0,
            700 => 1,
            1070 => 2,
            550 => 3,
            _ => 4,
        };

    /// <summary>Amount in the process data: euros with exactly two decimals, '.' separator, leading '-' for refunds.</summary>
    public static string Amount(long minor) =>
        (minor < 0 ? "-" : "") + (Math.Abs(minor) / 100).ToString(CultureInfo.InvariantCulture) + "." + (Math.Abs(minor) % 100).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>
    /// processData of Kassenbeleg-V1: <c>Beleg^19%_7%_10,7%_5,5%_0%^Betrag:Bar_Betrag:Unbar</c> — gross per VAT bucket, then the
    /// amounts per payment type (cash "Bar" first, then "Unbar"; zero amounts omitted; currency only if not EUR).
    /// </summary>
    public static string ReceiptProcessData(IReadOnlyList<TaxTotal> vat, IReadOnlyList<PaymentTotal> payments, string currency = Money.Eur)
    {
        var buckets = new long[5];
        foreach (var total in vat)
            buckets[VatBucket(total.Treatment, total.VatRateBasisPoints)] += total.GrossMinor;
        var suffix = currency == Money.Eur ? "" : ":" + currency;
        var paid = payments.Where(p => p.AmountMinor != 0)
            .OrderBy(p => p.Method == PaymentMethod.Cash ? 0 : 1)
            .Select(p => Amount(p.AmountMinor) + ":" + (p.Method == PaymentMethod.Cash ? "Bar" : "Unbar") + suffix);
        return ReceiptType + "^" + string.Join("_", buckets.Select(Amount)) + "^" + string.Join("_", paid);
    }

    /// <summary>processData of a <c>SonstigerVorgang</c> (cash movement, closing): HandyNet-defined, <c>Vorgang^Detail^Betrag</c>.</summary>
    public static string OtherProcessData(string operation, string? detail, long? amountMinor) =>
        string.Join("^", new[] { Clean(operation), Clean(detail ?? ""), amountMinor is { } a ? Amount(a) : "" });

    /// <summary>ISO 8601 UTC with milliseconds as required for the QR code (<c>YYYY-MM-DDThh:mm:ss.fffZ</c>).</summary>
    public static string QrTime(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// QR code content per DSFinV-K Anhang I / AEAO zu §146a: <c>V0;kassen-seriennummer;processType;processData;transaktionsnummer;
    /// signaturzaehler;start-zeit;log-time;sig-alg;log-time-format;signatur;public-key</c>.
    /// </summary>
    public static string QrPayload(string clientId, string processType, string processData, long transactionNumber, long signatureCounter,
        DateTimeOffset start, DateTimeOffset end, string algorithm, string timeFormat, string signature, string publicKey) =>
        string.Join(";", QrVersion, clientId, processType, processData, transactionNumber.ToString(CultureInfo.InvariantCulture),
            signatureCounter.ToString(CultureInfo.InvariantCulture), QrTime(start), QrTime(end), algorithm, timeFormat, signature, publicKey);

    /// <summary>Separators of the process data and the QR code must not appear inside a field.</summary>
    private static string Clean(string value) => value.Replace("^", " ").Replace(";", ",").Replace("_", "-").Trim();
}

/// <summary>A log message of the simulated TSE (start or finish of a transaction), as signed. Canonical text, UTF-8.</summary>
public sealed record TseLogMessage(
    string Operation,
    string SerialNumber,
    string ClientId,
    long TransactionNumber,
    long SignatureCounter,
    DateTimeOffset LogTime,
    string ProcessType,
    string ProcessData)
{
    public const string StartOperation = "StartTransaction";
    public const string FinishOperation = "FinishTransaction";

    /// <summary>
    /// Not the BSI TR-03151 ASN.1 log message: a documented, deterministic text form so signatures can be verified with the
    /// public key in tests and exports (<c>HandyNet-SimTSE-V1|op|serial|client|tx|counter|unixTime|processType|processData</c>).
    /// </summary>
    public byte[] ToBytes() => Encoding.UTF8.GetBytes(string.Join("|", "HandyNet-SimTSE-V1", Operation, SerialNumber, ClientId,
        TransactionNumber.ToString(CultureInfo.InvariantCulture), SignatureCounter.ToString(CultureInfo.InvariantCulture),
        LogTime.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ProcessType, ProcessData));
}

/// <summary>ECDSA key handling and signatures of the simulated TSE (P-256 / P-384, plain r‖s signatures, Base64).</summary>
public static class SimulatedTseCrypto
{
    public sealed record KeyPair(byte[] Pkcs8PrivateKey, string PublicKey, string SerialNumber);

    public static bool IsSupported(string algorithm) => KassenSichV.Algorithms.Contains(algorithm);

    public static KeyPair Generate(string algorithm)
    {
        using var key = ECDsa.Create(Curve(algorithm));
        var publicKey = Convert.ToBase64String(PublicPoint(key));
        return new KeyPair(key.ExportPkcs8PrivateKey(), publicKey, SerialFor(publicKey));
    }

    /// <summary>Like a real TSE, the serial number is the SHA-256 hash of the public key (hex).</summary>
    public static string SerialFor(string publicKeyBase64) =>
        Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(publicKeyBase64)));

    public static string Sign(byte[] pkcs8PrivateKey, string algorithm, TseLogMessage message)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        return Convert.ToBase64String(key.SignData(message.ToBytes(), Hash(algorithm), DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public static bool Verify(string publicKeyBase64, string algorithm, TseLogMessage message, string signatureBase64)
    {
        var point = Convert.FromBase64String(publicKeyBase64);
        var size = (point.Length - 1) / 2;
        if (point.Length < 3 || point[0] != 0x04 || point.Length != 1 + 2 * size)
            return false;
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = Curve(algorithm),
            Q = new ECPoint { X = point[1..(1 + size)], Y = point[(1 + size)..] },
        });
        return key.VerifyData(message.ToBytes(), Convert.FromBase64String(signatureBase64), Hash(algorithm),
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Uncompressed EC point 0x04‖X‖Y, as TSEs publish their public key.</summary>
    private static byte[] PublicPoint(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return [0x04, .. p.Q.X!, .. p.Q.Y!];
    }

    private static ECCurve Curve(string algorithm) => algorithm switch
    {
        KassenSichV.AlgorithmP256 => ECCurve.NamedCurves.nistP256,
        KassenSichV.AlgorithmP384 => ECCurve.NamedCurves.nistP384,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Unsupported signature algorithm."),
    };

    private static HashAlgorithmName Hash(string algorithm) => algorithm == KassenSichV.AlgorithmP384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256;
}

/// <summary>The simulated TSE of an organization (one per organization; registers are its clients).</summary>
public sealed class SimulatedTse
{
    private SimulatedTse() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string SerialNumber { get; private set; } = null!;
    public string PublicKey { get; private set; } = null!;
    public string SignatureAlgorithm { get; private set; } = null!;
    public string TimeFormat { get; private set; } = null!;

    /// <summary>Last transaction number used (monotonic, +1 per started transaction; enforced by a trigger).</summary>
    public long TransactionCounter { get; private set; }

    /// <summary>Last signature counter used (monotonic; +1 for every signed log message: start and finish).</summary>
    public long SignatureCounter { get; private set; }

    /// <summary>PKCS#8 private key protected with ASP.NET Data Protection. Never leaves the server.</summary>
    [PersonalData]
    public string ProtectedPrivateKey { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }
}

/// <summary>Stored as text.</summary>
public enum SimulatedTseTransactionState
{
    Active,
    Finished,
}

/// <summary>A transaction of the simulated TSE: started (signed start log), then finished once (signed process data).</summary>
public sealed class SimulatedTseTransaction
{
    private SimulatedTseTransaction() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid TseId { get; private set; }

    /// <summary>Caller-chosen idempotency key (the receipt / movement id): a retried start or finish never signs twice.</summary>
    public Guid TransactionKey { get; private set; }
    public string ClientId { get; private set; } = null!;
    public long Number { get; private set; }
    public SimulatedTseTransactionState State { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public long StartSignatureCounter { get; private set; }
    public string StartSignature { get; private set; } = null!;
    public DateTimeOffset? FinishedAt { get; private set; }
    public string? ProcessType { get; private set; }
    public string? ProcessData { get; private set; }
    public long? SignatureCounter { get; private set; }
    public string? Signature { get; private set; }
}

/// <summary>
/// Fiscal configuration chosen by the platform administrator: one row per organization (<see cref="RegisterId"/> null,
/// mode required) and optional per-register overrides (null fields inherit the organization's values). Credentials are
/// never stored in plain text: either Data-Protection-encrypted values or a reference to a configuration secret.
/// </summary>
public sealed class FiscalConfiguration : IAuditable
{
    public const int MaxLatencyMs = 30_000;
    public const int MaxClientIdLength = 64;
    public const int MaxReferenceLength = 100;

    private FiscalConfiguration() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid? RegisterId { get; private set; }
    public FiscalMode? Mode { get; private set; }

    /// <summary>Simulated TSE: artificial latency per TSE call.</summary>
    public int? SimulatedLatencyMs { get; private set; }

    /// <summary>Simulated TSE: share of TSE calls that fail transiently (0–100 %).</summary>
    public int? SimulatedFailureRatePercent { get; private set; }

    /// <summary>Simulated TSE: forced outage ("TSE ausgefallen") — every call fails.</summary>
    public bool? SimulatedOutage { get; private set; }

    /// <summary>Signature algorithm of a newly created simulated TSE (organization row only).</summary>
    public string? SimulatedAlgorithm { get; private set; }

    /// <summary>Register row: the TSE client id of this register (fiskaly client UUID / Kassen-Seriennummer); null = register id.</summary>
    public string? ClientId { get; private set; }

    public Guid? FiskalyTssId { get; private set; }

    /// <summary>"Sandbox" or "Production" (informational; fiskaly decides by the API key).</summary>
    public string? FiskalyEnvironment { get; private set; }

    [PersonalData]
    public string? FiskalyApiKeyProtected { get; private set; }

    [PersonalData]
    public string? FiskalyApiSecretProtected { get; private set; }

    /// <summary>Name of a credential pair in configuration (<c>Pos:Fiskaly:Credentials:{name}:ApiKey|ApiSecret</c>).</summary>
    public string? FiskalyCredentialReference { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public uint Version { get; private set; }

    public bool HasFiskalyCredentials => FiskalyCredentialReference is not null || (FiskalyApiKeyProtected is not null && FiskalyApiSecretProtected is not null);

    public static Result<FiscalConfiguration> Create(Guid organizationId, Guid? registerId, FiscalSettings settings, Guid userId, DateTimeOffset now)
    {
        var config = new FiscalConfiguration { Id = Guid.CreateVersion7(), OrganizationId = organizationId, RegisterId = registerId };
        var applied = config.Apply(settings, userId, now);
        return applied.IsSuccess ? config : applied.Error!;
    }

    public Result Apply(FiscalSettings settings, Guid userId, DateTimeOffset now)
    {
        if (RegisterId is null && settings.Mode is null)
            return Error.Validation("pos.fiscal_mode_required", "The organization's fiscal mode is required.");
        if (settings.LatencyMs is < 0 or > MaxLatencyMs)
            return Error.Validation("pos.fiscal_latency", $"The latency must be between 0 and {MaxLatencyMs} ms.");
        if (settings.FailureRatePercent is < 0 or > 100)
            return Error.Validation("pos.fiscal_failure_rate", "The failure rate must be between 0 and 100 %.");
        if (settings.Algorithm is { } algorithm && !SimulatedTseCrypto.IsSupported(algorithm))
            return Error.Validation("pos.fiscal_algorithm", $"Supported algorithms: {string.Join(", ", KassenSichV.Algorithms)}.");
        if (settings.ClientId is { } client && (client.Trim().Length is 0 or > MaxClientIdLength || client.Any(c => c is ';' or '^' || char.IsControl(c))))
            return Error.Validation("pos.fiscal_client_id", $"The client id must have 1–{MaxClientIdLength} characters without ';' or '^'.");
        if (settings.ClientId is not null && RegisterId is null)
            return Error.Validation("pos.fiscal_client_id", "A client id belongs to a register.");
        if (settings.FiskalyEnvironment is { } env && env is not ("Sandbox" or "Production"))
            return Error.Validation("pos.fiscal_environment", "The fiskaly environment is 'Sandbox' or 'Production'.");
        if (settings.FiskalyCredentialReference is { } reference
            && (reference.Length is 0 or > MaxReferenceLength || !reference.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            return Error.Validation("pos.fiscal_credential_reference", "The credential reference may contain letters, digits, '-', '_' and '.'.");

        Mode = settings.Mode;
        SimulatedLatencyMs = settings.LatencyMs;
        SimulatedFailureRatePercent = settings.FailureRatePercent;
        SimulatedOutage = settings.ForcedOutage;
        SimulatedAlgorithm = settings.Algorithm;
        ClientId = settings.ClientId?.Trim();
        FiskalyTssId = settings.FiskalyTssId;
        FiskalyEnvironment = settings.FiskalyEnvironment;
        FiskalyCredentialReference = settings.FiskalyCredentialReference;
        UpdatedAt = now;
        UpdatedBy = userId;
        return Result.Success();
    }

    /// <summary>Stores new fiskaly credentials (already encrypted), or clears them with nulls.</summary>
    public void SetProtectedCredentials(string? protectedApiKey, string? protectedApiSecret)
    {
        FiskalyApiKeyProtected = protectedApiKey;
        FiskalyApiSecretProtected = protectedApiSecret;
    }
}

/// <summary>What the administrator sets; null = not set (register rows: inherit from the organization).</summary>
public sealed record FiscalSettings(
    FiscalMode? Mode,
    int? LatencyMs = null,
    int? FailureRatePercent = null,
    bool? ForcedOutage = null,
    string? Algorithm = null,
    string? ClientId = null,
    Guid? FiskalyTssId = null,
    string? FiskalyEnvironment = null,
    string? FiskalyCredentialReference = null);

/// <summary>
/// A TSE outage of one register ("Ausfall der Sicherheitseinrichtung"): opened by the first receipt that could not be
/// signed, closed by the next successful signature. Only <see cref="EndedAt"/> can be set later, once (trigger).
/// </summary>
public sealed class FiscalOutage
{
    public const int MaxReasonLength = 500;

    private FiscalOutage() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public Guid? SessionId { get; private set; }
    public FiscalMode Mode { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public string Reason { get; private set; } = null!;

    public static FiscalOutage Start(Guid organizationId, Guid registerId, Guid? sessionId, FiscalMode mode, string reason, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        RegisterId = registerId,
        SessionId = sessionId,
        Mode = mode,
        StartedAt = now,
        Reason = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason,
    };

    public Result End(DateTimeOffset now)
    {
        if (EndedAt is not null)
            return Error.Conflict("pos.outage_ended", "The outage has already ended.");
        EndedAt = now < StartedAt ? StartedAt : now;
        return Result.Success();
    }
}

/// <summary>Stored as text.</summary>
public enum FiscalRecordKind
{
    CashMovement,
    SessionClosing,
}

/// <summary>
/// Signature of a non-sale event (cash movement, daily closing; processType <c>SonstigerVorgang</c>). Append-only like
/// receipts (trigger); one per event.
/// </summary>
public sealed class FiscalRecord
{
    private FiscalRecord() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid RegisterId { get; private set; }
    public Guid SessionId { get; private set; }
    public FiscalRecordKind Kind { get; private set; }
    public Guid ReferenceId { get; private set; }
    public FiscalStatus Status { get; private set; }
    public long? TransactionNumber { get; private set; }
    public long? SignatureCounter { get; private set; }
    public FiscalData Data { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    public static FiscalRecord For(Guid organizationId, Guid registerId, Guid sessionId, FiscalRecordKind kind, Guid referenceId, FiscalData data,
        DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        RegisterId = registerId,
        SessionId = sessionId,
        Kind = kind,
        ReferenceId = referenceId,
        Status = data.Status,
        TransactionNumber = data.TransactionNumber,
        SignatureCounter = data.SignatureCounter,
        Data = data,
        CreatedAt = now,
    };
}
