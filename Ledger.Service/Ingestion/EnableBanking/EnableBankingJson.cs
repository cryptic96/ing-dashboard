using System.Globalization;
using System.Text.Json;
using Ledger.Domain.Banking;

namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>
/// Maps the aggregator's JSON shapes to the provider-neutral records. Every value is checked: a field of the wrong type or an
/// unusable value fails with a malformed-data error that carries no part of the payload.
/// </summary>
public static class EnableBankingJson
{
    private const string DateFormat = "yyyy-MM-dd";

    /// <summary>Maps one account of a session.</summary>
    /// <param name="element">The account object.</param>
    /// <returns>The account with its stable identification hash.</returns>
    /// <exception cref="BankProviderException">The account lacks its identification hash or currency, or a field has the wrong type.</exception>
    public static ProviderAccount MapAccount(JsonElement element)
    {
        return Guarded(() =>
        {
            RequireObject(element);

            var hash = Text(element, "identification_hash") ?? throw Malformed("account_without_identity");
            var currency = Text(element, "currency") ?? throw Malformed("account_without_currency");

            string? iban = null;
            if (Child(element, "account_id") is { } accountId)
            {
                iban = Text(accountId, "iban");
            }

            return new ProviderAccount(
                Text(element, "uid"),
                hash,
                iban,
                Text(element, "name"),
                Text(element, "product"),
                MapAccountKind(Text(element, "cash_account_type")),
                currency);
        });
    }

    /// <summary>Maps one balance. A balance without a reference date keeps a null date.</summary>
    /// <param name="element">The balance object.</param>
    /// <returns>The balance with its signed amount.</returns>
    /// <exception cref="BankProviderException">The balance has no usable amount or currency, or a field has the wrong type.</exception>
    public static ProviderBalance MapBalance(JsonElement element)
    {
        return Guarded(() =>
        {
            RequireObject(element);

            var amountElement = Child(element, "balance_amount") ?? throw Malformed("balance_without_amount");
            var amountText = Text(amountElement, "amount") ?? throw Malformed("balance_without_amount");
            var currency = Text(amountElement, "currency") ?? throw Malformed("balance_without_currency");
            var providerType = Text(element, "balance_type") ?? "unknown";

            return new ProviderBalance(
                MapBalanceKind(providerType),
                providerType,
                ParseSignedAmount(amountText),
                currency,
                Date(element, "reference_date"));
        });
    }

    /// <summary>Maps one transaction, keeping the untouched payload text.</summary>
    /// <param name="element">The transaction object.</param>
    /// <returns>The transaction with its signed amount, negative for money leaving the account.</returns>
    /// <exception cref="BankProviderException">The transaction has no usable amount, direction or date, or a field has the wrong type.</exception>
    public static ProviderTransaction MapTransaction(JsonElement element)
    {
        return Guarded(() =>
        {
            RequireObject(element);

            var amountElement = Child(element, "transaction_amount") ?? throw Malformed("transaction_without_amount");
            var amountText = Text(amountElement, "amount") ?? throw Malformed("transaction_without_amount");
            var currency = Text(amountElement, "currency") ?? throw Malformed("transaction_without_currency");

            var isDebit = Text(element, "credit_debit_indicator") switch
            {
                "DBIT" => true,
                "CRDT" => false,
                _ => throw Malformed("transaction_without_direction")
            };

            var counterpartyKey = isDebit ? "creditor" : "debtor";
            var counterpartyAccountKey = isDebit ? "creditor_account" : "debtor_account";

            string? counterpartyName = null;
            if (Child(element, counterpartyKey) is { } counterparty)
            {
                counterpartyName = Text(counterparty, "name");
            }

            string? counterpartyIban = null;
            if (Child(element, counterpartyAccountKey) is { } counterpartyAccount)
            {
                counterpartyIban = Text(counterpartyAccount, "iban");
            }

            return new ProviderTransaction(
                Text(element, "entry_reference"),
                MapStatus(Text(element, "status")),
                MoneyParser.Parse(amountText, isDebit),
                currency,
                Date(element, "booking_date"),
                Date(element, "value_date"),
                Date(element, "transaction_date"),
                counterpartyName,
                counterpartyIban,
                Description(element),
                element.GetRawText());
        });
    }

