using System.Text.Json.Serialization;
using HandyNet.SharedKernel;

namespace HandyNet.Modules.Pos.Domain;

/// <summary>
/// Loyalty programme of an organization (Treuepunkte, ADR-0020), one row per organization. Points are earned per full euro
/// paid on eligible lines and redeemed as a price reduction worth <see cref="PointValueMinor"/> cents each.
/// </summary>
public sealed class LoyaltySettings : IAuditable
{
    public const int MaxPointsPerEuro = 100;
    public const int MaxPointValueMinor = 100;
    public const int MaxMinRedeemPoints = 1_000_000;

    private LoyaltySettings() { }

    public Guid OrganizationId { get; private set; }
    public bool Enabled { get; private set; }

    /// <summary>Points per full euro paid on eligible lines (0–100).</summary>
    public int PointsPerEuro { get; private set; }

    /// <summary>What one point is worth at redemption, in cents (1–100).</summary>
    public int PointValueMinor { get; private set; }

    /// <summary>Smallest number of points that can be redeemed in one sale.</summary>
    public int MinRedeemPoints { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public uint Version { get; private set; }

    /// <summary>What applies while an organization has not configured anything: no programme.</summary>
    public static LoyaltySettings Default(Guid organizationId) => new() { OrganizationId = organizationId, Enabled = false, PointsPerEuro = 0, PointValueMinor = 1 };

    public static Result<LoyaltySettings> Create(Guid organizationId, bool enabled, int pointsPerEuro, int pointValueMinor, int minRedeemPoints, Guid userId,
        DateTimeOffset now)
    {
        var settings = new LoyaltySettings { OrganizationId = organizationId };
        var applied = settings.Update(enabled, pointsPerEuro, pointValueMinor, minRedeemPoints, userId, now);
        return applied.IsSuccess ? settings : applied.Error!;
    }

    public Result Update(bool enabled, int pointsPerEuro, int pointValueMinor, int minRedeemPoints, Guid userId, DateTimeOffset now)
    {
        if (pointsPerEuro is < 0 or > MaxPointsPerEuro)
            return Error.Validation("pos.loyalty_settings", $"Points per euro must be between 0 and {MaxPointsPerEuro}.");
        if (pointValueMinor is < 1 or > MaxPointValueMinor)
            return Error.Validation("pos.loyalty_settings", $"A point is worth between 1 and {MaxPointValueMinor} cents.");
        if (minRedeemPoints is < 0 or > MaxMinRedeemPoints)
            return Error.Validation("pos.loyalty_settings", $"The minimum redemption must be between 0 and {MaxMinRedeemPoints} points.");
        Enabled = enabled;
        PointsPerEuro = pointsPerEuro;
        PointValueMinor = pointValueMinor;
        MinRedeemPoints = minRedeemPoints;
        UpdatedAt = now;
        UpdatedBy = userId;
        return Result.Success();
    }

    /// <summary>
    /// What <paramref name="points"/> are worth in a sale, or why they cannot be redeemed: the programme must be on, the sale
    /// needs a customer, at least the minimum must be redeemed. (Balance and eligible total are checked where they are known.)
    /// </summary>
    public Result<LoyaltyRedemption> Redemption(int points, bool hasCustomer)
    {
        if (points <= 0)
            return Error.Validation("pos.loyalty_points", "The number of points to redeem must be positive.");
        if (!Enabled)
            return Error.Conflict("pos.loyalty_disabled", "The loyalty programme is not enabled.");
        if (!hasCustomer)
            return Error.Validation("pos.loyalty_customer_required", "Points can only be redeemed for a customer.");
        if (points < MinRedeemPoints)
            return Error.Validation("pos.loyalty_min_points", $"At least {MinRedeemPoints} points must be redeemed at once.")
                .WithDetails(new Dictionary<string, object?> { ["minRedeemPoints"] = MinRedeemPoints });
        return new LoyaltyRedemption(points, checked((long)points * PointValueMinor));
    }

    /// <summary>Points for a sale: per full euro of the eligible gross after all discounts (floor), nothing while disabled.</summary>
    public int PointsFor(long eligibleGrossMinor) =>
        Enabled && eligibleGrossMinor > 0 ? checked((int)Math.Min(int.MaxValue, eligibleGrossMinor / 100 * PointsPerEuro)) : 0;
}

/// <summary>Points redeemed in a sale and the price reduction they are worth.</summary>
public sealed record LoyaltyRedemption(int Points, long ValueMinor);

/// <summary>Stored as text.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LoyaltyTransactionType>))]
public enum LoyaltyTransactionType
{
    /// <summary>+points for a sale.</summary>
    Earn,

