using Ledger.Domain.Ingestion;
using Ledger.Repository.Conventions;
using Ledger.Service.Ingestion;
using Microsoft.Extensions.Options;

namespace Ledger.Service.Endpoints;

/// <summary>
/// Maps the guided bank link endpoints. Every handler only translates HTTP to a <see cref="BankLinkService"/> call. All
/// routes require an API key except the callback the bank redirects the browser to, which is protected by a one-time state.
/// </summary>
public static class BankEndpoints
{
    private const string CallbackFailureText = "This bank link could not be completed. Start again with a new link request.";
    private const string SyncNowRefusedText = "Sync now would use the last remaining bank call of the day for an account; try again later.";
    private const string SelectionPendingHint = "No account is selected yet, so nothing is synced. Select the accounts now: the bank returns the full transaction history only for about an hour after approval. If that time has passed, renew the connection to get another full-history window.";
    private const string NoAccountsText = "The bank approved the link but exposes no accounts. Link the accounts in the aggregator's control panel first, then start again with a new link request.";

    /// <summary>Maps the bank endpoints under /api/v1/bank.</summary>
    public static IEndpointRouteBuilder MapBankEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var bank = endpoints.MapGroup("/api/v1/bank");

        bank.MapPost("/connections/link", StartLinkAsync);
        bank.MapPost("/connections/{connectionKey}/renew", StartRenewAsync);
        bank.MapGet("/callback", CallbackAsync).AllowAnonymous();
        bank.MapGet("/connections", ListConnectionsAsync);
        bank.MapGet("/connections/{connectionKey}/accounts", ListAccountsAsync);
        bank.MapPut("/connections/{connectionKey}/accounts", SelectAccountsAsync);
        bank.MapDelete("/connections/{connectionKey}", RevokeAsync);
        bank.MapPost("/sync", SyncNowAsync);

