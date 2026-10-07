using Microsoft.AspNetCore.Identity;

namespace Ledger.Repository.Entities;

/// <summary>A person who can sign in to approve Claude's access to the ledger. Logins are rows, so adding another is a data change.</summary>
public class LedgerUserEntity : IdentityUser<Guid>
{
    /// <summary>When the login was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>SHA-256 of the last second-factor code that was accepted, so the same code cannot be used twice.</summary>
    public byte[]? LastTotpCodeSha256 { get; set; }

    /// <summary>When the last second-factor code was accepted.</summary>
    public DateTimeOffset? LastTotpAcceptedAt { get; set; }
}
