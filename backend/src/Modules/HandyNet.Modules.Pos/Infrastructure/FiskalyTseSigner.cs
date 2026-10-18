using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.SharedKernel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Infrastructure;

public sealed record FiskalyCredentials(string ApiKey, string ApiSecret);

/// <summary>Access tokens per API key, and clients already ensured per TSS (process-wide).</summary>
public sealed class FiskalyTokenCache
{
    internal ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> Tokens { get; } = new();
    internal ConcurrentDictionary<string, bool> Clients { get; } = new();
}

/// <summary>
/// HTTP client for fiskaly SIGN DE v2 (<c>POST /auth</c>, <c>GET/PUT /tss/{tss}/client/{client}</c>,
/// <c>PUT /tss/{tss}/tx/{tx}?tx_revision=n</c>, <c>GET /tss/{tss}/tx/{tx}</c>). Timeouts, 429 and 5xx are retried with the
/// same transaction revision (an idempotent PUT); a 401 refreshes the token once.
/// <para><b>Not verified against the real fiskaly service</b> — no credentials exist yet; only tested with a fake HTTP handler.
/// Field names follow fiskaly's public v2 documentation and must be checked in the sandbox before go-live.</para>
/// </summary>
public sealed class FiskalyApiClient(HttpClient http, FiskalyTokenCache cache, IOptions<PosOptions> options, IClock clock, ILogger<FiskalyApiClient> logger)
{
    private FiskalyOptions Options => options.Value.Fiskaly;

    public Task<Result<JsonObject>> PutTransactionAsync(FiskalyCredentials credentials, Guid tssId, Guid transactionId, int revision, JsonObject body,
        CancellationToken ct) =>
        SendAsync(credentials, HttpMethod.Put, $"tss/{tssId}/tx/{transactionId}?tx_revision={revision}", body, ct);

    public Task<Result<JsonObject>> GetTransactionAsync(FiskalyCredentials credentials, Guid tssId, Guid transactionId, CancellationToken ct) =>
        SendAsync(credentials, HttpMethod.Get, $"tss/{tssId}/tx/{transactionId}", null, ct);

    /// <summary>Creates the client (register) at the TSS once per process; an existing client (409) is fine.</summary>
    public async Task<Result> EnsureClientAsync(FiskalyCredentials credentials, Guid tssId, string clientId, string serialNumber, CancellationToken ct)
    {
        var key = $"{tssId}:{clientId}";
        if (cache.Clients.ContainsKey(key))
            return Result.Success();
        var created = await SendAsync(credentials, HttpMethod.Put, $"tss/{tssId}/client/{clientId}", new JsonObject { ["serial_number"] = serialNumber }, ct,
            acceptConflict: true);
        if (!created.IsSuccess)
            return created.Error!;
        cache.Clients[key] = true;
        return Result.Success();
    }

    private async Task<Result<JsonObject>> SendAsync(FiskalyCredentials credentials, HttpMethod method, string path, JsonObject? body, CancellationToken ct,
        bool acceptConflict = false)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            var token = await TokenAsync(credentials, ct);
            if (!token.IsSuccess)
                return token.Error!;
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Value);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning(ex, "fiskaly {Method} {Path} failed (attempt {Attempt})", method, path, attempt + 1);
                if (attempt < Options.MaxRetries)
                    continue;
                return FiscalErrors.Transient("fiskaly is not reachable.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                {
                    refreshed = true;
                    cache.Tokens.TryRemove(credentials.ApiKey, out _);
                    attempt--;
                    continue;
                }
                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    logger.LogWarning("fiskaly {Method} {Path}: HTTP {Status} (attempt {Attempt})", method, path, (int)response.StatusCode, attempt + 1);
                    if (attempt < Options.MaxRetries)
                        continue;
                    return FiscalErrors.Transient($"fiskaly answered HTTP {(int)response.StatusCode}.");
                }
                if (response.StatusCode == HttpStatusCode.Conflict && acceptConflict)
                    return new JsonObject();
                if (response.StatusCode == HttpStatusCode.Conflict)
                    return Error.Conflict("pos.fiskaly_conflict", "fiskaly reported a conflict (e.g. the transaction revision was already used).");
                if (!response.IsSuccessStatusCode)
                    return FiscalErrors.Refused($"fiskaly refused the request (HTTP {(int)response.StatusCode}).");
                var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                return json ?? new JsonObject();
            }
        }
    }

    private async Task<Result<string>> TokenAsync(FiskalyCredentials credentials, CancellationToken ct)
    {
        if (cache.Tokens.TryGetValue(credentials.ApiKey, out var cached) && cached.ExpiresAt > clock.UtcNow.AddSeconds(30))
            return cached.Token;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await http.PostAsJsonAsync("auth", new { api_key = credentials.ApiKey, api_secret = credentials.ApiSecret }, ct);
                if ((int)response.StatusCode >= 500 && attempt < Options.MaxRetries)
                    continue;
                if (!response.IsSuccessStatusCode)
                    return (int)response.StatusCode >= 500
                        ? FiscalErrors.Transient($"fiskaly authentication failed (HTTP {(int)response.StatusCode}).")
                        : FiscalErrors.Refused($"fiskaly refused the credentials (HTTP {(int)response.StatusCode}).");
                var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                var token = json?["access_token"]?.GetValue<string>();
                if (string.IsNullOrEmpty(token))
                    return FiscalErrors.Refused("fiskaly returned no access token.");
                var lifetime = json?["access_token_expires_in"]?.GetValue<long>() ?? 300;
                cache.Tokens[credentials.ApiKey] = (token, clock.UtcNow.AddSeconds(lifetime));
                return token;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt < Options.MaxRetries)
                    continue;
                logger.LogWarning(ex, "fiskaly authentication failed");
                return FiscalErrors.Transient("fiskaly is not reachable.");
            }
        }
    }
}

