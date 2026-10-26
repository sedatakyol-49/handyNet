using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HandyNet.Persistence;

public static class DbErrors
{
    public const string UniqueViolation = "23505";
    public const string CheckViolation = "23514";
    public const string ForeignKeyViolation = "23503";
    public const string InsufficientPrivilege = "42501";

    /// <summary>Raised by the <c>inventory.stock_units</c> trigger when a blacklisted IMEI would enter stock or be sold (ADR-0019).</summary>
    public const string ImeiBlacklisted = "HNB01";

    public const string SerializationFailure = "40001";
    public const string DeadlockDetected = "40P01";
    public const string LockNotAvailable = "55P03";

    /// <summary>
    /// A deadlock, serialization failure or lock timeout: PostgreSQL aborted the transaction because of concurrent
    /// work, so nothing was saved and repeating the whole unit of work can succeed.
    /// </summary>
    public static bool IsTransientContention(this Exception exception) =>
        Find(exception) is { SqlState: SerializationFailure or DeadlockDetected or LockNotAvailable };

    public const string TooManyConnections = "53300";
    public const string CannotConnectNow = "57P03";

    /// <summary>
    /// The server refused a new connection (all connection slots taken, or starting up / shutting down): nothing
    /// ran, and the request can be repeated once load drops.
    /// </summary>
    public static bool IsServerBusy(this Exception exception) =>
        Find(exception) is { SqlState: TooManyConnections or CannotConnectNow };

    public static bool IsImeiBlacklisted(this Exception exception) => Find(exception) is { SqlState: ImeiBlacklisted };

    public static bool IsUniqueViolation(this Exception exception, string? constraintName = null) =>
        Find(exception) is { SqlState: UniqueViolation } pg
        && (constraintName is null || string.Equals(pg.ConstraintName, constraintName, StringComparison.Ordinal));

    public static PostgresException? Find(Exception exception) => exception switch
    {
        PostgresException pg => pg,
        DbUpdateException { InnerException: { } inner } => Find(inner),
        _ when exception.InnerException is { } inner => Find(inner),
        _ => null,
    };
}
