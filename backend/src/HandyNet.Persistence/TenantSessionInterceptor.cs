using System.Data.Common;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HandyNet.Persistence;

/// <summary>
/// Sets the PostgreSQL session variable <c>app.current_org</c> every time EF opens a connection.
/// Row-Level Security policies compare tenant columns against this value, so isolation holds
/// even if application code forgets a WHERE clause. An empty value means "no tenant": policies
/// then match no tenant-owned rows. Npgsql resets session state (DISCARD ALL) when a connection
/// returns to the pool, and we overwrite the value on every open regardless.
/// <para>
/// Also sets <c>app.platform_admin</c> ('on' only for an authenticated platform administrator). Platform-only tables
/// such as <c>inventory.imei_blacklist</c> admit rows only in such sessions (<c>infra.is_platform_admin()</c>, ADR-0019), so tenant
/// code paths cannot read them even by mistake.
/// </para>
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenant, IServiceProvider services) : DbConnectionInterceptor
{
    public const string SettingName = "app.current_org";
    public const string PlatformAdminSetting = "app.platform_admin";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var user = services.GetService<ICurrentUser>();
        var isAdmin = user is { IsAuthenticated: true, IsPlatformAdmin: true };
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT set_config('{SettingName}', @org, false), set_config('{PlatformAdminSetting}', @admin, false)";
        command.Parameters.Add(new NpgsqlParameter("org", tenant.OrganizationId?.ToString() ?? string.Empty));
        command.Parameters.Add(new NpgsqlParameter("admin", isAdmin ? "on" : "off"));
        return command;
    }
}