/// <summary>
/// fiskaly SIGN DE v2 cloud TSE adapter: the register is a TSS client (its client id is a UUID; serial number = the
/// Kassen-Seriennummer), every receipt one transaction (revision 1 = ACTIVE, revision 2 = FINISHED with the
/// <c>standard_v1.receipt</c> schema; other events use the <c>raw</c> schema with processType <c>SonstigerVorgang</c>). The
/// transaction id is the caller's idempotency key; a retried revision that fiskaly reports as a conflict is resolved by
/// reading the transaction back. <b>Not verified against the real service.</b>
/// </summary>
public sealed class FiskalyTseSigner(FiskalyApiClient api, FiskalyCredentials credentials, Guid tssId, bool sandbox) : IFiscalSigner
{
    public FiscalMode Mode => FiscalMode.FiskalyCloudTse;

    public async Task<Result<FiscalTransaction>> StartTransactionAsync(FiscalTransactionRequest request, CancellationToken ct)
    {
        var client = await api.EnsureClientAsync(credentials, tssId, request.ClientId, request.RegisterId.ToString("N"), ct);
        if (!client.IsSuccess)
            return client.Error!;
        var started = await api.PutTransactionAsync(credentials, tssId, request.TransactionKey, 1,
            new JsonObject { ["state"] = "ACTIVE", ["client_id"] = request.ClientId }, ct);
        if (!started.IsSuccess && started.Error!.Code == "pos.fiskaly_conflict")
            started = await api.GetTransactionAsync(credentials, tssId, request.TransactionKey, ct);
        if (!started.IsSuccess)
            return started.Error!;
        var tx = started.Value;
        return new FiscalTransaction(request.TransactionKey, request.ClientId, Long(tx["number"]), Time(tx["time_start"]),
            tx["tss_serial_number"]?.GetValue<string>());
    }

    public async Task<Result<FiscalData>> FinishTransactionAsync(FiscalTransaction transaction, FiscalProcess process, CancellationToken ct)
    {
        var body = new JsonObject { ["state"] = "FINISHED", ["client_id"] = transaction.ClientId, ["schema"] = Schema(process) };
        var finished = await api.PutTransactionAsync(credentials, tssId, transaction.TransactionKey, 2, body, ct);
        if (!finished.IsSuccess && finished.Error!.Code == "pos.fiskaly_conflict")
        {
            finished = await api.GetTransactionAsync(credentials, tssId, transaction.TransactionKey, ct);
            if (finished.IsSuccess && finished.Value["state"]?.GetValue<string>() != "FINISHED")
                return FiscalErrors.Refused("fiskaly: the transaction is not finished.");
        }
        if (!finished.IsSuccess)
            return finished.Error!;
        return ToFiscalData(finished.Value, transaction.ClientId, process, sandbox);
    }

    internal static FiscalData ToFiscalData(JsonObject tx, string clientId, FiscalProcess process, bool sandbox)
    {
        var signature = tx["signature"] as JsonObject;
        var serial = tx["tss_serial_number"]?.GetValue<string>();
        var number = Long(tx["number"]);
        var counter = Long(signature?["counter"]);
        var value = signature?["value"]?.GetValue<string>();
        var algorithm = signature?["algorithm"]?.GetValue<string>();
        var publicKey = signature?["public_key"]?.GetValue<string>();
        var start = Time(tx["time_start"]);
        var end = Time(tx["time_end"]);
        var format = (tx["log"] as JsonObject)?["timestamp_format"]?.GetValue<string>() ?? KassenSichV.TimeFormatUnix;
        var qr = tx["qr_code_data"]?.GetValue<string>();
        if (qr is null && number is { } n && counter is { } c && start is { } s && end is { } e && value is not null && algorithm is not null && publicKey is not null)
            qr = KassenSichV.QrPayload(clientId, process.ProcessType, process.ProcessData, n, c, s, e, algorithm, format, value, publicKey);
        return new FiscalData(FiscalStatus.Signed, sandbox ? KassenSichV.TestTseNotice : null, serial, number, counter, value, start, end,
            Mode: nameof(FiscalMode.FiskalyCloudTse), IsTest: sandbox, ClientId: clientId, ProcessType: process.ProcessType, ProcessData: process.ProcessData,
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
        var payments = new JsonArray();
        foreach (var payment in receipt.Payments.Where(p => p.AmountMinor != 0).OrderBy(p => p.Method == PaymentMethod.Cash ? 0 : 1))
            payments.Add(new JsonObject
            {
                ["payment_type"] = payment.Method == PaymentMethod.Cash ? "CASH" : "NON_CASH",
                ["amount"] = KassenSichV.Amount(payment.AmountMinor),
            });
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

    private static long? Long(JsonNode? node) => node is null ? null : long.Parse(node.ToJsonString().Trim('"'), CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(JsonNode? node) => Long(node) is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
