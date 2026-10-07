using System.Text.Encodings.Web;
using Ledger.Service.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using ApplyTokenResponseContext = OpenIddict.Server.OpenIddictServerEvents.ApplyTokenResponseContext;

namespace Ledger.Service.OAuth;

/// <summary>
/// Authenticates a request to the MCP endpoint through OpenIddict's token validation and counts every bearer token that was
/// refused, by reason. OpenIddict stops its own handlers at the first rejection, so the outcome can only be read here, from the
/// result. A request with no token or an empty one is a normal first contact and is not counted.
/// </summary>
public sealed class CountingTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The name of the authentication scheme this handler serves.</summary>
    public const string SchemeName = "ledger-mcp-token";

    private const string BearerPrefix = "Bearer ";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var result = await Context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

        if (!result.Succeeded && CarriesBearerToken())
        {
            var items = result.Properties?.Items;
            var error = Item(items, OpenIddictValidationAspNetCoreConstants.Properties.Error);
            var description = Item(items, OpenIddictValidationAspNetCoreConstants.Properties.ErrorDescription);
            var uri = Item(items, OpenIddictValidationAspNetCoreConstants.Properties.ErrorUri);

            var reason = TokenRejectionReasons.Classify(error, description, uri);

            if (reason == McpMetrics.ReasonExpired)
            {
                reason = TokenRejectionReasons.ConfirmExpiry(reason, await ExpirationOfAsync(BearerValue()), TimeProvider.GetUtcNow());
            }

            McpMetrics.TokenRejected(reason);
        }

        return result;
    }

    private bool CarriesBearerToken()
    {
        return !string.IsNullOrWhiteSpace(BearerValue());
    }

    private string BearerValue()
    {
        var header = Request.Headers.Authorization.ToString();

        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : string.Empty;
    }

    private async Task<DateTimeOffset?> ExpirationOfAsync(string token)
    {
        var tokens = Context.RequestServices.GetRequiredService<IOpenIddictTokenManager>();
        var entry = await tokens.FindByReferenceIdAsync(token, Context.RequestAborted);

        return entry is null ? null : await tokens.GetExpirationDateAsync(entry, Context.RequestAborted);
    }

    private static string? Item(IDictionary<string, string?>? items, string key)
    {
        return items is not null && items.TryGetValue(key, out var value) ? value : null;
    }
}

/// <summary>
/// Counts and logs a refresh token that was presented again after it had already been used. OpenIddict has by then revoked every
/// token of that connection. Neither the metric nor the log line carries the token, the client or the caller's address.
/// </summary>
public sealed class ReusedRefreshTokenHandler(ILogger<ReusedRefreshTokenHandler> logger) : IOpenIddictServerHandler<ApplyTokenResponseContext>
{
    /// <summary>The descriptor that places this handler among OpenIddict's own token-response handlers.</summary>
    public static OpenIddictServerHandlerDescriptor Descriptor { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<ApplyTokenResponseContext>()
            .UseSingletonHandler<ReusedRefreshTokenHandler>()
            .SetOrder(int.MinValue + 100_000)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    /// <inheritdoc />
    public ValueTask HandleAsync(ApplyTokenResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (TokenRejectionReasons.IsRefreshTokenReuse(context.Error, context.Response?.ErrorDescription, context.Response?.ErrorUri))
        {
            McpMetrics.RefreshTokenReused();
            logger.LogWarning("A refresh token was reused; every token of its grant was revoked.");
        }

        return ValueTask.CompletedTask;
    }
}
