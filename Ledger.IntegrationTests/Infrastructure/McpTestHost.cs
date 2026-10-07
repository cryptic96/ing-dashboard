using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Ledger.Repository.Entities;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Ledger.IntegrationTests.Infrastructure;

/// <summary>An authenticated MCP session: the tokens that were issued and a client that uses the access token.</summary>
public sealed class McpConnection(string accessToken, string refreshToken, McpClient client, HttpClient httpClient) : IAsyncDisposable
{
    /// <summary>The access token the client sends as a bearer.</summary>
    public string AccessToken { get; } = accessToken;

    /// <summary>The refresh token issued with the access token.</summary>
    public string RefreshToken { get; } = refreshToken;

    /// <summary>The MCP client that talks to the host with the access token.</summary>
    public McpClient Client { get; } = client;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        httpClient.Dispose();
    }
}

/// <summary>
/// Starts the ledger host with the sign-in, OAuth and MCP surface switched on for the public test address, and offers the
/// pieces every OAuth test needs. This is the one place that configures OAuth for tests.
/// </summary>
public sealed class McpTestHost : IAsyncDisposable
{
    /// <summary>The public address the host answers to behind the emulated proxy.</summary>
    public const string PublicBaseUrl = "https://mcp.example.com";

    /// <summary>The home network the sign-in pages answer to in tests; the default proxied client address lies inside it.</summary>
    public const string SignInNetwork = "192.0.2.0/24";

    private readonly LedgerWebApplicationFactory _factory;

    private McpTestHost(LedgerWebApplicationFactory factory, string baseUrl)
    {
        _factory = factory;
        BaseUrl = baseUrl;
    }

    /// <summary>The public address this host answers to; the default one unless the test configured another.</summary>
    public string BaseUrl { get; }

    /// <summary>The running host.</summary>
    public LedgerWebApplicationFactory Factory => _factory;

    /// <summary>Starts a host on the given database with OAuth switched on, waits until it is healthy and returns it.</summary>
    /// <param name="connectionString">The runtime connection string of the database to use.</param>
    /// <param name="configuration">Extra configuration, for example token lifetimes.</param>
    /// <param name="configureServices">Replacements for services.</param>
    /// <param name="enableOAuth">Whether to set the public base address; false starts the host as an unconfigured one would.</param>
    public static async Task<McpTestHost> StartAsync(
        string connectionString,
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null,
        bool enableOAuth = true)
    {
        var settings = new Dictionary<string, string?>();

        if (enableOAuth)
        {
            settings["OAuth:PublicBaseUrl"] = PublicBaseUrl;
            settings["OAuth:SignInNetworks:0"] = SignInNetwork;
        }

        foreach (var (key, value) in configuration ?? new Dictionary<string, string?>())
        {
            settings[key] = value;
        }

        var startup = settings
            .Where(setting => setting.Key.StartsWith("OAuth:", StringComparison.Ordinal))
            .ToDictionary(setting => setting.Key.Replace(":", "__", StringComparison.Ordinal), setting => setting.Value);

        var factory = new LedgerWebApplicationFactory(
            connectionString,
            configureTestServices: configureServices,
            additionalConfiguration: settings,
            startupEnvironment: startup);

        try
        {
            await Wait.UntilReadyAsync(factory);
        }
        catch
        {
            await factory.DisposeAsync();
            throw;
        }

        return new McpTestHost(factory, settings.GetValueOrDefault("OAuth:PublicBaseUrl") ?? PublicBaseUrl);
    }

    /// <summary>Creates a client that reaches the host the way the reverse proxy does, with its own cookie jar.</summary>
    /// <param name="clientAddress">The address the proxy reports as the caller's; the default stands for a LAN client.</param>
    public HttpClient CreateBrowser(string? clientAddress = null)
    {
        var handler = new ProxyEmulatingHandler(_factory.ApiPort, clientAddress ?? ProxyEmulatingHandler.DefaultClientAddress);

        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
    }

