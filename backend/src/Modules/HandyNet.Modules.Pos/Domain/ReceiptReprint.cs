using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// A reprint (copy) of an issued receipt. Append-only: the receipt itself never changes; the count of reprints is shown
/// on the copy ("Kopie", "Duplikat Nr. n") so it cannot be mistaken for the original.
/// </summary>
public sealed class ReceiptReprint
{
    private ReceiptReprint() { }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid ReceiptId { get; private set; }

    /// <summary>1 for the first copy, then 2, 3, … per receipt (unique).</summary>
    public int Sequence { get; private set; }
    public DateTimeOffset PrintedAt { get; private set; }
    public Guid PrintedBy { get; private set; }

    public static Result<ReceiptReprint> Record(Receipt receipt, int previousReprints, Guid userId, DateTimeOffset now)
    {
        if (!receipt.IsSealed)
            return Error.Conflict("pos.receipt_not_issued", "Only issued receipts can be reprinted.");
        return new ReceiptReprint
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = receipt.OrganizationId,
            ReceiptId = receipt.Id,
            Sequence = previousReprints + 1,
            PrintedAt = now,
            PrintedBy = userId,
        };
    }
}
