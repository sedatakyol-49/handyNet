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
            ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
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
        // Added next to "errors" (same keys, same order) so clients can translate by stable code instead of showing server text.
        var errorCodes = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(ToFieldError).ToArray());
        return Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity,
            extensions: new Dictionary<string, object?> { ["code"] = "validation.failed", ["errorCodes"] = errorCodes });
    }

    /// <summary>Stable client-facing codes for FluentValidation built-ins; custom <c>validation.*</c> codes pass through.</summary>
    private static readonly Dictionary<string, string> BuiltInCodes = new(StringComparer.Ordinal)
    {
        ["NotEmptyValidator"] = "validation.required",
        ["NotNullValidator"] = "validation.required",
        ["MaximumLengthValidator"] = "validation.maxLength",
        ["MinimumLengthValidator"] = "validation.minLength",
        ["LengthValidator"] = "validation.length",
        ["ExactLengthValidator"] = "validation.exactLength",
        ["EmailValidator"] = "validation.email",
        ["AspNetCoreCompatibleEmailValidator"] = "validation.email",
        ["GreaterThanValidator"] = "validation.greaterThan",
        ["GreaterThanOrEqualValidator"] = "validation.min",
        ["LessThanValidator"] = "validation.lessThan",
        ["LessThanOrEqualValidator"] = "validation.max",
        ["InclusiveBetweenValidator"] = "validation.range",
        ["ExclusiveBetweenValidator"] = "validation.rangeExclusive",
        ["EnumValidator"] = "validation.enum",
        ["PrecisionScaleValidator"] = "validation.precision",
        ["EqualValidator"] = "validation.equal",
        ["NotEqualValidator"] = "validation.notEqual",
    };

    private static readonly string[] ParamNames =
        ["MaxLength", "MinLength", "ComparisonValue", "From", "To", "ExpectedPrecision", "ExpectedScale", "Max", "Values"];

    private static object ToFieldError(FluentValidation.Results.ValidationFailure e)
    {
        string? code = null;
        if (e.ErrorCode is { Length: > 0 } c)
            code = c.StartsWith("validation.", StringComparison.Ordinal) ? c : BuiltInCodes.GetValueOrDefault(c);
        var parameters = new Dictionary<string, string>();
        if (code is not null && e.FormattedMessagePlaceholderValues is { } values)
            foreach (var name in ParamNames)
                if (values.TryGetValue(name, out var v) && v is IFormattable or string)
                    parameters[char.ToLowerInvariant(name[0]) + name[1..]] = v is IFormattable f
                        ? f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                        : (string)v;
        return new { code, @params = parameters };
    }
}
