using System.Text;

namespace Ledger.Domain.Ingestion;

/// <summary>Produces the comparison form of bank text. Stored text is never normalised; only comparisons use this form.</summary>
public static class TextNormalizer
{
    /// <summary>
    /// Returns the text in Unicode NFC, trimmed, with every run of whitespace collapsed to one space and upper-cased
    /// with the invariant culture. Null stays null.
    /// </summary>
    public static string? ForMatching(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var composed = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(composed.Length);
        var pendingSpace = false;

        foreach (var character in composed)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString().ToUpperInvariant();
    }
}
