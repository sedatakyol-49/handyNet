using HandyNet.SharedKernel;

namespace HandyNet.Modules.Inventory.Domain;

/// <summary>Why a device is blocked. Stored as text — never rename members without a data migration.</summary>
public enum BlacklistReason
{
    Lost,
    Stolen,
    Fraud,
    Other,
}

/// <summary>
/// Platform-wide block of one IMEI (lost, stolen, fraud). Not tenant-owned: only platform administrators maintain the
/// list; tenants learn only yes/no through <c>inventory.imei_blacklisted</c>. Entries are never deleted. An entry is
/// deactivated once, with a reason; blocking the same IMEI again creates a new entry, so the history stays complete.
/// The database enforces one active entry per IMEI and that only the deactivation fields can change (trigger).
/// </summary>
public sealed class ImeiBlacklistEntry : IAuditable
{
    public const int MaxNoteLength = 1000;
    public const int MaxSourceLength = 200;
    public const int MaxDeactivationReasonLength = 500;

    private ImeiBlacklistEntry() { }

    public Guid Id { get; private set; }
    public string Imei { get; private set; } = null!;
    public BlacklistReason Reason { get; private set; }
    public string? Note { get; private set; }

    /// <summary>Where the report came from, e.g. "Polizei Berlin, Az. 123/26" or "Händlermeldung".</summary>
    public string Source { get; private set; } = null!;
    public Guid CreatedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }
    public Guid? DeactivatedByUserId { get; private set; }
    public string? DeactivationReason { get; private set; }
    public uint Version { get; private set; }

    public bool IsActive => DeactivatedAt is null;

    public static Result<ImeiBlacklistEntry> Create(string imei, BlacklistReason reason, string? note, string source, Guid userId, DateTimeOffset now)
    {
        if (!Domain.Imei.TryNormalize(imei, out var normalized))
            return Error.Validation("imei.invalid", "IMEI must be 15 digits with a valid check digit.");
        if (!Enum.IsDefined(reason))
            return Error.Validation("blacklist.reason", "Unknown reason.");
        var trimmedSource = source?.Trim() ?? string.Empty;
        if (trimmedSource.Length == 0 || trimmedSource.Length > MaxSourceLength)
            return Error.Validation("blacklist.source", $"Source is required (max {MaxSourceLength} characters).");
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is { Length: > MaxNoteLength })
            return Error.Validation("blacklist.note", $"Note is too long (max {MaxNoteLength} characters).");
        if (reason == BlacklistReason.Other && trimmedNote is null)
            return Error.Validation("blacklist.note_required", "Describe the reason in the note when the reason is Other.");

        return new ImeiBlacklistEntry
        {
            Id = Guid.CreateVersion7(),
            Imei = normalized,
            Reason = reason,
            Note = trimmedNote,
            Source = trimmedSource,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
    }

    /// <summary>Lifts the block (e.g. the device was found and returned to its owner). The entry stays as history.</summary>
    public Result Deactivate(string reason, Guid userId, DateTimeOffset now)
    {
        if (!IsActive)
            return Error.Conflict("blacklist.already_inactive", "This entry is already deactivated.");
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxDeactivationReasonLength)
            return Error.Validation("blacklist.deactivation_reason", $"A reason is required (max {MaxDeactivationReasonLength} characters).");
        DeactivatedAt = now;
        DeactivatedByUserId = userId;
        DeactivationReason = trimmed;
        return Result.Success();
    }
}

/// <summary>Result of an optional external IMEI database query (e.g. GSMA Device Check).</summary>
public enum ExternalImeiStatus
{
    /// <summary>No external database is connected (default) or it could not be asked.</summary>
    NotChecked,
    Clean,
    Blocked,
}
