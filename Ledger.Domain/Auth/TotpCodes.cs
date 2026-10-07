namespace Ledger.Domain.Auth;

/// <summary>
/// The shape of the one-time codes an authenticator app shows. Only the canonical spelling is accepted, so whatever is stored,
/// compared or claimed about a code is the same text however the person typed it.
/// </summary>
public static class TotpCodes
{
    /// <summary>How many digits a code has.</summary>
    public const int Digits = 6;

    /// <summary>
    /// Whether the text is exactly six ASCII digits. A sign, whitespace, extra leading zeros and non-ASCII digits are all refused,
    /// because the number parser behind the authenticator check would read many spellings as the same code.
    /// </summary>
    public static bool IsWellFormed(string? code)
    {
        return code is { Length: Digits } && code.All(char.IsAsciiDigit);
    }
}
