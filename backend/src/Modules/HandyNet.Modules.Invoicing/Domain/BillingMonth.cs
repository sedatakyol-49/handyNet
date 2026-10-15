using System.Globalization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Invoicing.Domain;

/// <summary>
/// A calendar month in German local time (Europe/Berlin). Commission invoices cover one month of captured payments;
/// credit notes are keyed by the month in which the refunds happened.
/// </summary>
public readonly record struct BillingMonth(int Year, int Month) : IComparable<BillingMonth>
{
    public DateOnly FirstDay => new(Year, Month, 1);
    public DateOnly LastDay => FirstDay.AddMonths(1).AddDays(-1);
    public BillingMonth Next => From(FirstDay.AddMonths(1));
    public BillingMonth Previous => From(FirstDay.AddMonths(-1));

    public static BillingMonth From(DateOnly day) => new(day.Year, day.Month);

    /// <summary>"2026-09" → September 2026.</summary>
    public static Result<BillingMonth> Parse(string? value)
    {
        if (value is null || !DateOnly.TryParseExact(value.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            || day.Year < 2000)
            return Error.Validation("invoicing.month", "Month must have the form YYYY-MM.");
        return From(day);
    }

    /// <summary>Start of the month (local midnight on the 1st) as an instant.</summary>
    public DateTimeOffset Start(TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(new DateTime(Year, Month, 1, 0, 0, 0, DateTimeKind.Unspecified), zone), TimeSpan.Zero);

    /// <summary>Exclusive end: start of the next month.</summary>
    public DateTimeOffset End(TimeZoneInfo zone) => Next.Start(zone);

    public bool IsOver(DateTimeOffset now, TimeZoneInfo zone) => End(zone) <= now;

    /// <summary>The month before the one that contains <paramref name="now"/> (local time).</summary>
    public static BillingMonth PreviousOf(DateTimeOffset now, TimeZoneInfo zone) =>
        From(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime)).Previous;

    public int CompareTo(BillingMonth other) => FirstDay.CompareTo(other.FirstDay);

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}

public static class InvoiceCalendar
{
    /// <summary>German local time for months and printed dates (falls back to UTC if the zone is unknown on the host).</summary>
    public static readonly TimeZoneInfo GermanTimeZone = Find();

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Utc;
    }
}