    /// <summary>−points redeemed in a sale.</summary>
    Redeem,

    /// <summary>−points: earned points taken back by a storno or return (at most the current balance).</summary>
    ReverseEarn,

    /// <summary>+points: redeemed points given back by a storno.</summary>
    ReverseRedeem,

    /// <summary>±points: manual correction by a manager, with a reason.</summary>
    Adjust,
}

/// <summary>
/// The points of one customer. Changes only together with an append-only <see cref="LoyaltyTransaction"/>; never negative
/// (CHECK), and the database verifies at commit that the balance equals the sum of the customer's transactions.
/// </summary>
public sealed class LoyaltyAccount
{
    public const int MaxReasonLength = 200;
    public const int MaxAdjustment = 1_000_000;

    private LoyaltyAccount() { }

    public Guid OrganizationId { get; private set; }
    public Guid CustomerId { get; private set; }
    public int Points { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public uint Version { get; private set; }

    public static LoyaltyAccount Open(Guid organizationId, Guid customerId, DateTimeOffset now) =>
        new() { OrganizationId = organizationId, CustomerId = customerId, Points = 0, UpdatedAt = now };

    public Result<LoyaltyTransaction> Redeem(int points, Guid receiptId, Guid userId, DateTimeOffset now)
    {
        if (points <= 0)
            return Error.Validation("pos.loyalty_points", "The number of points to redeem must be positive.");
        if (points > Points)
            return Error.Conflict("pos.loyalty_insufficient_points", $"The customer has {Points} points; {points} cannot be redeemed.")
                .WithDetails(new Dictionary<string, object?> { ["points"] = Points });
        return Apply(LoyaltyTransactionType.Redeem, -points, receiptId, null, userId, now);
    }

    public LoyaltyTransaction? Earn(int points, Guid receiptId, Guid userId, DateTimeOffset now) =>
        points <= 0 ? null : Apply(LoyaltyTransactionType.Earn, points, receiptId, null, userId, now);

    /// <summary>Gives redeemed points back (storno).</summary>
    public LoyaltyTransaction? ReverseRedeem(int points, Guid receiptId, Guid userId, DateTimeOffset now) =>
        points <= 0 ? null : Apply(LoyaltyTransactionType.ReverseRedeem, points, receiptId, null, userId, now);

    /// <summary>
    /// Takes earned points back (storno, return) — at most what the customer still has; the transaction records the actual
    /// figure. Null when nothing can be taken.
    /// </summary>
    public LoyaltyTransaction? ReverseEarn(int points, Guid receiptId, Guid userId, DateTimeOffset now)
    {
        var taken = Math.Min(Math.Max(points, 0), Points);
        return taken == 0 ? null : Apply(LoyaltyTransactionType.ReverseEarn, -taken, receiptId, null, userId, now);
    }

    /// <summary>Manual correction by a manager: signed points, a reason, never below zero.</summary>
    public Result<LoyaltyTransaction> Adjust(int points, string? reason, Guid userId, DateTimeOffset now)
    {
        if (points == 0 || Math.Abs((long)points) > MaxAdjustment)
            return Error.Validation("pos.loyalty_points", $"An adjustment is between 1 and {MaxAdjustment} points, up or down.");
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxReasonLength)
            return Error.Validation("pos.reason", $"A reason (max {MaxReasonLength} characters) is required.");
        if (Points + (long)points < 0)
            return Error.Conflict("pos.loyalty_insufficient_points", $"The customer has {Points} points; {-points} cannot be taken.")
                .WithDetails(new Dictionary<string, object?> { ["points"] = Points });
        if (Points + (long)points > int.MaxValue)
            return Error.Validation("pos.loyalty_points", "The balance would be too large.");
        return Apply(LoyaltyTransactionType.Adjust, points, null, text, userId, now);
    }

