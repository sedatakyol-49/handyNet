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
    private const string AppLogin = "handynet_test_app";

    private PostgreSqlContainer? _container;
    private string _adminConnection = null!;
    private string _databaseName = null!;

    public string OwnerConnectionString { get; private set; } = null!;
    public string AppConnectionString { get; private set; } = null!;
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public CapturingEmailSender Emails { get; } = new();

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
                DO $$ BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{AppLogin}') THEN
                        CREATE ROLE {AppLogin} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{appPassword}';
                    ELSE
                        ALTER ROLE {AppLogin} LOGIN NOSUPERUSER NOBYPASSRLS PASSWORD '{appPassword}';
                    END IF;
                END $$;
                GRANT handynet_app TO {AppLogin};
                GRANT CONNECT ON DATABASE {_databaseName} TO {AppLogin};
                """);
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(_adminConnection)
        {
            Database = _databaseName,
            Username = AppLogin,
            Password = appPassword,
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
            web.UseSetting("Shipping:EnabledCarriers:0", "Manual");
            web.UseSetting("Shipping:EnabledCarriers:1", "Simulated");
            web.UseSetting("Shipping:SimulatedTransitMinutes", "0");
            web.UseSetting("Payments:EnabledProviders:0", "Simulated");
            web.UseSetting("Pos:FiscalSigner", "Disabled");
            web.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(Emails));
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
        }
        if (_container is not null)
            await _container.DisposeAsync();
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

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        Sent.Enqueue((to, subject, body));
        return Task.CompletedTask;
    }
}
