using System.Text.Json;
using HandyNet.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace HandyNet.UnitTests;

public sealed class DbContentionTests
{
    private static PostgresException Pg(string sqlState) => new("message", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData("40001")] // serialization_failure
    [InlineData("40P01")] // deadlock_detected
    [InlineData("55P03")] // lock_not_available
    public void Transaction_aborts_from_concurrent_work_are_contention(string sqlState)
    {
        Assert.Equal(DbContention.Conflict, DbContentionExceptionHandler.Classify(Pg(sqlState)));
        Assert.Equal(DbContention.Conflict, DbContentionExceptionHandler.Classify(new DbUpdateException("save", Pg(sqlState))));
        Assert.Equal(DbContention.Conflict, DbContentionExceptionHandler.Classify(new InvalidOperationException("wrapped", Pg(sqlState))));
    }

    [Theory]
    [InlineData("23505")] // unique_violation: services map these to their own codes
    [InlineData("23503")]
    [InlineData("42501")]
    public void Other_database_errors_are_not_contention(string sqlState) =>
        Assert.Equal(DbContention.None, DbContentionExceptionHandler.Classify(new DbUpdateException("save", Pg(sqlState))));

    [Theory]
    [InlineData("53300")] // too_many_connections: no free connection slot on the server
    [InlineData("57P03")] // cannot_connect_now
    public void A_server_without_free_connections_is_busy(string sqlState) =>
        Assert.Equal(DbContention.Busy, DbContentionExceptionHandler.Classify(new InvalidOperationException("transient", Pg(sqlState))));

    [Fact]
    public void An_exhausted_connection_pool_is_busy_but_other_timeouts_are_not()
    {
        var exhausted = new NpgsqlException(
            "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' (currently 15 seconds) in your connection string.",
            new TimeoutException());
        Assert.Equal(DbContention.Busy, DbContentionExceptionHandler.Classify(exhausted));
        Assert.Equal(DbContention.None, DbContentionExceptionHandler.Classify(new NpgsqlException("Exception while reading from stream", new TimeoutException())));
        Assert.Equal(DbContention.None, DbContentionExceptionHandler.Classify(new InvalidOperationException("bug")));
    }

    [Theory]
    [InlineData("40P01", 409, "request.concurrent_conflict")]
    [InlineData("40001", 409, "request.concurrent_conflict")]
    [InlineData("53300", 503, "service.busy")]
    public async Task Contention_is_answered_with_a_coded_problem_and_retry_after(string sqlState, int status, string code)
    {
        var (handler, context) = Create();

        var handled = await handler.TryHandleAsync(context, new DbUpdateException("save", Pg(sqlState)), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Other_exceptions_are_left_to_the_default_500_handler()
    {
        var (handler, context) = Create();
        Assert.False(await handler.TryHandleAsync(context, new DbUpdateException("save", Pg("23505")), CancellationToken.None));
        Assert.False(await handler.TryHandleAsync(context, new InvalidOperationException("bug"), CancellationToken.None));
        Assert.Equal(200, context.Response.StatusCode); // untouched
    }

    private static (DbContentionExceptionHandler Handler, DefaultHttpContext Context) Create()
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        context.Request.Headers.Accept = "application/json";
        var handler = new DbContentionExceptionHandler(services.GetRequiredService<IProblemDetailsService>(),
            NullLogger<DbContentionExceptionHandler>.Instance);
        return (handler, context);
    }
}
