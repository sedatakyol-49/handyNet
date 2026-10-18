using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// A till (Kasse) selling from one warehouse. Receipt numbers are per register, sequential and gapless: the next
/// number is taken with a row-locking <c>UPDATE … RETURNING</c> inside the transaction that writes the receipt, so a
/// rolled-back sale also gives its number back. <see cref="NextReceiptNumber"/> is never written by application code.
/// </summary>
public sealed class Register : IAuditable
{
    public const int MaxNameLength = 100;

    private Register() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; } = null!;
    public Guid WarehouseId { get; private set; }
    public long NextReceiptNumber { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public uint Version { get; private set; }

    /// <summary>Shared device: lock the till (cashier PIN needed again) after this many minutes without activity. Null = off.</summary>
    public int? AutoLockMinutes { get; private set; }

    public const int MaxAutoLockMinutes = 240;

    /// <summary>Sets the auto-lock; 0 or null turns it off.</summary>
    public Result SetAutoLock(int? minutes)
    {
        if (minutes is < 0 or > MaxAutoLockMinutes)
            return Error.Validation("pos.auto_lock_minutes", $"The auto-lock must be between 1 and {MaxAutoLockMinutes} minutes (0 = off).");
        AutoLockMinutes = minutes is > 0 ? minutes : null;
        return Result.Success();
    }

    public static Result<Register> Create(Guid organizationId, string name, Guid warehouseId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
            return Error.Validation("pos.register_name", $"A register name (max {MaxNameLength} characters) is required.");
        return new Register
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            Name = name.Trim(),
            WarehouseId = warehouseId,
            NextReceiptNumber = 1,
            IsActive = true,
            CreatedAt = now,
        };
    }

    public Result Update(string name, bool isActive, bool hasOpenSession)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
            return Error.Validation("pos.register_name", $"A register name (max {MaxNameLength} characters) is required.");
        if (!isActive && hasOpenSession)
            return Error.Conflict("pos.session_open", "Close the open cash session before deactivating the register.");
        Name = name.Trim();
        IsActive = isActive;
        return Result.Success();
    }
}