    private LoyaltyTransaction Apply(LoyaltyTransactionType type, int points, Guid? receiptId, string? reason, Guid userId, DateTimeOffset now)
    {
        Points = checked(Points + points);
        UpdatedAt = now;
        return new LoyaltyTransaction(this, type, points, receiptId, reason, userId, now);
    }
}

/// <summary>One change of a customer's points. Append-only (trigger).</summary>
public sealed class LoyaltyTransaction : IAuditable
{
    private LoyaltyTransaction() { }

    internal LoyaltyTransaction(LoyaltyAccount account, LoyaltyTransactionType type, int points, Guid? receiptId, string? reason, Guid userId, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        OrganizationId = account.OrganizationId;
        CustomerId = account.CustomerId;
        Type = type;
        Points = points;
        BalanceAfter = account.Points;
        ReceiptId = receiptId;
        Reason = reason;
        CreatedAt = now;
        CreatedBy = userId;
    }

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid CustomerId { get; private set; }
    public LoyaltyTransactionType Type { get; private set; }

    /// <summary>Signed.</summary>
    public int Points { get; private set; }
    public int BalanceAfter { get; private set; }
    public Guid? ReceiptId { get; private set; }
    public string? Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
}

public static class LoyaltyMath
{
    /// <summary>
    /// Splits a price reduction across lines in proportion to their gross (largest remainder, ties in line order), so the
    /// parts add up to exactly <paramref name="totalMinor"/> and no line gets more than its own gross.
    /// </summary>
    public static IReadOnlyList<long> Allocate(long totalMinor, IReadOnlyList<long> lineGrossMinor)
    {
        var sum = lineGrossMinor.Sum();
        if (totalMinor < 0 || totalMinor > sum || lineGrossMinor.Any(g => g < 0))
            throw new ArgumentOutOfRangeException(nameof(totalMinor), "The reduction must be between 0 and the sum of the lines.");
        var parts = new long[lineGrossMinor.Count];
        if (totalMinor == 0 || sum == 0)
            return parts;
        var remainders = new (int Index, System.Numerics.BigInteger Remainder)[lineGrossMinor.Count];
        long allocated = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var product = (System.Numerics.BigInteger)totalMinor * lineGrossMinor[i];
            parts[i] = (long)(product / sum);
            remainders[i] = (i, product % sum);
            allocated += parts[i];
        }
        // floor(total × g / sum) ≤ g, and a line gets +1 only if it had a remainder, i.e. its floor was below its share ≤ g.
        foreach (var (index, _) in remainders.OrderByDescending(r => r.Remainder).ThenBy(r => r.Index).Take((int)(totalMinor - allocated)))
            parts[index]++;
        return parts;
    }

    /// <summary>
    /// Earned points to take back for a return: pro rata to the returned share of the eligible gross (floor), as the difference
    /// of the cumulative figures so several partial returns never take more than was earned.
    /// </summary>
    public static int EarnedToReverse(int earned, long eligibleGrossMinor, long returnedBeforeMinor, long returnedNowMinor)
    {
        if (earned <= 0 || eligibleGrossMinor <= 0 || returnedNowMinor <= 0)
            return 0;
        static long Share(int earned, long part, long of) => (long)((System.Numerics.BigInteger)earned * Math.Min(part, of) / of);
        return (int)(Share(earned, returnedBeforeMinor + returnedNowMinor, eligibleGrossMinor) - Share(earned, returnedBeforeMinor, eligibleGrossMinor));
    }
}
