using System.Text.Json;

namespace Ledger.Dashboards;

/// <summary>Looks up dashboard text per language from the single translation file and remembers which keys were used.</summary>
public sealed class Translator
{
    private const string ResourceName = "translations.json";

    private readonly HashSet<string> _usedKeys = new(StringComparer.Ordinal);

    private Translator(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> languages)
    {
        Languages = languages;
    }

    /// <summary>The translations per language code, each mapping a key to its text.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Languages { get; }

    /// <summary>Every key that has been looked up so far.</summary>
    public IReadOnlySet<string> UsedKeys => _usedKeys;

    /// <summary>Loads the translation file embedded in this assembly.</summary>
    public static Translator Load()
    {
        using var stream = typeof(Translator).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);

        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses translations from JSON shaped as one object per language that maps keys to text.</summary>
    public static Translator Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(json)
            ?? throw new InvalidOperationException("The translation file is empty.");

        var languages = parsed.ToDictionary(
            language => language.Key,
            language => (IReadOnlyDictionary<string, string>)language.Value
                .ToDictionary(entry => entry.Key, entry => entry.Value ?? string.Empty, StringComparer.Ordinal),
            StringComparer.Ordinal);

        return new Translator(languages);
    }

    /// <summary>Returns the text of a key in a language, throwing when the language or key is unknown or the text is empty.</summary>
    public string T(string language, string key)
    {
        if (!Languages.TryGetValue(language, out var texts))
        {
            throw new InvalidOperationException($"Unknown language '{language}' requested for translation key '{key}'.");
        }

        _usedKeys.Add(key);

        if (!texts.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Translation key '{key}' is missing or empty for language '{language}'.");
        }

        return text;
    }
}
