using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HandyNet.Persistence;

public static class DbErrors
{
    public const string UniqueViolation = "23505";
    public const string CheckViolation = "23514";
    public const string ForeignKeyViolation = "23503";
    public const string InsufficientPrivilege = "42501";

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
