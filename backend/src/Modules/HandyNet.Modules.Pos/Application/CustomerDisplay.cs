using System.Collections.Concurrent;
using System.Text.Json;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Modules.Tenancy.Contracts;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Path relative to the web app ("/kundendisplay/{token}"); the token is returned only here.</summary>
public sealed record DisplayLinkDto(string Token, string Path, DateTimeOffset ExpiresAt);

public sealed record DisplayLinkStatusDto(bool Active, DateTimeOffset ExpiresAt);

public sealed record DisplayStateAcceptedDto(long Version);

public sealed record DisplayShopDto(string Name, string? LogoUrl);

/// <summary>What the public display page gets: nothing but the display state and the shop's name and logo.</summary>
public sealed record PublicDisplayDto(long Version, DisplayShopDto Shop, DisplayState State);

/// <summary>
/// The display states of all registers, in memory of this API instance (ADR-0023): no database write and no audit for what
/// is on the customer's screen. Lost on restart — a display then shows idle until the till pushes again. Versions only grow
/// (seeded with the start time, so they do not repeat after a restart).
/// </summary>
public sealed class DisplayStateStore
{
    private sealed record Entry(long Version, DisplayState State, TaskCompletionSource Changed);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private long _version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Entry Empty() => new(0, DisplayState.Idle, NewSignal());

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public (long Version, DisplayState State) Get(Guid registerId)
    {
        var entry = _entries.GetOrAdd(registerId, _ => Empty());
        return (entry.Version, entry.State);
    }

    /// <summary>Replaces the register's state and wakes the displays waiting for a change.</summary>
    public long Set(Guid registerId, DisplayState state)
    {
        var version = Interlocked.Increment(ref _version);
        Entry? previous = null;
        _entries.AddOrUpdate(registerId, _ => new Entry(version, state, NewSignal()), (_, old) =>
        {
            previous = old;
            // A later push may race an earlier one: the higher version wins.
            return old.Version > version ? old : new Entry(version, state, NewSignal());
        });
        previous?.Changed.TrySetResult();
        return version;
    }

    /// <summary>Back to idle (link revoked or replaced): waiting displays wake up and re-check their token.</summary>
    public void Clear(Guid registerId) => Set(registerId, DisplayState.Idle);

