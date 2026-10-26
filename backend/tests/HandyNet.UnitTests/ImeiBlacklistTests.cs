using HandyNet.Modules.Imports.Domain;
using HandyNet.Modules.Inventory.Domain;

namespace HandyNet.UnitTests;

public class ImeiBlacklistEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Admin = Guid.NewGuid();

    [Fact]
    public void Creates_an_active_entry_with_normalized_imei()
    {
        var entry = ImeiBlacklistEntry.Create("49 015420 323751 8", BlacklistReason.Stolen, " Anzeige ", " Polizei Berlin ", Admin, Now);

        Assert.True(entry.IsSuccess);
        Assert.Equal("490154203237518", entry.Value.Imei);
        Assert.True(entry.Value.IsActive);
        Assert.Equal("Anzeige", entry.Value.Note);
        Assert.Equal("Polizei Berlin", entry.Value.Source);
    }

    [Fact]
    public void Validates_imei_source_and_note_for_other()
    {
        Assert.Equal("imei.invalid", ImeiBlacklistEntry.Create("490154203237519", BlacklistReason.Lost, null, "x", Admin, Now).Error?.Code);
        Assert.Equal("blacklist.source", ImeiBlacklistEntry.Create("490154203237518", BlacklistReason.Lost, null, " ", Admin, Now).Error?.Code);
        Assert.Equal("blacklist.note_required", ImeiBlacklistEntry.Create("490154203237518", BlacklistReason.Other, null, "x", Admin, Now).Error?.Code);
        Assert.Equal("blacklist.reason", ImeiBlacklistEntry.Create("490154203237518", (BlacklistReason)42, null, "x", Admin, Now).Error?.Code);
    }

    [Fact]
    public void Deactivation_needs_a_reason_and_happens_once()
    {
        var entry = ImeiBlacklistEntry.Create("490154203237518", BlacklistReason.Lost, null, "Kundenmeldung", Admin, Now).Value;

        Assert.Equal("blacklist.deactivation_reason", entry.Deactivate(" ", Admin, Now).Error?.Code);
        Assert.True(entry.IsActive);

        Assert.True(entry.Deactivate("Gerät wiedergefunden", Admin, Now.AddDays(1)).IsSuccess);
        Assert.False(entry.IsActive);
        Assert.Equal(Now.AddDays(1), entry.DeactivatedAt);
        Assert.Equal("Gerät wiedergefunden", entry.DeactivationReason);

        Assert.Equal("blacklist.already_inactive", entry.Deactivate("again", Admin, Now).Error?.Code);
    }
}

public class StockMovementUnitTests
{
    [Fact]
    public void Unit_movements_carry_the_unit_id()
    {
        var level = StockLevel.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var unitId = Guid.NewGuid();

        var movement = level.Adjust(1, MovementType.UnitReceipt, "manual:x", null, DateTimeOffset.UtcNow, unitId).Value!;
        var plain = level.Adjust(1, MovementType.Receipt, "x", null, DateTimeOffset.UtcNow).Value!;

        Assert.Equal(unitId, movement.UnitId);
        Assert.Null(plain.UnitId);
        Assert.Equal(2, plain.OnHandAfter);
    }
}

public class ManualEntryImeiConflictTests
{
    private const string A = "490154203237518";
    private const string B = "356938035643809";
    private const string C = "353918057834218";

    private static List<(string, string)> Problems(HandyNet.SharedKernel.Error error) =>
        ((IEnumerable<ImeiProblem>)error.Details!["imeis"]!).Select(p => (p.Imei, p.Code)).ToList();

    [Fact]
    public void Lists_every_offending_imei_in_entry_order_with_blacklist_taking_precedence()
    {
        var error = ManualEntryRules.ImeiConflicts([A, B, C], blacklisted: new HashSet<string> { C, B }, inStock: new HashSet<string> { A, B });

        Assert.Equal("entry.imei_conflicts", error.Code);
        Assert.Equal([(A, "unit.imei_in_stock"), (B, "imei.blacklisted"), (C, "imei.blacklisted")], Problems(error));
    }

    [Fact]
    public void A_single_offending_imei_keeps_its_specific_code()
    {
        var blacklisted = ManualEntryRules.ImeiConflicts([A, B], blacklisted: new HashSet<string> { B }, inStock: new HashSet<string>());
        var inStock = ManualEntryRules.ImeiConflicts([A, B], blacklisted: new HashSet<string>(), inStock: new HashSet<string> { A });

        Assert.Equal(("imei.blacklisted", B), (blacklisted.Code, blacklisted.Details!["imei"]));
        Assert.Equal(("unit.imei_in_stock", A), (inStock.Code, inStock.Details!["imei"]));
    }
}
