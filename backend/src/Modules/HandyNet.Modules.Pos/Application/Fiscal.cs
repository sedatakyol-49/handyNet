using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Application;

public sealed class PosOptions
{
    public const string Section = "Pos";

    /// <summary>
    /// Default fiscal mode for organizations without their own fiscal configuration (set by the platform admin in
    /// <c>pos.fiscal_configurations</c>). Not set and no configuration = POS sales are refused.
    /// </summary>
    public FiscalMode? FiscalSigner { get; set; }

    public FiscalOptions Fiscal { get; set; } = new();

    public FiskalyOptions Fiskaly { get; set; } = new();

    /// <summary>Longest lifetime of a cashier sign-in by PIN on a shared device (hours, 1–24; default 12).</summary>
    public int CashierSessionHours { get; set; } = 12;

    /// <summary>Validity of a manager approval given by PIN (seconds; default 120). It is used once.</summary>
    public int ApprovalSeconds { get; set; } = 120;

    /// <summary>Validity of a public (QR) receipt link in days (default 90).</summary>
    public int PublicReceiptLinkDays { get; set; } = 90;

    /// <summary>Oldest offline sale (hours between its recording on the device and its booking) the API accepts (default 72).</summary>
    public int OfflineMaxAgeHours { get; set; } = 72;

    public TerminalOptions Terminal { get; set; } = new();

    /// <summary>Validity of a remote customer display link in days (default 30).</summary>
    public int DisplayLinkDays { get; set; } = 30;

    /// <summary>How long the public display endpoint holds a long-poll request open when nothing changed (seconds, default 20).</summary>
    public int DisplayPollSeconds { get; set; } = 20;
}

public sealed class TerminalOptions
{
    /// <summary>Staging only: allows the simulated card terminal in the Production environment. Never set for real operation.</summary>
    public bool AllowSimulatedInProduction { get; set; }
}

public sealed class FiscalOptions
{
    /// <summary>Staging only: allows the simulated test TSE in the Production environment. Never set for real operation.</summary>
    public bool AllowSimulatedTseInProduction { get; set; }

    /// <summary>Attempts per TSE call (start, finish) before the sale continues as a TSE outage.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Pause between attempts (multiplied by the attempt number).</summary>
    public int RetryDelayMs { get; set; } = 200;
}

/// <summary>fiskaly SIGN DE v2. Credentials never in appsettings: user-secrets, environment or the secret store.</summary>
public sealed class FiskalyOptions
{
    /// <summary>
    /// The fiskaly SIGN middleware. Per the SIGN DE v2 spec it serves ALL operations, while createClient, updateTss and
    /// upsertTransaction are "only available via Middleware". TEST or LIVE is decided by the API key, not by the URL.
    /// </summary>
    public const string DefaultBaseUrl = "https://kassensichv-middleware.fiskaly.com/api/v2";

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>Timeout of one HTTP request (seconds).</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Timeout of slow TSS administration calls (deploying a TSS: the spec asks for at least 30 s).</summary>
    public int AdministrationTimeoutSeconds { get; set; } = 60;

    /// <summary>HTTP retries for timeouts, 429, 499 and 5xx (same transaction revision, so retries are idempotent).</summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base of the exponential backoff between HTTP retries (ms); a Retry-After header wins (capped at 5 s).</summary>
    public int RetryBaseDelayMs { get; set; } = 250;

    /// <summary>Named credential pairs referenced by a fiscal configuration (<c>Pos:Fiskaly:Credentials:{name}:ApiKey|ApiSecret</c>).</summary>
    public Dictionary<string, FiskalyCredentialOptions> Credentials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// fiskaly ids. The SIGN DE v2 spec asks for UUIDv4 TSS, client and transaction ids (and warns that a future major version
/// will enforce it), while HandyNet ids are UUIDv7. Any other UUID is mapped to a deterministic id in UUIDv4 format (SHA-256
/// of key + purpose, version/variant bits set), so a retry with the same key always addresses the same fiskaly resource.
/// </summary>
public static class FiskalyIds
{
    public static Guid Transaction(Guid key) => V4(key, "tx");

    public static Guid Client(Guid registerId) => V4(registerId, "client");

    public static bool IsV4(Guid id) => id.Version == 4 && (id.Variant & 0b1100) == 0b1000;

