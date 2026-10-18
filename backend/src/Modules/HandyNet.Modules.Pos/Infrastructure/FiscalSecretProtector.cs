using System.Security.Cryptography;
using HandyNet.Modules.Pos.Application;
using Microsoft.AspNetCore.DataProtection;

namespace HandyNet.Modules.Pos.Infrastructure;

/// <summary>
/// ASP.NET Data Protection for fiscal secrets at rest (fiskaly API key/secret, simulated TSE private keys). The key ring
/// must be persisted and shared by all API instances (<c>DataProtection:KeysPath</c>, see docs/operations.md); losing it
/// makes stored fiskaly credentials unreadable (enter them again) and replaces simulated TSEs' keys only for new TSEs.
/// </summary>
internal sealed class FiscalSecretProtector(IDataProtectionProvider provider) : IFiscalSecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("HandyNet.Pos.Fiscal.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string protectedValue)
    {
        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