    /// <summary>
    /// Creates a login with a random name and password and a confirmed authenticator, as the command-line tool will, and
    /// returns it with the authenticator key.
    /// </summary>
    public async Task<TestLogin> CreateLoginAsync()
    {
        return await CreateLoginAsync(enrolSecondFactor: true);
    }

    /// <summary>Creates a login with a random name and password, optionally without any second factor.</summary>
    public async Task<TestLogin> CreateLoginAsync(bool enrolSecondFactor)
    {
        var userName = "user-" + Guid.NewGuid().ToString("N")[..10];
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LedgerUserEntity>>();
        var user = new LedgerUserEntity
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await users.CreateAsync(user, password);
        result.Succeeded.Should().BeTrue(string.Join(", ", result.Errors.Select(error => error.Code)));

        var authenticatorKey = string.Empty;

        if (enrolSecondFactor)
        {
            (await users.ResetAuthenticatorKeyAsync(user)).Succeeded.Should().BeTrue();
            authenticatorKey = (await users.GetAuthenticatorKeyAsync(user))!;
            authenticatorKey.Should().NotBeNullOrEmpty();
            (await users.SetTwoFactorEnabledAsync(user, true)).Succeeded.Should().BeTrue();
        }

        return new TestLogin(user.Id, userName, password, authenticatorKey);
    }

    /// <summary>Deletes a login, so its tokens can no longer be refreshed.</summary>
    public async Task DeleteLoginAsync(TestLogin login)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LedgerUserEntity>>();
        var user = await users.FindByIdAsync(login.Id.ToString());
        user.Should().NotBeNull();

        var result = await users.DeleteAsync(user!);
        result.Succeeded.Should().BeTrue();
    }

    /// <summary>Runs the whole chain for a client and returns the issued tokens with an MCP client that uses the access token.</summary>
    public async Task<McpConnection> ConnectAsync(TestLogin login, string clientId = ClientRegistrations.CodeClientId)
    {
        var (discovery, tokens) = await SignInAsync(login, clientId);

        return await OpenAsync(tokens.AccessToken!, tokens.RefreshToken!);
    }

    /// <summary>Runs the discovery, sign-in, consent and code exchange for a client and returns the discovery documents with the tokens.</summary>
    public async Task<(DiscoveryDocuments Discovery, TokenResult Tokens)> SignInAsync(
        TestLogin login,
        string clientId = ClientRegistrations.CodeClientId)
    {
        using var browser = CreateBrowser();
        var driver = new OAuthTestDriver(browser);

        var discovery = await driver.DiscoverAsync();
        var redirectUri = clientId == ClientRegistrations.HostedClientId
            ? OAuthTestDriver.HostedRedirectUri
            : OAuthTestDriver.LoopbackRedirectUri;
        var outcome = await driver.AuthorizeAsync(discovery, clientId, login, redirectUri);
        var tokens = await driver.ExchangeAsync(discovery, clientId, outcome);

        tokens.Succeeded.Should().BeTrue($"the code exchange must issue tokens, got {tokens.Status} {tokens.Error}");

        return (discovery, tokens);
    }

    /// <summary>Opens an MCP session that sends the given access token as a bearer.</summary>
    public async Task<McpConnection> OpenAsync(string accessToken, string refreshToken = "")
    {
        var httpClient = CreateBrowser();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(BaseUrl + "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);

        var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);

        return new McpConnection(accessToken, refreshToken, client, httpClient);
    }

    /// <summary>Posts a tool-listing request with the given bearer, or none, and returns the raw response.</summary>
    public async Task<HttpResponseMessage> PostToolsListAsync(string? accessToken)
    {
        using var browser = CreateBrowser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await browser.SendAsync(request, HttpCompletionOption.ResponseContentRead, TestContext.Current.CancellationToken);
    }

    /// <summary>Returns whether a tools-listing request with the bearer is answered with the given status.</summary>
    public async Task<HttpStatusCode> ToolsListStatusAsync(string? accessToken)
    {
        using var response = await PostToolsListAsync(accessToken);

        return response.StatusCode;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }
}
