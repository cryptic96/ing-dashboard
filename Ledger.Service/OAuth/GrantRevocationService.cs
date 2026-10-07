using Ledger.Repository.Entities;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Ledger.Service.OAuth;

/// <summary>One grant, as the operator sees it: who approved it for which client, and how many tokens of it still work. Never a token.</summary>
/// <param name="GrantId">The identifier of the grant.</param>
/// <param name="ClientId">The client the grant was approved for.</param>
/// <param name="LoginName">The login that approved it, when that login still exists.</param>
/// <param name="CreatedAt">When the grant was approved.</param>
/// <param name="Status">The status of the grant, for example valid or revoked.</param>
/// <param name="LiveTokens">How many tokens of the grant are valid and not expired.</param>
public sealed record GrantSummary(
    string GrantId,
    string ClientId,
    string? LoginName,
    DateTimeOffset? CreatedAt,
    string Status,
    int LiveTokens);

/// <summary>What a revocation changed.</summary>
/// <param name="Grants">How many grants were revoked.</param>
/// <param name="Tokens">How many tokens were revoked.</param>
public sealed record RevocationResult(int Grants, int Tokens);

/// <summary>
/// Lists and revokes the grants Claude clients hold. A revoked grant and a revoked token are both refused on the very next
/// request, because the token validator checks the stored rows on every call.
/// </summary>
public sealed class GrantRevocationService(
    IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens,
    UserManager<LedgerUserEntity> users)
{
    /// <summary>Every grant with its client, login, creation time, status and live token count.</summary>
    public async Task<IReadOnlyList<GrantSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<GrantSummary>();
        var clientIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var loginNames = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var authorization in await MaterialiseAsync(authorizations.ListAsync(cancellationToken: cancellationToken)))
        {
            var grantId = (await authorizations.GetIdAsync(authorization, cancellationToken))!;
            var clientId = await ClientIdAsync(authorization, clientIds, cancellationToken);
            var loginName = await LoginNameAsync(await authorizations.GetSubjectAsync(authorization, cancellationToken), loginNames);
            var created = await authorizations.GetCreationDateAsync(authorization, cancellationToken);
            var status = await authorizations.GetStatusAsync(authorization, cancellationToken) ?? string.Empty;
            var live = await CountLiveTokensAsync(grantId, cancellationToken);

            summaries.Add(new GrantSummary(grantId, clientId, loginName, created, status, live));
        }

        return summaries.OrderBy(summary => summary.CreatedAt).ToList();
    }

    /// <summary>Revokes one grant and every token of it. Reports no grants when the identifier is unknown.</summary>
    public async Task<RevocationResult> RevokeAsync(string grantId, CancellationToken cancellationToken = default)
    {
        var authorization = await authorizations.FindByIdAsync(grantId, cancellationToken);

        if (authorization is null)
        {
            return new RevocationResult(0, 0);
        }

        var grants = await authorizations.TryRevokeAsync(authorization, cancellationToken) ? 1 : 0;
        var revokedTokens = await RevokeTokensAsync(
            await MaterialiseAsync(tokens.FindByAuthorizationIdAsync(grantId, cancellationToken)),
            cancellationToken);

        return new RevocationResult(grants, revokedTokens);
    }

    /// <summary>Revokes every grant and every token, including any token that belongs to no grant.</summary>
    public async Task<RevocationResult> RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        var grants = await RevokeGrantsAsync(await MaterialiseAsync(authorizations.ListAsync(cancellationToken: cancellationToken)), cancellationToken);
        var revokedTokens = await RevokeTokensAsync(await MaterialiseAsync(tokens.ListAsync(cancellationToken: cancellationToken)), cancellationToken);

        return new RevocationResult(grants, revokedTokens);
    }

    /// <summary>Revokes the grants and tokens that belong to one login.</summary>
    public async Task<RevocationResult> RevokeForLoginAsync(Guid loginId, CancellationToken cancellationToken = default)
    {
        var subject = loginId.ToString();
        var grants = await RevokeGrantsAsync(await MaterialiseAsync(authorizations.FindBySubjectAsync(subject, cancellationToken)), cancellationToken);
        var revokedTokens = await RevokeTokensAsync(await MaterialiseAsync(tokens.FindBySubjectAsync(subject, cancellationToken)), cancellationToken);

        return new RevocationResult(grants, revokedTokens);
    }

    /// <summary>How many valid grants a login holds.</summary>
    public async Task<int> CountActiveGrantsAsync(Guid loginId, CancellationToken cancellationToken = default)
    {
        var count = 0;

        foreach (var authorization in await MaterialiseAsync(authorizations.FindBySubjectAsync(loginId.ToString(), cancellationToken)))
        {
            if (await authorizations.HasStatusAsync(authorization, Statuses.Valid, cancellationToken))
            {
                count++;
            }
        }

        return count;
    }

    private async Task<int> RevokeGrantsAsync(IReadOnlyList<object> grants, CancellationToken cancellationToken)
    {
        var revoked = 0;

        foreach (var authorization in grants)
        {
            if (await authorizations.TryRevokeAsync(authorization, cancellationToken))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private async Task<int> RevokeTokensAsync(IReadOnlyList<object> candidates, CancellationToken cancellationToken)
    {
        var revoked = 0;

        foreach (var token in candidates)
        {
            if (await tokens.HasStatusAsync(token, Statuses.Valid, cancellationToken)
                && await tokens.TryRevokeAsync(token, cancellationToken))
            {
                revoked++;
            }
        }

        return revoked;
    }

    private async Task<int> CountLiveTokensAsync(string grantId, CancellationToken cancellationToken)
    {
        var live = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var token in await MaterialiseAsync(tokens.FindByAuthorizationIdAsync(grantId, cancellationToken)))
        {
            var expires = await tokens.GetExpirationDateAsync(token, cancellationToken);

            if (await tokens.HasStatusAsync(token, Statuses.Valid, cancellationToken) && (expires is null || expires > now))
            {
                live++;
            }
        }

        return live;
    }

    private async Task<string> ClientIdAsync(object authorization, Dictionary<string, string> cache, CancellationToken cancellationToken)
    {
        var applicationId = await authorizations.GetApplicationIdAsync(authorization, cancellationToken);

        if (applicationId is null)
        {
            return string.Empty;
        }

        if (!cache.TryGetValue(applicationId, out var clientId))
        {
            var application = await applications.FindByIdAsync(applicationId, cancellationToken);
            clientId = application is null ? string.Empty : await applications.GetClientIdAsync(application, cancellationToken) ?? string.Empty;
            cache[applicationId] = clientId;
        }

        return clientId;
    }

    private async Task<string?> LoginNameAsync(string? subject, Dictionary<string, string?> cache)
    {
        if (subject is null)
        {
            return null;
        }

        if (!cache.TryGetValue(subject, out var name))
        {
            name = (await users.FindByIdAsync(subject))?.UserName;
            cache[subject] = name;
        }

        return name;
    }

    private static async Task<IReadOnlyList<object>> MaterialiseAsync(IAsyncEnumerable<object> source)
    {
        var items = new List<object>();

        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }
}
