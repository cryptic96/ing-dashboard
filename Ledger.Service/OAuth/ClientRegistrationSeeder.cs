using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ledger.Service.OAuth;

/// <summary>
/// Makes the registered OAuth clients exactly the two Claude clients at every start: it creates a missing client, updates one
/// whose redirect addresses, permissions, requirements or name differ, and deletes every other client. Nothing registers a
/// client at run time, so no client can obtain a token without being listed here.
/// </summary>
public class ClientRegistrationSeeder(IServiceScopeFactory scopeFactory) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        var desired = new[]
        {
            Describe(
                ClientRegistrations.HostedClientId,
                ClientRegistrations.HostedDisplayName,
                ApplicationTypes.Web,
                ClientRegistrations.HostedRedirectUris),
            Describe(
                ClientRegistrations.CodeClientId,
                ClientRegistrations.CodeDisplayName,
                ApplicationTypes.Native,
                ClientRegistrations.CodeRedirectUris)
        };

        foreach (var descriptor in desired)
        {
            var existing = await manager.FindByClientIdAsync(descriptor.ClientId!, cancellationToken);

            if (existing is null)
            {
                await manager.CreateAsync(descriptor, cancellationToken);
            }
            else if (await DiffersAsync(manager, existing, descriptor, cancellationToken))
            {
                await manager.UpdateAsync(existing, descriptor, cancellationToken);
            }
        }

        var allowed = desired.Select(descriptor => descriptor.ClientId).ToHashSet(StringComparer.Ordinal);
        var unexpected = new List<object>();

        await foreach (var application in manager.ListAsync(count: null, offset: null, cancellationToken))
        {
            var clientId = await manager.GetClientIdAsync(application, cancellationToken);

            if (clientId is null || !allowed.Contains(clientId))
            {
                unexpected.Add(application);
            }
        }

        foreach (var application in unexpected)
        {
            await manager.DeleteAsync(application, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static OpenIddictApplicationDescriptor Describe(
        string clientId,
        string displayName,
        string applicationType,
        IReadOnlyList<string> redirectUris)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            DisplayName = displayName,
            ApplicationType = applicationType,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Explicit
        };

        foreach (var redirectUri in redirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(redirectUri, UriKind.Absolute));
        }

        descriptor.Permissions.UnionWith(
        [
            Permissions.Endpoints.Authorization,
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.AuthorizationCode,
            Permissions.GrantTypes.RefreshToken,
            Permissions.ResponseTypes.Code,
            Permissions.Prefixes.Scope + ClientRegistrations.ReadScope,
            Permissions.Prefixes.Scope + Scopes.OfflineAccess
        ]);

        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        return descriptor;
    }

    private static async Task<bool> DiffersAsync(
        IOpenIddictApplicationManager manager,
        object existing,
        OpenIddictApplicationDescriptor desired,
        CancellationToken cancellationToken)
    {
        var current = new OpenIddictApplicationDescriptor();
        await manager.PopulateAsync(current, existing, cancellationToken);

        return current.DisplayName != desired.DisplayName
            || current.ApplicationType != desired.ApplicationType
            || current.ClientType != desired.ClientType
            || current.ConsentType != desired.ConsentType
            || !current.RedirectUris.SetEquals(desired.RedirectUris)
            || !current.Permissions.SetEquals(desired.Permissions)
            || !current.Requirements.SetEquals(desired.Requirements)
            || !string.IsNullOrEmpty(current.ClientSecret)
            || current.PostLogoutRedirectUris.Count > 0;
    }
}
