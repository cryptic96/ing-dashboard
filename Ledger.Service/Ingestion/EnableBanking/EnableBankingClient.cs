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
    TimeProvider timeProvider) : IBankDataProvider
{
    private const string PersonalPsuType = "personal";
    private const string LongestStrategy = "longest";

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

        TimeSpan maximumValidity;

        using (var aspsps = await SendAsync(
                   HttpMethod.Get,
                   $"/aspsps?country={Uri.EscapeDataString(settings.AspspCountry)}&service=AIS",
                   null,
                   null,
                   cancellationToken))
        {
            maximumValidity = FindMaximumValidity(aspsps.RootElement, settings);
        }

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

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProviderBalance>> GetBalancesAsync(
        ProviderAccountRef account,
        FetchContext context,
        CancellationToken cancellationToken)
    {
        if (context.Meter is not null)
        {
            await context.Meter.BeforeCallAsync(ProviderCallKind.Balances, cancellationToken);
        }

        using var response = await SendAsync(
            HttpMethod.Get,
            $"/accounts/{Uri.EscapeDataString(account.AccountUid)}/balances",
            null,
            null,
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
        var fixedParameters = BuildFixedParameters(query);
        string? continuationKey = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                null,
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
        using var ended = await SendAsync(
            HttpMethod.Delete,
            $"/sessions/{Uri.EscapeDataString(sessionId)}",
            null,
            null,
            cancellationToken);
    }

    private static string BuildFixedParameters(TransactionQuery query)
    {
        var parameters = new List<string>();

        if (query.Depth == HistoryDepth.Longest)
        {
            parameters.Add($"strategy={LongestStrategy}");
        }
        else if (query.DateFrom is { } dateFrom)
        {
            parameters.Add($"date_from={dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        return string.Join('&', parameters);
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

    private static TimeSpan FindMaximumValidity(JsonElement aspsps, EnableBankingOptions settings)
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

            if (entry.TryGetProperty("maximum_consent_validity", out var seconds) && seconds.ValueKind == JsonValueKind.Number
                && seconds.TryGetDouble(out var value) && value > 0)
            {
                return TimeSpan.FromSeconds(value);
            }

            return TimeSpan.FromDays(settings.ConsentValidityDays);
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
}
