using System.Data.Common;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace HandyNet.Persistence;

/// <summary>
/// Sets the PostgreSQL session variable <c>app.current_org</c> every time EF opens a connection.
/// Row-Level Security policies compare tenant columns against this value, so isolation holds
/// even if application code forgets a WHERE clause. An empty value means "no tenant": policies
/// then match no tenant-owned rows. Npgsql resets session state (DISCARD ALL) when a connection
/// returns to the pool, and we overwrite the value on every open regardless.
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public const string SettingName = "app.current_org";

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
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT set_config('{SettingName}', @org, false)";
        command.Parameters.Add(new NpgsqlParameter("org", tenant.OrganizationId?.ToString() ?? string.Empty));
        return command;
    }
}
