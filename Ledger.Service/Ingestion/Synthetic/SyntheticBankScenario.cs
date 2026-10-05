using System.Globalization;
using System.Security.Cryptography;
using Ledger.Domain.Banking;

namespace Ledger.Service.Ingestion.Synthetic;

/// <summary>
/// A scripted, entirely synthetic bank for tests and local runs: accounts, transactions, balances and injected failures.
/// It never contains real bank data; identifiers use the unassigned XX country code.
/// </summary>
public class SyntheticBankScenario
{
    private readonly object _gate = new();
    private readonly List<SyntheticAccount> _accounts = [];
    private readonly List<SyntheticCall> _calls = [];
    private PendingFailure? _failure;

    private SyntheticBankScenario()
    {
        AuthorizationCode = "synthetic-code-" + RandomHex(8);
        SessionId = "synthetic-session-" + RandomHex(8);
        SessionValidUntil = DateTimeOffset.UtcNow.AddDays(90);
    }

    /// <summary>The one code the synthetic provider accepts when a consent is completed.</summary>
    public string AuthorizationCode { get; }

    /// <summary>The session id the synthetic provider hands out.</summary>
    public string SessionId { get; }

    /// <summary>When the synthetic consent ends.</summary>
    public DateTimeOffset SessionValidUntil { get; set; }

    /// <summary>How many transactions fit on one page.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Every provider call made so far, in order.</summary>
    public IReadOnlyList<SyntheticCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    /// <summary>The accounts the synthetic bank exposes, in creation order.</summary>
    public IReadOnlyList<SyntheticAccount> Accounts
    {
        get
        {
            lock (_gate)
            {
                return _accounts.ToList();
            }
        }
    }

    /// <summary>Creates an empty scenario.</summary>
    public static SyntheticBankScenario Create()
    {
        return new SyntheticBankScenario();
    }

    /// <summary>Adds an account with random synthetic identifiers.</summary>
    public SyntheticAccount AddAccount(AccountKind kind, string currency = "EUR")
    {
        var account = new SyntheticAccount(
            "synthetic-account-" + RandomHex(8),
            RandomHex(16),
            "XX" + RandomDigits(2) + "SYNT" + RandomDigits(10),
            "Synthetic " + kind + " account",
            kind,
            currency);

        lock (_gate)
        {
            _accounts.Add(account);
        }

        return account;
    }

    /// <summary>Appends a transaction to the account's feed.</summary>
    public void AddTransaction(SyntheticAccount account, ProviderTransaction transaction)
    {
        lock (_gate)
        {
            account.Transactions.Add(transaction);
        }
    }

    /// <summary>Replaces the transaction at the given feed position, for example a pending item by its booked version.</summary>
    public void Replace(SyntheticAccount account, int index, ProviderTransaction transaction)
    {
        lock (_gate)
        {
            account.Transactions[index] = transaction;
        }
    }

    /// <summary>Removes the transaction at the given feed position, as a bank does when it stops reporting a pending item.</summary>
    public void Remove(SyntheticAccount account, int index)
    {
        lock (_gate)
        {
            account.Transactions.RemoveAt(index);
        }
    }

    /// <summary>Returns a copy of the transaction carrying another entry reference, or none, as a bank does when it re-identifies a payment.</summary>
    public ProviderTransaction WithReference(ProviderTransaction item, string? entryReference)
    {
        return item with { EntryReference = entryReference };
    }

    /// <summary>Sets the balances the account reports.</summary>
    public void SetBalances(SyntheticAccount account, IReadOnlyList<ProviderBalance> balances)
    {
        lock (_gate)
        {
            account.Balances = balances;
        }
    }

    /// <summary>Makes the next fetch that reaches the given one-based page fail once with the given error.</summary>
    public void FailOnPage(int pageNumber, ProviderErrorKind kind, string? providerCode)
    {
        lock (_gate)
        {
            _failure = new PendingFailure(pageNumber, kind, providerCode);
        }
    }

    internal void RecordCall(string method, string? accountUid, string query)
    {
        lock (_gate)
        {
            _calls.Add(new SyntheticCall(method, accountUid, query));
        }
    }

    internal SyntheticAccount? FindAccount(string uid)
    {
        lock (_gate)
        {
            return _accounts.SingleOrDefault(account => account.Uid == uid);
        }
    }

    internal IReadOnlyList<ProviderTransaction> TransactionsFor(SyntheticAccount account, TransactionQuery query)
    {
        lock (_gate)
        {
            return account.Transactions
                .Where(transaction => IsWithinQuery(transaction, query))
                .ToList();
        }
    }

    internal BankProviderException? TakeFailureFor(int pageNumber)
    {
        lock (_gate)
        {
            if (_failure is not { } failure || failure.PageNumber != pageNumber)
            {
                return null;
            }

            _failure = null;
            return new BankProviderException(failure.Kind, failure.ProviderCode, "The synthetic bank reported a failure.");
        }
    }

    private static bool IsWithinQuery(ProviderTransaction transaction, TransactionQuery query)
    {
        if (query.Depth == HistoryDepth.Longest || query.DateFrom is null)
        {
            return true;
        }

        var date = transaction.BookingDate ?? transaction.TransactionDate ?? transaction.ValueDate;
        return date is null || date >= query.DateFrom;
    }

    private static string RandomHex(int byteCount)
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static string RandomDigits(int count)
    {
        var digits = new char[count];
        for (var i = 0; i < count; i++)
        {
            digits[i] = RandomNumberGenerator.GetInt32(10).ToString(CultureInfo.InvariantCulture)[0];
        }

        return new string(digits);
    }

    private sealed record PendingFailure(int PageNumber, ProviderErrorKind Kind, string? ProviderCode);
}

/// <summary>One account of a synthetic bank with its scripted feed.</summary>
public class SyntheticAccount(
    string uid,
    string identificationHash,
    string iban,
    string name,
    AccountKind kind,
    string currency)
{
    /// <summary>The provider's session-scoped identifier for the account.</summary>
    public string Uid { get; } = uid;

    /// <summary>The stable identifier across sessions.</summary>
    public string IdentificationHash { get; } = identificationHash;

    /// <summary>A synthetic account number using the unassigned XX country code.</summary>
    public string Iban { get; } = iban;

    /// <summary>A synthetic account name.</summary>
    public string Name { get; } = name;

    /// <summary>The kind of account.</summary>
    public AccountKind Kind { get; } = kind;

    /// <summary>The currency of the account.</summary>
    public string Currency { get; } = currency;

    /// <summary>The account's scripted feed in provider order.</summary>
    public List<ProviderTransaction> Transactions { get; } = [];

    /// <summary>The balances the account reports.</summary>
    public IReadOnlyList<ProviderBalance> Balances { get; set; } = [];
}

/// <summary>A record of one call made to the synthetic provider.</summary>
public record SyntheticCall(string Method, string? AccountUid, string Query);
