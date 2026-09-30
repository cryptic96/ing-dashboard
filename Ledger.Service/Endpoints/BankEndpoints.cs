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
    private const string NoAccountsText = "The bank approved the link but exposes no accounts. Link the accounts in the aggregator's control panel first, then start again with a new link request.";

    /// <summary>Maps the bank endpoints under /api/v1/bank.</summary>
    public static IEndpointRouteBuilder MapBankEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var bank = endpoints.MapGroup("/api/v1/bank");

        bank.MapPost("/connections/link", StartLinkAsync);
        bank.MapGet("/callback", CallbackAsync).AllowAnonymous();
        bank.MapGet("/connections", ListConnectionsAsync);
        bank.MapGet("/connections/{connectionKey}/accounts", ListAccountsAsync);
        bank.MapPut("/connections/{connectionKey}/accounts", SelectAccountsAsync);

        return endpoints;
    }

    private static Task<IResult> StartLinkAsync(BankLinkService service, CancellationToken cancellationToken)
    {
        return Translate(async () =>
        {
            var start = await service.StartLinkAsync(cancellationToken);
            return Results.Ok(new AuthorizationResponse(start.AuthorizationUrl.AbsoluteUri, start.ExpiresAt));
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
                CallbackResult.Completed => Results.Text(CompletedText(outcome.AccountCount), "text/plain"),
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
            overview.Connection.AuthorizedAt)).ToList());
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

    private static string CompletedText(int accountCount)
    {
        var noun = accountCount == 1 ? "account" : "accounts";
        return $"The bank link is complete and {accountCount} {noun} were found. You can close this page and select the accounts to sync.";
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
        DateTimeOffset AuthorizedAt);

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
