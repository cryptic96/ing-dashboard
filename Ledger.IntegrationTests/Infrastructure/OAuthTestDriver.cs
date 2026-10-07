using FluentAssertions;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// A login created for a test, with the password and the authenticator key it signs in with. Each call to <see cref="NextCode"/>
/// returns a code from a time step that was not handed out before, because the host refuses a code it has already accepted.
/// </summary>
public sealed class TestLogin(Guid id, string userName, string password, string authenticatorKey)
{
    private static readonly int[] StepOffsets = [0, 1, 2, -1, -2];
    private readonly HashSet<long> _usedSteps = [];
    private readonly List<string> _issuedCodes = [];

    /// <summary>
    /// The same login after its password was replaced. The time steps already handed out stay used, because the host remembers
    /// the codes it accepted whichever password followed.
    /// </summary>
    public TestLogin WithPassword(string newPassword)
    {
        var copy = new TestLogin(Id, UserName, newPassword, AuthenticatorKey);

        lock (_usedSteps)
        {
            copy._usedSteps.UnionWith(_usedSteps);
        }

        return copy;
    }

    /// <summary>The identifier of the login.</summary>
    public Guid Id { get; } = id;

    /// <summary>The user name of the login.</summary>
    public string UserName { get; } = userName;

    /// <summary>The password of the login.</summary>
    public string Password { get; } = password;

    /// <summary>The Base32 key an authenticator app holds for the login.</summary>
    public string AuthenticatorKey { get; } = authenticatorKey;

    /// <summary>Every code <see cref="NextCode"/> has handed out so far.</summary>
    public IReadOnlyList<string> IssuedCodes
    {
        get
        {
            lock (_usedSteps)
            {
                return [.. _issuedCodes];
            }
        }
    }

    /// <summary>A code the host accepts now and has not been given by this object before.</summary>
    public string NextCode()
    {
        lock (_usedSteps)
        {
            var now = DateTimeOffset.UtcNow;

            foreach (var offset in StepOffsets)
            {
                var moment = now.AddSeconds(offset * 30);

                if (_usedSteps.Add(moment.ToUnixTimeSeconds() / 30))
                {
                    var code = TotpCode.Compute(AuthenticatorKey, moment);
                    _issuedCodes.Add(code);

                    return code;
                }
            }
        }

        throw new InvalidOperationException("Every time step that the host accepts has been used for this login.");
    }
}

/// <summary>The discovery documents a client reads before it signs in, and the challenge that started the chain.</summary>
public sealed record DiscoveryDocuments(
    string Challenge,
    string ResourceMetadataUrl,
    JsonElement ResourceMetadata,
    string Issuer,
    JsonElement AuthorizationServerMetadata)
{
    /// <summary>The authorization endpoint the authorization-server metadata advertises.</summary>
    public string AuthorizationEndpoint => AuthorizationServerMetadata.GetProperty("authorization_endpoint").GetString()!;

    /// <summary>The token endpoint the authorization-server metadata advertises.</summary>
    public string TokenEndpoint => AuthorizationServerMetadata.GetProperty("token_endpoint").GetString()!;
}

/// <summary>What the authorization step handed back: the code and the PKCE verifier that goes with it, or the error the redirect carried.</summary>
public sealed record AuthorizationOutcome(string? Code, string? Error, string CodeVerifier, string RedirectUri, string Location, string ConsentHtml);

/// <summary>The answer of the token endpoint.</summary>
public sealed record TokenResult(HttpStatusCode Status, string? AccessToken, string? RefreshToken, string? Error, JsonElement Body)
{
    /// <summary>Whether the endpoint issued tokens.</summary>
    public bool Succeeded => Status == HttpStatusCode.OK && AccessToken is not null;
}

/// <summary>
/// Runs the connection chain exactly as Claude Code does: a call without a token, the metadata documents, a browser sign-in with
/// consent, and the code exchange with PKCE. It talks to the host through a browser client that goes through the proxy emulation.
/// </summary>
public sealed partial class OAuthTestDriver(HttpClient browser)
{
    /// <summary>The loopback redirect Claude Code uses, with an arbitrary port.</summary>
    public const string LoopbackRedirectUri = "http://localhost:53682/callback";

    /// <summary>The redirect of the hosted Claude clients.</summary>
    public const string HostedRedirectUri = "https://claude.ai/api/mcp/auth_callback";

