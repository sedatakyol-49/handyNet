using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Infrastructure;

public sealed record FiskalyCredentials(string ApiKey, string ApiSecret);

/// <summary>A bearer token of <c>POST /auth</c>; Environment is <c>access_token_claims.env</c> (TEST or LIVE).</summary>
public sealed record FiskalyToken(string AccessToken, DateTimeOffset ExpiresAt, string? Environment);

/// <summary>Access tokens per API key, and clients known to be REGISTERED per TSS (process-wide).</summary>
public sealed class FiskalyTokenCache
{
    internal ConcurrentDictionary<string, FiskalyToken> Tokens { get; } = new();
    internal ConcurrentDictionary<string, bool> Clients { get; } = new();
}

/// <summary>Error codes of the fiskaly adapter beyond the generic <see cref="FiscalErrors"/>.</summary>
public static class FiskalyErrors
{
    public const string Conflict = "pos.fiskaly_conflict";
    public const string NotFound = "pos.fiskaly_not_found";
    public const string CredentialsRejected = "pos.fiskaly_credentials_rejected";
    public const string ClientNotRegistered = "pos.fiskaly_client_not_registered";
    public const string InvalidClientId = "pos.fiskaly_invalid_client_id";
    public const string AdminPinRejected = "pos.fiskaly_admin_pin_rejected";
}

