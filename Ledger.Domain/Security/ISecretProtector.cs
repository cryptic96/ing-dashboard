namespace Ledger.Domain.Security;

/// <summary>Protects and unprotects short secret payloads at rest.</summary>
public interface ISecretProtector
{
    /// <summary>Protects the given plaintext, returning an opaque payload suitable for storage.</summary>
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect(string)"/>, returning the original plaintext.</summary>
    string Unprotect(string protectedPayload);
}
