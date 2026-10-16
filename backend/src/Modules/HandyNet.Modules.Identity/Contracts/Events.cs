using HandyNet.SharedKernel;

namespace HandyNet.Modules.Identity.Contracts;

/// <summary>Supported email languages. German is the platform default.</summary>
public static class EmailLanguage
{
    public const string German = "de";
    public const string English = "en";

    public static string Normalize(string? language) =>
        language?.Trim().StartsWith(English, StringComparison.OrdinalIgnoreCase) == true ? English : German;

    public static bool IsSupported(string? language) => language is null or German or English;
}

/// <summary>
/// A user needs a (new) email verification link. The payload deliberately contains no token: the consumer asks
/// <see cref="IUserAccounts.IssueEmailVerificationTokenAsync"/> for one, so no plaintext token is stored in the outbox.
/// </summary>
public sealed record EmailVerificationRequested(Guid UserId, string Language) : IDomainEvent;

/// <summary>A user asked for a password reset link. Like <see cref="EmailVerificationRequested"/>, carries no token.</summary>
public sealed record PasswordResetRequested(Guid UserId, string Language) : IDomainEvent;