/// <summary>
/// HTTP client for fiskaly SIGN DE v2, shaped after the published OpenAPI spec (<c>sign-de-v2.json</c>): <c>POST /auth</c>,
/// TSS lifecycle (<c>PUT/GET/PATCH /tss/{tss}</c>, <c>PATCH /tss/{tss}/admin</c>, <c>POST /tss/{tss}/admin/auth|logout</c>),
/// clients (<c>PUT/GET /tss/{tss}/client/{client}</c>) and transactions (<c>PUT /tss/{tss}/tx/{tx}?tx_revision=n</c>,
/// <c>GET /tss/{tss}/tx/{tx}</c>). Timeouts, 429, 499 and 5xx are retried with exponential backoff (Retry-After honoured)
/// and the same request — the API is idempotent, so a retried transaction revision never signs twice; a 401 refreshes the
/// token once. Error bodies <c>{status_code, error, code, message}</c> are reported with fiskaly's code.
/// <para>Contract checked against the spec with recorded responses; a real TEST run needs credentials (see ADR-0012).</para>
/// </summary>
public sealed class FiskalyApiClient(HttpClient http, FiskalyTokenCache cache, IOptions<PosOptions> options, IClock clock, ILogger<FiskalyApiClient> logger)
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(5);

    private FiskalyOptions Options => options.Value.Fiskaly;

    private TimeSpan AdminTimeout => TimeSpan.FromSeconds(Math.Max(30, Options.AdministrationTimeoutSeconds));

    // ---- authentication ------------------------------------------------------------------------------------------------

    /// <summary>A valid token (cached until 30 s before expiry); <paramref name="fresh"/> forces <c>POST /auth</c>.</summary>
    public async Task<Result<FiskalyToken>> AuthenticateAsync(FiskalyCredentials credentials, bool fresh, CancellationToken ct)
    {
        if (!fresh && cache.Tokens.TryGetValue(credentials.ApiKey, out var cached) && cached.ExpiresAt > clock.UtcNow.AddSeconds(30))
            return cached;
        var body = new JsonObject { ["api_key"] = credentials.ApiKey, ["api_secret"] = credentials.ApiSecret };
        var reply = await ExchangeAsync(null, HttpMethod.Post, "auth", body, null, ct);
        if (!reply.IsSuccess)
            return reply.Error!;
        var (status, json, _) = reply.Value;
        if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden)
            return Error.Conflict(FiskalyErrors.CredentialsRejected,
                $"fiskaly rejected the API key or secret ({Detail(status, json)}). Keys are bound to TEST or LIVE.");
        if (!IsSuccess(status))
            return Failure(status, json, "POST /auth");
        var token = Text(json["access_token"]);
        if (string.IsNullOrEmpty(token))
            return FiscalErrors.Refused("fiskaly returned no access token.");
        var expiresAt = Long(json["access_token_expires_in"]) is { } seconds
            ? clock.UtcNow.AddSeconds(seconds)
            : Long(json["access_token_expires_at"]) is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : clock.UtcNow.AddMinutes(5);
        var result = new FiskalyToken(token, expiresAt, Text((json["access_token_claims"] as JsonObject)?["env"]));
        cache.Tokens[credentials.ApiKey] = result;
        return result;
    }

    // ---- TSS -----------------------------------------------------------------------------------------------------------

    public Task<Result<JsonObject>> GetTssAsync(FiskalyCredentials credentials, Guid tssId, CancellationToken ct) =>
        SendAsync(credentials, HttpMethod.Get, $"tss/{tssId}", null, ct);

    /// <summary><c>createTss</c>: state CREATED; the response carries the admin PUK (only visible while CREATED).</summary>
    public Task<Result<JsonObject>> CreateTssAsync(FiskalyCredentials credentials, Guid tssId, CancellationToken ct) =>
        SendAsync(credentials, HttpMethod.Put, $"tss/{tssId}", new JsonObject(), ct, AdminTimeout);

    /// <summary><c>updateTss</c>: CREATED → UNINITIALIZED (deploy, slow) → INITIALIZED (admin auth) → DISABLED (admin auth).</summary>
    public Task<Result<JsonObject>> UpdateTssStateAsync(FiskalyCredentials credentials, Guid tssId, string state, CancellationToken ct) =>
        SendAsync(credentials, HttpMethod.Patch, $"tss/{tssId}", new JsonObject { ["state"] = state }, ct, AdminTimeout);

    /// <summary><c>changeAdminPin</c>: sets the admin PIN with the PUK (required after creating a TSS).</summary>
    public async Task<Result> ChangeAdminPinAsync(FiskalyCredentials credentials, Guid tssId, string adminPuk, string newAdminPin, CancellationToken ct) =>
        Plain(await SendAsync(credentials, HttpMethod.Patch, $"tss/{tssId}/admin",
            new JsonObject { ["admin_puk"] = adminPuk, ["new_admin_pin"] = newAdminPin }, ct, AdminTimeout));

    /// <summary>
    /// <c>authenticateAdmin</c>: admin session for the current token (createClient, INITIALIZED, DISABLED). A 401 here means a
    /// wrong PIN, so it is never retried (five failures block the PIN).
    /// </summary>
    public async Task<Result> AuthenticateAdminAsync(FiskalyCredentials credentials, Guid tssId, string adminPin, CancellationToken ct)
    {
        var result = await SendAsync(credentials, HttpMethod.Post, $"tss/{tssId}/admin/auth", new JsonObject { ["admin_pin"] = adminPin }, ct,
            refreshOn401: false);
        if (!result.IsSuccess && result.Error!.Code == FiskalyErrors.CredentialsRejected)
            return Error.Conflict(FiskalyErrors.AdminPinRejected, "fiskaly rejected the admin PIN of the TSS.");
        return Plain(result);
    }

    public async Task<Result> LogoutAdminAsync(FiskalyCredentials credentials, Guid tssId, CancellationToken ct) =>
        Plain(await SendAsync(credentials, HttpMethod.Post, $"tss/{tssId}/admin/logout", new JsonObject(), ct));

    /// <summary>
    /// Creates, deploys and initializes a TSS: createTss → UNINITIALIZED → admin PIN from the PUK → admin auth → INITIALIZED →
    /// logout. Returns the TSS as retrieved at the end. Used by the opt-in sandbox test; the PUK is never returned or logged.
    /// </summary>
    public async Task<Result<JsonObject>> ProvisionTssAsync(FiskalyCredentials credentials, Guid tssId, string adminPin, CancellationToken ct)
    {
        var created = await CreateTssAsync(credentials, tssId, ct);
        if (!created.IsSuccess)
            return created.Error!;
        var puk = Text(created.Value["admin_puk"]);
        if (string.IsNullOrEmpty(puk))
            return FiscalErrors.Refused("fiskaly returned no admin PUK for the new TSS.");
        var deployed = await UpdateTssStateAsync(credentials, tssId, "UNINITIALIZED", ct);
        if (!deployed.IsSuccess)
            return deployed.Error!;
        var pin = await ChangeAdminPinAsync(credentials, tssId, puk, adminPin, ct);
        if (!pin.IsSuccess)
            return pin.Error!;
        var admin = await AuthenticateAdminAsync(credentials, tssId, adminPin, ct);
        if (!admin.IsSuccess)
            return admin.Error!;
        try
        {
            var initialized = await UpdateTssStateAsync(credentials, tssId, "INITIALIZED", ct);
            if (!initialized.IsSuccess)
                return initialized.Error!;
        }
        finally
        {
            await LogoutAdminAsync(credentials, tssId, CancellationToken.None);
        }
        return await GetTssAsync(credentials, tssId, ct);
    }

    // ---- clients -------------------------------------------------------------------------------------------------------

    /// <summary><c>retrieveClient</c>; null when the client does not exist at this TSS (404).</summary>
    public async Task<Result<JsonObject?>> GetClientAsync(FiskalyCredentials credentials, Guid tssId, Guid clientId, CancellationToken ct)
    {
        var client = await SendAsync(credentials, HttpMethod.Get, $"tss/{tssId}/client/{clientId}", null, ct);
        if (!client.IsSuccess && client.Error!.Code == FiskalyErrors.NotFound)
            return Result<JsonObject?>.Success(null);
        return client.IsSuccess ? client.Value : client.Error!;
    }

    /// <summary>
    /// Registers a client (<c>createClient</c> requires admin authentication, so it is an administration step with the admin
    /// PIN, never done from the till): admin auth → create (idempotent) → logout. An already REGISTERED client is returned as is.
    /// </summary>
    public async Task<Result<JsonObject>> RegisterClientAsync(FiskalyCredentials credentials, Guid tssId, Guid clientId, string serialNumber,
        string adminPin, CancellationToken ct)
    {
        var existing = await GetClientAsync(credentials, tssId, clientId, ct);
        if (!existing.IsSuccess)
            return existing.Error!;
        if (existing.Value is { } found && Text(found["state"]) == "REGISTERED")
            return found;
        if (existing.Value is not null)
            return FiscalErrors.Refused("The fiskaly client is DEREGISTERED; register the till with a new client id.");
        var admin = await AuthenticateAdminAsync(credentials, tssId, adminPin, ct);
        if (!admin.IsSuccess)
            return admin.Error!;
        try
        {
            var created = await SendAsync(credentials, HttpMethod.Put, $"tss/{tssId}/client/{clientId}",
                new JsonObject { ["serial_number"] = serialNumber }, ct);
            if (created.IsSuccess)
                cache.Clients[$"{tssId}:{clientId}"] = true;
            return created;
        }
        finally
        {
            await LogoutAdminAsync(credentials, tssId, CancellationToken.None);
        }
    }

    /// <summary>Checks once per process that the client is REGISTERED at the TSS (<c>retrieveClient</c>, no admin rights needed).</summary>
    public async Task<Result> EnsureClientRegisteredAsync(FiskalyCredentials credentials, Guid tssId, Guid clientId, CancellationToken ct)
    {
        var key = $"{tssId}:{clientId}";
        if (cache.Clients.ContainsKey(key))
            return Result.Success();
        var client = await GetClientAsync(credentials, tssId, clientId, ct);
        if (!client.IsSuccess)
            return client.Error!;
        var state = client.Value is { } c ? Text(c["state"]) : null;
        if (state != "REGISTERED")
            return Error.Conflict(FiskalyErrors.ClientNotRegistered, state is null
                ? "The till is not registered as a fiskaly client at this TSS. Register it in \"Kassen & TSE\" (admin PIN required)."
                : $"The fiskaly client of this till is {state}.");
        cache.Clients[key] = true;
        return Result.Success();
    }

    // ---- transactions --------------------------------------------------------------------------------------------------

    public Task<Result<JsonObject>> PutTransactionAsync(FiskalyCredentials credentials, Guid tssId, Guid transactionId, int revision, JsonObject body,
        CancellationToken ct) =>
        SendAsync(credentials, HttpMethod.Put, $"tss/{tssId}/tx/{transactionId}?tx_revision={revision}", body, ct);

    public Task<Result<JsonObject>> GetTransactionAsync(FiskalyCredentials credentials, Guid tssId, Guid transactionId, CancellationToken ct) =>
        SendAsync(credentials, HttpMethod.Get, $"tss/{tssId}/tx/{transactionId}", null, ct);

    // ---- transport -----------------------------------------------------------------------------------------------------

    /// <summary>An authenticated call; success = 2xx JSON body, otherwise an <see cref="Error"/> with fiskaly's code in the message.</summary>
    private async Task<Result<JsonObject>> SendAsync(FiskalyCredentials credentials, HttpMethod method, string path, JsonObject? body, CancellationToken ct,
        TimeSpan? timeout = null, bool refreshOn401 = true)
    {
        var refreshed = !refreshOn401;
        while (true)
        {
            var token = await AuthenticateAsync(credentials, fresh: false, ct);
            if (!token.IsSuccess)
                return token.Error!;
            var reply = await ExchangeAsync(token.Value.AccessToken, method, path, body, timeout, ct);
            if (!reply.IsSuccess)
                return reply.Error!;
            var (status, json, _) = reply.Value;
            if (status == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                cache.Tokens.TryRemove(credentials.ApiKey, out _);
                continue;
            }
            return IsSuccess(status) ? json : Failure(status, json, $"{method} {path.Split('?')[0]}");
        }
    }

    /// <summary>One request with retries for transport failures, timeouts, 429, 499 and 5xx; other statuses are returned.</summary>
    private async Task<Result<(HttpStatusCode Status, JsonObject Body, TimeSpan? RetryAfter)>> ExchangeAsync(string? bearer, HttpMethod method, string path,
        JsonObject? body, TimeSpan? timeout, CancellationToken ct)
    {
        var label = $"{method} {path.Split('?')[0]}";
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            string failure;
            using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(Options.TimeoutSeconds));
                using var request = new HttpRequestMessage(method, path);
                if (bearer is not null)
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                try
                {
                    using var response = await http.SendAsync(request, timeoutSource.Token);
                    var status = response.StatusCode;
                    var json = await ReadJsonAsync(response, timeoutSource.Token);
                    retryAfter = response.Headers.RetryAfter?.Delta
                                 ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock.UtcNow : null);
                    if (!IsRetryable(status))
                        return (status, json, retryAfter);
                    failure = $"HTTP {(int)status}{(FiskalyCode(json) is { } code ? " " + code : "")}";
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    failure = ex is OperationCanceledException ? "timeout" : "not reachable";
                }
            }

            logger.LogWarning("fiskaly {Request}: {Failure} (attempt {Attempt})", label, failure, attempt + 1);
            if (attempt >= Options.MaxRetries)
                return FiscalErrors.Transient($"fiskaly {label}: {failure}.");
            await Task.Delay(Backoff(attempt, retryAfter), ct);
        }
    }

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        if (retryAfter is { } wait && wait > TimeSpan.Zero)
            return wait < MaxRetryAfter ? wait : MaxRetryAfter;
        return TimeSpan.FromMilliseconds(Math.Max(0, Options.RetryBaseDelayMs) * (1 << Math.Min(attempt, 6)));
    }

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and <= 299;

    /// <summary>429, 499 (client closed request — retry per the spec) and every 5xx incl. 503 E_TSS_LOCKED.</summary>
    private static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests || (int)status == 499 || (int)status >= 500;

    /// <summary>fiskaly's error code (or the HTTP status when there is none, as for <c>/auth</c>) and its message, shortened.</summary>
    private static string Detail(HttpStatusCode status, JsonObject json)
    {
        var message = Text(json["message"]);
        return (FiskalyCode(json) ?? $"HTTP {(int)status}") + (message is null ? "" : ": " + (message.Length > 200 ? message[..200] : message));
    }

    private static Error Failure(HttpStatusCode status, JsonObject json, string request)
    {
        var detail = Detail(status, json);
        return status switch
        {
            HttpStatusCode.Conflict => Error.Conflict(FiskalyErrors.Conflict, $"fiskaly {request}: conflict ({detail})."),
            HttpStatusCode.NotFound => Error.Conflict(FiskalyErrors.NotFound, $"fiskaly {request}: not found ({detail})."),
            HttpStatusCode.Unauthorized => Error.Conflict(FiskalyErrors.CredentialsRejected, $"fiskaly {request}: unauthorized ({detail})."),
            _ => FiscalErrors.Refused($"fiskaly {request} refused (HTTP {(int)status}, {detail})."),
        };
    }

    private static Result Plain(Result<JsonObject> result) => result.IsSuccess ? Result.Success() : result.Error!;

    internal static string? FiskalyCode(JsonObject json) => Text(json["code"]);

    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Integers may come as JSON numbers or (BigintCounter) as strings.</summary>
    internal static long? Long(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
}

