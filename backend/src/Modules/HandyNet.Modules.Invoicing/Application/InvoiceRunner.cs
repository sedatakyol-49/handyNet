using HandyNet.Modules.Invoicing.Domain;
using HandyNet.Modules.Payments.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Invoicing.Application;

/// <summary>
/// Cross-tenant entry points (monthly job, platform admin). Sellers and documents are found through SECURITY DEFINER
/// functions that return ids only; every seller is then processed in its own scope as its system actor, so reads and
/// writes of payments and invoices stay under that seller's RLS policies (ADR-0005).
/// </summary>
public sealed class InvoiceRunner(IServiceScopeFactory scopeFactory, ILogger<InvoiceRunner> logger)
{
    private static TimeZoneInfo Zone => InvoiceCalendar.GermanTimeZone;

    public async Task<Result<IssueRunResult>> IssueMonthAsync(BillingMonth month, CancellationToken ct)
    {
        IReadOnlyList<Guid> sellers;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            if (!month.IsOver(clock.UtcNow, Zone))
                return Error.Validation("invoicing.month_not_over", $"{month} is not over yet; invoices are issued for completed months only.");
            var issuer = scope.ServiceProvider.GetRequiredService<InvoiceIssuer>().Issuer();
            if (!issuer.IsSuccess)
                return issuer.Error!;
            sellers = await scope.ServiceProvider.GetRequiredService<ICommissionStatements>()
                .GetSellersWithActivityAsync(month.Start(Zone), month.End(Zone), ct);
        }

        var documents = new List<InvoiceSummaryDto>();
        var failures = new List<IssueFailure>();
        int invoices = 0, notes = 0, existing = 0;
        foreach (var seller in sellers.Distinct().Order())
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(seller);
            Result<SellerIssueOutcome> outcome;
            try
            {
                outcome = await scope.ServiceProvider.GetRequiredService<InvoiceIssuer>().IssueMonthAsync(month, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Issuing invoices for {Month} failed for seller {OrganizationId}", month, seller);
                failures.Add(new IssueFailure(seller, "invoicing.failed", "Unexpected error; see logs."));
                continue;
            }

            if (!outcome.IsSuccess)
            {
                logger.LogWarning("Invoices for {Month}, seller {OrganizationId}: {Code} {Message}", month, seller, outcome.Error!.Code, outcome.Error.Message);
                failures.Add(new IssueFailure(seller, outcome.Error.Code, outcome.Error.Message));
                continue;
            }

            invoices += outcome.Value.Created.Count(i => i.Kind == InvoiceKind.Invoice);
            notes += outcome.Value.Created.Count(i => i.Kind == InvoiceKind.CreditNote);
            existing += outcome.Value.Existing.Count;
            documents.AddRange(outcome.Value.Created.Concat(outcome.Value.Existing).Select(InvoiceMapping.ToSummary));
        }

        logger.LogInformation("Invoicing {Month}: {Sellers} sellers, {Invoices} invoices and {Notes} credit notes issued, {Existing} already issued, {Failures} failures",
            month, sellers.Count, invoices, notes, existing, failures.Count);
        return new IssueRunResult(month.ToString(), sellers.Count, invoices, notes, existing,
            documents.OrderBy(d => d.Number, StringComparer.Ordinal).ToList(), failures);
    }

    /// <summary>All documents (invoices and credit notes) with the given billing month, across sellers.</summary>
    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListMonthAsync(BillingMonth month, CancellationToken ct)
    {
        List<Owner> owners;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            owners = await scope.ServiceProvider.GetRequiredService<HandyNetDbContext>().Database
                .SqlQuery<Owner>($"SELECT id, organization_id FROM invoicing.documents_for_month({month.FirstDay})")
                .ToListAsync(ct);
        }

        var result = new List<InvoiceSummaryDto>();
        foreach (var group in owners.GroupBy(o => o.OrganizationId))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(group.Key);
            var ids = group.Select(o => o.Id).ToList();
            var rows = await scope.ServiceProvider.GetRequiredService<HandyNetDbContext>().Set<Invoice>().AsNoTracking().Include(i => i.Lines)
                .Where(i => ids.Contains(i.Id) && i.OrganizationId == group.Key)
                .ToListAsync(ct);
            result.AddRange(rows.Select(InvoiceMapping.ToSummary));
        }
        return result.OrderBy(d => d.Number, StringComparer.Ordinal).ToList();
    }

    private sealed record Owner(Guid Id, Guid OrganizationId);
}

/// <summary>
/// Issues last month's invoices and credit notes. Checks periodically (default hourly) and runs once per month per
/// process after the month is over — normally in the first hour of the 1st; after downtime it catches up. Re-runs are
/// harmless (idempotent per seller and month).
/// </summary>
public sealed class InvoiceIssuingWorker(
    InvoiceRunner runner,
    IOptions<InvoicingOptions> options,
    IHostEnvironment environment,
    IClock clock,
    ILogger<InvoiceIssuingWorker> logger) : BackgroundService
{
    private BillingMonth? _completed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoIssue)
        {
            logger.LogInformation("Automatic commission invoicing is disabled (Invoicing:AutoIssue).");
            return;
        }
        if (environment.IsProduction() && options.Value.Issuer.ToParty().IsPlaceholder)
            logger.LogWarning("Invoicing:Issuer still contains placeholders; commission invoices will be refused until it is configured.");

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.AutoIssueCheckMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Commission invoicing run failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Issues the previous month unless this process already did so completely. Returns the run result, or null if skipped.</summary>
    public async Task<IssueRunResult?> RunOnceAsync(CancellationToken ct)
    {
        var month = BillingMonth.PreviousOf(clock.UtcNow, InvoiceCalendar.GermanTimeZone);
        if (_completed == month)
            return null;
        var result = await runner.IssueMonthAsync(month, ct);
        if (!result.IsSuccess)
        {
            logger.LogWarning("Commission invoicing for {Month} refused: {Code} {Message}", month, result.Error!.Code, result.Error.Message);
            return null;
        }
        if (result.Value.Failures.Count == 0)
            _completed = month;
        return result.Value;
    }
}
