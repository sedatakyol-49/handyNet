using HandyNet.SharedKernel;

namespace HandyNet.Modules.Ankauf.Domain;

/// <summary>A sheet row that produced no price (kept on the list so the admin sees what will lose its price).</summary>
public sealed record SkippedRow(int RowNumber, string Text, string Reason);

public enum PriceListStatus
{
    /// <summary>Imported; entries that need approval are being reviewed. No price is active yet.</summary>
    Review,

    /// <summary>Accepted and approved entries became immutable price versions.</summary>
    Published,

    /// <summary>Discarded without publishing.</summary>
    Discarded,
}

/// <summary>One monthly Ankaufspreisliste upload (platform-level, not tenant data).</summary>
public sealed class PriceList : IAuditable
{
    private PriceList() { }

    public Guid Id { get; private set; }
    public string Label { get; private set; } = null!;
    public string FileName { get; private set; } = null!;
    public string FileSha256 { get; private set; } = null!;
    public string SheetName { get; private set; } = null!;
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset ValidUntil { get; private set; }
    public PriceListStatus Status { get; private set; }
    public List<SkippedRow> SkippedRows { get; private set; } = [];
    public DateTimeOffset ImportedAt { get; private set; }
    public Guid ImportedBy { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public Guid? PublishedBy { get; private set; }
    public uint Version { get; private set; }

    public static Result<PriceList> Create(string label, string fileName, string sha, string sheet, DateTimeOffset validFrom,
        DateTimeOffset validUntil, IReadOnlyList<SkippedRow> skipped, Guid userId, DateTimeOffset now)
    {
        if (validUntil <= validFrom)
            return Error.Validation("ankauf.validity", "The list must be valid for a positive period.");
        if (validUntil - validFrom > TimeSpan.FromDays(92))
            return Error.Validation("ankauf.validity", "Price lists are monthly; validity is limited to three months.");
        return new PriceList
        {
            Id = Guid.CreateVersion7(),
            Label = label.Trim(),
            FileName = fileName,
            FileSha256 = sha,
            SheetName = sheet,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Status = PriceListStatus.Review,
            SkippedRows = skipped.ToList(),
            ImportedAt = now,
            ImportedBy = userId,
        };
    }

    public Result Publish(Guid userId, DateTimeOffset now)
    {
        if (Status != PriceListStatus.Review)
            return Error.Conflict("ankauf.list_not_in_review", $"A list in status {Status} cannot be published.");
        Status = PriceListStatus.Published;
        PublishedAt = now;
        PublishedBy = userId;
        return Result.Success();
    }

    public Result Discard()
    {
        if (Status != PriceListStatus.Review)
            return Error.Conflict("ankauf.list_not_in_review", $"A list in status {Status} cannot be discarded.");
        Status = PriceListStatus.Discarded;
        return Result.Success();
    }
}

public sealed class PriceListEntry
{
    private PriceListEntry() { }

    public long Id { get; private set; }
    public Guid PriceListId { get; private set; }
    public int RowNumber { get; private set; }
    public string ModelKey { get; private set; } = null!;
    public string Brand { get; private set; } = null!;
    public string Model { get; private set; } = null!;
    public string? Storage { get; private set; }
    public long PriceMinor { get; private set; }
    public long? PreviousPriceMinor { get; private set; }
    public PriceChange Change { get; private set; }
    public EntryDecision Decision { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }

    public static PriceListEntry Create(Guid listId, int row, string brand, string model, string? storage, long priceMinor, long? previousMinor)
    {
        var (change, decision) = PriceApproval.Classify(previousMinor, priceMinor);
        return new PriceListEntry
        {
            PriceListId = listId,
            RowNumber = row,
            ModelKey = ModelKeys.Of(brand, model, storage),
            Brand = brand.Trim(),
            Model = model.Trim(),
            Storage = string.IsNullOrWhiteSpace(storage) ? null : storage.Trim(),
            PriceMinor = priceMinor,
            PreviousPriceMinor = previousMinor,
            Change = change,
            Decision = decision,
        };
    }

    public Result Decide(bool approve, Guid userId, DateTimeOffset now)
    {
        if (Decision is not (EntryDecision.Pending or EntryDecision.Approved or EntryDecision.Rejected))
            return Error.Conflict("ankauf.entry_auto", "Automatically accepted prices need no decision.");
        Decision = approve ? EntryDecision.Approved : EntryDecision.Rejected;
        DecidedBy = userId;
        DecidedAt = now;
        return Result.Success();
    }