        return endpoints;
    }

    /// <summary>
    /// POST /api/v1/bank/sync: queues a sync of the active connection, or of the one named by connectionKey. It answers 202 when
    /// queued, 409 when there is nothing to sync or a sync is already running, and 429 when an unattended sync would use an
    /// account's last remaining background call of the day.
    /// </summary>
    private static Task<IResult> SyncNowAsync(
        HttpContext context,
        BankLinkService service,
        IOptions<IngestionOptions> ingestionOptions,
        string? connectionKey,
        CancellationToken cancellationToken)
    {
        var psu = PsuContextFactory.FromRequest(context, ingestionOptions.Value);

        return Translate(async () =>
        {
            var result = await service.SyncNowAsync(string.IsNullOrWhiteSpace(connectionKey) ? null : connectionKey, psu, cancellationToken);

            return result switch
            {
                SyncNowResult.Queued => Results.Accepted(value: new SyncQueuedResponse("queued")),
                SyncNowResult.NotConfigured => Results.Problem(title: "Bank linking is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable),
                SyncNowResult.NoConnection => Results.Problem(title: "There is no active bank connection to sync.", statusCode: StatusCodes.Status409Conflict),
                SyncNowResult.NoAccountsSelected => Results.Problem(title: "No accounts are selected for this connection. Select the accounts to sync first.", statusCode: StatusCodes.Status409Conflict),
                SyncNowResult.AlreadyRunning => Results.Problem(title: "A sync is running for this connection. Wait for it to finish.", statusCode: StatusCodes.Status409Conflict),
                SyncNowResult.AlreadyQueued => Results.Problem(title: "A sync is already queued for this connection. Wait for it to finish.", statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(title: SyncNowRefusedText, statusCode: StatusCodes.Status429TooManyRequests)
            };
        });
    }

    private static Task<IResult> StartLinkAsync(BankLinkService service, CancellationToken cancellationToken)
    {
        return Translate(async () =>
        {
            var start = await service.StartLinkAsync(cancellationToken);
            return Results.Ok(new AuthorizationResponse(start.AuthorizationUrl.AbsoluteUri, start.ExpiresAt));
        });
    }

    private static Task<IResult> StartRenewAsync(
        string connectionKey,
        BankLinkService service,
        CancellationToken cancellationToken)
    {
        return Translate(async () =>
        {
            var start = await service.StartRenewAsync(connectionKey, cancellationToken);
            return Results.Ok(new AuthorizationResponse(start.AuthorizationUrl.AbsoluteUri, start.ExpiresAt));
        });
    }

    private static Task<IResult> RevokeAsync(
        string connectionKey,
        BankLinkService service,
        CancellationToken cancellationToken)
    {
        return Translate(async () =>
        {
            await service.RevokeAsync(connectionKey, cancellationToken);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> CallbackAsync(
        HttpContext context,
        BankLinkService service,
        IOptions<IngestionOptions> ingestionOptions,
        ILoggerFactory loggerFactory,
        string? state,
        string? code,
        string? error,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        try
        {
            var psu = PsuContextFactory.FromRequest(context, ingestionOptions.Value);
            var outcome = await service.CompleteAsync(state, code, error, psu, cancellationToken);

            return outcome.Result switch
            {
                CallbackResult.Completed => Results.Text(CompletedText(outcome), "text/plain"),
                CallbackResult.NoAccounts => Results.Text(NoAccountsText, "text/plain", statusCode: StatusCodes.Status400BadRequest),
                _ => GenericFailure()
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            loggerFactory.CreateLogger(typeof(BankEndpoints)).LogWarning(
                "Bank link callback failed with {ExceptionType}.",
                exception.GetType().Name);
            return GenericFailure();
        }
    }

    private static async Task<IResult> ListConnectionsAsync(BankLinkService service, CancellationToken cancellationToken)
    {
        var overviews = await service.ListConnectionsAsync(cancellationToken);

        return Results.Ok(overviews.Select(overview => new ConnectionResponse(
            overview.Connection.ConnectionKey,
            overview.Connection.Provider,
            EnumText.ToText(overview.Connection.Status),
            overview.Consent.State.ToString().ToLowerInvariant(),
            (int)Math.Floor(overview.Consent.DaysUntilExpiry),
            overview.Connection.ValidUntil,
            overview.Connection.AuthorizedAt,
            overview.SelectionPending,
            overview.SelectionPending ? SelectionPendingHint : null)).ToList());
    }

    private static async Task<IResult> ListAccountsAsync(
        string connectionKey,
        BankLinkService service,
        CancellationToken cancellationToken)
    {
        return await Translate(async () =>
        {
            var accounts = await service.ListAccountsAsync(connectionKey, cancellationToken);
            return Results.Ok(accounts.Select(ToResponse).ToList());
        });
    }

    private static async Task<IResult> SelectAccountsAsync(
        string connectionKey,
        SelectionRequest request,
        HttpContext context,
        BankLinkService service,
        IOptions<IngestionOptions> ingestionOptions,
        CancellationToken cancellationToken)
    {
        if (request.Accounts is null || request.Accounts.Any(item => item is null))
        {
            return Results.Problem(title: "The request needs an accounts list.", statusCode: StatusCodes.Status400BadRequest);
        }

        var selections = request.Accounts
            .Select(item => new AccountSelection(item!.AccountKey ?? string.Empty, item.DisplayName, item.Sync))
            .ToList();
        var psu = PsuContextFactory.FromRequest(context, ingestionOptions.Value);

        return await Translate(async () =>
        {
            var result = await service.SelectAccountsAsync(connectionKey, selections, psu, cancellationToken);

            return Results.Ok(new SelectionResponse(
                result.Accounts.Select(ToResponse).ToList(),
                result.FirstSyncQueued ? "queued" : "not-needed"));
        });
    }

    private static async Task<IResult> Translate(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (BankLinkException exception)
        {
            return exception.Failure switch
            {
                BankLinkFailure.NotConfigured => Results.Problem(title: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
                BankLinkFailure.NotFound => Results.Problem(title: exception.Message, statusCode: StatusCodes.Status404NotFound),
                BankLinkFailure.Invalid => Results.Problem(title: exception.Message, statusCode: StatusCodes.Status400BadRequest),
                BankLinkFailure.Conflict => Results.Problem(title: exception.Message, statusCode: StatusCodes.Status409Conflict),
                BankLinkFailure.Busy => Results.Problem(title: exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Problem(title: exception.Message, statusCode: StatusCodes.Status502BadGateway)
            };
        }
    }

    private static IResult GenericFailure()
    {
        return Results.Text(CallbackFailureText, "text/plain", statusCode: StatusCodes.Status400BadRequest);
    }

    private static string CompletedText(CallbackOutcome outcome)
    {
        var noun = outcome.AccountCount == 1 ? "account" : "accounts";
        var action = outcome.Renewed ? "was renewed" : "is complete";
        var text = $"The bank link {action} and {outcome.AccountCount} {noun} were found.";

        if (!outcome.SyncQueued)
        {
            text += " The first sync could not be queued. Start it now with a sync request.";
        }

        if (outcome.UnselectedCount > 0)
        {
            var which = outcome.Renewed ? "new accounts" : "accounts";
            text += $" Select the {which} to sync now, before anything else: the bank returns the full transaction history only for about an hour after approval, and an account you do not select is never read. You can close this page and select the accounts through the API.";
        }

        return text;
    }

    private static AccountResponse ToResponse(LinkedAccount account)
    {
        return new AccountResponse(
            account.AccountKey,
            MaskIban(account.Iban),
            account.Kind.ToString().ToLowerInvariant(),
            account.ProviderName,
            account.DisplayName,
            account.SyncEnabled,
            account.Currency);
    }

    private static string? MaskIban(string? iban)
    {
        if (string.IsNullOrEmpty(iban) || iban.Length < 8)
        {
            return null;
        }

        return string.Concat(iban.AsSpan(0, 2), "…", iban.AsSpan(iban.Length - 4));
    }

    private sealed record ConnectionResponse(
        string ConnectionKey,
        string Provider,
        string Status,
        string ConsentState,
        int DaysUntilExpiry,
        DateTimeOffset ValidUntil,
        DateTimeOffset AuthorizedAt,
        bool SelectionPending,
        string? Hint);

    private sealed record SyncQueuedResponse(string Status);

    private sealed record AuthorizationResponse(string AuthorizationUrl, DateTimeOffset ExpiresAt);

    private sealed record AccountResponse(
        string AccountKey,
        string? MaskedIban,
        string Kind,
        string? ProviderName,
        string? DisplayName,
        bool SyncEnabled,
        string Currency);

    private sealed record SelectionResponse(IReadOnlyList<AccountResponse> Accounts, string FirstSync);

    private sealed record SelectionRequest(List<SelectionItem?>? Accounts);

    private sealed record SelectionItem(string? AccountKey, string? DisplayName, bool Sync);
}
