using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Pos.Infrastructure;
using HandyNet.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HandyNet.UnitTests;

public class KassenSichVFormatTests
{
    [Theory]
    [InlineData(0, "0.00")]
    [InlineData(5, "0.05")]
    [InlineData(123_456, "1234.56")]
    [InlineData(-1_990, "-19.90")]
    public void Amounts_have_two_decimals_and_a_dot(long minor, string expected) => Assert.Equal(expected, KassenSichV.Amount(minor));

    [Fact]
    public void Receipt_process_data_lists_gross_per_vat_bucket_then_payments_cash_first()
    {
        var vat = new List<TaxTotal>
        {
            new(TaxTreatment.Standard19, 1900, 19_00, 15_97, 3_03),
            new(TaxTreatment.Margin25a, 1900, 300_00, 287_23, 12_77),
        };
        var payments = new List<PaymentTotal> { new(PaymentMethod.Card, 100_00), new(PaymentMethod.Cash, 219_00) };
        Assert.Equal("Beleg^319.00_0.00_0.00_0.00_0.00^219.00:Bar_100.00:Unbar", KassenSichV.ReceiptProcessData(vat, payments));
    }

    [Fact]
    public void Refund_process_data_is_negative_and_omits_zero_payments()
    {
        var vat = new List<TaxTotal> { new(TaxTreatment.Standard19, 1900, -10_00, -8_40, -1_60) };
        var payments = new List<PaymentTotal> { new(PaymentMethod.Cash, -10_00), new(PaymentMethod.Card, 0) };
        Assert.Equal("Beleg^-10.00_0.00_0.00_0.00_0.00^-10.00:Bar", KassenSichV.ReceiptProcessData(vat, payments));
    }

    [Fact]
    public void Other_process_data_strips_separators()
    {
        Assert.Equal("Einlage^ChangeFund^100.00", KassenSichV.OtherProcessData("Einlage", "ChangeFund", 100_00));
        Assert.Equal("Kassenabschluss^3 Belege x-y,z^", KassenSichV.OtherProcessData("Kassenabschluss", "3 Belege x_y;z", null));
    }

    [Fact]
    public void Qr_payload_has_the_twelve_dsfinvk_fields_with_iso_times()
    {
        var start = new DateTimeOffset(2026, 10, 18, 9, 30, 0, 123, TimeSpan.FromHours(2));
        var qr = KassenSichV.QrPayload("K1", KassenSichV.ProcessTypeReceipt, "Beleg^1.00_0.00_0.00_0.00_0.00^1.00:Bar", 7, 15, start, start.AddSeconds(2),
            KassenSichV.AlgorithmP256, KassenSichV.TimeFormatUnix, "c2ln", "cHVi");
        var fields = qr.Split(';');
        Assert.Equal(12, fields.Length);
        Assert.Equal(["V0", "K1", "Kassenbeleg-V1", "Beleg^1.00_0.00_0.00_0.00_0.00^1.00:Bar", "7", "15", "2026-10-18T07:30:00.123Z", "2026-10-18T07:30:02.123Z",
            "ecdsa-plain-SHA256", "unixTime", "c2ln", "cHVi"], fields);
    }
}

