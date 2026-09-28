using System.Security.Claims;
using System.Text.Encodings.Web;
using Ledger.Domain.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Auth;

/// <summary>Authenticates requests carrying a valid X-Api-Key header. Never logs, echoes or stores the presented value.</summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IApiKeyStore apiKeyStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <summary>The authentication scheme name.</summary>
    public const string SchemeName = "ApiKey";

    /// <summary>The header carrying the presented token.</summary>
    public const string HeaderName = "X-Api-Key";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || string.IsNullOrEmpty(values[0]))
        {
            return AuthenticateResult.Fail("Malformed X-Api-Key header.");
        }

        var presentedToken = values[0]!;

        if (!ApiKeyToken.TryParse(presentedToken, out var keyId, out _))
        {
            return AuthenticateResult.Fail("Malformed X-Api-Key header.");
        }

        var identity = await apiKeyStore.ValidateAsync(presentedToken, Context.RequestAborted);

        if (identity is null)
        {
            Logger.LogDebug("Rejected API key with key id {KeyId}.", keyId);
            return AuthenticateResult.Fail("Invalid or revoked API key.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, identity.Name),
            new Claim("ledger:api_key_id", identity.KeyId)
        };

        var claimsIdentity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(claimsIdentity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        return Task.CompletedTask;
    }
}
