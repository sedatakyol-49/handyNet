namespace HandyNet.SharedKernel;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public static class Paging
{
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize) =>
        (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, MaxPageSize));
}

public static class Text
{
    /// <summary>Normalizes an identifier (SKU, MPN, brand) for equality matching: trimmed, upper-case, no inner whitespace/dashes.</summary>
    public static string NormalizeKey(string value) =>
        new(value.Trim().ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_' && c != '.').ToArray());

    /// <summary>Escapes LIKE/ILIKE wildcards in user search input.</summary>
    public static string LikePattern(string search) =>
        "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