public class SimulatedTseTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Clear-text "protection" for tests.</summary>
    private sealed class PlainProtector : IFiscalSecretProtector
    {
        public string Protect(string plaintext) => "p:" + plaintext;
        public string? Unprotect(string protectedValue) => protectedValue.StartsWith("p:") ? protectedValue[2..] : null;
    }

    /// <summary>Scripted faults: a queue of (fail, after) per call; empty = no fault.</summary>
    private sealed class ScriptedFaults : ISimulatedTseFaults
    {
        public Queue<bool?> Script { get; } = new();
        private bool _after;

        public bool ShouldFail(int failureRatePercent)
        {
            if (Script.Count == 0)
                return false;
            var next = Script.Dequeue();
            _after = next == true;
            return next is not null;
        }

        public bool FailAfterProcessing() => _after;
    }

    internal sealed class MemoryStore(IFiscalSecretProtector protector) : ISimulatedTseStore
    {
        public SimulatedTseState? Tse { get; private set; }
        public List<SimulatedTseTxRow> Transactions { get; } = [];

        public Task<SimulatedTseState> LockAsync(Guid organizationId, string algorithmForNewTse, CancellationToken ct)
        {
            if (Tse is null)
            {
                var keys = SimulatedTseCrypto.Generate(algorithmForNewTse);
                Tse = new SimulatedTseState(Guid.NewGuid(), organizationId, keys.SerialNumber, keys.PublicKey, algorithmForNewTse, KassenSichV.TimeFormatUnix, 0, 0,
                    protector.Protect(Convert.ToBase64String(keys.Pkcs8PrivateKey)));
            }
            return Task.FromResult(Tse);
        }

        public Task<SimulatedTseTxRow?> FindAsync(Guid tseId, Guid transactionKey, CancellationToken ct) =>
            Task.FromResult(Transactions.FirstOrDefault(t => t.TseId == tseId && t.TransactionKey == transactionKey));

        public Task StartAsync(SimulatedTseState tse, SimulatedTseTxRow transaction, CancellationToken ct)
        {
            Assert.True(tse.TransactionCounter > Tse!.TransactionCounter && tse.SignatureCounter > Tse.SignatureCounter, "counters must increase");
            Transactions.Add(transaction);
            Tse = tse;
            return Task.CompletedTask;
        }

        public Task FinishAsync(SimulatedTseState tse, SimulatedTseTxRow transaction, CancellationToken ct)
        {
            Assert.True(tse.SignatureCounter > Tse!.SignatureCounter, "signature counter must increase");
            var index = Transactions.FindIndex(t => t.Id == transaction.Id);
            Assert.Equal(SimulatedTseTransactionState.Active, Transactions[index].State);
            Transactions[index] = transaction;
            Tse = tse;
            return Task.CompletedTask;
        }
    }

    private static (SimulatedTseSigner Signer, MemoryStore Store, ScriptedFaults Faults) Create(SimulatedTseBehaviour? behaviour = null)
    {
        var protector = new PlainProtector();
        var store = new MemoryStore(protector);
        var faults = new ScriptedFaults();
        return (new SimulatedTseSigner(store, protector, faults, new FixedClock(), Org, behaviour ?? SimulatedTseBehaviour.Normal), store, faults);
    }

    private static FiscalTransactionRequest Request(Guid? key = null) =>
        new(Org, Guid.NewGuid(), key ?? Guid.NewGuid(), "KASSE-1", KassenSichV.ProcessTypeReceipt);

    private static readonly FiscalProcess Process = new(KassenSichV.ProcessTypeReceipt, "Beleg^19.00_0.00_0.00_0.00_0.00^19.00:Bar");

    [Theory]
    [InlineData(KassenSichV.AlgorithmP256)]
    [InlineData(KassenSichV.AlgorithmP384)]
    public async Task Signature_verifies_with_the_public_key_and_tampering_is_detected(string algorithm)
    {
        var (signer, store, _) = Create(SimulatedTseBehaviour.Normal with { Algorithm = algorithm });
        var tx = (await signer.StartTransactionAsync(Request(), default)).Value;
        var data = (await signer.FinishTransactionAsync(tx, Process, default)).Value;

        Assert.Equal(FiscalStatus.Signed, data.Status);
        Assert.True(data.IsTest);
        Assert.Equal(KassenSichV.TestTseNotice, data.Notice);
        Assert.Equal(algorithm, data.SignatureAlgorithm);
        Assert.Equal(SimulatedTseCrypto.SerialFor(data.PublicKey!), data.TseSerialNumber);
        var message = new TseLogMessage(TseLogMessage.FinishOperation, data.TseSerialNumber!, "KASSE-1", data.TransactionNumber!.Value,
            data.SignatureCounter!.Value, data.FinishedAt!.Value, Process.ProcessType, Process.ProcessData);
        Assert.True(SimulatedTseCrypto.Verify(data.PublicKey!, algorithm, message, data.Signature!));
        Assert.False(SimulatedTseCrypto.Verify(data.PublicKey!, algorithm, message with { ProcessData = "Beleg^1.00_0.00_0.00_0.00_0.00^1.00:Bar" }, data.Signature!));
        Assert.False(SimulatedTseCrypto.Verify(data.PublicKey!, algorithm, message with { SignatureCounter = 99 }, data.Signature!));

        // Another TSE's key does not verify it.
        var other = SimulatedTseCrypto.Generate(algorithm);
        Assert.False(SimulatedTseCrypto.Verify(other.PublicKey, algorithm, message, data.Signature!));

        // The start log is signed too.
        var start = store.Transactions.Single();
        Assert.True(SimulatedTseCrypto.Verify(data.PublicKey!, algorithm, new TseLogMessage(TseLogMessage.StartOperation, data.TseSerialNumber!, "KASSE-1",
            start.Number, start.StartSignatureCounter, start.StartedAt, KassenSichV.ProcessTypeReceipt, ""), start.StartSignature));
    }

    [Fact]
    public async Task Transaction_numbers_and_signature_counters_increase_monotonically()
    {
        var (signer, store, _) = Create();
        var results = new List<FiscalData>();
        for (var i = 0; i < 3; i++)
        {
            var tx = (await signer.StartTransactionAsync(Request(), default)).Value;
            results.Add((await signer.FinishTransactionAsync(tx, Process, default)).Value);
        }

        Assert.Equal([1L, 2L, 3L], results.Select(r => r.TransactionNumber!.Value));
        // Every transaction signs its start and its finish: counters 2, 4, 6.
        Assert.Equal([2L, 4L, 6L], results.Select(r => r.SignatureCounter!.Value));
        Assert.Equal(3, store.Tse!.TransactionCounter);
        Assert.Equal(6, store.Tse.SignatureCounter);
        Assert.Single(results.Select(r => r.TseSerialNumber).Distinct());
    }

    [Fact]
    public async Task Qr_payload_of_a_signed_receipt_matches_its_fiscal_data()
    {
        var (signer, _, _) = Create();
        var tx = (await signer.StartTransactionAsync(Request(), default)).Value;
        var data = (await signer.FinishTransactionAsync(tx, Process, default)).Value;
        var fields = data.QrPayload!.Split(';');
        Assert.Equal(12, fields.Length);
        Assert.Equal("V0", fields[0]);
        Assert.Equal("KASSE-1", fields[1]);
        Assert.Equal(KassenSichV.ProcessTypeReceipt, fields[2]);
        Assert.Equal(Process.ProcessData, fields[3]);
        Assert.Equal(data.TransactionNumber.ToString(), fields[4]);
        Assert.Equal(data.SignatureCounter.ToString(), fields[5]);
        Assert.Equal(KassenSichV.QrTime(data.StartedAt!.Value), fields[6]);
        Assert.Equal(KassenSichV.QrTime(data.FinishedAt!.Value), fields[7]);
        Assert.Equal(data.SignatureAlgorithm, fields[8]);
        Assert.Equal(KassenSichV.TimeFormatUnix, fields[9]);
        Assert.Equal(data.Signature, fields[10]);
        Assert.Equal(data.PublicKey, fields[11]);
    }

    [Fact]
    public async Task Retries_after_a_lost_response_never_sign_twice()
    {
        var (signer, store, faults) = Create(SimulatedTseBehaviour.Normal with { FailureRatePercent = 50 });
        var request = Request();

        faults.Script.Enqueue(true); // start processed, response lost
        var lostStart = await signer.StartTransactionAsync(request, default);
        Assert.Equal(FiscalErrors.Unavailable, lostStart.Error?.Code);
        var start = await signer.StartTransactionAsync(request, default); // retry with the same key
        Assert.True(start.IsSuccess);
        Assert.Single(store.Transactions);
        Assert.Equal(1, store.Tse!.TransactionCounter);

        faults.Script.Enqueue(true); // finish processed, response lost
        Assert.Equal(FiscalErrors.Unavailable, (await signer.FinishTransactionAsync(start.Value, Process, default)).Error?.Code);
        var counterAfterFinish = store.Tse.SignatureCounter;
        var retried = await signer.FinishTransactionAsync(start.Value, Process, default);
        Assert.True(retried.IsSuccess);
        Assert.Equal(counterAfterFinish, store.Tse.SignatureCounter); // the retry returned the stored signature
        Assert.Equal(store.Transactions.Single().Signature, retried.Value.Signature);
        Assert.Equal(2, store.Tse.SignatureCounter);
    }

    [Fact]
    public async Task A_failure_before_processing_changes_nothing()
    {
        var (signer, store, faults) = Create(SimulatedTseBehaviour.Normal with { FailureRatePercent = 50 });
        faults.Script.Enqueue(false);
        Assert.Equal(FiscalErrors.Unavailable, (await signer.StartTransactionAsync(Request(), default)).Error?.Code);
        Assert.Null(store.Tse);
        Assert.Empty(store.Transactions);
    }

    [Fact]
    public async Task Forced_outage_fails_every_call_without_touching_the_tse()
    {
        var (signer, store, _) = Create(SimulatedTseBehaviour.Normal with { ForcedOutage = true });
        var start = await signer.StartTransactionAsync(Request(), default);
        Assert.Equal(FiscalErrors.Outage, start.Error?.Code);
        Assert.False(FiscalErrors.IsTransient(start.Error!));
        Assert.Null(store.Tse);
    }

    [Fact]
    public async Task Finish_of_an_unknown_transaction_is_refused()
    {
        var (signer, _, _) = Create();
        var result = await signer.FinishTransactionAsync(new FiscalTransaction(Guid.NewGuid(), "KASSE-1"), Process, default);
        Assert.Equal(FiscalErrors.Rejected, result.Error?.Code);
    }

    [Fact]
    public void Receipt_document_marks_test_signatures_and_outages()
    {
        var header = new ReceiptHeader(Org, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var sale = Receipt.CreateSale(header,
            [new SaleLineInput(Guid.NewGuid(), null, null, "Kabel", 1, 10_00, 0, TaxTreatment.Standard19, null)],
            [new PaymentInput(PaymentMethod.Cash, 10_00)]).Value;
        var seller = new SellerInfo("Shop", "Shop GmbH", "Hauptstr. 1", "10115", "Berlin", "DE", null);

        var outage = new FiscalData(FiscalStatus.Failed, KassenSichV.OutageNotice, null, null, null, null, null, null, Mode: "SimulatedTse", IsTest: true);
        var document = ReceiptDocument.For(sale, seller, "Kasse 1", null, outage);
        Assert.Contains(KassenSichV.OutageNotice, document.Notes);
        Assert.Contains(KassenSichV.TestTseNotice, document.Notes);
        Assert.Equal("Failed", document.Fiscal.Status);
        Assert.True(document.Fiscal.IsTest);

        Assert.True(sale.Seal(outage, document).IsSuccess);
        Assert.Equal(FiscalStatus.Failed, sale.Fiscal!.Status);
        Assert.True(sale.Fiscal.IsTest);
    }
}

