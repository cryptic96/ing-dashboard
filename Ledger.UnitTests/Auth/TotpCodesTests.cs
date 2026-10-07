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
}