    /// <summary>Sends a request without a token to the MCP endpoint, reads the challenge and follows it to both metadata documents.</summary>
    public async Task<DiscoveryDocuments> DiscoverAsync()
    {
        using var challenge = await browser.PostAsync(
            "/mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        challenge.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var header = string.Join(", ", challenge.Headers.WwwAuthenticate.Select(value => value.ToString()));
        var resourceMetadataUrl = ResourceMetadataParameter().Match(header).Groups["url"].Value;
        resourceMetadataUrl.Should().NotBeEmpty("the challenge must point at the protected-resource metadata");

        var resourceMetadata = await GetJsonAsync(resourceMetadataUrl);
        var issuer = resourceMetadata.GetProperty("authorization_servers")[0].GetString()!;
        var metadataUrl = new Uri(new Uri(issuer), "/.well-known/oauth-authorization-server").ToString();
        var authorizationServerMetadata = await GetJsonAsync(metadataUrl);

        return new DiscoveryDocuments(header, resourceMetadataUrl, resourceMetadata, issuer, authorizationServerMetadata);
    }

    /// <summary>
    /// Starts the authorization request, signs in on the login page, and approves or denies on the consent page. The redirect back
    /// to the client is read from its Location header and never followed.
    /// </summary>
    public async Task<AuthorizationOutcome> AuthorizeAsync(
        DiscoveryDocuments discovery,
        string clientId,
        TestLogin login,
        string redirectUri,
        bool approve = true,
        string? resource = null,
        string scope = "ledger.read offline_access")
    {
        var (authorizeUrl, verifier, state) = BuildAuthorizeAddress(discovery, clientId, redirectUri, resource, scope);

        using var first = await browser.GetAsync(authorizeUrl, TestContext.Current.CancellationToken);

        if (IsRedirectToClient(first, redirectUri))
        {
            return Outcome(first, verifier, redirectUri, state, string.Empty);
        }

        first.StatusCode.Should().Be(HttpStatusCode.Redirect, "an unauthenticated authorize request must go to the sign-in page: " + await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var loginAddress = first.Headers.Location!.ToString();
        loginAddress.Should().Contain("/account/login");

        using var passwordPosted = await PostPasswordAsync(loginAddress, login.UserName, login.Password);
        passwordPosted.StatusCode.Should().Be(HttpStatusCode.Redirect, "a correct password continues to the one-time code page");
        var codeAddress = passwordPosted.Headers.Location!.ToString();
        codeAddress.Should().Contain("/account/totp");

        using var signedIn = await PostCodeAsync(codeAddress, login.NextCode());
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect, "a correct code continues to the authorize request");

        using var consent = await browser.GetAsync(signedIn.Headers.Location!.ToString(), TestContext.Current.CancellationToken);
        consent.StatusCode.Should().Be(HttpStatusCode.OK);
        var consentHtml = await consent.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var decision = HiddenFields(consentHtml);
        decision["decision"] = approve ? "approve" : "deny";

        using var decided = await browser.PostAsync(
            discovery.AuthorizationEndpoint,
            new FormUrlEncodedContent(decision),
            TestContext.Current.CancellationToken);

        return Outcome(decided, verifier, redirectUri, state, consentHtml);
    }

    /// <summary>The address of the authorization request a client starts with, and the PKCE verifier and state that go with it.</summary>
    public static (string Address, string Verifier, string State) BuildAuthorizeAddress(
        DiscoveryDocuments discovery,
        string clientId,
        string redirectUri,
        string? resource = null,
        string scope = "ledger.read offline_access")
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var resourceValue = resource ?? discovery.ResourceMetadata.GetProperty("resource").GetString()!;

        var address = discovery.AuthorizationEndpoint
            + "?" + FormEncode(
            [
                ("response_type", "code"),
                ("client_id", clientId),
                ("redirect_uri", redirectUri),
                ("code_challenge", challenge),
                ("code_challenge_method", "S256"),
                ("state", state),
                ("resource", resourceValue),
                ("scope", scope)
            ]);

        return (address, verifier, state);
    }

