namespace Ledger.Domain.Queries;

/// <summary>Normalises and masks account numbers. A masked account number is the only form that may ever appear in a result.</summary>
public static class IbanText
{
    private const string Hidden = "••••";

    /// <summary>Returns the account number with all whitespace removed and upper-cased, which is how two spellings are compared. Null stays null.</summary>
    /// <param name="iban">The account number as written.</param>
    public static string? Normalize(string? iban)
    {
        if (iban is null)
        {
            return null;
        }

        return string.Concat(iban.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
    }

    /// <summary>
    /// Returns the two leading letters, four bullets and the last four characters of the normalised account number. A number
    /// shorter than eight characters shows four bullets only. Null stays null.
    /// </summary>
    /// <param name="iban">The account number as written.</param>
    public static string? Mask(string? iban)
    {
        var normalized = Normalize(iban);

        if (normalized is null)
        {
            return null;
        }

        return normalized.Length < 8
            ? Hidden
            : normalized[..2] + Hidden + normalized[^4..];
    }
}
