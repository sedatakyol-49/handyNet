using HandyNet.SharedKernel;

namespace HandyNet.Modules.Identity.Contracts;

/// <summary>Supported email languages (the seven UI locales). German is the platform default and the fallback.</summary>
public static class EmailLanguage
{
    public const string German = "de";
    public const string English = "en";

    public static readonly IReadOnlyList<string> All = ["de", "en", "tr", "es", "ru", "ar", "fa"];

    /// <summary>Primary subtag of <paramref name="language"/> ("en-GB" becomes "en") when supported, otherwise German.</summary>
    public static string Normalize(string? language)
    {
        var primary = language?.Trim().Split('-', '_')[0].ToLowerInvariant();
        return primary is not null && All.Contains(primary) ? primary : German;
    }

    public static bool IsSupported(string? language) => language is null || All.Contains(language);
}

/// <summary>
/// A user needs a (new) email verification link. The payload deliberately contains no token: the consumer asks
/// <see cref="IUserAccounts.IssueEmailVerificationTokenAsync"/> for one, so no plaintext token is stored in the outbox.
/// </summary>
public sealed record EmailVerificationRequested(Guid UserId, string Language) : IDomainEvent;

/// <summary>A user asked for a password reset link. Like <see cref="EmailVerificationRequested"/>, carries no token.</summary>
public sealed record PasswordResetRequested(Guid UserId, string Language) : IDomainEvent;