    public bool BecomesPrice => Decision is EntryDecision.AutoAccepted or EntryDecision.Approved;
}

/// <summary>
/// An immutable, dated price for one model in condition "Gut". The active price is the version with the latest
/// <see cref="ValidFrom"/> whose window contains "now". Versions are never edited or extended: when a list expires
/// and no newer version exists, the model simply has no price.
/// </summary>
public sealed class PriceVersion
{
    private PriceVersion() { }

    public Guid Id { get; private set; }
    public string ModelKey { get; private set; } = null!;
    public string Brand { get; private set; } = null!;
    public string Model { get; private set; } = null!;
    public string? Storage { get; private set; }
    public long PriceMinor { get; private set; }
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset ValidUntil { get; private set; }
    public Guid PriceListId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PriceVersion From(PriceListEntry entry, PriceList list, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        ModelKey = entry.ModelKey,
        Brand = entry.Brand,
        Model = entry.Model,
        Storage = entry.Storage,
        PriceMinor = entry.PriceMinor,
        ValidFrom = list.ValidFrom,
        ValidUntil = list.ValidUntil,
        PriceListId = list.Id,
        CreatedAt = now,
    };
}

/// <summary>Immutable version of the pricing parameters; the newest one whose EffectiveFrom has passed applies.</summary>
public sealed class ConditionPolicy
{
    private ConditionPolicy() { }

    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public PolicySettings Settings { get; private set; } = null!;
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }

    public static Result<ConditionPolicy> Create(int number, PolicySettings settings, DateTimeOffset effectiveFrom, Guid userId, DateTimeOffset now)
    {
        if (settings.Validate() is { } invalid)
            return invalid;
        if (effectiveFrom < now.AddMinutes(-1))
            return Error.Validation("ankauf.policy_backdated", "A policy cannot take effect in the past.");
        return new ConditionPolicy
        {
            Id = Guid.CreateVersion7(),
            Number = number,
            Settings = settings,
            EffectiveFrom = effectiveFrom,
            CreatedAt = now,
            CreatedBy = userId,
        };
    }
}

/// <summary>
/// A dealer's preliminary buyback quote. It records exactly which price version and policy produced the numbers,
/// so later price changes never alter an existing quote. Final payout is confirmed after physical inspection.
/// </summary>
public sealed class Quote : IAuditable
{
    public const int ValidityDays = 7;

    private Quote() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid CreatedBy { get; private set; }
    public string ModelKey { get; private set; } = null!;
    public string Brand { get; private set; } = null!;
    public string Model { get; private set; } = null!;
    public string? Storage { get; private set; }
    public Guid PriceVersionId { get; private set; }
    public Guid ConditionPolicyId { get; private set; }
    public DeviceCondition Condition { get; private set; }
    public List<DeviceDefect> Defects { get; private set; } = [];
    public int? BatteryHealthPercent { get; private set; }
    public string? Imei { get; private set; }
    public long ReferenceGutMinor { get; private set; }
    public int ConditionAdjustment { get; private set; }
    public int TotalDeduction { get; private set; }
    public long ReferencePurchasePriceMinor { get; private set; }
    public long PlatformOfferMinor { get; private set; }
    public long MaxConsumerPriceMinor { get; private set; }
    public long? ConsumerPriceMinor { get; private set; }
    public long? DealerMarginMinor { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ValidUntil { get; private set; }

    public static Quote Create(Guid organizationId, Guid userId, PriceVersion price, ConditionPolicy policy, QuoteInput input,
        QuoteCalculation calc, string? imei, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        CreatedBy = userId,
        ModelKey = price.ModelKey,
        Brand = price.Brand,
        Model = price.Model,
        Storage = price.Storage,
        PriceVersionId = price.Id,
        ConditionPolicyId = policy.Id,
        Condition = input.Condition,
        Defects = input.Defects.Distinct().Order().ToList(),
        BatteryHealthPercent = input.BatteryHealthPercent,
        Imei = string.IsNullOrWhiteSpace(imei) ? null : imei.Trim(),
        ReferenceGutMinor = calc.ReferenceGutMinor,
        ConditionAdjustment = calc.ConditionAdjustment,
        TotalDeduction = calc.TotalDeduction,
        ReferencePurchasePriceMinor = calc.ReferencePurchasePriceMinor,
        PlatformOfferMinor = calc.PlatformOfferMinor,
        MaxConsumerPriceMinor = calc.MaxConsumerPriceMinor,
        ConsumerPriceMinor = input.ConsumerPriceMinor,
        DealerMarginMinor = calc.DealerMarginMinor,
        CreatedAt = now,
        // A quote never outlives the price it is based on.
        ValidUntil = new[] { now.AddDays(ValidityDays), price.ValidUntil }.Min(),
    };
}
