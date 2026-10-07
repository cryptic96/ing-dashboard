using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ledger.Domain.Banking;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>
/// Reads account information through the Enable Banking aggregator. It can start and complete a consent, read balances and
/// transactions, and end a session; nothing here can initiate a payment. Requests are signed with a short-lived client token,
/// counted by the call meter before they are sent, and never retried.
/// </summary>
public sealed class EnableBankingClient(
    HttpClient http,
    EnableBankingTokenMinter minter,
    IOptions<EnableBankingOptions> options,
    TimeProvider timeProvider,
    AspspRequirementsCache requirements) : IBankDataProvider
{
    private const string PersonalPsuType = "personal";
    private const string LongestStrategy = "longest";

    /// <summary>How many days back the bank answers a plain date range. Anything further back needs the longest strategy.</summary>
    private const int PlainRangeDays = 89;

    /// <summary>The most pages one fetch may follow. A stream that never ends is cut off rather than followed forever.</summary>
    private const int MaxPages = 500;

    /// <inheritdoc />
    public string Name => "enablebanking";

    /// <inheritdoc />
    public async Task<AuthorizationStart> StartAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        using (var application = await SendAsync(HttpMethod.Get, "/application", null, null, cancellationToken))
        {
            EnsureApplicationIsReadOnlyAndRegistered(application.RootElement, request.RedirectUrl);
        }

        var aspsp = await LoadAspspAsync(cancellationToken);
        var maximumValidity = aspsp.MaximumValidity ?? TimeSpan.FromDays(settings.ConsentValidityDays);
        var validity = TimeSpan.FromDays(settings.ConsentValidityDays);
        var validUntil = timeProvider.GetUtcNow() + (validity < maximumValidity ? validity : maximumValidity);

        var body = new Dictionary<string, object?>
        {
            ["access"] = new Dictionary<string, object?> { ["valid_until"] = FormatInstant(validUntil) },
            ["aspsp"] = new Dictionary<string, object?> { ["name"] = settings.AspspName, ["country"] = settings.AspspCountry },
            ["state"] = request.State,
            ["redirect_url"] = request.RedirectUrl.AbsoluteUri,
            ["psu_type"] = PersonalPsuType
        };

        using var started = await SendAsync(HttpMethod.Post, "/auth", body, null, cancellationToken);

        var url = ReadString(started.RootElement, "url");

        if (!IsAllowedAuthorizationUrl(url, settings, out var authorizationUrl))
        {
            throw new BankProviderException(
                ProviderErrorKind.ProviderAuth,
                "authorization_url_refused",
                "The bank data provider returned an authorisation address that is not on an allowed host.");
        }

        return new AuthorizationStart(authorizationUrl, ReadString(started.RootElement, "authorization_id") ?? "none");
    }

    /// <inheritdoc />
    public async Task<ProviderSession> CompleteAuthorizationAsync(string code, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/sessions", new Dictionary<string, object?> { ["code"] = code }, null, cancellationToken);
        var root = response.RootElement;

        var sessionId = ReadString(root, "session_id") ?? throw EnableBankingErrors.ForMalformed("session_without_id");

        try
        {
            return ReadSession(sessionId, root);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await EndUnusableSessionAsync(sessionId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
        ProviderAccountRef account,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        var psuHeaders = await ResolvePsuHeadersAsync(context, cancellationToken);

        if (context.Meter is not null)
        {
            await context.Meter.BeforeCallAsync(ProviderCallKind.Balances, cancellationToken);
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            $"/accounts/{Uri.EscapeDataString(account.AccountUid)}/balances",
            null,
            psuHeaders,
            cancellationToken);

        if (!response.RootElement.TryGetProperty("balances", out var balances) || balances.ValueKind != JsonValueKind.Array)
        {
            throw EnableBankingErrors.ForMalformed("balances_missing");
        }

        return balances.EnumerateArray().Select(EnableBankingJson.MapBalance).ToList();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProviderTransactionPage> GetTransactionsAsync(
        ProviderAccountRef account,
        TransactionQuery query,
        FetchContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var path = $"/accounts/{Uri.EscapeDataString(account.AccountUid)}/transactions";
        var fixedParameters = BuildFixedParameters(query, DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));
        var psuHeaders = await ResolvePsuHeadersAsync(context, cancellationToken);
        string? continuationKey = null;
        var pageCount = 0;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++pageCount > MaxPages)
            {
                throw EnableBankingErrors.ForTransport("too_many_pages");
            }

            var queryString = continuationKey is null
                ? fixedParameters
                : $"{fixedParameters}&continuation_key={Uri.EscapeDataString(continuationKey)}";

            if (context.Meter is not null)
            {
                await context.Meter.BeforeCallAsync(ProviderCallKind.Transactions, cancellationToken);
            }

            using var response = await SendAsync(
                HttpMethod.Get,
                string.IsNullOrEmpty(queryString) ? path : $"{path}?{queryString}",
                null,
                psuHeaders,
                cancellationToken);

            var (page, nextKey) = ReadPage(response.RootElement);
            continuationKey = nextKey;

            yield return page;
        }
        while (!string.IsNullOrEmpty(continuationKey));
    }

    /// <inheritdoc />
    public async Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            using var ended = await SendAsync(
                HttpMethod.Delete,
                $"/sessions/{Uri.EscapeDataString(sessionId)}",
                null,
                null,
                cancellationToken);
        }
        catch (BankProviderException exception) when (
            exception.ProviderCode is not null && EnableBankingErrors.SessionAlreadyEndedCodes.Contains(exception.ProviderCode))
        {
        }
    }

    /// <summary>Reads the validity and accounts of a session the aggregator just created.</summary>
    private static ProviderSession ReadSession(string sessionId, JsonElement root)
    {
        if (!root.TryGetProperty("access", out var access)
            || access.ValueKind != JsonValueKind.Object
            || ReadString(access, "valid_until") is not { } validUntilText)
        {
            throw EnableBankingErrors.ForMalformed("session_without_validity");
        }

        var accounts = new List<ProviderAccount>();

        if (root.TryGetProperty("accounts", out var accountList) && accountList.ValueKind == JsonValueKind.Array)
        {
            foreach (var account in accountList.EnumerateArray())
            {
                accounts.Add(EnableBankingJson.MapAccount(account));
            }
        }

        return new ProviderSession(sessionId, EnableBankingJson.ParseInstant(validUntilText), accounts);
    }

    /// <summary>
    /// Ends a session the aggregator created but whose answer could not be used, so no consent stays live at the bank that the
    /// caller never learns about. A failure to end it is ignored: the original failure is the one worth reporting.
    /// </summary>
    private async Task EndUnusableSessionAsync(string sessionId)
    {
        try
        {
            await RevokeSessionAsync(sessionId, CancellationToken.None);
        }
        catch (BankProviderException)
        {
        }
    }

    /// <summary>
    /// Builds the parameters every page of one fetch repeats unchanged. The longest strategy reaches back as far as the bank
    /// allows; an incremental query older than the bank's plain range uses it with the start date as a lower bound, which the
    /// aggregator accepts where it would refuse the plain range.
    /// </summary>
    private static string BuildFixedParameters(TransactionQuery query, DateOnly today)
    {
        var parameters = new List<string>();

        if (query.Depth == HistoryDepth.Longest)
        {
            parameters.Add($"strategy={LongestStrategy}");
        }
        else if (query.DateFrom is { } dateFrom)
        {
            parameters.Add($"date_from={dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");

            if (dateFrom < today.AddDays(-PlainRangeDays))
            {
                parameters.Add($"strategy={LongestStrategy}");
            }
        }

        return string.Join('&', parameters);
    }

    /// <summary>
    /// Returns the PSU headers to send, or null for none. They are sent only when a person is present and every header the
    /// bank requires is one the ledger can supply; a bank is sent either all of its required headers or none.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> ResolvePsuHeadersAsync(FetchContext context, CancellationToken cancellationToken)
    {
        if (context.Psu is not { } psu || !IsUsableHeaderValue(psu.IpAddress) || !IsUsableHeaderValue(psu.UserAgent))
        {
            return null;
        }

        if (!requirements.TryGet(timeProvider.GetUtcNow(), out var required))
        {
            required = (await LoadAspspAsync(cancellationToken)).RequiredPsuHeaders;
        }

        var suppliable = new[] { "psu-ip-address", "psu-user-agent" };

        if (!required.All(header => suppliable.Contains(header, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new Dictionary<string, string>
        {
            ["Psu-Ip-Address"] = psu.IpAddress,
            ["Psu-User-Agent"] = psu.UserAgent
        };
    }

    private static bool IsUsableHeaderValue(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    }

    /// <summary>Reads the configured bank's entry from the aggregator's bank list and remembers which PSU headers it requires.</summary>
    private async Task<AspspInfo> LoadAspspAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;

        using var response = await SendAsync(
            HttpMethod.Get,
            $"/aspsps?country={Uri.EscapeDataString(settings.AspspCountry)}&service=AIS",
            null,
            null,
            cancellationToken);

        var info = ReadAspsp(response.RootElement, settings);
        requirements.Store(info.RequiredPsuHeaders, timeProvider.GetUtcNow());
        return info;
    }

    private static (ProviderTransactionPage Page, string? ContinuationKey) ReadPage(JsonElement root)
    {
        if (!root.TryGetProperty("transactions", out var transactions) || transactions.ValueKind != JsonValueKind.Array)
        {
            throw EnableBankingErrors.ForMalformed("transactions_missing");
        }

        var mapped = transactions.EnumerateArray().Select(EnableBankingJson.MapTransaction).ToList();
        return (new ProviderTransactionPage(mapped), ReadString(root, "continuation_key"));
    }

    private static void EnsureApplicationIsReadOnlyAndRegistered(JsonElement application, Uri redirectUrl)
    {
        if (!application.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True)
        {
            throw Refused("application_inactive", "The aggregator application is not active.");
        }

        var services = ReadStrings(application, "services");

        if (!services.Contains("AIS", StringComparer.OrdinalIgnoreCase))
        {
            throw Refused("account_information_disabled", "The aggregator application does not offer account information.");
        }

        if (services.Contains("PIS", StringComparer.OrdinalIgnoreCase))
        {
            throw Refused("payment_service_enabled", "The aggregator application offers payment initiation, which this ledger never uses.");
        }

        var registered = ReadStrings(application, "redirect_urls");

        if (!registered.Contains(redirectUrl.AbsoluteUri, StringComparer.Ordinal)
            && !registered.Contains(redirectUrl.OriginalString, StringComparer.Ordinal))
        {
            throw Refused("redirect_not_registered", "The redirect address is not registered with the aggregator application.");
        }
    }

    private static AspspInfo ReadAspsp(JsonElement aspsps, EnableBankingOptions settings)
    {
        if (!aspsps.TryGetProperty("aspsps", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw EnableBankingErrors.ForMalformed("aspsps_missing");
        }

        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !string.Equals(ReadString(entry, "name"), settings.AspspName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(ReadString(entry, "country"), settings.AspspCountry, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TimeSpan? maximumValidity = null;

            if (entry.TryGetProperty("maximum_consent_validity", out var seconds) && seconds.ValueKind == JsonValueKind.Number
                && seconds.TryGetDouble(out var value) && value > 0)
            {
                maximumValidity = TimeSpan.FromSeconds(value);
            }

            return new AspspInfo(maximumValidity, ReadStrings(entry, "required_psu_headers"));
        }

        throw EnableBankingErrors.ForMalformed("aspsp_not_listed");
    }

    private static bool IsAllowedAuthorizationUrl(string? text, EnableBankingOptions settings, out Uri url)
    {
        url = null!;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = parsed.IdnHost.ToLowerInvariant();

        var allowed = settings.AuthorizationHosts.Any(entry =>
            !string.IsNullOrWhiteSpace(entry)
            && (host == entry.ToLowerInvariant() || host.EndsWith("." + entry.ToLowerInvariant(), StringComparison.Ordinal)));

        if (!allowed)
        {
            return false;
        }

        url = parsed;
        return true;
    }

    private static string FormatInstant(DateTimeOffset instant)
    {
        return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static List<string> ReadStrings(JsonElement element, string name)
    {
        var values = new List<string>();

        if (element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                {
                    values.Add(text);
                }
            }
        }

        return values;
    }

    private static BankProviderException Refused(string code, string message)
    {
        return new BankProviderException(ProviderErrorKind.ProviderAuth, code, message);
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        string pathAndQuery,
        object? body,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(pathAndQuery, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minter.Create());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        RawResponse result;

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            result = new RawResponse(response.StatusCode, response.IsSuccessStatusCode, content);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw EnableBankingErrors.ForTransport("timeout");
        }
        catch (HttpRequestException)
        {
            throw EnableBankingErrors.ForTransport("connection_failed");
        }

        if (!result.Success)
        {
            throw EnableBankingErrors.ForResponse(result.Status, ReadErrorCode(result.Content));
        }

        return ParseBody(result.Content);
    }

    private static JsonDocument ParseBody(string content)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
        }
        catch (JsonException)
        {
            throw EnableBankingErrors.ForMalformed("invalid_json");
        }
    }

    private static string? ReadErrorCode(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return ReadString(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct RawResponse(HttpStatusCode Status, bool Success, string Content);

    private readonly record struct AspspInfo(TimeSpan? MaximumValidity, IReadOnlyList<string> RequiredPsuHeaders);
}

/// <summary>
/// Remembers which PSU headers the bank requires, as last reported by the aggregator's bank list, so a fetch does not have to
/// ask again every time. It is shared by every client of the process.
/// </summary>
public sealed class AspspRequirementsCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private IReadOnlyList<string>? _requiredHeaders;
    private DateTimeOffset _storedAt;

    /// <summary>Stores the headers the bank requires.</summary>
    /// <param name="requiredHeaders">The header names, as the aggregator spelled them.</param>
    /// <param name="now">The current time.</param>
    public void Store(IReadOnlyList<string> requiredHeaders, DateTimeOffset now)
    {
        lock (_gate)
        {
            _requiredHeaders = requiredHeaders;
            _storedAt = now;
        }
    }

    /// <summary>Returns the stored header names when they are still fresh.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="requiredHeaders">The header names when the result is true.</param>
    /// <returns>Whether fresh header names were available.</returns>
    public bool TryGet(DateTimeOffset now, out IReadOnlyList<string> requiredHeaders)
    {
        lock (_gate)
        {
            if (_requiredHeaders is not null && now - _storedAt < Lifetime)
            {
                requiredHeaders = _requiredHeaders;
                return true;
            }
        }

        requiredHeaders = [];
        return false;
    }
}
