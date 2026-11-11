using HandyNet.Modules.Pos.Application;
using HandyNet.Modules.Pos.Domain;

namespace HandyNet.UnitTests;

/// <summary>Customer display (ADR-0023): state shape, in-memory store with long poll, link lifetime.</summary>
public class PosDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static DisplayState Cart(string description = "Ladekabel USB-C") =>
        new("cart", [new DisplayLine(description, 2, 10_00, 1_00, 19_00)], 19_00, 1_00, Locale: "de");

    [Fact]
    public void State_is_normalized_and_keeps_its_figures()
    {
        var state = (Cart("  Ladekabel  ") with
        {
            Payment = new DisplayPayment(19_00, 20_00, 1_00, " Cash "), Loyalty = new DisplayLoyalty(120, 1_20), Message = "  Vielen Dank ",
            ReceiptUrl = " /beleg/abc ",
        }).Normalize().Value;
        Assert.Equal("Ladekabel", state.Lines!.Single().Description);
        Assert.Equal(("Vielen Dank", "/beleg/abc", "Cash", "EUR"), (state.Message, state.ReceiptUrl, state.Payment!.Method, state.Currency));
        Assert.Equal((19_00L, 1_00L, 120), (state.TotalMinor, state.DiscountTotalMinor, state.Loyalty!.Points));
        Assert.Empty((new DisplayState("idle", null, 0, 0)).Normalize().Value.Lines!);
    }

    public static TheoryData<DisplayState, string> InvalidStates() => new()
    {
        { Cart() with { Phase = "checkout" }, "phase" },
        { Cart() with { Phase = null! }, "phase" },
        { Cart() with { Lines = Enumerable.Repeat(new DisplayLine("x", 1, 1, 0, 1), DisplayState.MaxLines + 1).ToList() }, "lines" },
        { Cart(new string('x', 301)), "lines" },
        { Cart(" "), "lines" },
        { Cart() with { TotalMinor = long.MaxValue }, "totalMinor" },
        { Cart() with { Payment = new DisplayPayment(1, 1, 0, new string('m', 31)) }, "payment" },
        { Cart() with { Loyalty = new DisplayLoyalty(-1, 0) }, "loyalty" },
        { Cart() with { Message = new string('m', 501) }, "message" },
        { Cart() with { ReceiptUrl = "javascript:alert(1)" }, "receiptUrl" },
        { Cart() with { ReceiptUrl = "//evil.example/x" }, "receiptUrl" },
        { Cart() with { ReceiptUrl = "https://user:pw@shop.example/beleg/x" }, "receiptUrl" },
        { Cart() with { Currency = "eur" }, "currency" },
        { Cart() with { Locale = "de DE<script>" }, "locale" },
    };

    [Theory]
    [MemberData(nameof(InvalidStates))]
    public void Invalid_states_are_refused_with_the_field(DisplayState state, string field)
    {
        var result = state.Normalize();
        Assert.Equal(("pos.display_state_invalid", field), (result.Error?.Code, result.Error?.Details?["field"]));
    }

    [Fact]
    public void Store_starts_idle_and_versions_only_grow()
    {
        var store = new DisplayStateStore();
        var register = Guid.NewGuid();
        Assert.Equal((0L, DisplayState.Idle), store.Get(register));

        var first = store.Set(register, Cart());
        var second = store.Set(register, Cart("Hülle"));
        Assert.True(first > 0 && second > first);
        Assert.Equal((second, "Hülle"), (store.Get(register).Version, store.Get(register).State.Lines!.Single().Description));
        // Registers do not see each other's state.
        Assert.Equal((0L, DisplayState.Idle), store.Get(Guid.NewGuid()));

        store.Clear(register);
        Assert.Equal(DisplayState.Idle, store.Get(register).State);
        Assert.True(store.Get(register).Version > second);
    }

    [Fact]
    public async Task Long_poll_returns_at_once_for_another_version_wakes_on_a_push_and_times_out_otherwise()
    {
        var store = new DisplayStateStore();
        var register = Guid.NewGuid();
        var version = store.Set(register, Cart());

        Assert.True(await store.WaitForChangeAsync(register, version - 1, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.False(await store.WaitForChangeAsync(register, version, TimeSpan.FromMilliseconds(50), CancellationToken.None));

        var waiting = store.WaitForChangeAsync(register, version, TimeSpan.FromSeconds(30), CancellationToken.None);
        var other = store.WaitForChangeAsync(Guid.NewGuid(), 0, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        store.Set(register, Cart("Hülle"));
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await other); // a push on one register does not wake another register's display

        using var cancelled = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.WaitForChangeAsync(register, store.Get(register).Version, TimeSpan.FromSeconds(30), cancelled.Token));
    }

    [Fact]
    public void Link_is_active_until_it_expires_or_is_revoked()
    {
        var link = DisplayLink.Create(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), Guid.NewGuid(), Now, TimeSpan.FromDays(30));
        Assert.True(link.IsActive(Now.AddDays(29)));
        Assert.False(link.IsActive(Now.AddDays(30)));
        link.Revoke(Now.AddDays(1));
        link.Revoke(Now.AddDays(2));
        Assert.Equal(Now.AddDays(1), link.RevokedAt);
        Assert.False(link.IsActive(Now.AddDays(1)));
    }
}
