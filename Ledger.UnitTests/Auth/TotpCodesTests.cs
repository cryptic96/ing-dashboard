using FluentAssertions;
using Ledger.Domain.Auth;

namespace Ledger.UnitTests.Auth;

/// <summary>Verifies that only the canonical six-digit spelling of a one-time code is accepted.</summary>
[Trait("Category", "OAuth")]
public class TotpCodesTests
{
    [Theory]
    [InlineData("050465")]
    [InlineData("000000")]
    [InlineData("999999")]
    public void Six_ascii_digits_are_well_formed(string code)
    {
        TotpCodes.IsWellFormed(code).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("+050465")]
    [InlineData("-050465")]
    [InlineData("0050465")]
    [InlineData("00050465")]
    [InlineData("\t050465")]
    [InlineData("050465 ")]
    [InlineData(" 050465")]
    [InlineData("05046٥")]
    [InlineData("05046a")]
    [InlineData("050.65")]
    public void Any_other_spelling_is_refused(string? code)
    {
        TotpCodes.IsWellFormed(code).Should().BeFalse();
    }

    private const string Seed = SeedHalf + SeedHalf;
    private const string SeedHalf = "GEZDGNBVGY3TQOJQ";
    private const string ReferenceCodeForStepOne = "287082";

    [Fact]
    public void A_code_reports_the_time_step_it_belongs_to()
    {
        TotpCodes.MatchTimeStep(Seed, ReferenceCodeForStepOne, DateTimeOffset.FromUnixTimeSeconds(59)).Should().Be(1);
    }

    [Theory]
    [InlineData(-60, 1)]
    [InlineData(60, 1)]
    public void A_code_still_verifies_within_two_steps_either_side_and_keeps_its_own_step(int shiftSeconds, long expectedStep)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59 + shiftSeconds);

        TotpCodes.MatchTimeStep(Seed, ReferenceCodeForStepOne, now).Should().Be(expectedStep);
    }

    [Fact]
    public void A_code_outside_the_tolerance_does_not_match()
    {
        TotpCodes.MatchTimeStep(Seed, ReferenceCodeForStepOne, DateTimeOffset.FromUnixTimeSeconds(59 + 90)).Should().BeNull();
    }

    [Theory]
    [InlineData("+287082")]
    [InlineData("0287082")]
    [InlineData("\t287082")]
    [InlineData("")]
    [InlineData(null)]
    public void A_badly_formed_code_never_matches(string? code)
    {
        TotpCodes.MatchTimeStep(Seed, code, DateTimeOffset.FromUnixTimeSeconds(59)).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base32 !")]
    public void An_unreadable_key_never_matches(string? key)
    {
        TotpCodes.MatchTimeStep(key, ReferenceCodeForStepOne, DateTimeOffset.FromUnixTimeSeconds(59)).Should().BeNull();
    }
}
