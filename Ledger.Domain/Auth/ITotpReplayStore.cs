namespace Ledger.Domain.Auth;

/// <summary>Remembers the one-time code a login last used, so a code that was accepted once cannot be accepted again.</summary>
public interface ITotpReplayStore
{
    /// <summary>
    /// Records the code as used by the login unless the same code was already recorded within the window. Returns true when the
    /// code was recorded, which makes the caller the only one that may act on it, and false when it was seen before. Two calls
    /// with the same code at the same moment yield exactly one true.
    /// </summary>
    /// <param name="loginId">The login the code was presented for.</param>
    /// <param name="codeSha256">The SHA-256 of the code; the code itself is never stored.</param>
    /// <param name="now">The current moment.</param>
    /// <param name="window">How long a recorded code keeps being refused.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<bool> TryClaimAsync(Guid loginId, byte[] codeSha256, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken);
}
