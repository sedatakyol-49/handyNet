using FluentValidation;
using Microsoft.AspNetCore.Http;

namespace HandyNet.SharedKernel.Web;

public static class Problems
{
    public static IResult From(Error error) => Results.Problem(
        statusCode: error.Kind switch
        {
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status422UnprocessableEntity,
        },
        title: error.Message,
        extensions: error.Details is null
            ? new Dictionary<string, object?> { ["code"] = error.Code }
            : new Dictionary<string, object?>(error.Details) { ["code"] = error.Code });

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : From(result.Error!);

    public static IResult ToHttp(this Result result, Func<IResult> onSuccess) =>
        result.IsSuccess ? onSuccess() : From(result.Error!);

    /// <summary>Runs a FluentValidation validator; returns a 422 problem response, or null when valid.</summary>
    public static async Task<IResult?> ProblemIfInvalidAsync<T>(this IValidator<T> validator, T request, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(request, ct);
        if (result.IsValid) return null;
        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = "validation.failed" });
    }
}
