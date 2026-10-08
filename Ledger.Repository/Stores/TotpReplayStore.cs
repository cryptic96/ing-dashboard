using Ledger.Domain.Auth;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Repository.Stores;

/// <summary>
/// Claims a one-time code time step with a single conditional update on the login row, so the database decides which of two
/// simultaneous submissions wins and a restart or a second host cannot forget a step that was used.
/// </summary>
public class TotpReplayStore(LedgerDbContext dbContext) : ITotpReplayStore
{
    /// <inheritdoc />
    public async Task<bool> TryClaimAsync(Guid loginId, long timeStep, CancellationToken cancellationToken)
    {
        var changed = await dbContext.Users
            .Where(login => login.Id == loginId && (login.LastTotpStep == null || login.LastTotpStep < timeStep))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(login => login.LastTotpStep, timeStep),
                cancellationToken);

        return changed == 1;
    }
}
