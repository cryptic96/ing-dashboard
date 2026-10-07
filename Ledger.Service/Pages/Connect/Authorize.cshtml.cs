using System.Collections.Immutable;
using System.Security.Claims;
using Ledger.Repository.Entities;
using Ledger.Service.Mcp;
using Ledger.Service.OAuth;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ledger.Service.Pages.Connect;

/// <summary>
/// The authorization endpoint. It refuses a request for any resource other than the MCP endpoint, sends a person who is not signed
/// in to the sign-in page, asks for explicit consent naming the client and where the browser returns to, and on approval records
/// the grant and lets OpenIddict issue the code.
/// </summary>
[AllowAnonymous]
public class AuthorizeModel(
    IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations,
    UserManager<LedgerUserEntity> users,
    IOptions<LedgerOAuthOptions> oauthOptions,
    ILogger<AuthorizeModel> logger) : PageModel
{
    private const string DecisionField = "decision";
    private const string ApproveDecision = "approve";
    private const string DenyDecision = "deny";

    private static readonly string[] ForwardedParameters =
    [
        "client_id", "redirect_uri", "response_type", "scope", "state", "nonce",
        "code_challenge", "code_challenge_method", "resource", "response_mode", "prompt"
    ];

    /// <summary>The name of the client asking for access.</summary>
    public string ClientName { get; private set; } = string.Empty;

    /// <summary>The host the browser is sent back to after the decision.</summary>
    public string RedirectHost { get; private set; } = string.Empty;

    /// <summary>The name of the signed-in login.</summary>
    public string LoginName { get; private set; } = string.Empty;

    /// <summary>Whether the client asked to stay connected.</summary>
    public bool OfflineAccess { get; private set; }

    /// <summary>The OAuth request parameters the consent form carries along, taken from an explicit allow-list and never the decision.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestParameters { get; private set; } = [];

    /// <summary>Handles the redirect from the client: sign-in, then the consent page.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        return await HandleAsync(decision: null);
    }

    /// <summary>
    /// Handles the decision from the consent page. The decision is read only from the one form field the page's own buttons send;
    /// a form that carries the field more than once or under another spelling is read as a refusal.
    /// </summary>
    public async Task<IActionResult> OnPostAsync()
    {
        return await HandleAsync(ReadDecision());
    }

    private async Task<IActionResult> HandleAsync(string? decision)
    {
        var request = HttpContext.GetOpenIddictServerRequest();

        if (request is null)
        {
            return BadRequest();
        }

        var canonicalResource = oauthOptions.Value.ResourceUrl;
        var requestedResources = request.GetResources();

        if (requestedResources.Any(resource => !ResourceIndicator.IsCanonicalMatch(resource, canonicalResource)))
        {
            return Rejected(Errors.InvalidTarget, "The resource is not served here.");
        }

        var cookie = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = cookie.Succeeded ? await users.GetUserAsync(cookie.Principal!) : null;

        if (user is null)
        {
            return Challenge(
                new AuthenticationProperties { RedirectUri = AuthorizeAddress() },
                IdentityConstants.ApplicationScheme);
        }

        var application = await applications.FindByClientIdAsync(request.ClientId ?? string.Empty);

        if (application is null || !Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out var redirect))
        {
            return BadRequest();
        }

        if (string.Equals(decision, DenyDecision, StringComparison.Ordinal))
        {
            return Rejected(Errors.AccessDenied, "The access was not approved.");
        }

        if (string.Equals(decision, ApproveDecision, StringComparison.Ordinal))
        {
            return await ApproveAsync(request, application, user, canonicalResource);
        }

        SignInPageHeaders.AllowRedirectOrigins(HttpContext, await applications.GetRedirectUrisAsync(application));

        ClientName = await applications.GetDisplayNameAsync(application) ?? request.ClientId ?? string.Empty;
        RedirectHost = redirect.Authority;
        LoginName = user.UserName ?? string.Empty;
        OfflineAccess = request.HasScope(Scopes.OfflineAccess);
        RequestParameters = ForwardedRequestParameters().ToList();

        return Page();
    }

    private async Task<IActionResult> ApproveAsync(OpenIddictRequest request, object application, LedgerUserEntity user, string resource)
    {
        var scopes = new List<string> { ClientRegistrations.ReadScope };

        if (request.HasScope(Scopes.OfflineAccess))
        {
            scopes.Add(Scopes.OfflineAccess);
        }

        var subject = user.Id.ToString();
        var identity = new ClaimsIdentity(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            Claims.Name,
            Claims.Role);

        identity.SetClaim(Claims.Subject, subject);
        identity.SetClaim(Claims.Name, user.UserName);
        identity.SetScopes(scopes);
        identity.SetResources(resource);

        var authorization = await authorizations.CreateAsync(
            identity,
            subject,
            (await applications.GetIdAsync(application))!,
            AuthorizationTypes.AdHoc,
            [.. scopes]);

        var grantId = await authorizations.GetIdAsync(authorization);
        identity.SetAuthorizationId(grantId);
        McpMetrics.GrantCreated();
        logger.LogInformation(
            "A Claude connection was approved for client {ClientId} as grant {GrantId}.",
            request.ClientId,
            grantId);
        identity.SetDestinations(_ => ImmutableArray.Create(Destinations.AccessToken));

        return SignIn(new ClaimsPrincipal(identity), OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private ForbidResult Rejected(string error, string description)
    {
        var properties = new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        });

        return Forbid(properties, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private string AuthorizeAddress()
    {
        var query = QueryString.Create(ForwardedRequestParameters()
            .Select(parameter => new KeyValuePair<string, string?>(parameter.Key, parameter.Value)));

        return Request.PathBase + Request.Path + query;
    }

    private string? ReadDecision()
    {
        var fields = Request.Form
            .Where(field => field.Key.Equals(DecisionField, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return fields switch
        {
            [] => null,
            [{ Key: DecisionField, Value: { Count: 1 } value }] => value[0],
            _ => DenyDecision
        };
    }

    private IEnumerable<KeyValuePair<string, string>> ForwardedRequestParameters()
    {
        return CurrentParameters().Where(parameter => ForwardedParameters.Contains(parameter.Key, StringComparer.Ordinal));
    }

    private IEnumerable<KeyValuePair<string, string>> CurrentParameters()
    {
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> source =
            Request.HasFormContentType ? Request.Form : Request.Query;

        foreach (var parameter in source)
        {
            foreach (var value in parameter.Value)
            {
                yield return new KeyValuePair<string, string>(parameter.Key, value ?? string.Empty);
            }
        }
    }
}
