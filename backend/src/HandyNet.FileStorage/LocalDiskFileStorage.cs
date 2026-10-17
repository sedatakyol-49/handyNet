using HandyNet.SharedKernel.Storage;

namespace HandyNet.FileStorage;

/// <summary>
/// Stores objects as files below one root directory (development, tests, single-host setups with a backed-up volume).
/// Keys are validated (<see cref="StorageKey"/>) and the resolved path must stay inside the root, so a key can never
/// escape it. Writes go to a temporary file first and are then moved into place, so readers never see half a file.
/// </summary>
public sealed class LocalDiskFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalDiskFileStorage(string rootPath)
    {
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
    }

    public string RootPath => _root;

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public Task<StoredFile?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (!File.Exists(path))
            return Task.FromResult<StoredFile?>(null);
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);
            return Task.FromResult<StoredFile?>(new StoredFile(stream, ContentTypes.FromKey(key), stream.Length));
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<StoredFile?>(null); // deleted between the check and the open
        }
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        StorageKey.EnsureValid(key);
        var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Storage key '{key}' resolves outside the storage root.", nameof(key));
        return path;
    }
}

internal static class ContentTypes
{
    public static string FromKey(string key) => Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };
}
