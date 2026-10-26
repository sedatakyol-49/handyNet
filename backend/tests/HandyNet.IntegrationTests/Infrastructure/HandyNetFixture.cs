using System.Security.Cryptography;
using System.Text.Json;
using HandyNet.Migrations;
using Microsoft.AspNetCore.Hosting;
using System.Collections.Concurrent;
using HandyNet.Modules.Notifications.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace HandyNet.IntegrationTests.Infrastructure;

/// <summary>
/// Real PostgreSQL for integration tests. Uses, in order:
/// 1. env HANDYNET_TEST_PG_ADMIN (superuser connection string, e.g. in CI service containers),
/// 2. testsettings.local.json { "AdminConnection": "..." } (developer machines, git-ignored),
/// 3. a Testcontainers postgres:16 container (needs Docker).
/// Each test run creates its own throw-away database, applies the real migrations as owner and runs
/// the API as a separate login role that inherits handynet_app — so Row-Level Security is enforced
/// exactly as in production.
/// </summary>
public sealed class HandyNetFixture : IAsyncLifetime
{
    public const string AdminEmail = "admin@test.handynet";
    public const string AdminPassword = "Admin-Passwort-123";
    // Unique per run, not a shared name: login roles are cluster-wide, and a shared role whose password every run
    // resets breaks suites running concurrently on the same server (other worktrees, CI jobs) with 28P01 as soon as
    // their connection pool opens a new connection, e.g. during the concurrency tests' request bursts.
    private readonly string _appLogin = $"handynet_test_app_{Guid.NewGuid():N}"[..34];

    private PostgreSqlContainer? _container;
    private string _adminConnection = null!;
    private string _databaseName = null!;

    public string OwnerConnectionString { get; private set; } = null!;