    /// <summary>Maps the aggregator's account type to the provider-neutral kind.</summary>
    /// <param name="cashAccountType">The type code, or null.</param>
    /// <returns>The kind of account.</returns>
    public static AccountKind MapAccountKind(string? cashAccountType)
    {
        return cashAccountType switch
        {
            "CACC" => AccountKind.Current,
            "SVGS" => AccountKind.Savings,
            "CARD" => AccountKind.Card,
            _ => AccountKind.Other
        };
    }

    /// <summary>Maps the aggregator's balance type to the provider-neutral kind. The expected balance is the only one some banks send.</summary>
    /// <param name="balanceType">The type code.</param>
    /// <returns>The kind of balance.</returns>
    public static BalanceKind MapBalanceKind(string balanceType)
    {
        return balanceType switch
        {
            "CLBD" => BalanceKind.ClosingBooked,
            "ITBD" => BalanceKind.InterimBooked,
            "ITAV" => BalanceKind.InterimAvailable,
            "CLAV" => BalanceKind.ClosingAvailable,
            "OPBD" => BalanceKind.OpeningBooked,
            "XPCD" => BalanceKind.Expected,
            _ => BalanceKind.Other
        };
    }

    /// <summary>Maps the aggregator's transaction status to the provider-neutral status.</summary>
    /// <param name="status">The status code, or null.</param>
    /// <returns>The status of the transaction.</returns>
    public static ProviderTransactionStatus MapStatus(string? status)
    {
        return status switch
        {
            "BOOK" => ProviderTransactionStatus.Booked,
            "PDNG" => ProviderTransactionStatus.Pending,
            "CNCL" or "RJCT" => ProviderTransactionStatus.Cancelled,
            _ => ProviderTransactionStatus.Other
        };
    }

    /// <summary>Parses a date the way the aggregator sends it, as year, month and day with no time.</summary>
    /// <param name="text">The date text.</param>
    /// <returns>The calendar date.</returns>
    /// <exception cref="BankProviderException">The text is not a date in that form.</exception>
    public static DateOnly ParseDate(string text)
    {
        if (!DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw Malformed("invalid_date");
        }

        return date;
    }

    /// <summary>Parses an instant such as the end of a consent.</summary>
    /// <param name="text">The instant text in ISO 8601 form with an offset.</param>
    /// <returns>The instant.</returns>
    /// <exception cref="BankProviderException">The text is not an instant.</exception>
    public static DateTimeOffset ParseInstant(string text)
    {
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var instant))
        {
            throw Malformed("invalid_instant");
        }

        return instant;
    }

    private static decimal ParseSignedAmount(string text)
    {
        return text.StartsWith('-') ? MoneyParser.Parse(text[1..], isDebit: true) : MoneyParser.Parse(text, isDebit: false);
    }

    private static string? Description(JsonElement element)
    {
        if (!element.TryGetProperty("remittance_information", out var lines) || lines.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (lines.ValueKind != JsonValueKind.Array)
        {
            throw Malformed("invalid_field_type");
        }

        var parts = new List<string>();

        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (line.ValueKind != JsonValueKind.String)
            {
                throw Malformed("invalid_field_type");
            }

            var value = line.GetString();

            if (!string.IsNullOrEmpty(value))
            {
                parts.Add(value);
            }
        }

        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Malformed("invalid_field_type");
        }

        var text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static DateOnly? Date(JsonElement element, string name)
    {
        var text = Text(element, name);
        return text is null ? null : ParseDate(text);
    }

    private static JsonElement? Child(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Malformed("invalid_field_type");
        }

        return value;
    }

    private static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Malformed("invalid_field_type");
        }
    }

    private static T Guarded<T>(Func<T> map)
    {
        try
        {
            return map();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or ArgumentException)
        {
            throw Malformed("invalid_field_type");
        }
    }

    private static BankProviderException Malformed(string code)
    {
        return EnableBankingErrors.ForMalformed(code);
    }
}
