using System.Net;
using System.Text.RegularExpressions;
using Ledger.Domain.Banking;

namespace Ledger.Service.Ingestion.EnableBanking;

/// <summary>
/// Classifies aggregator failures into the provider-neutral kinds the scheduler and the alerts act on, and builds exceptions that
/// carry only a fixed message and a validated error code, never any text from the response.
/// </summary>
public static partial class EnableBankingErrors
{
    private static readonly HashSet<string> RateLimitCodes = new(StringComparer.Ordinal)
    {
        "ASPSP_RATE_LIMIT_EXCEEDED"
    };

    private static readonly HashSet<string> ConsentCodes = new(StringComparer.Ordinal)
    {
        "EXPIRED_SESSION",
        "CLOSED_SESSION",
        "REVOKED_SESSION",
        "SESSION_DOES_NOT_EXIST",
        "WRONG_SESSION_STATUS",
        "NO_ACCOUNTS_ADDED",
        "EXPIRED_AUTHORIZATION_CODE",
        "WRONG_AUTHORIZATION_CODE"
    };

    private static readonly HashSet<string> CredentialCodes = new(StringComparer.Ordinal)
    {
        "UNAUTHORIZED_ACCESS",
        "UNAUTHORIZED_IP",
        "ACCESS_DENIED",
        "AUTHORIZATION_NOT_PROVIDED",
        "REDIRECT_URI_NOT_ALLOWED",
        "PSU_HEADER_NOT_PROVIDED",
        "PSU_HEADER_INVALID"
    };

    private static readonly HashSet<string> TransientCodes = new(StringComparer.Ordinal)
    {
        "ASPSP_ERROR",
        "ASPSP_TIMEOUT",
        "ASPSP_ACCOUNT_NOT_ACCESSIBLE",
        "WRONG_CONTINUATION_KEY",
        "WRONG_TRANSACTIONS_PERIOD"
    };

    /// <summary>The session codes that mean a session is already gone, so ending it again has nothing left to do.</summary>
    public static IReadOnlySet<string> SessionAlreadyEndedCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "EXPIRED_SESSION",
        "CLOSED_SESSION",
        "REVOKED_SESSION",
        "SESSION_DOES_NOT_EXIST"
    };

    [GeneratedRegex(@"\A[A-Z_]{1,64}\z")]
    private static partial Regex ErrorCodePattern();

    /// <summary>
    /// Maps an HTTP status and the error code of the response body to a failure kind. A known error code decides; otherwise the
    /// status does. Anything unrecognised is treated as temporary, so the day's retry can try again.
    /// </summary>
    /// <param name="status">The HTTP status of the response.</param>
    /// <param name="errorCode">The error code from the body, or null when there was none.</param>
    /// <returns>The provider-neutral kind of the failure.</returns>
    public static ProviderErrorKind Map(HttpStatusCode status, string? errorCode)
    {
        var code = Sanitize(errorCode);

        if (code is not null)
        {
            if (RateLimitCodes.Contains(code))
            {
                return ProviderErrorKind.RateLimited;
            }

            if (ConsentCodes.Contains(code))
            {
                return ProviderErrorKind.ConsentRejected;
            }

            if (CredentialCodes.Contains(code))
            {
                return ProviderErrorKind.ProviderAuth;
            }

            if (TransientCodes.Contains(code))
            {
                return ProviderErrorKind.Transient;
            }
        }

        return status switch
        {
            HttpStatusCode.TooManyRequests => ProviderErrorKind.RateLimited,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrorKind.ProviderAuth,
            _ => ProviderErrorKind.Transient
        };
    }

    /// <summary>Returns the error code when it is made only of capital letters and underscores and is at most 64 long, otherwise null.</summary>
    /// <param name="errorCode">A code taken from a response body.</param>
    /// <returns>The code, or null when it is absent or has an unexpected shape.</returns>
    public static string? Sanitize(string? errorCode)
    {
        return errorCode is not null && ErrorCodePattern().IsMatch(errorCode) ? errorCode : null;
    }

    /// <summary>Builds the exception for a failed response. The message is fixed per kind and the code is the validated one.</summary>
    /// <param name="status">The HTTP status of the response.</param>
    /// <param name="errorCode">The error code from the body, or null.</param>
    /// <returns>An exception that is safe to log.</returns>
    public static BankProviderException ForResponse(HttpStatusCode status, string? errorCode)
    {
        var kind = Map(status, errorCode);
        var code = Sanitize(errorCode) ?? $"http_{(int)status}";
        return new BankProviderException(kind, code, MessageFor(kind));
    }

    /// <summary>Builds the exception for a request that timed out or never completed.</summary>
    /// <param name="code">A short fixed code describing what happened.</param>
    /// <returns>A temporary-failure exception.</returns>
    public static BankProviderException ForTransport(string code)
    {
        return new BankProviderException(ProviderErrorKind.Transient, code, MessageFor(ProviderErrorKind.Transient));
    }

    /// <summary>Builds the exception for a response whose content cannot be used.</summary>
    /// <param name="code">A short fixed code describing what was wrong.</param>
    /// <returns>A malformed-data exception.</returns>
    public static BankProviderException ForMalformed(string code)
    {
        return new BankProviderException(ProviderErrorKind.MalformedData, code, MessageFor(ProviderErrorKind.MalformedData));
    }

    private static string MessageFor(ProviderErrorKind kind)
    {
        return kind switch
        {
            ProviderErrorKind.RateLimited => "The bank data provider refused the call because a call allowance was used up.",
            ProviderErrorKind.ConsentRejected => "The bank consent was rejected, revoked or has expired.",
            ProviderErrorKind.ProviderAuth => "The bank data provider refused the application's credentials or request.",
            ProviderErrorKind.MalformedData => "The bank data provider returned data that cannot be stored faithfully.",
            _ => "The bank data provider failed temporarily."
        };
    }
}
