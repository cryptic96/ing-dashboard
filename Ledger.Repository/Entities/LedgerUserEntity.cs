using Microsoft.AspNetCore.Identity;

namespace Ledger.Repository.Entities;

/// <summary>A person who can sign in to approve Claude's access to the ledger. Logins are rows, so adding another is a data change.</summary>
public class LedgerUserEntity : IdentityUser<Guid>
{
    /// <summary>When the login was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The latest 30-second time step of a second-factor code that was accepted, so that step and every earlier one are refused from then on.</summary>
    public long? LastTotpStep { get; set; }
}
