using System.Globalization;

namespace HandyNet.Modules.Notifications.Domain;

/// <summary>
/// German plain-text emails (German is the platform default); account emails (verification, password reset) also
/// in English, chosen by the language the user sent with the request. Per-user language selection comes with
/// user preferences; templates take only the data they print, so they stay unit-testable.
/// </summary>
public static class EmailTemplates
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Euro(long minor) => (minor / 100m).ToString("#,##0.00", German) + " €";

    public static RenderedEmail OrderPlacedForSeller(string sellerName, string buyerName, long grossMinor, string link) => new(
        $"Neue Bestellung von {buyerName}",
        $"""
        Guten Tag,

        {buyerName} hat bei {sellerName} eine Bestellung über {Euro(grossMinor)} (brutto) aufgegeben.
        Der Bestand ist reserviert. Bitte bestätigen oder lehnen Sie die Bestellung ab:

        {link}

        Ihr SEHA-Team
        """);

    public static RenderedEmail OrderStatusForCounterparty(string status, string otherPartyName, string? reason, string link)
    {
        var (subject, sentence) = status switch
        {
            "Confirmed" => ("Ihre Bestellung wurde bestätigt", $"{otherPartyName} hat Ihre Bestellung bestätigt."),
            "Shipped" => ("Ihre Bestellung wurde versendet", $"{otherPartyName} hat Ihre Bestellung als versendet markiert."),
            "Rejected" => ("Ihre Bestellung wurde abgelehnt", $"{otherPartyName} hat Ihre Bestellung abgelehnt."),
            "Cancelled" => ("Bestellung storniert", $"{otherPartyName} hat die Bestellung storniert. Der reservierte Bestand wurde freigegeben."),
            _ => ($"Bestellstatus: {status}", $"Der Status der Bestellung ist jetzt {status}."),
        };

        var reasonLine = string.IsNullOrWhiteSpace(reason) ? string.Empty : $"\nBegründung: {reason}\n";
        return new RenderedEmail(subject, $"""
            Guten Tag,

            {sentence}
            {reasonLine}
            Details: {link}

            Ihr SEHA-Team
            """);
    }

    /// <summary>Template ids of account emails. Their bodies hold a one-time link and are redacted after delivery.</summary>
    public const string EmailVerificationTemplate = "auth.email_verification";
    public const string PasswordResetTemplate = "auth.password_reset";

    private static string Duration(TimeSpan validFor, bool english)
    {
        if (validFor.TotalHours >= 1 && validFor.TotalMinutes % 60 == 0)
        {
            var hours = (int)validFor.TotalHours;
            return english ? (hours == 1 ? "1 hour" : $"{hours} hours") : (hours == 1 ? "1 Stunde" : $"{hours} Stunden");
        }
        var minutes = (int)Math.Ceiling(validFor.TotalMinutes);
        return english ? $"{minutes} minutes" : $"{minutes} Minuten";
    }

    /// <param name="language">"en" for English; anything else renders German.</param>
    public static RenderedEmail EmailVerification(string displayName, string link, TimeSpan validFor, string language) =>
        language == "en"
            ? new RenderedEmail(
                "Please confirm your email address",
                $"""
                Hello {displayName},

                please confirm your email address for your SEHA account by opening this link:

                {link}

                The link is valid for {Duration(validFor, english: true)} and can be used once.
                If you did not create a SEHA account, you can ignore this email.

                Your SEHA team
                """)
            : new RenderedEmail(
                "Bitte bestätigen Sie Ihre E-Mail-Adresse",
                $"""
                Guten Tag {displayName},

                bitte bestätigen Sie die E-Mail-Adresse Ihres SEHA-Kontos über diesen Link:

                {link}

                Der Link ist {Duration(validFor, english: false)} gültig und kann einmal verwendet werden.
                Falls Sie kein SEHA-Konto angelegt haben, können Sie diese E-Mail ignorieren.

                Ihr SEHA-Team
                """);

    /// <param name="language">"en" for English; anything else renders German.</param>
    public static RenderedEmail PasswordReset(string displayName, string link, TimeSpan validFor, string language) =>
        language == "en"
            ? new RenderedEmail(
                "Reset your SEHA password",
                $"""
                Hello {displayName},

                we received a request to reset the password of your SEHA account. To choose a new password, open this link:

                {link}

                The link is valid for {Duration(validFor, english: true)} and can be used once. Setting a new password signs you out on all devices.
                If you did not request this, you can ignore this email — your password stays unchanged.

                Your SEHA team
                """)
            : new RenderedEmail(
                "SEHA-Passwort zurücksetzen",
                $"""
                Guten Tag {displayName},

                wir haben eine Anfrage erhalten, das Passwort Ihres SEHA-Kontos zurückzusetzen. Über diesen Link vergeben Sie ein neues Passwort:

                {link}

                Der Link ist {Duration(validFor, english: false)} gültig und kann einmal verwendet werden. Mit dem neuen Passwort werden Sie auf allen Geräten abgemeldet.
                Falls Sie das nicht angefordert haben, können Sie diese E-Mail ignorieren – Ihr Passwort bleibt unverändert.

                Ihr SEHA-Team
                """);

    public static RenderedEmail OrganizationVerified(string organizationName, string link) => new(
        $"{organizationName} ist freigeschaltet",
        $"""
        Guten Tag,

        Ihr Unternehmen {organizationName} wurde geprüft und auf SEHA freigeschaltet.
        Die für Sie aktivierten Module finden Sie nach der Anmeldung:

        {link}

        Ihr SEHA-Team
        """);
}
