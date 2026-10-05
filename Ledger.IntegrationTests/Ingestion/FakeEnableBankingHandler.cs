using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ledger.Domain.Banking;
using Ledger.Service.Ingestion.Synthetic;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>One request the fake aggregator received.</summary>
public record RecordedAggregatorRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Returns the value of one query parameter, or null when it was not sent.</summary>
    public string? Parameter(string name)
    {
        return Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == name)
            .Select(pair => Uri.UnescapeDataString(pair.Length > 1 ? pair[1] : string.Empty))
            .FirstOrDefault();
    }
}

/// <summary>
/// A stand-in for the aggregator's API that renders a synthetic bank scenario in the aggregator's JSON shape. It answers the
/// seven account-information routes, pages transactions with continuation keys, records every request and can be told to fail
/// the next request with a given status and error code. It never contacts the network.
/// </summary>
public sealed partial class FakeEnableBankingHandler(SyntheticBankScenario scenario, string redirectUrl) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<RecordedAggregatorRequest> _requests = [];
    private readonly Queue<(HttpStatusCode Status, string? ErrorCode)> _failures = new();

    /// <summary>The host of the authorisation address the fake hands out.</summary>
    public string AuthorizationHost { get; set; } = "auth.enablebanking.com";

    /// <summary>The ING-like advertised maximum consent validity in seconds.</summary>
    public int MaximumConsentValiditySeconds { get; set; } = 180 * 24 * 60 * 60;

    /// <summary>Every request received so far, in order.</summary>
    public IReadOnlyList<RecordedAggregatorRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>Makes the next request fail with the given status and error code.</summary>
    public void FailNext(HttpStatusCode status, string? errorCode)
    {
        lock (_gate)
        {
            _failures.Enqueue((status, errorCode));
        }
    }

    [GeneratedRegex(@"\A/accounts/(?<uid>[^/]+)/(?<kind>balances|transactions)\z")]
    private static partial Regex AccountRoute();

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedAggregatorRequest(request.Method.Method, uri.AbsolutePath, uri.Query, headers);
        (HttpStatusCode Status, string? ErrorCode)? failure = null;

        lock (_gate)
        {
            _requests.Add(recorded);

            if (_failures.Count > 0)
            {
                failure = _failures.Dequeue();
            }
        }

        if (failure is { } injected)
        {
            return Json(injected.Status, injected.ErrorCode is null ? "{}" : JsonSerializer.Serialize(new { error = injected.ErrorCode }));
        }

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

        return (request.Method.Method, uri.AbsolutePath) switch
        {
            ("GET", "/application") => Json(HttpStatusCode.OK, Application()),
            ("GET", "/aspsps") => Json(HttpStatusCode.OK, Aspsps()),
            ("POST", "/auth") => Json(HttpStatusCode.OK, Authorization(body)),
            ("POST", "/sessions") => Session(body),
            ("DELETE", var path) when path.StartsWith("/sessions/", StringComparison.Ordinal) => Json(HttpStatusCode.OK, "{\"message\":\"OK\"}"),
            ("GET", var path) when AccountRoute().IsMatch(path) => Account(recorded),
            _ => Json(HttpStatusCode.NotFound, "{\"error\":\"UNKNOWN_ROUTE\"}")
        };
    }

    private string Application()
    {
        return JsonSerializer.Serialize(new
        {
            kid = "00000000-0000-0000-0000-000000000001",
            environment = "PRODUCTION",
            active = true,
            services = new[] { "AIS" },
            redirect_urls = new[] { redirectUrl }
        });
    }

    private string Aspsps()
    {
        return JsonSerializer.Serialize(new
        {
            aspsps = new[]
            {
                new
                {
                    name = "ING",
                    country = "NL",
                    maximum_consent_validity = MaximumConsentValiditySeconds,
                    required_psu_headers = new[] { "psu-ip-address" },
                    psu_types = new[] { "business", "personal" }
                }
            }
        });
    }

    private string Authorization(string body)
    {
        using var document = JsonDocument.Parse(body);
        var state = document.RootElement.GetProperty("state").GetString()!;

        return JsonSerializer.Serialize(new
        {
            url = $"https://{AuthorizationHost}/ais/start?state={Uri.EscapeDataString(state)}",
            authorization_id = "synthetic-authorization-" + Guid.NewGuid().ToString("N")
        });
    }

    private HttpResponseMessage Session(string body)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.GetProperty("code").GetString() != scenario.AuthorizationCode)
        {
            return Json(HttpStatusCode.UnprocessableEntity, "{\"error\":\"WRONG_AUTHORIZATION_CODE\"}");
        }

        var accounts = scenario.Accounts.Select(account => new Dictionary<string, object?>
        {
            ["uid"] = account.Uid,
            ["identification_hash"] = account.IdentificationHash,
            ["account_id"] = new { iban = account.Iban },
            ["name"] = account.Name,
            ["product"] = account.Kind.ToString(),
            ["currency"] = account.Currency,
            ["cash_account_type"] = account.Kind switch
            {
                AccountKind.Savings => "SVGS",
                AccountKind.Card => "CARD",
                AccountKind.Other => "OTHR",
                _ => "CACC"
            }
        });

        return Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                session_id = scenario.SessionId,
                accounts,
                access = new { valid_until = scenario.SessionValidUntil.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) }
            }));
    }

    private HttpResponseMessage Account(RecordedAggregatorRequest request)
    {
        var match = AccountRoute().Match(request.Path);
        var account = scenario.Accounts.SingleOrDefault(candidate => candidate.Uid == match.Groups["uid"].Value);

        if (account is null)
        {
            return Json(HttpStatusCode.NotFound, "{\"error\":\"SESSION_DOES_NOT_EXIST\"}");
        }

        return match.Groups["kind"].Value == "balances" ? Json(HttpStatusCode.OK, Balances(account)) : Transactions(account, request);
    }

    private static string Balances(SyntheticAccount account)
    {
        var balances = account.Balances.Select(balance => new
        {
            name = balance.ProviderType,
            balance_type = balance.ProviderType,
            balance_amount = new
            {
                currency = balance.Currency,
                amount = balance.Amount.ToString(CultureInfo.InvariantCulture)
            }
        });

        return JsonSerializer.Serialize(new { balances });
    }

    private HttpResponseMessage Transactions(SyntheticAccount account, RecordedAggregatorRequest request)
    {
        var dateFrom = request.Parameter("date_from");
        var continuation = request.Parameter("continuation_key");
        var pageSize = Math.Max(1, scenario.PageSize);

        var visible = account.Transactions
            .Where(transaction => dateFrom is null || (transaction.BookingDate ?? transaction.TransactionDate) >= DateOnly.ParseExact(dateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();

        var pageIndex = 0;

        if (continuation is not null && (!continuation.StartsWith("page-", StringComparison.Ordinal)
            || !int.TryParse(continuation["page-".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out pageIndex)))
        {
            return Json(HttpStatusCode.UnprocessableEntity, "{\"error\":\"WRONG_CONTINUATION_KEY\"}");
        }

        var items = visible.Skip(pageIndex * pageSize).Take(pageSize).Select(Render).ToList();
        var hasMore = visible.Count > (pageIndex + 1) * pageSize;

        return Json(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(new
            {
                transactions = items,
                continuation_key = hasMore ? "page-" + (pageIndex + 1).ToString(CultureInfo.InvariantCulture) : null
            }));
    }

    private static Dictionary<string, object?> Render(ProviderTransaction transaction)
    {
        var isDebit = transaction.Amount < 0;
        var counterparty = transaction.CounterpartyName is null ? null : new { name = transaction.CounterpartyName };
        var counterpartyAccount = transaction.CounterpartyIban is null ? null : new { iban = transaction.CounterpartyIban };

        var rendered = new Dictionary<string, object?>
        {
            ["entry_reference"] = transaction.EntryReference,
            ["status"] = transaction.Status == ProviderTransactionStatus.Pending ? "PDNG" : "BOOK",
            ["credit_debit_indicator"] = isDebit ? "DBIT" : "CRDT",
            ["transaction_amount"] = new
            {
                currency = transaction.Currency,
                amount = Math.Abs(transaction.Amount).ToString(CultureInfo.InvariantCulture)
            },
            ["booking_date"] = Format(transaction.BookingDate),
            ["value_date"] = Format(transaction.ValueDate),
            ["transaction_date"] = Format(transaction.TransactionDate),
            ["remittance_information"] = transaction.Description is null ? Array.Empty<string>() : new[] { transaction.Description }
        };

        rendered[isDebit ? "creditor" : "debtor"] = counterparty;
        rendered[isDebit ? "creditor_account" : "debtor_account"] = counterpartyAccount;
        return rendered;
    }

    private static string? Format(DateOnly? date)
    {
        return date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
