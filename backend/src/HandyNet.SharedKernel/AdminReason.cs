namespace HandyNet.SharedKernel;

/// <summary>Reason / note of a platform-admin action. Stored in the audit log; required for destructive actions.</summary>
public static class AdminReason
{
    public const int MaxLength = 1000;

    /// <summary>Validation error when a required reason is missing or too long; null when it is fine.</summary>
    public static Error? Require(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? Error.Validation("reason.required", "A reason is required.") : Check(reason);

    /// <summary>Validation error when an optional reason is too long; null when it is fine (or absent).</summary>
    public static Error? Check(string? reason) =>
        reason is not null && reason.Trim().Length > MaxLength
            ? Error.Validation("reason.too_long", $"The reason may have at most {MaxLength} characters.")
            : null;

    public static string? Normalize(string? reason) => string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
}
