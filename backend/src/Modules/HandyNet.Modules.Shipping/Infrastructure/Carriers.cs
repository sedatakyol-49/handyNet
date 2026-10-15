using System.Security.Cryptography;
using System.Text;
using HandyNet.Modules.Shipping.Domain;
using HandyNet.SharedKernel;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Shipping.Infrastructure;

public sealed class ShippingOptions
{
    public const string Section = "Shipping";

    /// <summary>Carriers sellers may choose. Real carriers appear here only once an adapter and credentials exist.</summary>
    public List<Carrier> EnabledCarriers { get; set; } = [];

    /// <summary>Per-carrier limits; see <see cref="CarrierProfile"/>.</summary>
    public Dictionary<Carrier, CarrierProfile> Carriers { get; set; } = [];

    /// <summary>Simulated carrier: minutes from handover to delivery (0 = delivered on next sync).</summary>
    public int SimulatedTransitMinutes { get; set; } = 2;

    /// <summary>How often active shipments are polled for tracking updates.</summary>
    public int TrackingSyncMinutes { get; set; } = 15;

    public CarrierProfile ProfileFor(Carrier carrier) => Carriers.TryGetValue(carrier, out var p) ? p : new CarrierProfile();
}

public sealed record LabelRequest(Guid ShipmentId, string OrderNumber, Parcel Parcel, bool Insured, long DeclaredValueMinor, string? TrackingNumber);

public sealed record LabelResult(string TrackingNumber, byte[]? LabelPdf);

/// <summary>
/// One implementation per carrier API (DHL Parcel DE, DPD, GLS, UPS). Adapters translate HandyNet requests
/// and carrier events; they never decide business state — the Shipment aggregate does.
/// </summary>
public interface ICarrierAdapter
{
    Carrier Carrier { get; }

    Task<Result<LabelResult>> CreateLabelAsync(LabelRequest request, CancellationToken ct);

    Task<Result> CancelAsync(string trackingNumber, CancellationToken ct);

    /// <summary>All events known for the parcel (adapters may return the full history; duplicates are ignored).</summary>
    Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(string trackingNumber, DateTimeOffset? handedOverAt, CancellationToken ct);
}

/// <summary>Seller booked the parcel outside HandyNet. No label, no tracking API; delivery is confirmed by the buyer.</summary>
internal sealed class ManualCarrierAdapter : ICarrierAdapter
{
    public Carrier Carrier => Carrier.Manual;

    public Task<Result<LabelResult>> CreateLabelAsync(LabelRequest request, CancellationToken ct) =>
        Task.FromResult<Result<LabelResult>>(string.IsNullOrWhiteSpace(request.TrackingNumber)
            ? Error.Validation("shipment.tracking_required", "Enter the tracking number from the carrier.")
            : new LabelResult(request.TrackingNumber.Trim(), null));

    public Task<Result> CancelAsync(string trackingNumber, CancellationToken ct) => Task.FromResult(Result.Success());

    public Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(string trackingNumber, DateTimeOffset? handedOverAt, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CarrierTrackingEvent>>([]);
}

/// <summary>
/// Development and demo carrier. Labels say "SIMULATION – KEIN GÜLTIGES VERSANDETIKETT"; tracking is derived from
/// the handover time. Must never be enabled in production (startup check in ShippingModule).
/// </summary>
internal sealed class SimulatedCarrierAdapter(IOptions<ShippingOptions> options, IClock clock) : ICarrierAdapter
{
    public Carrier Carrier => Carrier.Simulated;

    public Task<Result<LabelResult>> CreateLabelAsync(LabelRequest request, CancellationToken ct)
    {
        var tracking = "SIM" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var pdf = MinimalPdf.Create(
            "SIMULATION - KEIN GUELTIGES VERSANDETIKETT",
            $"Sendung {tracking}",
            $"Bestellung {request.OrderNumber}",
            $"Gewicht {request.Parcel.WeightGrams / 1000m:0.00} kg  Versichert: {(request.Insured ? "ja" : "nein")}");
        return Task.FromResult<Result<LabelResult>>(new LabelResult(tracking, pdf));
    }

    public Task<Result> CancelAsync(string trackingNumber, CancellationToken ct) => Task.FromResult(Result.Success());

    public Task<IReadOnlyList<CarrierTrackingEvent>> GetTrackingAsync(string trackingNumber, DateTimeOffset? handedOverAt, CancellationToken ct)
    {
        if (handedOverAt is not { } start)
            return Task.FromResult<IReadOnlyList<CarrierTrackingEvent>>([]);

        var transit = TimeSpan.FromMinutes(options.Value.SimulatedTransitMinutes);
        var now = clock.UtcNow;
        var all = new List<CarrierTrackingEvent>
        {
            new($"{trackingNumber}-PU", start, TrackingKind.PickedUp, "Sendung vom Absender übernommen", "Paketzentrum"),
            new($"{trackingNumber}-IT", start + transit / 2, TrackingKind.InTransit, "Sendung im Zustellnetz", "Paketzentrum"),
            new($"{trackingNumber}-DL", start + transit, TrackingKind.Delivered, "Sendung zugestellt", "Empfänger"),
        };
        return Task.FromResult<IReadOnlyList<CarrierTrackingEvent>>(all.Where(e => e.OccurredAt <= now).ToList());
    }
}

/// <summary>Writes a one-page PDF with plain text lines (enough for a clearly marked simulation label).</summary>
internal static class MinimalPdf
{
    public static byte[] Create(params string[] lines)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var content = new StringBuilder("BT /F1 14 Tf 40 360 Td 18 TL\n");
        foreach (var line in lines)
            content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
        content.Append("ET");

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 420 420] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
