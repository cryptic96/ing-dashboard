using System.Text.RegularExpressions;

namespace Ledger.Domain.Queries;

/// <summary>Normalises and masks account numbers. A masked account number is the only form that may ever appear in a result.</summary>
public static class IbanText
{
    private const string Hidden = "••••";
    private const int MinimumIbanLength = 15;

    private static readonly Regex IbanShape = new(
        @"[A-Za-z]{2}\d{2}(?:[ \u00A0]?[A-Za-z0-9]{4}){2,7}(?:[ \u00A0]?[A-Za-z0-9]{1,3})?",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

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

    /// <summary>
    /// Replaces every account number found inside free text with its masked form. A number is two letters, two digits and eleven to
    /// thirty more letters or digits, written with or without spaces and in any case. Text without a number is returned as it is.
    /// Null stays null.
    /// </summary>
    /// <param name="text">The text as the bank sent it.</param>
    public static string? MaskInText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            return IbanShape.Replace(
                text,
                match => Normalize(match.Value)!.Length < MinimumIbanLength ? match.Value : Mask(match.Value)!);
        }
        catch (RegexMatchTimeoutException)
        {
            return Hidden;
        }
    }
}
