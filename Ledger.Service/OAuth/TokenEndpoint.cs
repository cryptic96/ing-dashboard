using System.Collections.Immutable;
using Ledger.Repository.Entities;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ledger.Service.OAuth;

/// <summary>
/// Maps the token endpoint. OpenIddict validates the request and its code or refresh token; this handler only confirms the
/// login behind the token still exists and is not locked out, and then issues the new tokens with the same claims.
/// </summary>
public static class TokenEndpoint
{
    /// <summary>Maps POST /connect/token, reachable without a sign-in because the client proves itself with its code or refresh token.</summary>
    public static IEndpointRouteBuilder MapTokenEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/connect/token", ExchangeAsync).AllowAnonymous();

        return endpoints;
    }

    private static async Task<IResult> ExchangeAsync(HttpContext context, UserManager<LedgerUserEntity> userManager)
    {
        var request = context.GetOpenIddictServerRequest();

        if (request is null || !(request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType()))
        {
            return Rejected(Errors.UnsupportedGrantType, "The grant type is not supported.");
        }

        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (!result.Succeeded || result.Principal is null)
        {
            return Rejected(Errors.InvalidGrant, "The token is no longer valid.");
        }

        var user = await userManager.FindByIdAsync(result.Principal.GetClaim(Claims.Subject) ?? string.Empty);

        if (user is null || await userManager.IsLockedOutAsync(user))
        {
            return Rejected(Errors.InvalidGrant, "The login is no longer valid.");
        }

        var principal = result.Principal;
        principal.SetDestinations(_ => ImmutableArray.Create(Destinations.AccessToken));

        return Results.SignIn(principal, properties: null, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Rejected(string error, string description)
    {
        var properties = new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
        });

        return Results.Forbid(properties, [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    }
}
