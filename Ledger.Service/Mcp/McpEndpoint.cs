using System.Reflection;
using Ledger.Service.OAuth;
using Ledger.Service.Queries;
using ModelContextProtocol.AspNetCore.Authentication;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

namespace Ledger.Service.Mcp;

/// <summary>
/// Registers and maps the MCP endpoint. It has its own bearer-only authorization policy: the API-key scheme never applies to it
/// and the REST fallback policy never covers it. Nothing is registered or mapped while no public base address is configured.
/// </summary>
public static class McpEndpoint
{
    /// <summary>The path the endpoint is served on.</summary>
    public const string Path = LedgerOAuthOptions.McpPath;

    /// <summary>The path of the protected-resource metadata document.</summary>
    public const string ResourceMetadataPath = LedgerOAuthOptions.ResourceMetadataPath;

    /// <summary>The name of the authorization policy of the endpoint.</summary>
    public const string PolicyName = "mcp";

    /// <summary>
    /// Registers the stateless MCP server with the ledger tools, the MCP authentication scheme that forwards to OpenIddict's token
    /// validation, and the <see cref="PolicyName"/> policy.
    /// </summary>
    public static IServiceCollection AddLedgerMcp(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(LedgerOAuthOptions.SectionName).Get<LedgerOAuthOptions>()
            ?? new LedgerOAuthOptions();

        if (!options.IsEnabled)
        {
            return services;
        }

        services.AddScoped<LedgerQueryService>();

        services
            .AddMcpServer(server =>
            {
                server.ServerInfo = new Implementation
                {
                    Name = "household-ledger",
                    Version = AssemblyVersion()
                };
                server.ServerInstructions = ServerInstructions.Text;
            })
            .WithHttpTransport(transport => transport.Stateless = true)
            .WithTools<LedgerTools>();

        services.AddAuthentication()
            .AddMcp(mcp =>
            {
                mcp.ForwardAuthenticate = CountingTokenAuthenticationHandler.SchemeName;
                mcp.ResourceMetadataUri = new Uri(options.ResourceMetadataUrl, UriKind.Absolute);
                mcp.ResourceMetadata = new ProtectedResourceMetadata
                {
                    Resource = options.ResourceUrl,
                    AuthorizationServers = { options.IssuerText },
                    ScopesSupported = { ClientRegistrations.ReadScope },
                    BearerMethodsSupported = { "header" }
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy
                .AddAuthenticationSchemes(McpAuthenticationDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasScope(ClientRegistrations.ReadScope)));

        return services;
    }

    /// <summary>Maps the MCP endpoint behind the <see cref="PolicyName"/> policy. Does nothing while OAuth is not enabled.</summary>
    public static WebApplication MapLedgerMcp(this WebApplication app)
    {
        if (app.Services.GetService<LedgerOAuthSurface>() is null)
        {
            return app;
        }

        app.MapMcp(Path).RequireAuthorization(PolicyName);

        return app;
    }

    private static string AssemblyVersion()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return "unknown";
        }

        var plusIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);

        return plusIndex >= 0 ? informationalVersion[..plusIndex] : informationalVersion;
    }
}
