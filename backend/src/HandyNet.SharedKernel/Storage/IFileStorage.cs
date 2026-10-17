namespace HandyNet.SharedKernel.Storage;

/// <summary>A stored object opened for reading. Dispose it to release the underlying stream/connection.</summary>
public sealed class StoredFile(Stream content, string contentType, long length) : IAsyncDisposable, IDisposable
{
    public Stream Content { get; } = content;
    public string ContentType { get; } = contentType;
    public long Length { get; } = length;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// Binary object storage (product images today; documents later). Implementations: local disk for development and
/// single-host setups, S3-compatible object storage for production (ADR-0011). Keys are relative, slash-separated
/// paths made of safe characters only (<see cref="StorageKey"/>); callers never pass user input as a key.
/// </summary>
public interface IFileStorage
{
    /// <summary>Stores (or replaces) the object. The stream is read to its end.</summary>
    Task PutAsync(string key, Stream content, string contentType, CancellationToken ct);

    /// <summary>Opens the object, or returns null when it does not exist.</summary>
    Task<StoredFile?> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>Deletes the object; deleting a missing object is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}

public static class StorageKey
{
    public const int MaxLength = 400;

    /// <summary>
    /// True for keys like <c>products/0193…/0193…/thumb.webp</c>: segments of [A-Za-z0-9._-], no empty, "." or ".."
    /// segments, no leading slash, no backslash. Prevents path traversal in the disk store and odd S3 keys.
    /// </summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength)
            return false;
        foreach (var segment in key.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
            if (!segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
                return false;
        }
        return true;
    }

    public static void EnsureValid(string key)
    {
        if (!IsValid(key))
            throw new ArgumentException($"Invalid storage key '{key}'.", nameof(key));
    }
}
