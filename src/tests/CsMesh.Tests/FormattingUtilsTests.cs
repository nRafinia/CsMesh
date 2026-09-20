using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A percentage must never round up into "100%". 199/200 is 99.5%, and {value:F0} printed it as a
/// clean hundred; the value is floored now, and 100% is reserved for a true 100%.
/// </summary>
public sealed class FormattingUtilsTests
{
    [Theory]
    [InlineData(199, 200, "99%")]
    [InlineData(200, 200, "100%")]
    [InlineData(0, 200, "0%")]
    [InlineData(1, 3, "33%")]
    public void PercentagesAreFloored(int numerator, int denominator, string expected)
    {
        Assert.Equal(expected, FormattingUtils.Pct(numerator, denominator));
    }

    [Fact]
    public void AnEmptyDenominatorIsZeroNotADivideByZero()
    {
        Assert.Equal("0%", FormattingUtils.Pct(0, 0));
    }
}