public class FiscalConfigurationTests
{
    [Fact]
    public void Organization_rows_need_a_mode_and_values_are_range_checked()
    {
        var org = Guid.NewGuid();
        Assert.Equal("pos.fiscal_mode_required", FiscalConfiguration.Create(org, null, new FiscalSettings(null), Guid.NewGuid(), DateTimeOffset.UtcNow).Error?.Code);
        Assert.True(FiscalConfiguration.Create(org, Guid.NewGuid(), new FiscalSettings(null), Guid.NewGuid(), DateTimeOffset.UtcNow).IsSuccess);
        Assert.Equal("pos.fiscal_failure_rate",
            FiscalConfiguration.Create(org, null, new FiscalSettings(FiscalMode.SimulatedTse, FailureRatePercent: 101), Guid.NewGuid(), DateTimeOffset.UtcNow).Error?.Code);
        Assert.Equal("pos.fiscal_latency",
            FiscalConfiguration.Create(org, null, new FiscalSettings(FiscalMode.SimulatedTse, LatencyMs: -1), Guid.NewGuid(), DateTimeOffset.UtcNow).Error?.Code);
        Assert.Equal("pos.fiscal_algorithm",
            FiscalConfiguration.Create(org, null, new FiscalSettings(FiscalMode.SimulatedTse, Algorithm: "rsa"), Guid.NewGuid(), DateTimeOffset.UtcNow).Error?.Code);
        Assert.Equal("pos.fiscal_client_id",
            FiscalConfiguration.Create(org, Guid.NewGuid(), new FiscalSettings(null, ClientId: "a;b"), Guid.NewGuid(), DateTimeOffset.UtcNow).Error?.Code);
        Assert.Equal("pos.fiscal_client_id",
            FiscalConfiguration.Create(org, null, new FiscalSettings(FiscalMode.SimulatedTse, ClientId: "K1"), Guid.NewGuid(), DateTimeOffset.UtcNow).Error?.Code);
    }

