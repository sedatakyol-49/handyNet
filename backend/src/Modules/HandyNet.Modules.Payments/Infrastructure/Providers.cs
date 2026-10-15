using System.Collections.Concurrent;
using System.Security.Cryptography;
using HandyNet.Modules.Payments.Domain;
using HandyNet.SharedKernel;
using Microsoft.Extensions.Options;

namespace HandyNet.Modules.Payments.Infrastructure;

public sealed class PaymentOptions
{
    public const string Section = "Payments";

    /// <summary>Empty = online payment disabled. Simulated is refused at startup in Production.</summary>
    public List<PaymentProvider> EnabledProviders { get; set; } = [];

    /// <summary>Platform commission on the net goods value, in basis points (300 = 3 %). Snapshotted per payment.</summary>
    public int CommissionBasisPoints { get; set; } = 300;

    /// <summary>VAT rate applied to the commission (German standard rate).</summary>
    public int CommissionVatBasisPoints { get; set; } = 1900;

    /// <summary>Public base URL of the API (for provider webhooks) and of the web app (for the buyer's return page).</summary>
    public string ApiBaseUrl { get; set; } = "http://localhost:5170";
    public string WebBaseUrl { get; set; } = "http://localhost:4200";
}

public sealed record ProviderPaymentRequest(Guid PaymentId, Guid OrderId, string OrderNumber, long AmountMinor, string Currency,
    Guid SellerOrganizationId, long ApplicationFeeMinor, string ReturnUrl, string WebhookUrl);

public sealed record ProviderPayment(string ProviderPaymentId, string CheckoutUrl);

/// <summary>
/// A licensed payment service provider that holds buyer funds and pays sellers (marketplace "connect" model).
/// HandyNet never receives third-party money into its own bank account; it instructs the provider and records
/// the resulting obligations in the ledger.
/// </summary>
public interface IPaymentProvider
{
    PaymentProvider Provider { get; }

    Task<Result<ProviderPayment>> CreatePaymentAsync(ProviderPaymentRequest request, CancellationToken ct);

    /// <summary>Authoritative status lookup (used for every webhook).</summary>
    Task<ProviderPaymentStatus> GetStatusAsync(string providerPaymentId, CancellationToken ct);

    Task<Result> CancelAsync(string providerPaymentId, CancellationToken ct);

    Task<Result> RefundAsync(string providerPaymentId, long amountMinor, CancellationToken ct);

    /// <summary>Releases the seller's share to the seller's provider account. Must be idempotent per payment.</summary>
    Task<Result<string>> ReleaseToSellerAsync(string providerPaymentId, Guid sellerOrganizationId, long amountMinor, CancellationToken ct);
}

/// <summary>
/// In-memory stand-in for a PSP, for development and automated tests only. The "checkout page" is an API endpoint
/// that lets a developer choose the outcome. State is lost on restart (open payments then stay open).
/// </summary>
public sealed class SimulatedPaymentProvider(IOptions<PaymentOptions> options) : IPaymentProvider
{
    private static readonly ConcurrentDictionary<string, ProviderPaymentStatus> States = new();
    private static readonly ConcurrentDictionary<string, string> Releases = new();
    private static readonly ConcurrentDictionary<string, string> ReturnUrls = new();

    public PaymentProvider Provider => PaymentProvider.Simulated;

    public Task<Result<ProviderPayment>> CreatePaymentAsync(ProviderPaymentRequest request, CancellationToken ct)
    {
        var id = "sim_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        States[id] = ProviderPaymentStatus.Open;
        ReturnUrls[id] = request.ReturnUrl;
        var checkout = $"{options.Value.ApiBaseUrl.TrimEnd('/')}/api/payments/simulated/{id}";
        return Task.FromResult<Result<ProviderPayment>>(new ProviderPayment(id, checkout));
    }

    public Task<ProviderPaymentStatus> GetStatusAsync(string providerPaymentId, CancellationToken ct) =>
        Task.FromResult(States.GetValueOrDefault(providerPaymentId, ProviderPaymentStatus.Open));

    public Task<Result> CancelAsync(string providerPaymentId, CancellationToken ct)
    {
        States.TryUpdate(providerPaymentId, ProviderPaymentStatus.Canceled, ProviderPaymentStatus.Open);
        return Task.FromResult(Result.Success());
    }

    public Task<Result> RefundAsync(string providerPaymentId, long amountMinor, CancellationToken ct) =>
        Task.FromResult(States.GetValueOrDefault(providerPaymentId) == ProviderPaymentStatus.Paid
            ? Result.Success()
            : Result.Failure(Error.Conflict("psp.not_paid", "The provider has no paid payment to refund.")));

    public Task<Result<string>> ReleaseToSellerAsync(string providerPaymentId, Guid sellerOrganizationId, long amountMinor, CancellationToken ct) =>
        Task.FromResult<Result<string>>(Releases.GetOrAdd(providerPaymentId, _ => "simtr_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))));

    /// <summary>What the simulated checkout page does when the developer picks an outcome.</summary>
    public static bool Complete(string providerPaymentId, ProviderPaymentStatus outcome) =>
        States.TryUpdate(providerPaymentId, outcome, ProviderPaymentStatus.Open);

    /// <summary>Where the buyer goes after the simulated checkout (the order page), if known.</summary>
    public static string? ReturnUrlFor(string providerPaymentId) => ReturnUrls.GetValueOrDefault(providerPaymentId);
}
