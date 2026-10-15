using HandyNet.Modules.Ankauf.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Ankauf.Application;

public sealed class AnkaufOptions
{
    public const string Section = "Ankauf";

    /// <summary>
    /// Years after completion (or receipt, for intakes never closed) after which the private seller's personal data is
    /// anonymized. Null = disabled, because the legal period is not confirmed yet (ADR-0009).
    /// </summary>
    public int? SellerDataRetentionYears { get; set; }

    /// <summary>How often the retention job runs.</summary>
    public int SellerDataRetentionCheckHours { get; set; } = 24;
}

/// <summary>
/// Anonymizes the seller data of intakes whose retention period is over. Due intakes are found through the SECURITY
/// DEFINER function <c>ankauf.intakes_due_for_anonymization</c> (ids and owning organization only); each
/// organization's intakes are then changed in a scope acting as that organization's system actor, under RLS (ADR-0005).
/// </summary>
public sealed class SellerDataRetention(
    IServiceScopeFactory scopeFactory,
    IOptions<AnkaufOptions> options,
    IHostEnvironment environment,
    IClock clock,
    ILogger<SellerDataRetention> logger) : BackgroundService
{
    private const int BatchSize = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.SellerDataRetentionYears is null)
        {
            if (environment.IsProduction())
                logger.LogWarning("Ankauf:SellerDataRetentionYears is not set: seller personal data of Ankauf intakes is kept indefinitely. "
                                  + "Configure the confirmed legal retention period.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, options.Value.SellerDataRetentionCheckHours));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Seller data retention run failed");
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

    /// <summary>Anonymizes all due intakes; returns how many were anonymized (0 when disabled).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (options.Value.SellerDataRetentionYears is not { } years)
            return 0;
        var now = clock.UtcNow;
        var cutoff = Intake.RetentionCutoff(now, years);
        var anonymized = 0;

        while (true)
        {
            List<DueIntake> due;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                due = await scope.ServiceProvider.GetRequiredService<HandyNetDbContext>().Database
                    .SqlQuery<DueIntake>($"SELECT id, organization_id FROM ankauf.intakes_due_for_anonymization({cutoff}, {BatchSize})")
                    .ToListAsync(ct);
            }
            if (due.Count == 0)
                break;

            var progress = 0;
            foreach (var organization in due.GroupBy(d => d.OrganizationId))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().SetSystemActor(organization.Key);
                var db = scope.ServiceProvider.GetRequiredService<HandyNetDbContext>();
                var ids = organization.Select(d => d.Id).ToList();
                var intakes = await db.Set<Intake>().Where(i => ids.Contains(i.Id) && i.OrganizationId == organization.Key).ToListAsync(ct);
                var changed = 0;
                foreach (var intake in intakes.Where(i => !i.IsAnonymized))
                {
                    var result = intake.AnonymizeSeller(cutoff, now);
                    if (result.IsSuccess)
                        changed++;
                    else
                        logger.LogWarning("Intake {IntakeId}: {Code}", intake.Id, result.Error!.Code);
                }
                try
                {
                    await db.SaveChangesAsync(ct);
                    progress += changed;
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Changed concurrently (e.g. a status change); picked up again in the next round.
                    logger.LogInformation("Concurrent change while anonymizing intakes of {OrganizationId}; retrying later", organization.Key);
                }
            }

            anonymized += progress;
            if (due.Count < BatchSize || progress == 0)
                break;
        }

        if (anonymized > 0)
            logger.LogInformation("Anonymized the seller data of {Count} Ankauf intakes (retention start before {Cutoff:O})", anonymized, cutoff);
        return anonymized;
    }

    private sealed record DueIntake(Guid Id, Guid OrganizationId);
}
