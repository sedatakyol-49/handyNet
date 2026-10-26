using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Pos.Infrastructure;
using HandyNet.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HandyNet.IntegrationTests;

/// <summary>Runs only when FISKALY_TEST_API_KEY and FISKALY_TEST_API_SECRET are set (fiskaly TEST keys; see docs/operations.md).</summary>
public sealed class FiskalySandboxFactAttribute : FactAttribute
{
    public const string KeyVariable = "FISKALY_TEST_API_KEY";
    public const string SecretVariable = "FISKALY_TEST_API_SECRET";
    public const string BaseUrlVariable = "FISKALY_TEST_BASE_URL";

    public FiskalySandboxFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyVariable))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SecretVariable)))
            Skip = $"Opt-in: set {KeyVariable} and {SecretVariable} (fiskaly TEST environment) to run the live sandbox test.";
    }
}

/// <summary>
/// The full fiskaly SIGN DE v2 flow against the real TEST environment, through the production adapter code: auth →
/// create, deploy and initialize a fresh TSS (admin PIN from the PUK) → register a client → start and finish a transaction
/// for a sale with 19 % and 7 % lines paid in cash → check the parsed signature data. The TSS is disabled afterwards.
/// Credentials, PUK and admin PIN are never printed; the test refuses LIVE keys.
/// </summary>
public class FiskalySandboxTests
{
    [FiskalySandboxFact]
    public async Task Full_flow_against_the_fiskaly_test_environment()
    {
        var credentials = new FiskalyCredentials(Environment.GetEnvironmentVariable(FiskalySandboxFactAttribute.KeyVariable)!.Trim(),
            Environment.GetEnvironmentVariable(FiskalySandboxFactAttribute.SecretVariable)!.Trim());
        var baseUrl = Environment.GetEnvironmentVariable(FiskalySandboxFactAttribute.BaseUrlVariable) is { Length: > 0 } custom
            ? custom
            : FiskalyOptions.DefaultBaseUrl;
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = Timeout.InfiniteTimeSpan };
        var options = Options.Create(new PosOptions { Fiskaly = new FiskalyOptions { TimeoutSeconds = 30, MaxRetries = 3 } });
        var api = new FiskalyApiClient(http, new FiskalyTokenCache(), options, new SystemClock(), NullLogger<FiskalyApiClient>.Instance);

        // 1. Auth: the keys must belong to TEST — never create resources with LIVE keys from a test.
        var token = await api.AuthenticateAsync(credentials, fresh: true, default);
        Assert.True(token.IsSuccess, token.Error?.Message);
        Assert.True(token.Value.Environment == "TEST", $"The fiskaly keys are for '{token.Value.Environment}', not TEST. Aborted before creating anything.");

        // 2. TSS: create → UNINITIALIZED → admin PIN → INITIALIZED.
        var tssId = Guid.NewGuid();
        var adminPin = RandomNumberGenerator.GetInt32(10_000_000, 100_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            var tss = await api.ProvisionTssAsync(credentials, tssId, adminPin, default);
            Assert.True(tss.IsSuccess, tss.Error?.Message);
            Assert.Equal("INITIALIZED", tss.Value["state"]?.GetValue<string>());
            var tssSerial = tss.Value["serial_number"]?.GetValue<string>();
            var tssPublicKey = tss.Value["public_key"]?.GetValue<string>();
            Assert.False(string.IsNullOrEmpty(tssSerial));
            Assert.False(string.IsNullOrEmpty(tssPublicKey));

            // 3. Client registration (admin authenticated), then the till's own check.
            var clientId = Guid.NewGuid();
            var serial = "HANDYNET-SANDBOX-" + clientId.ToString("N")[..12].ToUpperInvariant();
            var client = await api.RegisterClientAsync(credentials, tssId, clientId, serial, adminPin, default);
            Assert.True(client.IsSuccess, client.Error?.Message);
            Assert.Equal("REGISTERED", client.Value["state"]?.GetValue<string>());
            Assert.True((await api.EnsureClientRegisteredAsync(credentials, tssId, clientId, default)).IsSuccess);

            // 4. A sale: 11.90 at 19 %, 5.35 at 7 %, 17.25 cash. The key is a UUIDv7 like a receipt id (mapped to UUIDv4).
            var signer = new FiskalyTseSigner(api, credentials, tssId, sandbox: true);
            var key = Guid.CreateVersion7();
            var started = await signer.StartTransactionAsync(new FiscalTransactionRequest(Guid.NewGuid(), Guid.NewGuid(), key, clientId.ToString(),
                KassenSichV.ProcessTypeReceipt), default);
            Assert.True(started.IsSuccess, started.Error?.Message);
            Assert.True(started.Value.Number > 0);
            Assert.Equal(tssSerial, started.Value.TseSerialNumber);

            var vat = new List<TaxTotal> { new(TaxTreatment.Standard19, 1900, 11_90, 10_00, 1_90), new(TaxTreatment.Standard19, 700, 5_35, 5_00, 35) };
            var payments = new List<PaymentTotal> { new(PaymentMethod.Cash, 17_25) };
            var process = new FiscalProcess(KassenSichV.ProcessTypeReceipt, KassenSichV.ReceiptProcessData(vat, payments),
                new FiscalReceiptData(ReceiptKind.Sale, 1, 17_25, vat, payments));
            var finished = await signer.FinishTransactionAsync(started.Value, process, default);
            Assert.True(finished.IsSuccess, finished.Error?.Message);

            // 5. Parsed signature data.
            var data = finished.Value;
            Assert.Equal(FiscalStatus.Signed, data.Status);
            Assert.True(data.IsTest);
            Assert.Equal(started.Value.Number, data.TransactionNumber);
            Assert.True(data.SignatureCounter > 0);
            Assert.False(string.IsNullOrEmpty(data.Signature));
            Assert.Equal(tssSerial, data.TseSerialNumber);
            Assert.Equal(tssPublicKey, data.PublicKey);
            Assert.Equal("ecdsa-plain-SHA256", data.SignatureAlgorithm);
            Assert.Equal("unixTime", data.TimeFormat);
            Assert.NotNull(data.StartedAt);
            Assert.True(data.FinishedAt >= data.StartedAt);
            Assert.StartsWith("V0;", data.QrPayload);
            Assert.Contains("Kassenbeleg-V1", data.QrPayload);

            // 6. The transaction as stored by fiskaly carries what we sent.
            var stored = await api.GetTransactionAsync(credentials, tssId, FiskalyIds.Transaction(key), default);
            Assert.True(stored.IsSuccess, stored.Error?.Message);
            var receipt = stored.Value["schema"]?["standard_v1"]?["receipt"] as JsonObject;
            Assert.NotNull(receipt);
            Assert.Equal(2, receipt!["amounts_per_vat_rate"]!.AsArray().Count);
            Assert.Equal("TEST", stored.Value["_env"]?.GetValue<string>());
        }
        finally
        {
            // A TEST TSS is free, but disable it so test runs do not pile up active TSS (DISABLED is final).
            if ((await api.AuthenticateAdminAsync(credentials, tssId, adminPin, CancellationToken.None)).IsSuccess)
            {
                await api.UpdateTssStateAsync(credentials, tssId, "DISABLED", CancellationToken.None);
                await api.LogoutAdminAsync(credentials, tssId, CancellationToken.None);
            }
        }
    }
}