    /// <summary>
    /// Waits until the register's version differs from <paramref name="since"/> or the timeout passes. True = changed
    /// (or already different).
    /// </summary>
    public async Task<bool> WaitForChangeAsync(Guid registerId, long since, TimeSpan timeout, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(registerId, _ => Empty());
        if (entry.Version != since)
            return true;
        try
        {
            await entry.Changed.Task.WaitAsync(timeout, ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}

internal sealed class PublicDisplayRow
{
    public Guid RegisterId { get; set; }
    public Guid OrganizationId { get; set; }
    public string OrganizationName { get; set; } = null!;
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Customer display (ADR-0023): the remote link of a register, the state pushed by the till and the public read.</summary>
internal sealed class DisplayService(HandyNetDbContext db, ITenantContext tenant, PosActor actor, IOptions<PosOptions> options, IClock clock,
    IOrganizationBranding branding, DisplayStateStore store)
{
    public const string PublicPathPrefix = "/kundendisplay/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One answer for unknown, malformed, expired and revoked tokens.</summary>
    internal static readonly Error LinkNotFound = Error.NotFound("pos.display_link_not_found", "This display link does not exist or is no longer valid.");

    private Guid OrganizationId => tenant.RequiredOrganizationId;

    /// <summary>Creates the register's display link; earlier links of the register stop working at once.</summary>
    public async Task<Result<DisplayLinkDto>> CreateLinkAsync(Guid registerId, CancellationToken ct)
    {
        if (await RegisterCheckAsync(registerId, ct) is { } refused)
            return refused;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Serializes link changes per register, so two creations never leave two active links.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"display-link:" + registerId}, 0))", ct);
        var now = clock.UtcNow;
        foreach (var earlier in await db.Set<DisplayLink>().Where(l => l.RegisterId == registerId && l.RevokedAt == null).ToListAsync(ct))
            earlier.Revoke(now);
        await db.SaveChangesAsync(ct);
        var token = OpaqueToken.New();
        var link = DisplayLink.Create(OrganizationId, registerId, OpaqueToken.HashOf(token), actor.UserId, now, TimeSpan.FromDays(options.Value.DisplayLinkDays));
        db.Add(link);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        store.Clear(registerId); // displays on the old link wake up and get the 404
        return new DisplayLinkDto(token, PublicPathPrefix + token, link.ExpiresAt);
    }

    /// <summary>Success with null when the register has no active link.</summary>
    public async Task<Result<DisplayLinkStatusDto?>> GetLinkAsync(Guid registerId, CancellationToken ct)
    {
        if (!await RegisterExistsAsync(registerId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var now = clock.UtcNow;
        var link = await db.Set<DisplayLink>().AsNoTracking()
            .Where(l => l.RegisterId == registerId && l.RevokedAt == null && l.ExpiresAt > now).FirstOrDefaultAsync(ct);
        return link is null ? (DisplayLinkStatusDto?)null : new DisplayLinkStatusDto(true, link.ExpiresAt);
    }

    public async Task<Result> RevokeLinkAsync(Guid registerId, CancellationToken ct)
    {
        if (await RegisterCheckAsync(registerId, ct) is { } refused)
            return refused;
        var now = clock.UtcNow;
        foreach (var link in await db.Set<DisplayLink>().Where(l => l.RegisterId == registerId && l.RevokedAt == null).ToListAsync(ct))
            link.Revoke(now);
        await db.SaveChangesAsync(ct);
        store.Clear(registerId);
        return Result.Success();
    }

    /// <summary>The till pushes what the display shows. Memory only: nothing is written to the database or the audit log.</summary>
    public async Task<Result<DisplayStateAcceptedDto>> SetStateAsync(Guid registerId, Stream body, CancellationToken ct)
    {
        if (await RegisterCheckAsync(registerId, ct) is { } refused)
            return refused;
        var bytes = await ReadLimitedAsync(body, DisplayState.MaxBytes, ct);
        if (bytes is null)
            return Error.Validation("pos.display_state_too_large", $"The display state is limited to {DisplayState.MaxBytes / 1024} KiB.");
        DisplayState? state;
        try
        {
            state = JsonSerializer.Deserialize<DisplayState>(bytes, Json);
        }
        catch (JsonException)
        {
            state = null;
        }
        if (state is null)
            return Error.Validation("pos.display_state_invalid", "The display state is not valid JSON of the expected shape.");
        var normalized = state.Normalize();
        if (!normalized.IsSuccess)
            return normalized.Error!;
        return new DisplayStateAcceptedDto(store.Set(registerId, normalized.Value));
    }

    /// <summary>
    /// Anonymous read through the SECURITY DEFINER function <c>pos.public_display</c> (no tenant context exists here). With
    /// <paramref name="since"/> equal to the current version the request waits for a change (long poll) and answers null
    /// (204) when nothing changed. Unknown, malformed, expired and revoked tokens get the same 404 — also when the link is
    /// revoked while the request waits.
    /// </summary>
    public async Task<Result<PublicDisplayDto?>> GetPublicAsync(string token, long? since, CancellationToken ct)
    {
        if (await ResolveAsync(token, ct) is not { } link)
            return LinkNotFound;
        if (since is { } known && store.Get(link.RegisterId).Version == known)
        {
            var changed = await store.WaitForChangeAsync(link.RegisterId, known, TimeSpan.FromSeconds(options.Value.DisplayPollSeconds), ct);
            // The link may have been revoked or replaced meanwhile.
            if (await ResolveAsync(token, ct) is null)
                return LinkNotFound;
            if (!changed)
                return (PublicDisplayDto?)null;
        }
        var (version, state) = store.Get(link.RegisterId);
        var logo = (await branding.GetReceiptBrandingAsync(link.OrganizationId, ct)).LogoUrl;
        return new PublicDisplayDto(version, new DisplayShopDto(link.OrganizationName, logo), state);
    }

    private async Task<PublicDisplayRow?> ResolveAsync(string token, CancellationToken ct)
    {
        if (!OpaqueToken.LooksValid(token))
            return null;
        var hash = OpaqueToken.HashOf(token);
        return (await db.Database.SqlQuery<PublicDisplayRow>(
            $"SELECT register_id, organization_id, organization_name, expires_at FROM pos.public_display({hash})").ToListAsync(ct)).FirstOrDefault();
    }

    private Task<bool> RegisterExistsAsync(Guid registerId, CancellationToken ct) =>
        db.Set<Register>().AnyAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);

    private async Task<Error?> RegisterCheckAsync(Guid registerId, CancellationToken ct) =>
        !await RegisterExistsAsync(registerId, ct) ? Error.NotFound("pos.register_not_found", "Register not found.") : actor.CheckRegister(registerId);

    /// <summary>Reads at most <paramref name="max"/> bytes; null when the body is larger.</summary>
    private static async Task<byte[]?> ReadLimitedAsync(Stream body, int max, CancellationToken ct)
    {
        var buffer = new byte[max + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0)
                break;
            read += n;
        }
        return read > max ? null : buffer[..read];
    }
}

internal static class DisplayEndpoints
{
    public static void Map(IEndpointRouteBuilder app, RouteGroupBuilder sell)
    {
        sell.MapPost("/registers/{id:guid}/display-link", async (Guid id, DisplayService service, CancellationToken ct) =>
            (await service.CreateLinkAsync(id, ct)).ToHttp(l => Results.Created(l.Path, l)));

        sell.MapGet("/registers/{id:guid}/display-link", async (Guid id, DisplayService service, CancellationToken ct) =>
            (await service.GetLinkAsync(id, ct)).ToHttp(l => l is null ? Results.NoContent() : Results.Ok(l)));

        sell.MapDelete("/registers/{id:guid}/display-link", async (Guid id, DisplayService service, CancellationToken ct) =>
            (await service.RevokeLinkAsync(id, ct)).ToHttp(Results.NoContent));

        // The body is read by the service with a hard size limit (16 KiB), not bound by the framework.
        sell.MapPut("/registers/{id:guid}/display-state", async (Guid id, HttpRequest request, DisplayService service, CancellationToken ct) =>
            (await service.SetStateAsync(id, request.Body, ct)).ToHttp(Results.Ok));

        app.MapGet("/api/public/pos-display/{token}", async (string token, long? since, HttpResponse response, DisplayService service, CancellationToken ct) =>
            {
                response.Headers.CacheControl = "no-store";
                return (await service.GetPublicAsync(token, since, ct)).ToHttp(d => d is null ? Results.NoContent() : Results.Ok(d));
            })
            .WithTags("POS")
            .AllowAnonymous()
            .RequireRateLimiting(PosModule.PublicDisplayRateLimitPolicy);
    }
}
