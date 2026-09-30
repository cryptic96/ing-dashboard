using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ledger.Repository.Conventions;

/// <summary>Stores enum values as their lower-snake-case names, so views and check constraints read naturally.</summary>
public static class EnumText
{
    /// <summary>Returns the lower-snake-case name of the value, for example PostLink becomes post_link.</summary>
    public static string ToText<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        return SnakeCaseNaming.ToSnakeCase(value.ToString());
    }

    /// <summary>Returns the value whose lower-snake-case name is the given text.</summary>
    public static TEnum Parse<TEnum>(string text) where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (ToText(value) == text)
            {
                return value;
            }
        }

        throw new InvalidOperationException($"'{text}' is not a stored {typeof(TEnum).Name} value.");
    }

    /// <summary>Returns a quoted, comma-separated list of the stored names, optionally leaving some values out, for a check constraint.</summary>
    public static string CheckList<TEnum>(params TEnum[] excluded) where TEnum : struct, Enum
    {
        var names = Enum.GetValues<TEnum>()
            .Where(value => !excluded.Contains(value))
            .Select(value => $"'{ToText(value)}'");

        return string.Join(", ", names);
    }
}

/// <summary>An EF Core converter that stores an enum as its lower-snake-case name.</summary>
public sealed class SnakeCaseEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(value => EnumText.ToText(value), text => EnumText.Parse<TEnum>(text))
    where TEnum : struct, Enum;