    internal static Guid V4(Guid source, string purpose)
    {
        if (IsV4(source))
            return source;
        Span<byte> input = stackalloc byte[16 + purpose.Length];
        source.TryWriteBytes(input[..16], bigEndian: true, out _);
        System.Text.Encoding.ASCII.GetBytes(purpose, input[16..]);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x40);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}

public sealed class FiskalyCredentialOptions
{
    public string? ApiKey { get; set; }
    public string? ApiSecret { get; set; }
}

/// <summary>
/// Start of a TSE transaction. TransactionKey is chosen by the caller (receipt / event id) and makes start and finish
/// idempotent: a retry with the same key never creates a second transaction or signature.
/// </summary>
public sealed record FiscalTransactionRequest(Guid OrganizationId, Guid RegisterId, Guid TransactionKey, string ClientId, string ProcessType);

/// <summary>An open TSE transaction; null fields when the signer has no TSE.</summary>
public sealed record FiscalTransaction(
    Guid TransactionKey,
    string ClientId,
    long? Number = null,
    DateTimeOffset? StartedAt = null,
    string? TseSerialNumber = null,
    int Revision = 1);

/// <summary>What a TSE signs at the end of a receipt transaction (totals per VAT treatment and payment).</summary>
public sealed record FiscalReceiptData(ReceiptKind Kind, long ReceiptNumber, long TotalGrossMinor, IReadOnlyList<TaxTotal> VatTotals,
    IReadOnlyList<PaymentTotal> Payments);

/// <summary>Process type and data to sign; Receipt is set for Kassenbeleg-V1 (adapters with structured schemas use it).</summary>
public sealed record FiscalProcess(string ProcessType, string ProcessData, FiscalReceiptData? Receipt = null);

/// <summary>Errors of a signer. Transient ones are retried (idempotent key); the others go straight to the outage path.</summary>
public static class FiscalErrors
{
    public const string Unavailable = "pos.tse_unavailable";
    public const string Outage = "pos.tse_outage";
    public const string Rejected = "pos.tse_rejected";

    public static Error Transient(string message) => Error.Conflict(Unavailable, message);

    public static Error Down(string message) => Error.Conflict(Outage, message);

    public static Error Refused(string message) => Error.Conflict(Rejected, message);

    public static bool IsTransient(Error error) => error.Code == Unavailable;
}

/// <summary>
/// A TSE adapter (KassenSichV technical security device). Called inside the receipt transaction: start before the
/// receipt is final, finish with the process data; the result is stored on the immutable receipt. An adapter never
/// invents signature data — without a TSE it says so.
/// </summary>
public interface IFiscalSigner
{
    FiscalMode Mode { get; }

    Task<Result<FiscalTransaction>> StartTransactionAsync(FiscalTransactionRequest request, CancellationToken ct);

    Task<Result<FiscalData>> FinishTransactionAsync(FiscalTransaction transaction, FiscalProcess process, CancellationToken ct);
}

/// <summary>Encrypts secrets at rest (fiskaly credentials, simulated TSE keys) with ASP.NET Data Protection.</summary>
public interface IFiscalSecretProtector
{
    string Protect(string plaintext);

    /// <summary>Null when the value cannot be decrypted (e.g. the key ring was lost).</summary>
    string? Unprotect(string protectedValue);
}

/// <summary>State of a simulated TSE as read under its row lock.</summary>
public sealed record SimulatedTseState(
    Guid Id,
    Guid OrganizationId,
    string SerialNumber,
    string PublicKey,
    string Algorithm,
    string TimeFormat,
    long TransactionCounter,
    long SignatureCounter,
    string ProtectedPrivateKey);

public sealed record SimulatedTseTxRow(
    Guid Id,
    Guid TseId,
    Guid TransactionKey,
    string ClientId,
    long Number,
    SimulatedTseTransactionState State,
    DateTimeOffset StartedAt,
    long StartSignatureCounter,
    string StartSignature,
    DateTimeOffset? FinishedAt = null,
    string? ProcessType = null,
    string? ProcessData = null,
    long? SignatureCounter = null,
    string? Signature = null);

/// <summary>
/// Persistence port of the simulated TSE (SQL in the caller's transaction; in-memory in unit tests). Lock serializes all
/// calls on one TSE, so counters are strictly monotonic.
/// </summary>
public interface ISimulatedTseStore
{
    /// <summary>Locks the organization's TSE row, creating the TSE (new key pair) on first use.</summary>
    Task<SimulatedTseState> LockAsync(Guid organizationId, string algorithmForNewTse, CancellationToken ct);

    Task<SimulatedTseTxRow?> FindAsync(Guid tseId, Guid transactionKey, CancellationToken ct);

    /// <summary>Writes a started transaction and the TSE's new counters.</summary>
    Task StartAsync(SimulatedTseState tse, SimulatedTseTxRow transaction, CancellationToken ct);

    /// <summary>Writes the finished transaction and the TSE's new signature counter.</summary>
    Task FinishAsync(SimulatedTseState tse, SimulatedTseTxRow transaction, CancellationToken ct);
}

/// <summary>Test-only behaviour of the simulated TSE, configured by the platform admin.</summary>
public sealed record SimulatedTseBehaviour(int LatencyMs, int FailureRatePercent, bool ForcedOutage, string Algorithm)
{
    public static readonly SimulatedTseBehaviour Normal = new(0, 0, false, KassenSichV.AlgorithmP256);
}

/// <summary>Random source of the simulated failures (replaceable in tests).</summary>
public interface ISimulatedTseFaults
{
    /// <summary>True when this call should fail (probability = failure rate).</summary>
    bool ShouldFail(int failureRatePercent);

    /// <summary>For a failing call: true = the TSE processed the call but the response was lost; false = it never arrived.</summary>
    bool FailAfterProcessing();
}

public sealed class RandomSimulatedTseFaults : ISimulatedTseFaults
{
    public bool ShouldFail(int failureRatePercent) => failureRatePercent > 0 && Random.Shared.Next(100) < failureRatePercent;

    public bool FailAfterProcessing() => Random.Shared.Next(2) == 0;
}
