using System.Globalization;

namespace HandyNet.Modules.Notifications.Domain;

/// <summary>
/// Plain-text emails in the seven UI locales (<see cref="EmailText.Locales"/>). German is the platform default and the
/// fallback for unknown or missing languages. The language comes from the request the user sent (account emails); events
/// without a language render German. Templates take only the data they print, so they stay unit-testable.
/// </summary>
public static class EmailTemplates
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Euro(long minor) => (minor / 100m).ToString("#,##0.00", German) + " €";

    private static string P(string key, string language, params (string, string)[] values) => EmailText.Get(key, language, values);

    public static RenderedEmail OrderPlacedForSeller(string sellerName, string buyerName, long grossMinor, string link, string language = "de") => new(
        P("order.placed.subject", language, ("buyer", buyerName)),
        string.Join("\n\n",
            P("greeting", language),
            P("order.placed.body", language, ("buyer", buyerName), ("seller", sellerName), ("amount", Euro(grossMinor))),
            link,
            P("signature", language)));

    public static RenderedEmail OrderStatusForCounterparty(string status, string otherPartyName, string? reason, string link, string language = "de")
    {
        var known = status is "Confirmed" or "Shipped" or "Rejected" or "Cancelled";
        var subject = known
            ? P($"order.status.{status}.subject", language)
            : P("order.status.other.subject", language, ("status", status));
        var sentence = known
            ? P($"order.status.{status}.sentence", language, ("other", otherPartyName))
            : P("order.status.other.sentence", language, ("status", status));

        var reasonLine = string.IsNullOrWhiteSpace(reason) ? string.Empty : "\n" + P("order.status.reason", language, ("reason", reason)) + "\n";
        return new RenderedEmail(subject,
            $"{P("greeting", language)}\n\n{sentence}\n{reasonLine}\n{P("order.status.details", language, ("link", link))}\n\n{P("signature", language)}");
    }

    /// <summary>Template ids of account emails. Their bodies hold a one-time link and are redacted after delivery.</summary>
    public const string EmailVerificationTemplate = "auth.email_verification";
    public const string PasswordResetTemplate = "auth.password_reset";

    /// <param name="language">One of <see cref="EmailText.Locales"/>; anything else renders German.</param>
    public static RenderedEmail EmailVerification(string displayName, string link, TimeSpan validFor, string language) => new(
        P("verify.subject", language),
        string.Join("\n\n",
            P("greeting.named", language, ("name", displayName)),
            P("verify.intro", language),
            link,
            P("verify.outro", language, ("validity", EmailText.Duration(validFor, language))),
            P("signature", language)));

    /// <param name="language">One of <see cref="EmailText.Locales"/>; anything else renders German.</param>
    public static RenderedEmail PasswordReset(string displayName, string link, TimeSpan validFor, string language) => new(
        P("reset.subject", language),
        string.Join("\n\n",
            P("greeting.named", language, ("name", displayName)),
            P("reset.intro", language),
            link,
            P("reset.outro", language, ("validity", EmailText.Duration(validFor, language))),
            P("signature", language)));

    public static RenderedEmail OrganizationVerified(string organizationName, string link, string language = "de") => new(
        P("org.verified.subject", language, ("org", organizationName)),
        string.Join("\n\n",
            P("greeting", language),
            P("org.verified.body", language, ("org", organizationName)),
            link,
            P("signature", language)));
}
