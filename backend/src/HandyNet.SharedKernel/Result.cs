namespace HandyNet.SharedKernel;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,

    /// <summary>The presented credential (e.g. a refresh token) is not valid; HTTP 401.</summary>
    Unauthorized,
}

public sealed record Error(string Code, string Message, ErrorKind Kind = ErrorKind.Validation)
{
    /// <summary>Machine-readable details for clients (e.g. offerId, availableQuantity). Serialized into the problem response.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; init; }

    public Error WithDetails(IReadOnlyDictionary<string, object?> details) => this with { Details = details };

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);
    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);
    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);
    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);
    public static Error Unauthorized(string code, string message) => new(code, message, ErrorKind.Unauthorized);
}

public class Result
{
    protected Result(Error? error) => Error = error;

    public Error? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result Success() => new(null);
    public static Result Failure(Error error) => new(error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T? value, Error? error) : base(error) => _value = value;

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result has no value: {Error!.Code}");

    public static Result<T> Success(T value) => new(value, null);
    public static new Result<T> Failure(Error error) => new(default, error);

    public static implicit operator Result<T>(T value) => Success(value);
    public static implicit operator Result<T>(Error error) => Failure(error);
}
