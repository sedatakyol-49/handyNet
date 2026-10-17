using Microsoft.AspNetCore.Http.Metadata;

namespace HandyNet.SharedKernel.Web;

/// <summary>Per-endpoint request body limit (Kestrel reads <see cref="IRequestSizeLimitMetadata"/>): <c>.WithMetadata(new RequestBodyLimit(n))</c>.</summary>
public sealed class RequestBodyLimit(long maxBytes) : IRequestSizeLimitMetadata
{
    public long? MaxRequestBodySize => maxBytes;
}