    /// <summary>Superuser connection (maintenance database); for tests that need a database of their own.</summary>
    public string AdminConnectionString => _adminConnection;
    public string AppConnectionString { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public CapturingEmailSender Emails { get; } = new();

    public FakeFiskalyServer Fiskaly { get; } = new();

    /// <summary>Throw-away root of the local file storage (product images) for this test run.</summary>
    public string FileRoot { get; } = Path.Combine(Path.GetTempPath(), "handynet-test-files", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _adminConnection = Environment.GetEnvironmentVariable("HANDYNET_TEST_PG_ADMIN") ?? ReadLocalSettings() ?? await StartContainerAsync();
        _databaseName = $"handynet_test_{Guid.NewGuid():N}"[..40];
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        await using (var admin = new NpgsqlConnection(_adminConnection))
        {
            await admin.OpenAsync();
            await Execute(admin, $"CREATE DATABASE {_databaseName}");
        }

        OwnerConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _databaseName }.ConnectionString;
        await DatabaseMigrator.MigrateAsync(OwnerConnectionString); // creates role handynet_app (NOLOGIN) if missing

        await using (var owner = new NpgsqlConnection(OwnerConnectionString))
        {
            await owner.OpenAsync();
            await Execute(owner, $"""
                CREATE ROLE {_appLogin} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{appPassword}';
                GRANT handynet_app TO {_appLogin};
                GRANT CONNECT ON DATABASE {_databaseName} TO {_appLogin};
                """);
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection)
        {
            Database = _databaseName,
            Username = _appLogin,
            Password = appPassword,
            // Several suites (worktrees, CI jobs) share one server. Npgsql's default of 100 lets one test host's request
            // bursts take every connection slot (53300 for everyone else); with a bounded pool, bursts queue in the pool
            // and still run up to 20 transactions concurrently, which is what the concurrency tests need.
            MaxPoolSize = 20,
        }.ConnectionString;

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Testing");
            // Same DI checks as Development: catches singletons that capture scoped services at startup.
            web.UseDefaultServiceProvider(o =>
            {
                o.ValidateScopes = true;
                o.ValidateOnBuild = true;
            });
            web.UseSetting("ConnectionStrings:HandyNet", AppConnectionString);
            web.UseSetting("Jwt:SigningKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
            web.UseSetting("Bootstrap:AdminEmail", AdminEmail);
            web.UseSetting("Bootstrap:AdminPassword", AdminPassword);
            web.UseSetting("RateLimits:AuthPerMinute", "10000");
            web.UseSetting("RateLimits:RefreshPerMinute", "10000");
            web.UseSetting("RateLimits:AccountEmailsPerHour", "10000");
            web.UseSetting("RateLimits:PosPinPerMinute", "10000");
            web.UseSetting("RateLimits:PublicReceiptsPerMinute", "10000");
            web.UseSetting("RateLimits:AnkaufCheckInPerMinute", "10000");
            web.UseSetting("Shipping:EnabledCarriers:0", "Manual");
            web.UseSetting("Shipping:EnabledCarriers:1", "Simulated");
            web.UseSetting("Shipping:SimulatedTransitMinutes", "0");
            web.UseSetting("Payments:EnabledProviders:0", "Simulated");
            web.UseSetting("Pos:FiscalSigner", "Disabled");
            web.UseSetting("Pos:Fiscal:RetryDelayMs", "0");
            // Tests issue invoices explicitly (admin endpoint / job instance with a fixed clock).
            web.UseSetting("Invoicing:AutoIssue", "false");
            web.UseSetting("FileStorage:Provider", "Local");
            web.UseSetting("FileStorage:Local:RootPath", FileRoot);
            web.UseSetting("Pos:Fiskaly:RetryBaseDelayMs", "0");
            web.ConfigureTestServices(services =>
            {
                services.AddSingleton<IEmailSender>(Emails);
                // Tests never reach the real fiskaly service: the adapter talks to an in-process fake (FiskalySandboxTests is the opt-in exception).
                services.AddHttpClient<HandyNet.Modules.Pos.Infrastructure.FiskalyApiClient>().ConfigurePrimaryHttpMessageHandler(() => Fiskaly);
            });
        });
        _ = Factory.Server; // start host now (runs admin bootstrap)
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using (var admin = new NpgsqlConnection(_adminConnection))
        {
            await admin.OpenAsync();
            await Execute(admin, $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE)");
            await Execute(admin, $"DROP ROLE IF EXISTS {_appLogin}");
        }
        if (_container is not null)
            await _container.DisposeAsync();
        if (Directory.Exists(FileRoot))
            Directory.Delete(FileRoot, recursive: true);
    }

    public ApiClient CreateClient() => new(Factory.CreateClient());

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string? ReadLocalSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testsettings.local.json");
        if (!File.Exists(path))
            return null;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("AdminConnection", out var value) ? value.GetString() : null;
    }

    private async Task<string> StartContainerAsync()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _container.StartAsync();
        return _container.GetConnectionString();
    }
}

[CollectionDefinition(Name)]
public sealed class HandyNetCollection : ICollectionFixture<HandyNetFixture>
{
    public const string Name = "HandyNet";
}

public sealed class CapturingEmailSender : IEmailSender
{
    public ConcurrentQueue<(string To, string Subject, string Body)> Sent { get; } = new();

    /// <summary>HTML alternatives of sent emails (e-receipts), by recipient and subject.</summary>
    public ConcurrentQueue<(string To, string Subject, string Html)> SentHtml { get; } = new();

    /// <summary>Attachments of sent emails (quotation / invoice PDFs).</summary>
    public ConcurrentQueue<(string To, string Subject, string FileName, string ContentType, byte[] Content)> SentAttachments { get; } = new();

    public Task SendAsync(string to, string subject, string body, string? htmlBody, HandyNet.Modules.Notifications.Domain.EmailAttachment attachment,
        CancellationToken ct)
    {
        SentAttachments.Enqueue((to, subject, attachment.FileName, attachment.ContentType, attachment.Content));
        return SendAsync(to, subject, body, htmlBody, ct);
    }

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        Sent.Enqueue((to, subject, body));
        return Task.CompletedTask;
    }

    public Task SendAsync(string to, string subject, string body, string? htmlBody, CancellationToken ct)
    {
        if (htmlBody is not null)
            SentHtml.Enqueue((to, subject, htmlBody));
        return SendAsync(to, subject, body, ct);
    }
}
