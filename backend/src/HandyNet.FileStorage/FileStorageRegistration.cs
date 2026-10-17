using HandyNet.SharedKernel.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HandyNet.FileStorage;

public enum FileStorageProvider
{
    /// <summary>Files below <c>FileStorage:Local:RootPath</c> (default <c>App_Data/files</c> under the content root).</summary>
    Local,

    /// <summary>S3-compatible object storage (<c>FileStorage:S3</c>).</summary>
    S3,
}

public sealed class FileStorageOptions
{
    public const string Section = "FileStorage";

    public FileStorageProvider Provider { get; set; } = FileStorageProvider.Local;
    public LocalStorageOptions Local { get; set; } = new();
    public S3StorageOptions S3 { get; set; } = new();
}

public sealed class LocalStorageOptions
{
    public string? RootPath { get; set; }
}

/// <summary>Non-secret settings live in appsettings; AccessKey/SecretKey only in user-secrets or the environment.</summary>
public sealed class S3StorageOptions
{
    /// <summary>Endpoint of an S3-compatible service, e.g. https://fsn1.your-objectstorage.com. Empty = AWS S3.</summary>
    public string? ServiceUrl { get; set; }
    public string? Region { get; set; }
    public string? Bucket { get; set; }

    /// <summary>Optional key prefix, e.g. "prod/" when several environments share a bucket.</summary>
    public string? KeyPrefix { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool ForcePathStyle { get; set; } = true;

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Bucket))
            yield return "FileStorage:S3:Bucket is required.";
        if (string.IsNullOrWhiteSpace(ServiceUrl) && string.IsNullOrWhiteSpace(Region))
            yield return "FileStorage:S3:ServiceUrl or FileStorage:S3:Region is required.";
        if (string.IsNullOrWhiteSpace(AccessKey) != string.IsNullOrWhiteSpace(SecretKey))
            yield return "FileStorage:S3:AccessKey and SecretKey must be configured together (or both left empty for the default credential chain).";
    }
}

public static class FileStorageRegistration
{
    public static IServiceCollection AddHandyNetFileStorage(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<FileStorageOptions>().Bind(configuration.GetSection(FileStorageOptions.Section))
            .Validate(o => o.Provider != FileStorageProvider.S3 || !o.S3.Problems().Any(),
                "FileStorage:S3 is incomplete: Bucket, ServiceUrl or Region, and AccessKey+SecretKey together (or neither).")
            .ValidateOnStart();

        services.AddSingleton<IFileStorage>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<FileStorageOptions>>().Value;
            return options.Provider switch
            {
                FileStorageProvider.S3 => new S3FileStorage(options.S3),
                _ => new LocalDiskFileStorage(string.IsNullOrWhiteSpace(options.Local.RootPath)
                    ? Path.Combine(environment.ContentRootPath, "App_Data", "files")
                    : options.Local.RootPath),
            };
        });
        return services;
    }
}
