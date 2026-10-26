using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HandyNet.Persistence;

/// <summary>
/// Last line of defence for database contention that a service did not map itself: a deadlock, a serialization
/// failure or a lock timeout rolled the whole transaction back, so nothing was saved and the request can simply be
/// repeated. That is an expected outcome under load, not a server fault, so it is answered with 409
/// <c>request.concurrent_conflict</c> (and an exhausted connection pool or a server without free connection slots,
/// 53300/57P03, with 503 <c>service.busy</c>), both with
/// <c>Retry-After</c>, instead of a 500. Services still map the conflicts they expect (unique keys, idempotency) to
/// their own codes; this handler only sees what escaped them.
/// </summary>
public sealed class DbContentionExceptionHandler(IProblemDetailsService problemDetails, ILogger<DbContentionExceptionHandler> logger)
    : IExceptionHandler
{
    public const string ConflictCode = "request.concurrent_conflict";
    public const string BusyCode = "service.busy";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title) = Classify(exception) switch
        {
            DbContention.Conflict => (StatusCodes.Status409Conflict, ConflictCode,
                "The data was changed by another request at the same time. Nothing was saved; please try again."),
            DbContention.Busy => (StatusCodes.Status503ServiceUnavailable, BusyCode,
                "The service is busy right now. Nothing was saved; please try again in a moment."),
            _ => (0, null, null),
        };
        if (code is null || httpContext.Response.HasStarted)
            return false;

        logger.LogWarning(exception, "Database contention on {Method} {Path} answered with {Status} {Code}",
            httpContext.Request.Method, httpContext.Request.Path, status, code);
        httpContext.Response.StatusCode = status;
        httpContext.Response.Headers.RetryAfter = "1";
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Extensions = { ["code"] = code } },
        });
    }

    /// <summary>Whether an exception is transient database contention (see <see cref="DbErrors.IsTransientContention"/>).</summary>
    public static DbContention Classify(Exception exception)
    {
        if (exception.IsTransientContention())
            return DbContention.Conflict;
        if (exception.IsServerBusy())
            return DbContention.Busy;
        // Npgsql: "The connection pool has been exhausted ..." (wraps a TimeoutException).
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is NpgsqlException { InnerException: TimeoutException } && e.Message.Contains("pool", StringComparison.OrdinalIgnoreCase))
                return DbContention.Busy;
        return DbContention.None;
    }
}

public enum DbContention
{
    None,
    Conflict,
    Busy,
}
