using HandyNet.Modules.Notifications.Domain;

namespace HandyNet.UnitTests;

public class EmailMessageTests
{
    private static EmailMessage NewEmail() =>
        EmailMessage.Queue(Guid.NewGuid(), "t", " Kunde@Example.DE ", null, new RenderedEmail("s", "b"), DateTimeOffset.UnixEpoch);

    [Fact]
    public void Recipient_is_normalized() => Assert.Equal("kunde@example.de", NewEmail().Recipient);

    [Fact]
    public void Failed_attempts_back_off_and_eventually_give_up()
    {
        var email = NewEmail();
        var now = DateTimeOffset.UnixEpoch;
        email.MarkAttemptFailed("smtp down", now);
        Assert.Equal(EmailStatus.Pending, email.Status);
        Assert.Equal(now.AddMinutes(1), email.NextAttemptAt);
        email.MarkAttemptFailed("smtp down", now);
        Assert.Equal(now.AddMinutes(2), email.NextAttemptAt);

        for (var i = 2; i < EmailMessage.MaxAttempts; i++)
            email.MarkAttemptFailed("smtp down", now);
        Assert.Equal(EmailStatus.Failed, email.Status);
    }

    [Fact]
    public void Backoff_is_capped() => Assert.Equal(TimeSpan.FromHours(6), EmailMessage.BackoffFor(30));

    [Fact]
    public void Amounts_are_formatted_in_german() => Assert.Equal("1.234,56 €", EmailTemplates.Euro(123456));

    [Fact]
    public void Rejection_email_includes_the_reason()
    {
        var email = EmailTemplates.OrderStatusForCounterparty("Rejected", "Großhandel Berlin", "Ausverkauft", "https://x/app/bestellungen/1");
        Assert.Equal("Ihre Bestellung wurde abgelehnt", email.Subject);
        Assert.Contains("Begründung: Ausverkauft", email.Body);
        Assert.Contains("https://x/app/bestellungen/1", email.Body);
    }
}
