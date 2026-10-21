using System.Text.Json;
using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Persistence;

/// <summary>
/// Explicit audit entries for platform-admin work on companies and user accounts (in <c>infra.audit_log</c>, append-only):
/// <list type="bullet">
/// <item>actions: who (admin), when, target, operation, reason and — for master data — the values before and after;</item>
/// <item>access log: an admin opened the detail view of a company or user account (accountability for reads).</item>
/// </list>
/// They complement the automatic entity entries of <see cref="AuditInterceptor"/>, which carry no reason. Entries are
/// staged in the unit of work and committed with the change they describe.
/// </summary>
public static class AdminAudit
{
    /// <summary><c>audit_log.action</c> of an access-log entry.</summary>
    public const string ReadAction = "AdminRead";

    /// <summary>Marks entries written by the platform administration (<c>changes->>'Source'</c>).</summary>
    public const string Source = "platform";

    private const int MaxActionLength = 20;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <param name="targetType">Entity type of the target, e.g. "User", "Organization", "Membership".</param>
    /// <param name="operation">Short PascalCase verb, prefixed with "Admin" in the log (max 20 characters in total).</param>
    public static void Action(HandyNetDbContext db, IClock clock, Guid adminId, string targetType, string targetId, Guid? organizationId,
        string operation, string? reason, object? before = null, object? after = null, object? details = null)
    {
        var changes = new Dictionary<string, object?>
        {
            ["Source"] = Source,
            ["Reason"] = AdminReason.Normalize(reason),
        };
        if (before is not null)
            changes["Before"] = before;
        if (after is not null)
            changes["After"] = after;
        if (details is not null)
            changes["Details"] = details;
        Add(db, clock, adminId, targetType, targetId, organizationId, "Admin" + operation, changes);
    }

    /// <summary>Access log: <paramref name="adminId"/> viewed <paramref name="view"/> of the target (no values are copied).</summary>
    public static void Access(HandyNetDbContext db, IClock clock, Guid adminId, string targetType, string targetId, Guid? organizationId, string view) =>
        Add(db, clock, adminId, targetType, targetId, organizationId, ReadAction, new Dictionary<string, object?>
        {
            ["Source"] = Source,
            ["View"] = view,
        });

    private static void Add(HandyNetDbContext db, IClock clock, Guid adminId, string targetType, string targetId, Guid? organizationId,
        string action, Dictionary<string, object?> changes)
    {
        if (action.Length > MaxActionLength)
            throw new ArgumentException($"Audit action '{action}' is longer than {MaxActionLength} characters.", nameof(action));
        db.Set<AuditEntry>().Add(new AuditEntry
        {
            OccurredAt = clock.UtcNow,
            UserId = adminId,
            OrganizationId = organizationId,
            EntityType = targetType,
            EntityId = targetId,
            Action = action,
            Changes = JsonSerializer.Serialize(changes, Json),
        });
    }
}
