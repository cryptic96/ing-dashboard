using System.Security.Cryptography;
using System.Text;
using Ledger.Repository.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>
/// The login store. It behaves like the stock Identity store except that the authenticator secret of a login is encrypted with
/// the application's Data Protection key ring before it is written and decrypted when it is read, so a copy of the database
/// without the key ring cannot be used to generate one-time codes. A stored value that cannot be decrypted, such as one written
/// under another key ring, reads as a fresh random secret that nobody holds: the login still asks for a second factor, and no
/// code can satisfy it, so the second factor fails closed instead of the login silently losing it.
/// </summary>
public sealed class LedgerUserStore(
    LedgerDbContext context,
    IDataProtectionProvider dataProtectionProvider,
    IdentityErrorDescriber? describer = null)
    : UserOnlyStore<LedgerUserEntity, LedgerDbContext, Guid, IdentityUserClaim<Guid>, IdentityUserLogin<Guid>, IdentityUserToken<Guid>>(context, describer)
{
    /// <summary>The purpose that separates authenticator secrets from everything else the key ring protects.</summary>
    public const string ProtectorPurpose = "Ledger.Login.AuthenticatorKey.v1";

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int UnheldSecretBytes = 20;

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);

    /// <inheritdoc />
    public override Task SetAuthenticatorKeyAsync(LedgerUserEntity user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        return base.SetAuthenticatorKeyAsync(user, _protector.Protect(key), cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<string?> GetAuthenticatorKeyAsync(LedgerUserEntity user, CancellationToken cancellationToken)
    {
        var stored = await base.GetAuthenticatorKeyAsync(user, cancellationToken);

        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(stored);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return NewUnheldSecret();
        }
    }

    private static string NewUnheldSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(UnheldSecretBytes);
        var secret = new StringBuilder(UnheldSecretBytes * 8 / 5);
        var buffer = 0;
        var bits = 0;

        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                secret.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        return secret.ToString();
    }
}
