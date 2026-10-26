using FluentValidation;
using HandyNet.Modules.Pos.Domain;
using HandyNet.Persistence;
using HandyNet.SharedKernel;
using HandyNet.SharedKernel.Tenancy;
using HandyNet.SharedKernel.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace HandyNet.Modules.Pos.Application;

/// <summary>Create/update body of a printer profile. Version: required on update (optimistic concurrency).</summary>
public sealed record PrinterProfileRequest(
    string Name,
    PrinterConnection Connection,
    int PaperWidthMm,
    int CharsPerLine,
    bool AutoPrint,
    bool OpenDrawerOnCash,
    int Copies,
    bool CutPaper,
    bool PrintQr = true,
    PrinterCodepage Codepage = PrinterCodepage.Cp858,
    string? Host = null,
    int? Port = null,
    bool UseHttps = false,
    string? EposDeviceId = null,
    string? BridgeUrl = null,
    int? UsbVendorId = null,
    int? UsbProductId = null,
    int? BaudRate = null,
    bool? IsDefault = null,
    uint? Version = null)
{
    public PrinterProfileSettings ToSettings() =>
        new(Name, Connection, PaperWidthMm, CharsPerLine, AutoPrint, OpenDrawerOnCash, Copies, CutPaper, PrintQr, Codepage, Host, Port, UseHttps,
            EposDeviceId, BridgeUrl, UsbVendorId, UsbProductId, BaudRate);
}

public sealed record PrinterProfileDto(
    Guid Id,
    Guid RegisterId,
    string Name,
    PrinterConnection Connection,
    int PaperWidthMm,
    int CharsPerLine,
    bool AutoPrint,
    bool OpenDrawerOnCash,
    int Copies,
    bool CutPaper,
    bool PrintQr,
    PrinterCodepage Codepage,
    string? Host,
    int? Port,
    bool UseHttps,
    string? EposDeviceId,
    string? BridgeUrl,
    int? UsbVendorId,
    int? UsbProductId,
    int? BaudRate,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    uint Version);

internal sealed class PrinterProfileValidator : AbstractValidator<PrinterProfileRequest>
{
    public PrinterProfileValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(PrinterProfile.MaxNameLength);
        RuleFor(x => x.Connection).IsInEnum();
        RuleFor(x => x.Codepage).IsInEnum();
        RuleFor(x => x.PaperWidthMm).Must(w => PrinterProfile.PaperWidths.Contains(w)).WithMessage("The paper width must be 58 or 80 mm.").WithErrorCode("validation.paperWidth");
        RuleFor(x => x.CharsPerLine).InclusiveBetween(PrinterProfile.MinCharsPerLine, PrinterProfile.MaxCharsPerLine);
        RuleFor(x => x.Copies).InclusiveBetween(1, PrinterProfile.MaxCopies);
        RuleFor(x => x.Host).MaximumLength(PrinterProfile.MaxHostLength);
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).When(x => x.Port is not null);
        RuleFor(x => x.EposDeviceId).MaximumLength(PrinterProfile.MaxDeviceIdLength);
        RuleFor(x => x.BridgeUrl).MaximumLength(PrinterProfile.MaxBridgeUrlLength);
        RuleFor(x => x.UsbVendorId).InclusiveBetween(0, 0xFFFF).When(x => x.UsbVendorId is not null);
        RuleFor(x => x.UsbProductId).InclusiveBetween(0, 0xFFFF).When(x => x.UsbProductId is not null);
    }
}

/// <summary>
/// Printer profiles per register (ADR-0018). Read with <c>pos.sell</c> (every till needs its printer), written with
/// <c>pos.manage</c>. The server stores configuration only and never contacts a printer or a bridge.
/// </summary>
internal sealed class PrinterProfileService(HandyNetDbContext db, ITenantContext tenant, IClock clock)
{
    private Guid OrganizationId => tenant.RequiredOrganizationId;

    private static readonly Error NotFound = Error.NotFound("pos.printer_not_found", "Printer profile not found.");

    public async Task<Result<IReadOnlyList<PrinterProfileDto>>> ListAsync(Guid registerId, CancellationToken ct)
    {
        if (!await RegisterExistsAsync(registerId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");
        var profiles = await db.Set<PrinterProfile>().AsNoTracking()
            .Where(p => p.OrganizationId == OrganizationId && p.RegisterId == registerId)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct);
        return profiles.Select(ToDto).ToList();
    }

    public async Task<Result<PrinterProfileDto>> GetAsync(Guid id, CancellationToken ct) =>
        await db.Set<PrinterProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct) is { } p
            ? ToDto(p)
            : NotFound;

