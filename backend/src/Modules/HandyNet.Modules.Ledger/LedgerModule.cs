using HandyNet.Modules.Ledger.Application;
using HandyNet.Modules.Ledger.Contracts;
using HandyNet.Modules.Ledger.Infrastructure;
using HandyNet.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace HandyNet.Modules.Ledger;

/// <summary>Immutable double-entry ledger. Endpoints for balances live in the Payments module.</summary>
public static class LedgerModule
{
    public static IServiceCollection AddLedgerModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModel, LedgerModel>();
        services.AddScoped<ILedger, LedgerService>();
        return services;
    }
}
