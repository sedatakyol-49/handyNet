using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using HandyNet.SharedKernel.Storage;

namespace HandyNet.FileStorage;

/// <summary>
/// S3-compatible object storage (AWS S3, Hetzner Object Storage, MinIO, …), configured entirely from
/// <c>FileStorage:S3</c>. Credentials come from configuration supplied by the environment (user-secrets, environment
/// variables, a secret store) or, when not configured, from the AWS SDK's default credential chain — never from the
/// repository. NOTE: this adapter is compiled and unit-tested for its configuration only; it has not been verified
/// against a real bucket yet (see ADR-0011).
/// </summary>
public sealed class S3FileStorage : IFileStorage, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;

    public S3FileStorage(S3StorageOptions options)
        : this(CreateClient(options), options.Bucket!, options.KeyPrefix)
    {
    }

    internal S3FileStorage(IAmazonS3 client, string bucket, string? keyPrefix)
    {
        _client = client;
        _bucket = bucket;
        _prefix = string.IsNullOrWhiteSpace(keyPrefix) ? string.Empty : keyPrefix.Trim('/') + "/";
    }

    internal static AmazonS3Config CreateConfig(S3StorageOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            // Many S3-compatible services do not support the newer default integrity checksums.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
            if (!string.IsNullOrWhiteSpace(options.Region))
                config.AuthenticationRegion = options.Region;
        }
        else if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.Region);
        }
        return config;
    }

    private static AmazonS3Client CreateClient(S3StorageOptions options)
    {
        var config = CreateConfig(options);
        return string.IsNullOrWhiteSpace(options.AccessKey)
            ? new AmazonS3Client(config) // default credential chain (env vars, instance profile, …)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
    }

    public async Task PutAsync(string key, Stream content, string contentType, CancellationToken ct)
    {
        StorageKey.EnsureValid(key);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = _prefix + key,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        }, ct);
    }

    public async Task<StoredFile?> OpenReadAsync(string key, CancellationToken ct)
    {
        StorageKey.EnsureValid(key);
        try
        {
            var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = _prefix + key }, ct);
            return new StoredFile(response.ResponseStream, response.Headers.ContentType ?? ContentTypes.FromKey(key),
                response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        StorageKey.EnsureValid(key);
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = _prefix + key }, ct);
    }

    public void Dispose() => _client.Dispose();
}
