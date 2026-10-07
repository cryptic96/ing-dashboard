using Ledger.Repository;
using Ledger.Repository.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

namespace Ledger.Service.OAuth;

/// <summary>
/// Registers and maps the authorization server: logins, the sign-in cookie, the OAuth endpoints, the sign-in and consent pages
/// and the two Claude clients. Nothing is registered or mapped while no public base address is configured.
/// </summary>
public static class OAuthServiceCollectionExtensions
{
    private const string SignInCookieName = "__Host-ledger-signin";
    private const string SecondFactorCookieName = "__Host-ledger-2fa";
    private const string RememberedSecondFactorCookieName = "__Host-ledger-2fa-remember";
    private const string AntiforgeryCookieName = "__Host-ledger-af";
    private const string UserNameCharacters = "abcdefghijklmnopqrstuvwxyz0123456789-";

    /// <summary>
    /// Registers the login store, the sign-in cookies (the remembered-device cookie scheme exists only because Identity looks for
    /// it during every password check; nothing ever issues it, so every sign-in asks for a code), the OpenIddict server and validation, the Razor pages and the client
    /// seeder. The default authentication scheme is left as it is, because the REST endpoints keep using their API keys.
    /// The resource a client asks for is checked by the authorization page, which accepts every canonical spelling of the one MCP
    /// address and nothing else; OpenIddict's own check accepts only one exact string, so it and its per-client resource
    /// permission are switched off in favour of that single check.
    /// </summary>
    public static IServiceCollection AddLedgerOAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(LedgerOAuthOptions.SectionName).Get<LedgerOAuthOptions>()
            ?? new LedgerOAuthOptions();

        if (!options.IsEnabled)
        {
            return services;
        }

        services.Configure<LedgerOAuthOptions>(configuration.GetSection(LedgerOAuthOptions.SectionName));
        services.AddSingleton<LedgerOAuthSurface>(new LedgerOAuthSurface(options));

        services.AddLedgerOAuthCore(configuration);
        new IdentityBuilder(typeof(LedgerUserEntity), services).AddSignInManager();

        services.AddAuthentication()
            .AddCookie(IdentityConstants.ApplicationScheme, cookie =>
            {
                cookie.Cookie.Name = SignInCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(15);
                cookie.SlidingExpiration = false;
                cookie.LoginPath = "/account/login";
                cookie.ReturnUrlParameter = "returnUrl";
                cookie.AccessDeniedPath = "/account/login";
            })
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme, cookie =>
            {
                cookie.Cookie.Name = SecondFactorCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Lax;
                cookie.Cookie.Path = "/";
                cookie.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                cookie.SlidingExpiration = false;
            })
            .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, cookie =>
            {
                cookie.Cookie.Name = RememberedSecondFactorCookieName;
                cookie.Cookie.HttpOnly = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.Path = "/";
            });

        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.Name = AntiforgeryCookieName;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddRazorPages();

        services.AddOpenIddict()
            .AddServer(server =>
            {
                server.SetIssuer(options.Issuer)
                    .SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    .AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .RequireProofKeyForCodeExchange()
                    .DisableResourceValidation()
                    .IgnoreResourcePermissions()
                    .RegisterScopes(ClientRegistrations.ReadScope, OpenIddictConstants.Scopes.OfflineAccess)
                    .SetAccessTokenLifetime(options.AccessTokenLifetime)
                    .SetRefreshTokenLifetime(options.RefreshTokenLifetime)
                    .SetRefreshTokenReuseLeeway(options.RefreshTokenReuseLeeway)
                    .UseReferenceAccessTokens()
                    .UseReferenceRefreshTokens()
                    .AddEphemeralSigningKey()
                    .AddEphemeralEncryptionKey()
                    .UseDataProtection();

                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
                validation.UseDataProtection();
                validation.AddAudiences(options.ResourceUrl);
                validation.EnableTokenEntryValidation();
                validation.EnableAuthorizationEntryValidation();
            });

        services.AddHostedService<ClientRegistrationSeeder>();

        return services;
    }

    /// <summary>
    /// Registers what the web host and the operator commands share: the login store with the password rules and the authenticator
    /// code provider, the OpenIddict stores and the service that revokes grants. It adds no cookies, pages, endpoints or settings,
    /// so a command-line host can use it without a public address.
    /// </summary>
    public static IServiceCollection AddLedgerOAuthCore(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddIdentityCore<LedgerUserEntity>(identity =>
            {
                identity.Lockout.AllowedForNewUsers = true;
                identity.Lockout.MaxFailedAccessAttempts = 5;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                identity.Password.RequiredLength = 16;
                identity.Password.RequireDigit = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireNonAlphanumeric = false;
                identity.User.AllowedUserNameCharacters = UserNameCharacters;
            })
            .AddLedgerLoginStores();

        services.AddOpenIddict().AddCore(core => core.UseLedgerStores());
        services.AddScoped<GrantRevocationService>();

        return services;
    }

    /// <summary>Maps the sign-in and consent pages and the token endpoint, and adds the headers those pages need. Does nothing while OAuth is not enabled.</summary>
    public static WebApplication MapLedgerOAuth(this WebApplication app)
    {
        if (app.Services.GetService<LedgerOAuthSurface>() is null)
        {
            return app;
        }

        app.UseMiddleware<SignInPageHeaders>();
        app.MapRazorPages();
        app.MapTokenEndpoint();

        return app;
    }
}
