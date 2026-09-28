using FluentAssertions;
using Ledger.Domain.Auth;
using Ledger.IntegrationTests.Infrastructure;
using Ledger.Repository;
using Ledger.Repository.Stores;
using Ledger.Service.Cli;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ledger.IntegrationTests.Auth;

/// <summary>Proves `apikey create|list|revoke` behaves per the naming, hashing and revocation contract against a real database.</summary>
[Collection("Database")]
public class ApiKeyCommandTests(DatabaseFixture fixture)
{
    [Fact]
    [Trait("Category", "ApiKeyCli")]
    public async Task Create_list_revoke_and_recreate_a_named_key()
    {
        var (createExitCode, createStdout, createStderr) = await RunCliAsync("create", "operator");

        createExitCode.Should().Be(0);
        var createdLines = SplitLines(createStdout);
        createdLines.Should().ContainSingle();
        var token = createdLines[0];
        token.Should().MatchRegex("^ldg_[0-9a-f]{16}_[A-Za-z0-9_-]{43}$");
        createStderr.Should().NotBeNullOrWhiteSpace();

        ApiKeyToken.TryParse(token, out var keyId, out var secret).Should().BeTrue();

        await using (var context = CreateRuntimeContext())
        {
            var row = await context.ApiKeys.AsNoTracking()
                .SingleAsync(key => key.KeyId == keyId, TestContext.Current.CancellationToken);
            row.Name.Should().Be("operator");
            Convert.ToBase64String(row.SecretSha256).Should().NotContain(secret);
        }

        var (listExitCode, listStdout, _) = await RunCliAsync("list");

        listExitCode.Should().Be(0);
        listStdout.Should().Contain("name").And.Contain("key_id").And.Contain("created").And.Contain("revoked");
        listStdout.Should().Contain("operator");
        listStdout.Should().NotContain(secret);
        listStdout.Should().NotContain(token);

        var (duplicateExitCode, _, _) = await RunCliAsync("create", "operator");
        duplicateExitCode.Should().Be(1);

        var (revokeExitCode, _, _) = await RunCliAsync("revoke", "operator");
        revokeExitCode.Should().Be(0);

        var (recreateExitCode, _, _) = await RunCliAsync("create", "operator");
        recreateExitCode.Should().Be(0);
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("a")]
    [InlineData("x;y")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [Trait("Category", "ApiKeyCli")]
    public async Task Create_rejects_invalid_names(string name)
    {
        var (exitCode, stdout, _) = await RunCliAsync("create", name);

        exitCode.Should().Be(1);
        stdout.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "ApiKeyCli")]
    public async Task Store_validates_active_tokens_and_rejects_revoked_unknown_or_malformed_ones()
    {
        await using var context = CreateRuntimeContext();
        var store = new ApiKeyStore(context);

        var created = await store.CreateAsync("validate-test", TestContext.Current.CancellationToken);

        var activeIdentity = await store.ValidateAsync(created.Token, TestContext.Current.CancellationToken);
        activeIdentity.Should().NotBeNull();
        activeIdentity!.Name.Should().Be("validate-test");

        await store.RevokeAsync("validate-test", TestContext.Current.CancellationToken);
        (await store.ValidateAsync(created.Token, TestContext.Current.CancellationToken)).Should().BeNull();

        var unknownToken = ApiKeyToken.Generate().Token;
        (await store.ValidateAsync(unknownToken, TestContext.Current.CancellationToken)).Should().BeNull();

        (await store.ValidateAsync("not-a-token", TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ledger"] = fixture.ConnectionStringFor("ledger_runtime")
            })
            .Build();

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();

        try
        {
            Console.SetOut(stdoutWriter);
            Console.SetError(stderrWriter);

            var exitCode = await ApiKeyCommand.RunAsync(args, configuration);

            return (exitCode, stdoutWriter.ToString(), stderrWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private LedgerDbContext CreateRuntimeContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<LedgerDbContext>();
        optionsBuilder.UseNpgsql(fixture.ConnectionStringFor("ledger_runtime"));
        return new LedgerDbContext(optionsBuilder.Options);
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
