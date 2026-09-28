using FluentAssertions;
using Ledger.Domain.Auth;

namespace Ledger.UnitTests.Auth;

/// <summary>Proves token generation, strict parsing and fixed-time hash comparison.</summary>
public class ApiKeyTokenTests
{
    [Fact]
    public void Generate_produces_a_token_matching_the_expected_shape()
    {
        var (keyId, token, secretSha256) = ApiKeyToken.Generate();

        token.Should().MatchRegex("^ldg_[0-9a-f]{16}_[A-Za-z0-9_-]{43}$");
        keyId.Should().HaveLength(16);
        secretSha256.Should().HaveCount(32);
    }

    [Fact]
    public void Generate_produces_unique_tokens()
    {
        var tokens = Enumerable.Range(0, 1000)
            .Select(_ => ApiKeyToken.Generate().Token)
            .ToList();

        tokens.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void TryParse_accepts_a_generated_token_and_round_trips_its_key_id()
    {
        var (keyId, token, _) = ApiKeyToken.Generate();

        var parsed = ApiKeyToken.TryParse(token, out var parsedKeyId, out var parsedSecret);

        parsed.Should().BeTrue();
        parsedKeyId.Should().Be(keyId);
        parsedSecret.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TryParse_rejects_a_wrong_prefix()
    {
        var (_, token, _) = ApiKeyToken.Generate();
        var malformed = "xyz" + token[3..];

        ApiKeyToken.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_a_key_id_of_the_wrong_length()
    {
        var (keyId, token, _) = ApiKeyToken.Generate();
        var malformed = token[..4] + keyId[..15] + token[20..];

        ApiKeyToken.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_uppercase_hex_in_the_key_id()
    {
        var (keyId, token, _) = ApiKeyToken.Generate();
        var malformed = token[..4] + keyId.ToUpperInvariant() + token[20..];

        ApiKeyToken.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_a_secret_of_the_wrong_length()
    {
        var (_, token, _) = ApiKeyToken.Generate();
        var malformed = token[..^1];

        ApiKeyToken.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_a_secret_with_characters_outside_base64url()
    {
        var (_, token, _) = ApiKeyToken.Generate();
        var malformed = token[..^1] + "!";

        ApiKeyToken.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_an_extra_underscore()
    {
        var (keyId, token, _) = ApiKeyToken.Generate();
        var malformed = token[..4] + "_" + keyId + token[20..];

        ApiKeyToken.TryParse(malformed, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_leading_whitespace()
    {
        var (_, token, _) = ApiKeyToken.Generate();

        ApiKeyToken.TryParse(" " + token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_rejects_trailing_whitespace()
    {
        var (_, token, _) = ApiKeyToken.Generate();

        ApiKeyToken.TryParse(token + " ", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Matches_returns_true_only_for_the_original_secret()
    {
        var (_, token, storedHash) = ApiKeyToken.Generate();
        ApiKeyToken.TryParse(token, out _, out var correctSecret);

        var (_, otherToken, _) = ApiKeyToken.Generate();
        ApiKeyToken.TryParse(otherToken, out _, out var wrongSecret);

        ApiKeyToken.Matches(correctSecret, storedHash).Should().BeTrue();
        ApiKeyToken.Matches(wrongSecret, storedHash).Should().BeFalse();
    }
}
