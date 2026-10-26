using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Pos.Infrastructure;
using HandyNet.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HandyNet.UnitTests;

/// <summary>
/// Contract tests of the fiskaly SIGN DE v2 adapter. The recorded responses below follow the published OpenAPI spec
/// (https://workspace.fiskaly.com/specs/sign-de-v2.json: AuthenticationResponse, ClientResponse, TxResponse with
/// BigintCounter as string, CreateTssResponse, UpdateTssResponse, error body {status_code, error, code, message}).
/// </summary>
public class FiskalyContractTests
{
    private static readonly Guid Tss = Guid.Parse("8d3f2b1a-5c6e-4f70-8a91-b2c3d4e5f607");
    private static readonly Guid Client = Guid.Parse("1f0e2d3c-4b5a-4968-8776-655443322110");
    private const string TssSerial = "9f4c7f0a6b0d9b3c5e1a2f8d7c6b5a49382716051f2e3d4c5b6a79880a1b2c3d";
    private const string PublicKey = "BHhWOeisRpPBTGQ1W4VUH95TXx2GARf8e2NYZXJoInjtGqnxJ8sZ3CQpYgjI+LYEmW5A37sLWHsyU7nSJUBemyU=";
    private const string SignatureValue = "MEQCIBmwB6cXSYS4pKr+4qlDgIUVFXbsGPZtA6zrJkZNk4BdAiBzMmWeDnwrqcuOMEMqVBlT9BXl3rVkTAh/2+43J6grXQ==";
    private const string QrCodeData = "V0;KASSE01;Kassenbeleg-V1;Beleg^11.90_5.35_0.00_0.00_0.00^17.25:Bar;41;97;2026-10-18T10:00:00.000Z;2026-10-18T10:00:02.000Z;ecdsa-plain-SHA256;unixTime;"
                                      + SignatureValue + ";" + PublicKey;

