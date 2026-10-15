using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace HandyNet.IntegrationTests.Infrastructure;

public sealed record ApiResponse(HttpStatusCode Status, JsonNode? Body)
{
    public string? Code => Body?["code"]?.GetValue<string>();

    public JsonNode Json => Body ?? throw new InvalidOperationException($"Empty body (HTTP {(int)Status}).");

    public ApiResponse Expect(HttpStatusCode expected)
    {
        Assert.True(Status == expected, $"Expected {(int)expected} but got {(int)Status}: {Body?.ToJsonString()}");
        return this;
    }
}

/// <summary>Thin JSON client acting as one user, optionally on behalf of one organization.</summary>
public sealed class ApiClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string? Token { get; private set; }
    public Guid? OrganizationId { get; set; }

    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, object? body = null, HttpContent? content = null,
        IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (OrganizationId is { } org)
            request.Headers.Add("X-Organization-Id", org.ToString());
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
            request.Headers.Add(key, value);
        request.Content = content ?? (body is null ? null : JsonContent.Create(body, options: Json));

        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new ApiResponse(response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    public Task<ApiResponse> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
    public Task<ApiResponse> PostAsync(string path, object? body = null, IDictionary<string, string>? headers = null) =>
        SendAsync(HttpMethod.Post, path, body ?? new { }, headers: headers);
    public Task<ApiResponse> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);

    public async Task LoginAsync(string email, string password)
    {
        var response = (await PostAsync("/api/auth/login", new { email, password })).Expect(HttpStatusCode.OK);
        Token = response.Json["accessToken"]!.GetValue<string>();
    }

    public Task<ApiResponse> UploadImportAsync(string fileName, string csv, object? options = null, object? mapping = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);
        if (options is not null)
            form.Add(new StringContent(JsonSerializer.Serialize(options, Json)), "options");
        if (mapping is not null)
            form.Add(new StringContent(JsonSerializer.Serialize(mapping, Json)), "mapping");
        return SendAsync(HttpMethod.Post, "/api/imports", content: form);
    }
}
