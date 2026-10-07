using Ledger.Domain.Auth;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>
/// Claims a one-time code with a single conditional update on the login row, so the database decides which of two simultaneous
/// submissions wins and a restart or a second host cannot forget a code that was used.
/// </summary>
public class TotpReplayStore(LedgerDbContext dbContext) : ITotpReplayStore
{
    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(
        Guid loginId,
        byte[] codeSha256,
        DateTimeOffset now,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var oldest = now - window;

        var changed = await dbContext.Users
            .Where(login => login.Id == loginId
                && (login.LastTotpCodeSha256 == null
                    || login.LastTotpCodeSha256 != codeSha256
                    || login.LastTotpAcceptedAt == null
                    || login.LastTotpAcceptedAt < oldest))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(login => login.LastTotpCodeSha256, codeSha256)
                    .SetProperty(login => login.LastTotpAcceptedAt, now),
                cancellationToken);

        return changed == 1;
    }
}