    /// <summary>The spec's amount pattern for amounts_per_vat_rate / amounts_per_payment_type.</summary>
    private static readonly Regex AmountPattern = new(@"^-?\d+(\.\d{2,5})$");

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
    }

    // ---- recorded responses (spec-shaped) ------------------------------------------------------------------------------

    private static string AuthResponse(string env = "TEST") => $$"""
        {"access_token":"eyJhbGciOiJIUzI1NiJ9.test.sig","access_token_claims":{"env":"{{env}}","organization_id":"0b5c7a8e-1d2f-4e3a-9b4c-5d6e7f8a9b0c"},
         "access_token_expires_in":86400,"access_token_expires_at":1792400000,"refresh_token":"rt","refresh_token_expires_in":172800,
         "refresh_token_expires_at":1792486400}
        """;

    private static string ClientResponse(string state = "REGISTERED") => $$"""
        {"serial_number":"KASSE01","state":"{{state}}","tss_id":"{{Tss}}","metadata":{},"_id":"{{Client}}","_type":"CLIENT","_env":"TEST",
         "_version":"2.2.4","time_creation":1792390000}
        """;

    private static string TxActive(Guid txId) => $$$"""
        {"number":41,"time_start":1792404000,"client_serial_number":"KASSE01","tss_serial_number":"{{{TssSerial}}}","state":"ACTIVE",
         "client_id":"{{{Client}}}","schema":{"raw":{"process_type":"","process_data":""}},"revision":1,"latest_revision":1,"tss_id":"{{{Tss}}}",
         "metadata":{},"_type":"TRANSACTION","_id":"{{{txId}}}","_env":"TEST","_version":"2.2.4",
         "log":{"operation":"Start","timestamp":1792404000,"timestamp_format":"unixTime"},
         "signature":{"value":"MEUCIQD0start","algorithm":"ecdsa-plain-SHA256","counter":"96","public_key":"{{{PublicKey}}}"}}
        """;

    private static string TxFinished(Guid txId, string env = "TEST", bool withSignature = true, bool withQr = true)
    {
        var tx = JsonNode.Parse($$$"""
            {"number":41,"time_start":1792404000,"time_end":1792404002,"client_serial_number":"KASSE01","tss_serial_number":"{{{TssSerial}}}",
             "state":"FINISHED","client_id":"{{{Client}}}",
             "schema":{"standard_v1":{"receipt":{"receipt_type":"RECEIPT","amounts_per_vat_rate":[{"vat_rate":"NORMAL","amount":"11.90"},{"vat_rate":"REDUCED_1","amount":"5.35"}],
               "amounts_per_payment_type":[{"payment_type":"CASH","amount":"17.25","currency_code":"EUR"}]} } },
             "revision":2,"latest_revision":2,"tss_id":"{{{Tss}}}","metadata":{},"_type":"TRANSACTION","_id":"{{{txId}}}","_env":"{{{env}}}","_version":"2.2.4",
             "qr_code_data":"{{{QrCodeData}}}",
             "log":{"operation":"Finish","timestamp":1792404002,"timestamp_format":"unixTime"},
             "signature":{"value":"{{{SignatureValue}}}","algorithm":"ecdsa-plain-SHA256","counter":"97","public_key":"{{{PublicKey}}}"}}
            """)!.AsObject();
        if (!withQr)
            tx.Remove("qr_code_data");
        if (!withSignature)
            tx.Remove("signature");
        return tx.ToJsonString();
    }

    private static string ErrorBody(int status, string error, string code, string message) =>
        $$"""{"status_code":{{status}},"error":"{{error}}","code":"{{code}}","message":"{{message}}"}""";

    // ---- fake transport ------------------------------------------------------------------------------------------------

    private sealed record Recorded(HttpMethod Method, string Path, string? Body, string? Bearer);

    /// <summary>Answers "METHOD /path" (without query) from scripted queues first, then from the default routes.</summary>
    private sealed class FakeFiskaly : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];
        public Dictionary<string, Queue<Func<HttpResponseMessage>>> Script { get; } = [];
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public int Tokens { get; private set; }
        public bool Hang { get; set; }

        public void Enqueue(string route, params Func<HttpResponseMessage>[] responses)
        {
            if (!Script.TryGetValue(route, out var queue))
                Script[route] = queue = new Queue<Func<HttpResponseMessage>>();
            foreach (var r in responses)
                queue.Enqueue(r);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Recorded(request.Method, path, body, request.Headers.Authorization?.Parameter));
            if (Hang && !path.EndsWith("/auth"))
                await Task.Delay(Timeout.Infinite, ct);
            var route = $"{request.Method} {path.Split('?')[0]}";
            if (Script.TryGetValue(route, out var queue) && queue.Count > 0)
                return queue.Dequeue()();
            if (route == "POST /api/v2/auth")
            {
                Tokens++;
                return Json(HttpStatusCode.OK, AuthResponse());
            }
            return Routes.TryGetValue(route, out var respond)
                ? respond()
                : Json(HttpStatusCode.NotFound, ErrorBody(404, "Not Found", "E_NOT_FOUND", "no route"));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (FiskalyApiClient Api, FakeFiskaly Fake) CreateApi(int maxRetries = 2, int timeoutSeconds = 10)
    {
        var fake = new FakeFiskaly();
        var http = new HttpClient(fake) { BaseAddress = new Uri("https://kassensichv-middleware.fiskaly.com/api/v2/"), Timeout = Timeout.InfiniteTimeSpan };
        var options = Options.Create(new PosOptions
        {
            Fiskaly = new FiskalyOptions { MaxRetries = maxRetries, RetryBaseDelayMs = 0, TimeoutSeconds = timeoutSeconds },
        });
        return (new FiskalyApiClient(http, new FiskalyTokenCache(), options, new Clock(), NullLogger<FiskalyApiClient>.Instance), fake);
    }

    private static readonly FiskalyCredentials Credentials = new("test_key", "test_secret");

    private static (FiskalyTseSigner Signer, FakeFiskaly Fake) CreateSigner(bool sandbox = false, int maxRetries = 2, int timeoutSeconds = 10)
    {
        var (api, fake) = CreateApi(maxRetries, timeoutSeconds);
        fake.Routes[$"GET /api/v2/tss/{Tss}/client/{Client}"] = () => Json(HttpStatusCode.OK, ClientResponse());
        return (new FiskalyTseSigner(api, Credentials, Tss, sandbox), fake);
    }

    /// <summary>
    /// A sale: 11.90 at 19 % and 5.35 at 7 %, paid 17.25 in cash. HandyNet has no 7 % treatment yet; the fiskaly bucket is
    /// chosen by the rate in basis points, so a 700 bp total stands for the 7 % line.
    /// </summary>
    internal static FiscalProcess Sale()
    {
        var vat = new List<TaxTotal> { new(TaxTreatment.Standard19, 1900, 11_90, 10_00, 1_90), new(TaxTreatment.Standard19, 700, 5_35, 5_00, 35) };
        var payments = new List<PaymentTotal> { new(PaymentMethod.Cash, 17_25) };
        return new FiscalProcess(KassenSichV.ProcessTypeReceipt, KassenSichV.ReceiptProcessData(vat, payments),
            new FiscalReceiptData(ReceiptKind.Sale, 1, 17_25, vat, payments));
    }

    private static FiscalTransactionRequest Request(Guid key) =>
        new(Guid.NewGuid(), Guid.NewGuid(), key, Client.ToString(), KassenSichV.ProcessTypeReceipt);

    private static string TxRoute(Guid txId) => $"/api/v2/tss/{Tss}/tx/{txId}";

    // ---- tests ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Auth_sends_api_key_and_secret_and_reads_token_expiry_and_environment_claim()
    {
        var (api, fake) = CreateApi();
        var token = await api.AuthenticateAsync(Credentials, fresh: false, default);

        Assert.True(token.IsSuccess, token.Error?.Message);
        Assert.Equal("eyJhbGciOiJIUzI1NiJ9.test.sig", token.Value.AccessToken);
        Assert.Equal("TEST", token.Value.Environment);
        Assert.Equal(new Clock().UtcNow.AddSeconds(86400), token.Value.ExpiresAt);
        var body = JsonNode.Parse(fake.Requests.Single().Body!)!.AsObject();
        Assert.Equal(["api_key", "api_secret"], body.Select(p => p.Key).Order());
        Assert.Equal("test_key", body["api_key"]!.GetValue<string>());

        await api.AuthenticateAsync(Credentials, fresh: false, default); // cached
        Assert.Equal(1, fake.Tokens);
        await api.AuthenticateAsync(Credentials, fresh: true, default);
        Assert.Equal(2, fake.Tokens);
    }

    [Fact]
    public async Task Rejected_credentials_are_reported_with_fiskalys_code()
    {
        var (api, fake) = CreateApi();
        // Recorded from the real TEST middleware (2026-10-26, deliberately invalid key): no "code" field on /auth.
        fake.Enqueue("POST /api/v2/auth",
            () => Json(HttpStatusCode.Unauthorized, """{"message":"Invalid user credentials","status_code":401,"error":"Unauthorized"}"""));
        var token = await api.AuthenticateAsync(Credentials, fresh: true, default);
        Assert.Equal(FiskalyErrors.CredentialsRejected, token.Error?.Code);
        Assert.Contains("HTTP 401: Invalid user credentials", token.Error!.Message);
        Assert.DoesNotContain("test_secret", token.Error.Message);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Sale_with_19_and_7_percent_and_cash_follows_upsertTransaction_and_parses_the_signature()
    {
        var (signer, fake) = CreateSigner(sandbox: false);
        var key = Guid.CreateVersion7();
        var txId = FiskalyIds.Transaction(key);
        fake.Enqueue($"PUT {TxRoute(txId)}", () => Json(HttpStatusCode.OK, TxActive(txId)), () => Json(HttpStatusCode.OK, TxFinished(txId)));

        var started = await signer.StartTransactionAsync(Request(key), default);
        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(41, started.Value.Number);
        Assert.Equal(TssSerial, started.Value.TseSerialNumber);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792404000), started.Value.StartedAt);
        var finished = await signer.FinishTransactionAsync(started.Value, Sale(), default);
        Assert.True(finished.IsSuccess, finished.Error?.Message);

        Assert.Equal(["/api/v2/auth", $"/api/v2/tss/{Tss}/client/{Client}", $"{TxRoute(txId)}?tx_revision=1", $"{TxRoute(txId)}?tx_revision=2"],
            fake.Requests.Select(r => r.Path));
        Assert.All(fake.Requests.Skip(1), r => Assert.Equal("eyJhbGciOiJIUzI1NiJ9.test.sig", r.Bearer));
        Assert.True(FiskalyIds.IsV4(txId)); // the spec asks for UUIDv4 transaction ids; the receipt key is a v7

        // Revision 1: state + client_id only (the spec: type and data must be empty at the start).
        var start = JsonNode.Parse(fake.Requests[2].Body!)!.AsObject();
        Assert.Equal(["client_id", "state"], start.Select(p => p.Key).Order());
        Assert.Equal("ACTIVE", start["state"]!.GetValue<string>());
        Assert.Equal(Client.ToString(), start["client_id"]!.GetValue<string>());

        // Revision 2: FINISHED with exactly one schema (standard_v1.receipt).
        var finish = JsonNode.Parse(fake.Requests[3].Body!)!.AsObject();
        Assert.Equal("FINISHED", finish["state"]!.GetValue<string>());
        Assert.Equal(["standard_v1"], finish["schema"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["receipt"], finish["schema"]!["standard_v1"]!.AsObject().Select(p => p.Key));
        var receipt = finish["schema"]!["standard_v1"]!["receipt"]!;
        Assert.Equal("RECEIPT", receipt["receipt_type"]!.GetValue<string>());
        var vat = receipt["amounts_per_vat_rate"]!.AsArray().Select(v => (v!["vat_rate"]!.GetValue<string>(), v["amount"]!.GetValue<string>())).ToList();
        Assert.Equal([("NORMAL", "11.90"), ("REDUCED_1", "5.35")], vat);
        var paid = receipt["amounts_per_payment_type"]!.AsArray().Select(p => (p!["payment_type"]!.GetValue<string>(), p["amount"]!.GetValue<string>())).ToList();
        Assert.Equal([("CASH", "17.25")], paid);
        Assert.All(vat.Select(v => v.Item2).Concat(paid.Select(p => p.Item2)), a => Assert.Matches(AmountPattern, a));

        var data = finished.Value;
        Assert.Equal(FiscalStatus.Signed, data.Status);
        Assert.Equal(TssSerial, data.TseSerialNumber);
        Assert.Equal(41, data.TransactionNumber);
        Assert.Equal(97, data.SignatureCounter); // BigintCounter is a string in the spec
        Assert.Equal(SignatureValue, data.Signature);
        Assert.Equal("ecdsa-plain-SHA256", data.SignatureAlgorithm);
        Assert.Equal(PublicKey, data.PublicKey);
        Assert.Equal("unixTime", data.TimeFormat);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792404000), data.StartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792404002), data.FinishedAt);
        Assert.Equal(QrCodeData, data.QrPayload); // fiskaly's qr_code_data is used verbatim
        Assert.True(data.IsTest); // _env TEST wins over a configuration that says Production
        Assert.Equal(KassenSichV.TestTseNotice, data.Notice);
    }

    [Fact]
    public void Live_transaction_without_qr_code_data_builds_the_qr_from_the_client_serial_number()
    {
        var txId = Guid.NewGuid();
        var tx = JsonNode.Parse(TxFinished(txId, env: "LIVE", withQr: false))!.AsObject();
        var data = FiskalyTseSigner.ToFiscalData(tx, Client.ToString(), Sale(), sandbox: false);
        Assert.True(data.IsSuccess, data.Error?.Message);
        Assert.False(data.Value.IsTest);
        Assert.Null(data.Value.Notice);
        Assert.StartsWith("V0;KASSE01;Kassenbeleg-V1;Beleg^11.90_5.35_0.00_0.00_0.00^17.25:Bar;41;97;", data.Value.QrPayload);
    }

    [Fact]
    public async Task A_finished_response_without_signature_is_transient_never_a_signed_receipt()
    {
        var (signer, fake) = CreateSigner();
        var key = Guid.NewGuid();
        var txId = FiskalyIds.Transaction(key);
        Assert.Equal(key, txId); // a v4 key is used as is
        fake.Enqueue($"PUT {TxRoute(txId)}", () => Json(HttpStatusCode.OK, TxActive(txId)), () => Json(HttpStatusCode.OK, TxFinished(txId, withSignature: false)));
        var started = await signer.StartTransactionAsync(Request(key), default);
        var finished = await signer.FinishTransactionAsync(started.Value, Sale(), default);
        Assert.Equal(FiscalErrors.Unavailable, finished.Error?.Code);
    }

    [Fact]
    public async Task A_till_that_is_not_registered_is_refused_before_any_transaction()
    {
        var (signer, fake) = CreateSigner();
        fake.Routes[$"GET /api/v2/tss/{Tss}/client/{Client}"] = () => Json(HttpStatusCode.NotFound, ErrorBody(404, "Not Found", "E_CLIENT_NOT_FOUND", "client not found"));
        var started = await signer.StartTransactionAsync(Request(Guid.NewGuid()), default);
        Assert.Equal(FiskalyErrors.ClientNotRegistered, started.Error?.Code);
        Assert.False(FiscalErrors.IsTransient(started.Error!));
        Assert.DoesNotContain(fake.Requests, r => r.Path.Contains("/tx/"));
        Assert.DoesNotContain(fake.Requests, r => r.Method == HttpMethod.Put); // never createClient from the till (needs admin auth)
    }

    [Fact]
    public async Task A_client_id_that_is_not_a_uuid_is_refused()
    {
        var (signer, fake) = CreateSigner();
        var started = await signer.StartTransactionAsync(new FiscalTransactionRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "KASSE-ZWEI", "x"), default);
        Assert.Equal(FiskalyErrors.InvalidClientId, started.Error?.Code);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Rate_limits_499_and_tss_locked_are_retried_with_the_same_revision()
    {
        var (signer, fake) = CreateSigner(maxRetries: 3);
        var key = Guid.NewGuid();
        fake.Enqueue($"PUT {TxRoute(key)}",
            () =>
            {
                var r = Json(HttpStatusCode.TooManyRequests, ErrorBody(429, "Too Many Requests", "E_RATE_LIMIT", "slow down"));
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return r;
            },
            () => Json((HttpStatusCode)499, ErrorBody(499, "Client Closed Request", "E_CLOSED", "closed")),
            () => Json(HttpStatusCode.ServiceUnavailable, ErrorBody(503, "Service Unavailable", "E_TSS_LOCKED", "locked")),
            () => Json(HttpStatusCode.OK, TxActive(key)));
        var started = await signer.StartTransactionAsync(Request(key), default);
        Assert.True(started.IsSuccess, started.Error?.Message);
        var calls = fake.Requests.Where(r => r.Path.Contains("/tx/")).ToList();
        Assert.Equal(4, calls.Count);
        Assert.All(calls, r => Assert.EndsWith("tx_revision=1", r.Path));
        Assert.All(calls, r => Assert.Equal(calls[0].Body, r.Body)); // identical request = idempotent
    }

    [Fact]
    public async Task Persistent_server_errors_become_a_transient_tse_error()
    {
        var (signer, fake) = CreateSigner(maxRetries: 2);
        var key = Guid.NewGuid();
        fake.Enqueue($"PUT {TxRoute(key)}", Enumerable.Repeat<Func<HttpResponseMessage>>(() => new HttpResponseMessage(HttpStatusCode.BadGateway), 5).ToArray());
        var started = await signer.StartTransactionAsync(Request(key), default);
        Assert.Equal(FiscalErrors.Unavailable, started.Error?.Code);
        Assert.Equal(3, fake.Requests.Count(r => r.Path.Contains("/tx/"))); // 1 + MaxRetries
    }

    [Fact]
    public async Task A_401_refreshes_the_token_once()
    {
        var (signer, fake) = CreateSigner();
        var key = Guid.NewGuid();
        fake.Enqueue($"PUT {TxRoute(key)}", () => Json(HttpStatusCode.Unauthorized, ErrorBody(401, "Unauthorized", "E_UNAUTHORIZED", "expired")),
            () => Json(HttpStatusCode.OK, TxActive(key)));
        var started = await signer.StartTransactionAsync(Request(key), default);
        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(2, fake.Tokens);
    }

    [Fact]
    public async Task A_request_timeout_is_transient()
    {
        var (signer, fake) = CreateSigner(maxRetries: 0, timeoutSeconds: 1);
        fake.Hang = true;
        var started = await signer.StartTransactionAsync(Request(Guid.NewGuid()), default);
        Assert.Equal(FiscalErrors.Unavailable, started.Error?.Code);
        Assert.Contains("timeout", started.Error!.Message);
    }

    [Fact]
    public async Task Client_errors_carry_fiskalys_code_and_are_not_retried()
    {
        var (signer, fake) = CreateSigner();
        var key = Guid.NewGuid();
        fake.Enqueue($"PUT {TxRoute(key)}",
            () => Json(HttpStatusCode.BadRequest, ErrorBody(400, "Bad Request", "E_TSS_NOT_INITIALIZED", "The TSS is not initialized")));
        var started = await signer.StartTransactionAsync(Request(key), default);
        Assert.Equal(FiscalErrors.Rejected, started.Error?.Code);
        Assert.Contains("E_TSS_NOT_INITIALIZED", started.Error!.Message);
        Assert.Single(fake.Requests, r => r.Path.Contains("/tx/"));
    }

    [Fact]
    public async Task A_conflict_on_a_retried_finish_is_resolved_by_reading_the_transaction()
    {
        var (signer, fake) = CreateSigner();
        var key = Guid.NewGuid();
        fake.Enqueue($"PUT {TxRoute(key)}", () => Json(HttpStatusCode.OK, TxActive(key)),
            () => Json(HttpStatusCode.Conflict, ErrorBody(409, "Conflict", "E_PENDING_TX_CONFLICT", "conflict")));
        fake.Enqueue($"GET {TxRoute(key)}", () => Json(HttpStatusCode.OK, TxFinished(key)));
        var started = await signer.StartTransactionAsync(Request(key), default);
        var finished = await signer.FinishTransactionAsync(started.Value, Sale(), default);
        Assert.True(finished.IsSuccess, finished.Error?.Message);
        Assert.Equal(97, finished.Value.SignatureCounter);
        Assert.Equal(TxRoute(key), fake.Requests[^1].Path); // GET without tx_revision = latest revision
    }

    [Fact]
    public async Task A_defective_tss_is_refused()
    {
        var (signer, fake) = CreateSigner();
        var key = Guid.NewGuid();
        fake.Enqueue($"PUT {TxRoute(key)}", () => Json((HttpStatusCode)423, ErrorBody(423, "Locked", "E_TSS_DEFECTIVE", "defective")));
        var started = await signer.StartTransactionAsync(Request(key), default);
        Assert.Equal(FiscalErrors.Rejected, started.Error?.Code);
        Assert.Contains("E_TSS_DEFECTIVE", started.Error!.Message);
    }

    [Fact]
    public async Task Provisioning_a_tss_follows_create_deploy_pin_admin_initialize_logout()
    {
        var (api, fake) = CreateApi();
        var tssRoute = $"/api/v2/tss/{Tss}";
        fake.Enqueue($"PUT {tssRoute}", () => Json(HttpStatusCode.OK, $$"""
            {"certificate":"LS0tLS1CRUdJTi","serial_number":"{{TssSerial}}","public_key":"{{PublicKey}}","signature_algorithm":"ecdsa-plain-SHA256",
             "signature_timestamp_format":"unixTime","transaction_data_encoding":"UTF-8","max_number_registered_clients":199,
             "max_number_active_transactions":2000,"supported_update_variants":"SIGNED","_id":"{{Tss}}","_type":"TSS","_env":"TEST",
             "_version":"2.2.4","time_creation":1792400000,"admin_puk":"8a7b6c5d4e3f","state":"CREATED"}
            """));
        string TssJson(string state) => $$"""
            {"state":"{{state}}","certificate":"LS0t","serial_number":"{{TssSerial}}","public_key":"{{PublicKey}}","signature_algorithm":"ecdsa-plain-SHA256",
             "signature_timestamp_format":"unixTime","transaction_data_encoding":"UTF-8","max_number_registered_clients":199,
             "max_number_active_transactions":2000,"supported_update_variants":"SIGNED","_id":"{{Tss}}","_type":"TSS",
             "_env":"TEST","_version":"2.2.4","time_creation":1792400000,"time_uninit":1792400010}
            """;
        fake.Enqueue($"PATCH {tssRoute}", () => Json(HttpStatusCode.OK, TssJson("UNINITIALIZED")), () => Json(HttpStatusCode.OK, TssJson("INITIALIZED")));
        fake.Routes[$"PATCH {tssRoute}/admin"] = () => Json(HttpStatusCode.OK, "{}");
        fake.Routes[$"POST {tssRoute}/admin/auth"] = () => Json(HttpStatusCode.OK, "{}");
        fake.Routes[$"POST {tssRoute}/admin/logout"] = () => Json(HttpStatusCode.OK, "{}");
        fake.Routes[$"GET {tssRoute}"] = () => Json(HttpStatusCode.OK, TssJson("INITIALIZED"));

        var tss = await api.ProvisionTssAsync(Credentials, Tss, "123456", default);

        Assert.True(tss.IsSuccess, tss.Error?.Message);
        Assert.Equal("INITIALIZED", tss.Value["state"]!.GetValue<string>());
        Assert.Equal(
            ["PUT /tss", "PATCH /tss", "PATCH /tss/admin", "POST /tss/admin/auth", "PATCH /tss", "POST /tss/admin/logout", "GET /tss"],
            fake.Requests.Skip(1).Select(r => $"{r.Method} {r.Path.Replace(tssRoute, "/tss")}"));
        Assert.Equal("UNINITIALIZED", JsonNode.Parse(fake.Requests[2].Body!)!["state"]!.GetValue<string>());
        var pin = JsonNode.Parse(fake.Requests[3].Body!)!.AsObject();
        Assert.Equal(("8a7b6c5d4e3f", "123456"), (pin["admin_puk"]!.GetValue<string>(), pin["new_admin_pin"]!.GetValue<string>()));
        Assert.Equal("123456", JsonNode.Parse(fake.Requests[4].Body!)!["admin_pin"]!.GetValue<string>());
        Assert.Equal("INITIALIZED", JsonNode.Parse(fake.Requests[5].Body!)!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Registering_a_client_authenticates_the_admin_creates_the_client_and_logs_out()
    {
        var (api, fake) = CreateApi();
        var clientRoute = $"/api/v2/tss/{Tss}/client/{Client}";
        fake.Enqueue($"GET {clientRoute}", () => Json(HttpStatusCode.NotFound, ErrorBody(404, "Not Found", "E_CLIENT_NOT_FOUND", "not found")));
        fake.Routes[$"POST /api/v2/tss/{Tss}/admin/auth"] = () => Json(HttpStatusCode.OK, "{}");
        fake.Routes[$"PUT {clientRoute}"] = () => Json(HttpStatusCode.OK, ClientResponse());
        fake.Routes[$"POST /api/v2/tss/{Tss}/admin/logout"] = () => Json(HttpStatusCode.OK, "{}");

        var client = await api.RegisterClientAsync(Credentials, Tss, Client, "KASSE01", "123456", default);

        Assert.True(client.IsSuccess, client.Error?.Message);
        Assert.Equal("REGISTERED", client.Value["state"]!.GetValue<string>());
        Assert.Equal(["GET client", "POST admin/auth", "PUT client", "POST admin/logout"],
            fake.Requests.Skip(1).Select(r => $"{r.Method} {(r.Path.Contains("/client/") ? "client" : r.Path.Split($"{Tss}/")[1])}"));
        Assert.Equal("""{"serial_number":"KASSE01"}""", fake.Requests[3].Body);
        // The till now sees the client as registered without another lookup.
        Assert.True((await api.EnsureClientRegisteredAsync(Credentials, Tss, Client, default)).IsSuccess);
        Assert.Equal(5, fake.Requests.Count);
    }

    [Fact]
    public async Task A_wrong_admin_pin_is_reported_and_not_retried()
    {
        var (api, fake) = CreateApi();
        fake.Routes[$"GET /api/v2/tss/{Tss}/client/{Client}"] = () => Json(HttpStatusCode.NotFound, ErrorBody(404, "Not Found", "E_CLIENT_NOT_FOUND", "x"));
        fake.Routes[$"POST /api/v2/tss/{Tss}/admin/auth"] = () => Json(HttpStatusCode.Unauthorized, ErrorBody(401, "Unauthorized", "E_ADMIN_PIN", "wrong pin"));
        var client = await api.RegisterClientAsync(Credentials, Tss, Client, "KASSE01", "000000", default);
        Assert.Equal(FiskalyErrors.AdminPinRejected, client.Error?.Code);
        Assert.Single(fake.Requests, r => r.Path.EndsWith("/admin/auth")); // five failures would block the PIN
        Assert.DoesNotContain(fake.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public void Fiskaly_ids_are_uuid_v4_and_deterministic()
    {
        var v7 = Guid.CreateVersion7();
        var tx = FiskalyIds.Transaction(v7);
        Assert.Equal(tx, FiskalyIds.Transaction(v7));
        Assert.True(FiskalyIds.IsV4(tx));
        Assert.Equal('4', tx.ToString()[14]);
        Assert.NotEqual(FiskalyIds.Client(v7), tx); // different purposes never collide
        var v4 = Guid.NewGuid();
        Assert.Equal(v4, FiskalyIds.Transaction(v4));
        Assert.Equal(FiskalyIds.Client(v7).ToString(), FiscalSignerProvider.DefaultClientId(FiscalMode.FiskalyCloudTse, v7));
    }

    [Fact]
    public void Refund_amounts_match_the_spec_pattern()
    {
        var vat = new List<TaxTotal> { new(TaxTreatment.Standard19, 1900, -11_90, -10_00, -1_90) };
        var payments = new List<PaymentTotal> { new(PaymentMethod.Card, -11_90) };
        var schema = FiskalyTseSigner.Schema(new FiscalProcess(KassenSichV.ProcessTypeReceipt, "", new FiscalReceiptData(ReceiptKind.Storno, 2, -11_90, vat, payments)));
        var receipt = schema["standard_v1"]!["receipt"]!;
        Assert.Equal("-11.90", receipt["amounts_per_vat_rate"]![0]!["amount"]!.GetValue<string>());
        Assert.Equal("NON_CASH", receipt["amounts_per_payment_type"]![0]!["payment_type"]!.GetValue<string>());
        Assert.Matches(AmountPattern, receipt["amounts_per_payment_type"]![0]!["amount"]!.GetValue<string>());
    }

    [Fact]
    public void Non_receipt_events_use_the_raw_schema()
    {
        var schema = FiskalyTseSigner.Schema(new FiscalProcess(KassenSichV.ProcessTypeOther, "Einlage^ChangeFund^100.00"));
        Assert.Equal(["raw"], schema.Select(p => p.Key));
        Assert.Equal("SonstigerVorgang", schema["raw"]!["process_type"]!.GetValue<string>());
        Assert.Equal("Einlage^ChangeFund^100.00", Encoding.UTF8.GetString(Convert.FromBase64String(schema["raw"]!["process_data"]!.GetValue<string>())));
    }
}