    public async Task<Result<PrinterProfileDto>> CreateAsync(Guid registerId, PrinterProfileRequest request, CancellationToken ct)
    {
        if (!await RegisterExistsAsync(registerId, ct))
            return Error.NotFound("pos.register_not_found", "Register not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.Set<PrinterProfile>().Where(p => p.OrganizationId == OrganizationId && p.RegisterId == registerId).ToListAsync(ct);
        if (existing.Count >= PrinterProfile.MaxProfilesPerRegister)
            return Error.Conflict("pos.printer_limit", $"A register can have at most {PrinterProfile.MaxProfilesPerRegister} printer profiles.");
        // The first profile of a register becomes its default.
        var makeDefault = existing.Count == 0 || request.IsDefault == true;
        var profile = PrinterProfile.Create(OrganizationId, registerId, request.ToSettings(), makeDefault, clock.UtcNow);
        if (!profile.IsSuccess)
            return profile.Error!;
        if (makeDefault)
            await ClearDefaultAsync(existing, ct);
        db.Add(profile.Value);
        var saved = await SaveAsync(ct);
        if (!saved.IsSuccess)
            return saved.Error!;
        await tx.CommitAsync(ct);
        return ToDto(profile.Value);
    }

    public async Task<Result<PrinterProfileDto>> UpdateAsync(Guid id, PrinterProfileRequest request, CancellationToken ct)
    {
        if (request.Version is not { } expected)
            return Error.Validation("pos.printer_version", "The version of the profile is required.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<PrinterProfile>().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (profile is null)
            return NotFound;
        db.Entry(profile).Property(p => p.Version).OriginalValue = expected;
        var updated = profile.Update(request.ToSettings(), clock.UtcNow);
        if (!updated.IsSuccess)
            return updated.Error!;
        if (request.IsDefault == true && !profile.IsDefault)
        {
            await ClearDefaultAsync(await OthersAsync(profile, ct), ct);
            profile.SetDefault(true);
        }
        var saved = await SaveAsync(ct);
        if (!saved.IsSuccess)
            return saved.Error!;
        await tx.CommitAsync(ct);
        return ToDto(profile);
    }

    /// <summary>Makes the profile the register's default (the previous default loses the flag in the same transaction).</summary>
    public async Task<Result<PrinterProfileDto>> SetDefaultAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<PrinterProfile>().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (profile is null)
            return NotFound;
        if (!profile.IsDefault)
        {
            await ClearDefaultAsync(await OthersAsync(profile, ct), ct);
            profile.SetDefault(true);
            var saved = await SaveAsync(ct);
            if (!saved.IsSuccess)
                return saved.Error!;
        }
        await tx.CommitAsync(ct);
        return ToDto(profile);
    }

    /// <summary>Deletes the profile; when it was the default, the remaining profile with the first name becomes the default.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var profile = await db.Set<PrinterProfile>().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == OrganizationId, ct);
        if (profile is null)
            return NotFound;
        var others = await OthersAsync(profile, ct);
        db.Remove(profile);
        var saved = await SaveAsync(ct);
        if (!saved.IsSuccess)
            return saved.Error!;
        if (profile.IsDefault && others.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault() is { } next)
        {
            next.SetDefault(true);
            saved = await SaveAsync(ct);
            if (!saved.IsSuccess)
                return saved.Error!;
        }
        await tx.CommitAsync(ct);
        return Result.Success();
    }

    private Task<bool> RegisterExistsAsync(Guid registerId, CancellationToken ct) =>
        db.Set<Register>().AnyAsync(r => r.Id == registerId && r.OrganizationId == OrganizationId, ct);

    private Task<List<PrinterProfile>> OthersAsync(PrinterProfile profile, CancellationToken ct) =>
        db.Set<PrinterProfile>().Where(p => p.OrganizationId == OrganizationId && p.RegisterId == profile.RegisterId && p.Id != profile.Id).ToListAsync(ct);

    /// <summary>Clears the old default first (own statement), so the partial unique index never sees two defaults.</summary>
    private async Task ClearDefaultAsync(IEnumerable<PrinterProfile> profiles, CancellationToken ct)
    {
        var current = profiles.Where(p => p.IsDefault).ToList();
        if (current.Count == 0)
            return;
        foreach (var p in current)
            p.SetDefault(false);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Result> SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation("ux_printer_profiles_one_default"))
        {
            return Error.Conflict("concurrency", "The printer profiles were changed concurrently. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return Error.Conflict("pos.printer_name_taken", "This register already has a printer profile with this name.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrency", "The printer profile was changed concurrently. Reload and try again.");
        }
    }

    internal static PrinterProfileDto ToDto(PrinterProfile p) =>
        new(p.Id, p.RegisterId, p.Name, p.Connection, p.PaperWidthMm, p.CharsPerLine, p.AutoPrint, p.OpenDrawerOnCash, p.Copies, p.CutPaper, p.PrintQr,
            p.Codepage, p.Host, p.Port, p.UseHttps, p.EposDeviceId, p.BridgeUrl, p.UsbVendorId, p.UsbProductId, p.BaudRate, p.IsDefault, p.CreatedAt,
            p.UpdatedAt, p.Version);
}

internal static class PrinterEndpoints
{
    public static void Map(RouteGroupBuilder sell, RouteGroupBuilder manage)
    {
        sell.MapGet("/registers/{id:guid}/printers", async (Guid id, PrinterProfileService service, CancellationToken ct) =>
            (await service.ListAsync(id, ct)).ToHttp(Results.Ok));

        sell.MapGet("/printers/{id:guid}", async (Guid id, PrinterProfileService service, CancellationToken ct) =>
            (await service.GetAsync(id, ct)).ToHttp(Results.Ok));

        manage.MapPost("/registers/{id:guid}/printers", async (Guid id, PrinterProfileRequest request, IValidator<PrinterProfileRequest> validator,
                PrinterProfileService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct)
            ?? (await service.CreateAsync(id, request, ct)).ToHttp(p => Results.Created($"/api/pos/printers/{p.Id}", p)));

        manage.MapPut("/printers/{id:guid}", async (Guid id, PrinterProfileRequest request, IValidator<PrinterProfileRequest> validator,
                PrinterProfileService service, CancellationToken ct) =>
            await validator.ProblemIfInvalidAsync(request, ct) ?? (await service.UpdateAsync(id, request, ct)).ToHttp(Results.Ok));

        manage.MapPost("/printers/{id:guid}/default", async (Guid id, PrinterProfileService service, CancellationToken ct) =>
            (await service.SetDefaultAsync(id, ct)).ToHttp(Results.Ok));

        manage.MapDelete("/printers/{id:guid}", async (Guid id, PrinterProfileService service, CancellationToken ct) =>
            (await service.DeleteAsync(id, ct)).ToHttp(Results.NoContent));
    }
}