    /// <summary>Opens the sign-in page at the address and posts the user name and password with the page's own form token.</summary>
    public async Task<HttpResponseMessage> PostPasswordAsync(string loginAddress, string userName, string password)
    {
        using var page = await browser.GetAsync(loginAddress, TestContext.Current.CancellationToken);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var form = HiddenFields(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        form["UserName"] = userName;
        form["Password"] = password;

        return await browser.PostAsync(loginAddress, new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    /// <summary>Opens the one-time code page at the address and posts the code with the page's own form token.</summary>
    public async Task<HttpResponseMessage> PostCodeAsync(string codeAddress, string code)
    {
        using var page = await browser.GetAsync(codeAddress, TestContext.Current.CancellationToken);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var form = HiddenFields(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        form["Code"] = code;

        return await browser.PostAsync(codeAddress, new FormUrlEncodedContent(form), TestContext.Current.CancellationToken);
    }

    /// <summary>Exchanges the authorization code at the token endpoint, with the PKCE verifier and the resource.</summary>
    public async Task<TokenResult> ExchangeAsync(
        DiscoveryDocuments discovery,
        string clientId,
        AuthorizationOutcome outcome,
        string? resource = null)
    {
        outcome.Code.Should().NotBeNull("the exchange needs an authorization code");

        var fields = new List<(string, string)>
        {
            ("grant_type", "authorization_code"),
            ("code", outcome.Code!),
            ("redirect_uri", outcome.RedirectUri),
            ("client_id", clientId),
            ("code_verifier", outcome.CodeVerifier),
            ("resource", resource ?? discovery.ResourceMetadata.GetProperty("resource").GetString()!)
        };

        return await PostTokenAsync(discovery.TokenEndpoint, fields);
    }

    /// <summary>Presents a refresh token at the token endpoint and returns the new token pair or the OAuth error code.</summary>
    public async Task<TokenResult> RefreshAsync(
        DiscoveryDocuments discovery,
        string clientId,
        string refreshToken,
        string? resource = null)
    {
        var fields = new List<(string, string)>
        {
            ("grant_type", "refresh_token"),
            ("refresh_token", refreshToken),
            ("client_id", clientId),
            ("resource", resource ?? discovery.ResourceMetadata.GetProperty("resource").GetString()!)
        };

        return await PostTokenAsync(discovery.TokenEndpoint, fields);
    }

    private async Task<TokenResult> PostTokenAsync(string endpoint, IEnumerable<(string, string)> fields)
    {
        using var content = new StringContent(FormEncode(fields), Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await browser.PostAsync(endpoint, content, TestContext.Current.CancellationToken);

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();

        return new TokenResult(
            response.StatusCode,
            body.TryGetProperty("access_token", out var access) ? access.GetString() : null,
            body.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            body.TryGetProperty("error", out var error) ? error.GetString() : null,
            body);
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        using var response = await browser.GetAsync(url, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{new Uri(url).AbsolutePath} must be served");

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static AuthorizationOutcome Outcome(
        HttpResponseMessage response,
        string verifier,
        string redirectUri,
        string state,
        string consentHtml)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the decision must send the browser back to the client");
        var location = response.Headers.Location!;
        location.ToString().Should().StartWith(redirectUri);

        var query = System.Web.HttpUtility.ParseQueryString(location.Query);

        if (query["code"] is not null)
        {
            query["state"].Should().Be(state, "the client's state must come back unchanged");
        }

        return new AuthorizationOutcome(query["code"], query["error"], verifier, redirectUri, location.ToString(), consentHtml);
    }

    private static bool IsRedirectToClient(HttpResponseMessage response, string redirectUri)
    {
        return response.StatusCode == HttpStatusCode.Redirect
            && response.Headers.Location?.ToString().StartsWith(redirectUri, StringComparison.Ordinal) == true;
    }

    private static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match input in HiddenInput().Matches(html))
        {
            var tag = input.Value;
            var name = AttributeValue(tag, "name");
            var value = AttributeValue(tag, "value");

            if (name is not null)
            {
                fields[name] = WebUtility.HtmlDecode(value ?? string.Empty);
            }
        }

        return fields;
    }

    private static string? AttributeValue(string tag, string attribute)
    {
        var match = Regex.Match(tag, $"\\s{attribute}=\"(?<value>[^\"]*)\"");

        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string FormEncode(IEnumerable<(string Name, string Value)> fields)
    {
        return string.Join("&", fields.Select(field => $"{Uri.EscapeDataString(field.Name)}={Uri.EscapeDataString(field.Value)}"));
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Builds a client that sends the access token as a bearer, through the same proxy emulation.</summary>
    public static AuthenticationHeaderValue Bearer(string accessToken) => new("Bearer", accessToken);

    [GeneratedRegex("resource_metadata=\"(?<url>[^\"]+)\"")]
    private static partial Regex ResourceMetadataParameter();

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>")]
    private static partial Regex HiddenInput();
}
