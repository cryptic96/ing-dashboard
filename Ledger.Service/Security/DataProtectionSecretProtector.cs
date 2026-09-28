using Ledger.Domain.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Ledger.Service.Security;

/// <summary>Protects and unprotects secrets using ASP.NET Core Data Protection under a fixed purpose.</summary>
public class DataProtectionSecretProtector : ISecretProtector
{
    private const string Purpose = "HouseholdLedger.Secrets";

    private readonly IDataProtector _protector;

    /// <summary>Creates the protector under the fixed purpose string shared by every instance.</summary>
    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public string Protect(string plaintext) => _protector.Protect(plaintext);

    /// <inheritdoc />
    public string Unprotect(string protectedPayload) => _protector.Unprotect(protectedPayload);
}
