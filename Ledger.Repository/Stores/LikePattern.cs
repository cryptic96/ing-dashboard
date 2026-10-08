namespace Ledger.Repository.Stores;

/// <summary>Builds LIKE patterns from text a caller chose, so the caller's characters are matched literally and never as wildcards.</summary>
public static class LikePattern
{
    /// <summary>
    /// Returns a pattern that matches any text containing the term. Backslash, percent and underscore in the term are escaped with
    /// a backslash, which is PostgreSQL's default escape character, before the term is wrapped in percent signs.
    /// </summary>
    /// <param name="term">The text to look for.</param>
    public static string Contains(string term)
    {
        var escaped = term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

        return "%" + escaped + "%";
    }
}
