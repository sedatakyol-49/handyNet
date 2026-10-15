using FluentValidation;
using HandyNet.Modules.Invoicing.Domain;

namespace HandyNet.Modules.Invoicing.Application;

public sealed record IssueInvoicesRequest(string Month);

public sealed record InvoiceSummaryDto(
    Guid Id,
    string Kind,
    string Number,
    DateOnly IssueDate,
    string BillingMonth,
    DateOnly ServicePeriodStart,
    DateOnly ServicePeriodEnd,
    long TotalNetMinor,
    long TotalVatMinor,
    long TotalGrossMinor,
    string Currency,
    Guid? ReferencedInvoiceId,
    string? ReferencedInvoiceNumber,
    Guid OrganizationId,
    string RecipientName,
    int LineCount);

public sealed record InvoiceLineDto(int LineNumber, Guid OrderId, string OrderNumber, Guid PaymentId, DateTimeOffset PaidAt, long NetMinor,
    long VatMinor, long GrossMinor, Guid? CreditedInvoiceId);

public sealed record InvoiceDto(
    Guid Id,
    string Kind,
    string Number,
    DateOnly IssueDate,
    DateTimeOffset IssuedAt,
    string BillingMonth,
    DateOnly ServicePeriodStart,
    DateOnly ServicePeriodEnd,
    int VatRateBasisPoints,
    long TotalNetMinor,
    long TotalVatMinor,
    long TotalGrossMinor,
    string Currency,
    Guid? ReferencedInvoiceId,
    string? ReferencedInvoiceNumber,
    InvoiceParty Issuer,
    InvoiceParty Recipient,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record IssueFailure(Guid OrganizationId, string Code, string Message);

public sealed record IssueRunResult(
    string Month,
    int SellersChecked,
    int InvoicesIssued,
    int CreditNotesIssued,
    int AlreadyIssued,
    IReadOnlyList<InvoiceSummaryDto> Documents,
    IReadOnlyList<IssueFailure> Failures);

public sealed record InvoiceFile(string FileName, byte[] Content);

internal sealed class IssueInvoicesValidator : AbstractValidator<IssueInvoicesRequest>
{
    public IssueInvoicesValidator() =>
        RuleFor(x => x.Month).NotEmpty().Matches(@"^\d{4}-(0[1-9]|1[0-2])$").WithMessage("Month must have the form YYYY-MM.");
}

internal static class InvoiceMapping
{
    public static InvoiceSummaryDto ToSummary(Invoice i) => new(i.Id, i.Kind.ToString(), i.Number, i.IssueDate, Domain.BillingMonth.From(i.BillingMonth).ToString(),
        i.ServicePeriodStart, i.ServicePeriodEnd, i.TotalNetMinor, i.TotalVatMinor, i.TotalGrossMinor, i.Currency, i.ReferencedInvoiceId,
        i.ReferencedInvoiceNumber, i.OrganizationId, i.Recipient.DisplayName, i.Lines.Count);

    public static InvoiceDto ToDto(Invoice i) => new(i.Id, i.Kind.ToString(), i.Number, i.IssueDate, i.IssuedAt,
        Domain.BillingMonth.From(i.BillingMonth).ToString(), i.ServicePeriodStart, i.ServicePeriodEnd, i.VatRateBasisPoints, i.TotalNetMinor,
        i.TotalVatMinor, i.TotalGrossMinor, i.Currency, i.ReferencedInvoiceId, i.ReferencedInvoiceNumber, i.Issuer, i.Recipient,
        i.Lines.OrderBy(l => l.LineNumber).Select(l => new InvoiceLineDto(l.LineNumber, l.OrderId, l.OrderNumber, l.PaymentId, l.PaidAt, l.NetMinor,
            l.VatMinor, l.GrossMinor, l.CreditedInvoiceId)).ToList());
}
