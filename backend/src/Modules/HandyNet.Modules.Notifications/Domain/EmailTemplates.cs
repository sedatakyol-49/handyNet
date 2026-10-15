using System.Globalization;

namespace HandyNet.Modules.Notifications.Domain;

/// <summary>
/// German plain-text emails (German is the platform default). Per-user language selection comes with
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

        Ihr HandyNet-Team
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

            Ihr HandyNet-Team
            """);
    }

    public static RenderedEmail OrganizationVerified(string organizationName, string link) => new(
        $"{organizationName} ist freigeschaltet",
        $"""
        Guten Tag,

        Ihr Unternehmen {organizationName} wurde geprüft und auf HandyNet freigeschaltet.
        Die für Sie aktivierten Module finden Sie nach der Anmeldung:

        {link}

        Ihr HandyNet-Team
        """);
}
