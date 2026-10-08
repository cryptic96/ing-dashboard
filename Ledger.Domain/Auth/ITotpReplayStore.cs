namespace Ledger.Domain.Auth;

/// <summary>
/// Remembers the highest one-time code time step each login has used, so a code that was accepted once, and every code from an
/// earlier step, cannot be accepted afterwards.
/// </summary>
public interface ITotpReplayStore
{
    /// <summary>
    /// Records the time step as used by the login unless that step or a later one was already recorded. Returns true when the
    /// step was recorded, which makes the caller the only one that may act on the code, and false when it was not newer than the
    /// last accepted step. Two calls with the same step at the same moment yield exactly one true.
    /// </summary>
    /// <param name="loginId">The login the code was presented for.</param>
    /// <param name="timeStep">The 30-second time step the code belongs to.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<bool> TryClaimAsync(Guid loginId, long timeStep, CancellationToken cancellationToken);

    /// <summary>
    /// Forgets the steps the login used, which is right when its authenticator key was replaced: codes of the old key say nothing
    /// about the new one, so the first code of the new key must be accepted whatever step it belongs to.
    /// </summary>
    /// <param name="loginId">The login whose authenticator key was replaced.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task ClearAsync(Guid loginId, CancellationToken cancellationToken);
}
