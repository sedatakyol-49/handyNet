using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HandyNet.IntegrationTests.Infrastructure;

/// <summary>
/// In-process stand-in for the fiskaly SIGN DE v2 middleware, shaped like the spec (TEST environment): auth, retrieveTss
/// (every TSS is INITIALIZED), clients (admin PIN <see cref="AdminPin"/> required to create one), transactions (revision 1
/// ACTIVE, revision 2 FINISHED with signature and qr_code_data). Records every request; shared by the whole test collection,
/// so tests filter by their own TSS id.
/// </summary>
public sealed partial class FakeFiskalyServer : HttpMessageHandler
{
    public const string AdminPin = "246810";

    private readonly ConcurrentDictionary<string, string> _clients = new(); // "tss:client" → serial
    private readonly ConcurrentDictionary<Guid, bool> _adminSessions = new();
    private readonly ConcurrentDictionary<string, long> _counters = new();

    public ConcurrentQueue<(string Method, string Path, string? Body)> Requests { get; } = new();

    public IEnumerable<(string Method, string Path, string? Body)> For(Guid tssId) => Requests.Where(r => r.Path.Contains(tssId.ToString()));

    [GeneratedRegex("^/api/v2/tss/(?<tss>[0-9a-f-]{36})(/(?<kind>client|tx|admin)(/(?<id>[0-9a-z-]+))?)?$")]
    private static partial Regex Route();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method.Method, request.RequestUri.PathAndQuery, body));
        var json = body is null ? null : JsonNode.Parse(body) as JsonObject;

        if (path == "/api/v2/auth")
            return json?["api_key"]?.GetValue<string>() is { Length: > 0 } key && !key.Contains("wrong")
                ? Ok(new JsonObject
                {
                    ["access_token"] = "fake-" + Guid.NewGuid().ToString("N"),
                    ["access_token_claims"] = new JsonObject { ["env"] = "TEST", ["organization_id"] = Guid.NewGuid().ToString() },
                    ["access_token_expires_in"] = 86400,
                })
                : Error(401, "Unauthorized", null, "Invalid user credentials");
        if (request.Headers.Authorization?.Parameter?.StartsWith("fake-") != true)
            return Error(401, "Unauthorized", "E_UNAUTHORIZED", "missing token");

        var match = Route().Match(path);
        if (!match.Success)
            return Error(404, "Not Found", "E_NOT_FOUND", "unknown route");
        var tss = Guid.Parse(match.Groups["tss"].Value);
        var kind = match.Groups["kind"].Value;
        var id = match.Groups["id"].Value;
        var method = request.Method.Method;

        switch (kind, method)
        {
            case ("", "GET"):
                return Ok(new JsonObject
                {
                    ["_id"] = tss.ToString(), ["_type"] = "TSS", ["_env"] = "TEST", ["state"] = "INITIALIZED", ["serial_number"] = Serial(tss),
                    ["public_key"] = "BPUBLICKEY", ["signature_algorithm"] = "ecdsa-plain-SHA256", ["signature_timestamp_format"] = "unixTime",
                });
            case ("admin", "POST") when id == "auth":
                if (json?["admin_pin"]?.GetValue<string>() != AdminPin)
                    return Error(401, "Unauthorized", "E_ADMIN_PIN", "wrong admin pin");
                _adminSessions[tss] = true;
                return Ok(new JsonObject());
            case ("admin", "POST") when id == "logout":
                _adminSessions.TryRemove(tss, out _);
                return Ok(new JsonObject());
            case ("client", "GET"):
                return _clients.TryGetValue($"{tss}:{id}", out var serial)
                    ? Ok(Client(tss, id, serial))
                    : Error(404, "Not Found", "E_CLIENT_NOT_FOUND", "client not found");
            case ("client", "PUT"):
                if (!_adminSessions.ContainsKey(tss))
                    return Error(401, "Unauthorized", "E_UNAUTHORIZED", "admin authentication required");
                var created = _clients.GetOrAdd($"{tss}:{id}", json!["serial_number"]!.GetValue<string>());
                return Ok(Client(tss, id, created));
            case ("tx", "PUT"):
                return Transaction(tss, id, json!, request.RequestUri.Query);
            default:
                return Error(404, "Not Found", "E_NOT_FOUND", "unknown route");
        }
    }

    private HttpResponseMessage Transaction(Guid tss, string txId, JsonObject body, string query)
    {
        var clientId = body["client_id"]!.GetValue<string>();
        if (!_clients.TryGetValue($"{tss}:{clientId}", out var clientSerial))
            return Error(400, "Bad Request", "E_CLIENT_NOT_FOUND", "client not registered");
        var revision = query.Contains("tx_revision=2") ? 2 : 1;
        var number = _counters.GetOrAdd($"{tss}:{txId}:number", _ => _counters.AddOrUpdate($"{tss}:tx", 1, (_, n) => n + 1));
        var counter = _counters.AddOrUpdate($"{tss}:{txId}:sig{revision}", _ => _counters.AddOrUpdate($"{tss}:sig", 1, (_, n) => n + 1), (_, n) => n);
        var finished = body["state"]!.GetValue<string>() == "FINISHED";
        var tx = new JsonObject
        {
            ["_id"] = txId, ["_type"] = "TRANSACTION", ["_env"] = "TEST", ["_version"] = "2.2.4", ["tss_id"] = tss.ToString(), ["client_id"] = clientId,
            ["client_serial_number"] = clientSerial, ["tss_serial_number"] = Serial(tss), ["state"] = finished ? "FINISHED" : "ACTIVE", ["number"] = number,
            ["revision"] = revision, ["latest_revision"] = revision, ["time_start"] = 1_792_404_000,
            ["log"] = new JsonObject { ["operation"] = finished ? "Finish" : "Start", ["timestamp"] = 1_792_404_000 + revision, ["timestamp_format"] = "unixTime" },
            ["signature"] = new JsonObject
            {
                ["value"] = Convert.ToBase64String(Encoding.UTF8.GetBytes($"sig-{txId}-{revision}")), ["algorithm"] = "ecdsa-plain-SHA256",
                ["counter"] = counter.ToString(System.Globalization.CultureInfo.InvariantCulture), ["public_key"] = "BPUBLICKEY",
            },
        };
        if (body["schema"] is JsonObject schema)
            tx["schema"] = schema.DeepClone();
        if (finished)
        {
            tx["time_end"] = 1_792_404_002;
            tx["qr_code_data"] = $"V0;{clientSerial};Kassenbeleg-V1;Beleg^0.00;{number};{counter};2026-10-18T10:00:00.000Z;2026-10-18T10:00:02.000Z;ecdsa-plain-SHA256;unixTime;sig;BPUBLICKEY";
        }
        return Ok(tx);
    }

    private static JsonObject Client(Guid tss, string id, string serial) => new()
    {
        ["_id"] = id, ["_type"] = "CLIENT", ["_env"] = "TEST", ["_version"] = "2.2.4", ["tss_id"] = tss.ToString(), ["serial_number"] = serial,
        ["state"] = "REGISTERED", ["time_creation"] = 1_792_390_000,
    };

    private static string Serial(Guid tss) => "SERIAL-" + tss.ToString("N")[..16];

    private static HttpResponseMessage Ok(JsonObject json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(int status, string error, string? code, string message)
    {
        var json = new JsonObject { ["status_code"] = status, ["error"] = error, ["message"] = message };
        if (code is not null)
            json["code"] = code;
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
