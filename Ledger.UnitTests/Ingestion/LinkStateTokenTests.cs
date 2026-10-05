using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Ledger.Domain.Ingestion;

namespace Ledger.UnitTests.Ingestion;

/// <summary>Verifies the one-time link state is 256-bit random, hashed exactly and strictly validated.</summary>
[Trait("Category", "Callback")]
public class LinkStateTokenTests
{
    [Fact]
    public void Generate_returns_a_43_character_base64url_state_whose_sha256_is_returned()
    {
        var (state, sha256) = LinkStateToken.Generate();

        state.Should().HaveLength(43);
        state.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        sha256.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

    [Fact]
    public void Generate_returns_unique_states()
    {
        var states = Enumerable.Range(0, 1000).Select(_ => LinkStateToken.Generate().State).ToList();

        states.Distinct().Should().HaveCount(1000);
    }

    [Fact]
    public void TryHash_accepts_a_generated_state_and_returns_the_same_hash()
    {
        var (state, sha256) = LinkStateToken.Generate();

        var accepted = LinkStateToken.TryHash(state, out var presentedHash);

        accepted.Should().BeTrue();
        presentedHash.Should().Equal(sha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tooshort")]
    public void TryHash_rejects_missing_empty_and_short_input(string? presented)
    {
        LinkStateToken.TryHash(presented, out var hash).Should().BeFalse();
        hash.Should().BeEmpty();
    }

    [Fact]
    public void TryHash_rejects_42_and_44_characters()
    {
        var (state, _) = LinkStateToken.Generate();

        LinkStateToken.TryHash(state[..42], out _).Should().BeFalse();
        LinkStateToken.TryHash(state + "A", out _).Should().BeFalse();
    }

    [Fact]
    public void TryHash_rejects_padding_and_characters_outside_base64url()
    {
        var (state, _) = LinkStateToken.Generate();

        LinkStateToken.TryHash(state[..42] + "=", out _).Should().BeFalse();
        LinkStateToken.TryHash(state[..42] + "+", out _).Should().BeFalse();
        LinkStateToken.TryHash(state[..42] + "/", out _).Should().BeFalse();
        LinkStateToken.TryHash(state[..42] + " ", out _).Should().BeFalse();
        LinkStateToken.TryHash(state[..42] + "\n", out _).Should().BeFalse();
        LinkStateToken.TryHash(state[..42] + "é", out _).Should().BeFalse();
    }
}
