using System.Net;
using FluentAssertions;
using Ledger.Domain.Banking;
using Ledger.Domain.Ingestion;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Service.Ingestion;
using Ledger.Service.Ingestion.Synthetic;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.IntegrationTests.Ingestion;

/// <summary>
/// Verifies the operator is told, wherever the link flow shows something, that the accounts must be selected right away
/// because the bank returns the full history only for a short time after approval, while accounts that are not selected stay
/// unread.
/// </summary>
[Collection("Database")]
[Trait("Category", "Callback")]
public class SelectionReminderTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task The_page_after_a_first_link_says_to_select_the_accounts_now_and_that_unselected_accounts_are_never_read()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        scenario.AddAccount(AccountKind.Savings);

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var state = await host.StartLinkAsync();
        using var callback = await host.CallbackAsync(state, scenario.AuthorizationCode);

        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        page.Should().Contain("2 accounts were found");
        page.Should().Contain("Select the accounts to sync now");
        page.Should().Contain("about an hour after approval");
        page.Should().Contain("never read");
    }

    [Fact]
    public async Task A_connection_waiting_for_its_account_selection_says_so_in_the_connection_list_until_accounts_are_selected()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);

        await using var host = await BankLinkTestHost.StartAsync(fixture, scenario);

        var connectionKey = await host.LinkAsync(scenario);
        var waiting = (await host.ListConnectionsAsync()).Single(connection => connection.GetProperty("connectionKey").GetString() == connectionKey);

        waiting.GetProperty("selectionPending").GetBoolean().Should().BeTrue();
        waiting.GetProperty("hint").GetString().Should().Contain("Select the accounts now").And.Contain("renew the connection");

        var accounts = await host.ListAccountsAsync(connectionKey);
        await host.SelectAsync(connectionKey, (accounts[0].GetProperty("accountKey").GetString()!, "Joint", true));

        var done = (await host.ListConnectionsAsync()).Single(connection => connection.GetProperty("connectionKey").GetString() == connectionKey);
        done.GetProperty("selectionPending").GetBoolean().Should().BeFalse();
        done.GetProperty("hint").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Linking_again_with_accounts_that_were_selected_before_queues_the_first_sync_without_asking_for_a_selection()
    {
        var scenario = SyntheticBankScenario.Create();
        scenario.AddAccount(AccountKind.Current);
        var recorder = new RecordingSyncDispatcher();

        await using var host = await BankLinkTestHost.StartWithFactoryAsync(
            fixture,
            new LedgerWebApplicationFactory(
                fixture.ConnectionStringFor("ledger_runtime"),
                configureTestServices: services =>
                {
                    services.AddSingleton<IBankDataProvider>(new SyntheticBankDataProvider(scenario));
                    services.AddSingleton<ISyncDispatcher>(recorder);
                },
                additionalConfiguration: new Dictionary<string, string?> { ["BankLink:RedirectUrl"] = BankLinkTestHost.RedirectUrl }));

        var firstKey = await host.LinkAsync(scenario);
        var accounts = await host.ListAccountsAsync(firstKey);
        await host.SelectAsync(firstKey, (accounts[0].GetProperty("accountKey").GetString()!, "Joint", true));
        recorder.Requests.Should().ContainSingle();

        var state = await host.StartLinkAsync();
        using var callback = await host.CallbackAsync(state, scenario.AuthorizationCode);

        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain("Select the");
        var queued = recorder.Requests.Should().HaveCount(2).And.Subject.Last();
        queued.Trigger.Should().Be(SyncTrigger.PostLink);
        queued.ConnectionId.Should().NotBe(recorder.Requests[0].ConnectionId);
    }
}
