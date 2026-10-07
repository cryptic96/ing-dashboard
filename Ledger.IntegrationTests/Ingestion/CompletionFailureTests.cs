using System.Net;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository.Stores;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Verifies that a consent the bank created but the ledger could not record is ended at the bank, so the operator never has a
/// live read grant on the accounts that the ledger knows nothing about.
/// </summary>
[Collection("Database")]
[Trait("Category", "Callback")]
public class CompletionFailureTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task A_consent_that_exposes_no_accounts_is_ended_at_the_bank()
    {
        var scenario = SyntheticBankScenario.Create();
        var bankProvider = new RenewableSyntheticProvider(scenario);

        await using var host = await BankLinkTestHost.StartAsync(fixture, bankProvider);

        var state = await host.StartLinkAsync();
        using var response = await host.CallbackAsync(state, scenario.AuthorizationCode);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        bankProvider.RevokedSessionIds.Should().Equal(scenario.SessionId);
    }

    [Fact]
    public async Task A_link_that_cannot_be_recorded_ends_the_session_and_answers_with_the_generic_failure()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var bankProvider = new RenewableSyntheticProvider(scenario);
        var failures = new StoreFailures { FailAddConnection = true };

        await using var host = await StartWithFailingStoreAsync(bankProvider, failures);

        var state = await host.StartLinkAsync();
        using var response = await host.CallbackAsync(state, scenario.AuthorizationCode);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain(scenario.SessionId);
        bankProvider.RevokedSessionIds.Should().Equal(scenario.SessionId);
        host.Factory.CapturedLogMessages.Should().NotContain(message => message.Contains(scenario.SessionId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_renewal_that_cannot_be_recorded_ends_the_new_session_and_keeps_the_old_connection_active()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var bankProvider = new RenewableSyntheticProvider(scenario);
        var failures = new StoreFailures();

        await using var host = await StartWithFailingStoreAsync(bankProvider, failures);

        var connectionKey = await host.LinkAsync(scenario);
        failures.FailRenewal = true;
        bankProvider.ExposeRenewedSession = true;

        var renewState = await host.StartRenewAsync(connectionKey);
        using var response = await host.CallbackAsync(renewState, scenario.AuthorizationCode);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        bankProvider.RevokedSessionIds.Should().Equal(bankProvider.RenewedSessionId);

        var entry = (await host.ListConnectionsAsync()).Single(connection => connection.GetProperty("connectionKey").GetString() == connectionKey);
        entry.GetProperty("status").GetString().Should().Be("active");
        host.Factory.CapturedLogMessages.Should().NotContain(message => message.Contains(bankProvider.RenewedSessionId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_to_end_the_session_never_replaces_the_failure_or_leaks_the_session_id()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var bankProvider = new RenewableSyntheticProvider(scenario) { FailRevoke = true };
        var failures = new StoreFailures { FailAddConnection = true };

        await using var host = await StartWithFailingStoreAsync(bankProvider, failures);

        var state = await host.StartLinkAsync();
        using var response = await host.CallbackAsync(state, scenario.AuthorizationCode);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Factory.CapturedLogMessages.Should().Contain(message => message.Contains("could not be ended at the bank", StringComparison.Ordinal));
        host.Factory.CapturedLogMessages.Should().NotContain(message => message.Contains(scenario.SessionId, StringComparison.Ordinal));
    }

    private async Task<BankLinkTestHost> StartWithFailingStoreAsync(IBankDataProvider bankProvider, StoreFailures failures)
    {
        var factory = new LedgerWebApplicationFactory(
            fixture.ConnectionStringFor("ledger_runtime"),
            configureTestServices: services =>
            {
                services.AddSingleton(bankProvider);
                services.AddScoped<IBankConnectionStore>(provider =>
                    new FailingConnectionStore(ActivatorUtilities.CreateInstance<BankConnectionStore>(provider), failures));
            },
            additionalConfiguration: new Dictionary<string, string?> { ["BankLink:RedirectUrl"] = BankLinkTestHost.RedirectUrl });

        return await BankLinkTestHost.StartWithFactoryAsync(fixture, factory);
    }
}

/// <summary>Switches that make the failing connection store refuse the matching operation.</summary>
public sealed class StoreFailures
{
    /// <summary>When true, recording a new connection fails.</summary>
    public bool FailAddConnection { get; set; }

    /// <summary>When true, recording a renewal fails.</summary>
    public bool FailRenewal { get; set; }
}

/// <summary>A connection store that behaves like the real one except where a switch makes it fail.</summary>
public sealed class FailingConnectionStore(IBankConnectionStore inner, StoreFailures failures) : IBankConnectionStore
{
    /// <inheritdoc />
    public Task<LinkedConnection> AddConnectionAsync(
        string provider,
        string aspspName,
        string aspspCountry,
        ProviderSession session,
        string protectedSessionId,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken)
    {
        return failures.FailAddConnection
            ? throw new InvalidOperationException("The store was told to fail.")
            : inner.AddConnectionAsync(provider, aspspName, aspspCountry, session, protectedSessionId, authorizedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetAccountSelectionAsync(Guid connectionId, IReadOnlyList<AccountSelection> selections, CancellationToken cancellationToken)
    {
        return inner.SetAccountSelectionAsync(connectionId, selections, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SyncTarget?> GetSyncTargetAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return inner.GetSyncTargetAsync(connectionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConnectionSummary>> ListConnectionsAsync(CancellationToken cancellationToken)
    {
        return inner.ListConnectionsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConnectionSummary?> FindConnectionAsync(string connectionKey, CancellationToken cancellationToken)
    {
        return inner.FindConnectionAsync(connectionKey, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LinkedAccount>> ListAccountsAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return inner.ListAccountsAsync(connectionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HasAnySyncRunAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return inner.HasAnySyncRunAsync(connectionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<string?> GetProtectedSessionIdAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        return inner.GetProtectedSessionIdAsync(connectionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RenewalResult> ApplyRenewalAsync(
        Guid supersededConnectionId,
        string provider,
        string aspspName,
        string aspspCountry,
        ProviderSession session,
        string protectedSessionId,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken)
    {
        return failures.FailRenewal
            ? throw new InvalidOperationException("The store was told to fail.")
            : inner.ApplyRenewalAsync(supersededConnectionId, provider, aspspName, aspspCountry, session, protectedSessionId, authorizedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkStatusAsync(Guid connectionId, ConnectionStatus status, DateTimeOffset at, CancellationToken cancellationToken)
    {
        return inner.MarkStatusAsync(connectionId, status, at, cancellationToken);
    }
}