    [Fact]
    public void An_outage_ends_once()
    {
        var outage = FiscalOutage.Start(Guid.NewGuid(), Guid.NewGuid(), null, FiscalMode.SimulatedTse, "down", DateTimeOffset.UtcNow);
        Assert.True(outage.End(DateTimeOffset.UtcNow).IsSuccess);
        Assert.Equal("pos.outage_ended", outage.End(DateTimeOffset.UtcNow).Error?.Code);
    }
}

public class FiskalyAdapterTests
{
    private static readonly Guid Tss = Guid.Parse("2b7c7f4e-0000-4000-8000-000000000001");

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Fake fiskaly: answers by path, records every request; optional scripted status codes per path.</summary>
    private sealed class FakeFiskaly : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Requests { get; } = [];
        public Dictionary<string, Queue<HttpStatusCode>> Failures { get; } = [];
        public int TokenCounter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, path, body, request.Headers.Authorization?.Parameter));
            var key = path.Split('?')[0];
            if (Failures.TryGetValue(key, out var queue) && queue.Count > 0)
                return new HttpResponseMessage(queue.Dequeue());

            if (path.EndsWith("/auth"))
                return Json(new JsonObject { ["access_token"] = $"token-{++TokenCounter}", ["access_token_expires_in"] = 3600 });
            if (path.Contains("/client/"))
                return Json(new JsonObject { ["_id"] = path.Split('/').Last(), ["state"] = "REGISTERED" });
            if (path.Contains("/tx/") && path.Contains("tx_revision=1"))
                return Json(new JsonObject { ["number"] = 41, ["time_start"] = 1_792_400_000, ["state"] = "ACTIVE", ["tss_serial_number"] = "abc123" });
            if (path.Contains("/tx/"))
                return Json(new JsonObject
                {
                    ["number"] = 41,
                    ["state"] = "FINISHED",
                    ["time_start"] = 1_792_400_000,
                    ["time_end"] = 1_792_400_002,
                    ["tss_serial_number"] = "abc123",
                    ["signature"] = new JsonObject { ["value"] = "c2lnbmF0dXJl", ["counter"] = 97, ["algorithm"] = "ecdsa-plain-SHA384", ["public_key"] = "cHVibGlj" },
                    ["log"] = new JsonObject { ["timestamp_format"] = "unixTime" },
                });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(JsonObject json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static (FiskalyTseSigner Signer, FakeFiskaly Fake) Create()
    {
        var fake = new FakeFiskaly();
        var http = new HttpClient(fake) { BaseAddress = new Uri("https://fiskaly.test/api/v2/") };
        var options = Options.Create(new PosOptions { Fiskaly = new FiskalyOptions { MaxRetries = 2 } });
        var api = new FiskalyApiClient(http, new FiskalyTokenCache(), options, new Clock(), NullLogger<FiskalyApiClient>.Instance);
        return (new FiskalyTseSigner(api, new FiskalyCredentials("key", "secret"), Tss, sandbox: true), fake);
    }

    private static FiscalProcess ReceiptProcess()
    {
        var vat = new List<TaxTotal> { new(TaxTreatment.Standard19, 1900, 19_00, 15_97, 3_03) };
        var payments = new List<PaymentTotal> { new(PaymentMethod.Cash, 19_00) };
        return new FiscalProcess(KassenSichV.ProcessTypeReceipt, KassenSichV.ReceiptProcessData(vat, payments),
            new FiscalReceiptData(ReceiptKind.Sale, 1, 19_00, vat, payments));
    }

    [Fact]
    public async Task Start_and_finish_follow_the_sign_de_v2_shape()
    {
        var (signer, fake) = Create();
        var key = Guid.NewGuid();
        var clientId = Guid.NewGuid().ToString();
        var started = await signer.StartTransactionAsync(new FiscalTransactionRequest(Guid.NewGuid(), Guid.NewGuid(), key, clientId, KassenSichV.ProcessTypeReceipt), default);
        Assert.True(started.IsSuccess, started.Error?.Message);
        Assert.Equal(41, started.Value.Number);
        var finished = await signer.FinishTransactionAsync(started.Value, ReceiptProcess(), default);
        Assert.True(finished.IsSuccess, finished.Error?.Message);

        Assert.Equal(["/api/v2/auth", $"/api/v2/tss/{Tss}/client/{clientId}", $"/api/v2/tss/{Tss}/tx/{key}?tx_revision=1", $"/api/v2/tss/{Tss}/tx/{key}?tx_revision=2"],
            fake.Requests.Select(r => r.Path));
        Assert.All(fake.Requests.Skip(1), r => Assert.Equal("token-1", r.Auth));
        var auth = JsonNode.Parse(fake.Requests[0].Body!)!;
        Assert.Equal("key", auth["api_key"]!.GetValue<string>());
        var finish = JsonNode.Parse(fake.Requests[3].Body!)!;
        Assert.Equal("FINISHED", finish["state"]!.GetValue<string>());
        var receipt = finish["schema"]!["standard_v1"]!["receipt"]!;
        Assert.Equal("RECEIPT", receipt["receipt_type"]!.GetValue<string>());
        Assert.Equal("NORMAL", receipt["amounts_per_vat_rate"]![0]!["vat_rate"]!.GetValue<string>());
        Assert.Equal("19.00", receipt["amounts_per_vat_rate"]![0]!["amount"]!.GetValue<string>());
        Assert.Equal("CASH", receipt["amounts_per_payment_type"]![0]!["payment_type"]!.GetValue<string>());

        var data = finished.Value;
        Assert.Equal(FiscalStatus.Signed, data.Status);
        Assert.Equal("abc123", data.TseSerialNumber);
        Assert.Equal(97, data.SignatureCounter);
        Assert.Equal("ecdsa-plain-SHA384", data.SignatureAlgorithm);
        Assert.True(data.IsTest); // sandbox
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_792_400_002), data.FinishedAt);
        Assert.Equal(12, data.QrPayload!.Split(';').Length);
        Assert.StartsWith($"V0;{clientId};Kassenbeleg-V1;", data.QrPayload);
    }

    [Fact]
    public async Task Server_errors_are_retried_with_the_same_revision_and_401_refreshes_the_token()
    {
        var (signer, fake) = Create();
        var key = Guid.NewGuid();
        fake.Failures[$"/api/v2/tss/{Tss}/tx/{key}"] = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.Unauthorized]);
        var started = await signer.StartTransactionAsync(new FiscalTransactionRequest(Guid.NewGuid(), Guid.NewGuid(), key, "c", KassenSichV.ProcessTypeReceipt), default);
        Assert.True(started.IsSuccess, started.Error?.Message);
        var txCalls = fake.Requests.Where(r => r.Path.Contains("/tx/")).ToList();
        Assert.Equal(3, txCalls.Count);
        Assert.All(txCalls, r => Assert.EndsWith("tx_revision=1", r.Path));
        Assert.Equal(2, fake.TokenCounter); // re-authenticated after the 401
        Assert.Equal("token-2", txCalls[^1].Auth);
    }

    [Fact]
    public async Task Persistent_server_errors_become_a_transient_tse_error()
    {
        var (signer, fake) = Create();
        var key = Guid.NewGuid();
        fake.Failures[$"/api/v2/tss/{Tss}/tx/{key}"] = new Queue<HttpStatusCode>(Enumerable.Repeat(HttpStatusCode.BadGateway, 5));
        var started = await signer.StartTransactionAsync(new FiscalTransactionRequest(Guid.NewGuid(), Guid.NewGuid(), key, "c", KassenSichV.ProcessTypeReceipt), default);
        Assert.Equal(FiscalErrors.Unavailable, started.Error?.Code);
        Assert.Equal(3, fake.Requests.Count(r => r.Path.Contains("/tx/"))); // 1 + MaxRetries
    }

    [Fact]
    public void Non_receipt_events_use_the_raw_schema()
    {
        var schema = FiskalyTseSigner.Schema(new FiscalProcess(KassenSichV.ProcessTypeOther, "Einlage^ChangeFund^100.00"));
        Assert.Equal("SonstigerVorgang", schema["raw"]!["process_type"]!.GetValue<string>());
        Assert.Equal("Einlage^ChangeFund^100.00", Encoding.UTF8.GetString(Convert.FromBase64String(schema["raw"]!["process_data"]!.GetValue<string>())));
    }
}