/// <summary>
/// fiskaly SIGN DE v2 cloud TSE adapter: the register is a TSS client (registered beforehand by the admin, since
/// <c>createClient</c> needs admin authentication; serial number = the Kassen-Seriennummer), every receipt one transaction
/// (revision 1 = ACTIVE without schema, revision 2 = FINISHED with the <c>standard_v1.receipt</c> schema; other events use
/// the <c>raw</c> schema with processType <c>SonstigerVorgang</c>). The fiskaly transaction id is derived from the caller's
/// idempotency key (<see cref="FiskalyIds"/>); a conflict on a retried revision is resolved by reading the transaction back.
/// A FINISHED response without complete signature data is treated as transient — the adapter never stores a half signature.
/// </summary>
public sealed class FiskalyTseSigner(FiskalyApiClient api, FiskalyCredentials credentials, Guid tssId, bool sandbox) : IFiscalSigner
{
    public FiscalMode Mode => FiscalMode.FiskalyCloudTse;

    public async Task<Result<FiscalTransaction>> StartTransactionAsync(FiscalTransactionRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.ClientId, out var clientId))
            return Error.Conflict(FiskalyErrors.InvalidClientId, "The fiskaly client id of this till must be a UUID.");
        var client = await api.EnsureClientRegisteredAsync(credentials, tssId, clientId, ct);
        if (!client.IsSuccess)
            return client.Error!;
        var txId = FiskalyIds.Transaction(request.TransactionKey);
        var started = await api.PutTransactionAsync(credentials, tssId, txId, 1,
            new JsonObject { ["state"] = "ACTIVE", ["client_id"] = clientId.ToString() }, ct);
        if (!started.IsSuccess && started.Error!.Code == FiskalyErrors.Conflict)
            started = await api.GetTransactionAsync(credentials, tssId, txId, ct);
        if (!started.IsSuccess)
            return started.Error!;
        var tx = started.Value;
        if (FiskalyApiClient.Long(tx["number"]) is not { } number || Time(tx["time_start"]) is not { } startedAt)
            return FiscalErrors.Transient("fiskaly returned an incomplete transaction start.");
        return new FiscalTransaction(request.TransactionKey, clientId.ToString(), number, startedAt, FiskalyApiClient.Text(tx["tss_serial_number"]));
    }

    public async Task<Result<FiscalData>> FinishTransactionAsync(FiscalTransaction transaction, FiscalProcess process, CancellationToken ct)
    {
        var txId = FiskalyIds.Transaction(transaction.TransactionKey);
        var body = new JsonObject { ["state"] = "FINISHED", ["client_id"] = transaction.ClientId, ["schema"] = Schema(process) };
        var finished = await api.PutTransactionAsync(credentials, tssId, txId, 2, body, ct);
        if (!finished.IsSuccess && finished.Error!.Code == FiskalyErrors.Conflict)
            finished = await api.GetTransactionAsync(credentials, tssId, txId, ct);
        if (!finished.IsSuccess)
            return finished.Error!;
        if (FiskalyApiClient.Text(finished.Value["state"]) != "FINISHED")
            return FiscalErrors.Refused($"fiskaly: the transaction is {FiskalyApiClient.Text(finished.Value["state"]) ?? "in an unknown state"}, not FINISHED.");
        return ToFiscalData(finished.Value, transaction.ClientId, process, sandbox);
    }

    /// <summary>Maps a FINISHED transaction (TxResponse); transient error when a required signature field is missing.</summary>
    internal static Result<FiscalData> ToFiscalData(JsonObject tx, string clientId, FiscalProcess process, bool sandbox)
    {
        var signature = tx["signature"] as JsonObject;
        var serial = FiskalyApiClient.Text(tx["tss_serial_number"]);
        var number = FiskalyApiClient.Long(tx["number"]);
        var counter = FiskalyApiClient.Long(signature?["counter"]);
        var value = FiskalyApiClient.Text(signature?["value"]);
        var algorithm = FiskalyApiClient.Text(signature?["algorithm"]);
        var publicKey = FiskalyApiClient.Text(signature?["public_key"]);
        var start = Time(tx["time_start"]);
        var end = Time(tx["time_end"]);
        if (serial is null || number is null || counter is null || value is null || algorithm is null || publicKey is null || start is null || end is null)
            return FiscalErrors.Transient("fiskaly returned a finished transaction without complete signature data.");
        var format = FiskalyApiClient.Text((tx["log"] as JsonObject)?["timestamp_format"]) ?? KassenSichV.TimeFormatUnix;
        // _env is authoritative: a TEST key signs test receipts even when the configuration says Production.
        var isTest = sandbox || FiskalyApiClient.Text(tx["_env"]) == "TEST";
        var qr = FiskalyApiClient.Text(tx["qr_code_data"])
                 ?? KassenSichV.QrPayload(FiskalyApiClient.Text(tx["client_serial_number"]) ?? clientId, process.ProcessType, process.ProcessData,
                     number.Value, counter.Value, start.Value, end.Value, algorithm, format, value, publicKey);
        return new FiscalData(FiscalStatus.Signed, isTest ? KassenSichV.TestTseNotice : null, serial, number, counter, value, start, end,
            Mode: nameof(FiscalMode.FiskalyCloudTse), IsTest: isTest, ClientId: clientId, ProcessType: process.ProcessType, ProcessData: process.ProcessData,
            SignatureAlgorithm: algorithm, TimeFormat: format, PublicKey: publicKey, QrPayload: qr);
    }

    /// <summary>fiskaly's structured receipt schema for Kassenbeleg-V1; raw process data (Base64) for other events.</summary>
    internal static JsonObject Schema(FiscalProcess process)
    {
        if (process.Receipt is not { } receipt)
            return new JsonObject
            {
                ["raw"] = new JsonObject
                {
                    ["process_type"] = process.ProcessType,
                    ["process_data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(process.ProcessData)),
                },
            };

        string[] rates = ["NORMAL", "REDUCED_1", "SPECIAL_RATE_1", "SPECIAL_RATE_2", "NULL"];
        var buckets = new long[5];
        foreach (var total in receipt.VatTotals)
            buckets[KassenSichV.VatBucket(total.Treatment, total.VatRateBasisPoints)] += total.GrossMinor;
        var vat = new JsonArray();
        for (var i = 0; i < buckets.Length; i++)
            if (buckets[i] != 0)
                vat.Add(new JsonObject { ["vat_rate"] = rates[i], ["amount"] = KassenSichV.Amount(buckets[i]) });
        // One entry per fiskaly payment type: card and "other" both count as NON_CASH and are summed.
        var payments = new JsonArray();
        foreach (var group in receipt.Payments
                     .GroupBy(p => p.Method == PaymentMethod.Cash ? "CASH" : "NON_CASH")
                     .Select(g => (Type: g.Key, Amount: g.Sum(p => p.AmountMinor)))
                     .Where(g => g.Amount != 0)
                     .OrderBy(g => g.Type == "CASH" ? 0 : 1))
            payments.Add(new JsonObject { ["payment_type"] = group.Type, ["amount"] = KassenSichV.Amount(group.Amount) });
        return new JsonObject
        {
            ["standard_v1"] = new JsonObject
            {
                ["receipt"] = new JsonObject
                {
                    ["receipt_type"] = "RECEIPT",
                    ["amounts_per_vat_rate"] = vat,
                    ["amounts_per_payment_type"] = payments,
                },
            },
        };
    }

    private static DateTimeOffset? Time(JsonNode? node) => FiskalyApiClient.Long(node) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
