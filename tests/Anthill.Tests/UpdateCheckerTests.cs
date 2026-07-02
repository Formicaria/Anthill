using Anthill.Api;
using Xunit;

namespace Anthill.Tests;

/// <summary>
/// Version comparison behind the header's "update available" check (v1.8.14.4). The comparer must
/// order dotted numeric versions correctly, including the four-part patch scheme ANTHILL uses
/// (1.8.14.3), tolerate a leading "v", and treat missing trailing parts as zero.
/// </summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.8.15", "1.8.14", 1)]      // newer minor-patch
    [InlineData("1.8.14.3", "1.8.14.2", 1)]  // newer four-part patch
    [InlineData("1.8.14.2", "1.8.14.3", -1)] // older
    [InlineData("1.8.14", "1.8.14", 0)]      // equal
    [InlineData("1.8.14.0", "1.8.14", 0)]    // trailing zero == missing part
    [InlineData("1.9.0", "1.8.99", 1)]       // minor beats a big patch
    public void Compare_OrdersVersions(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(UpdateChecker.Compare(a, b)));
    }

    [Fact]
    public void Compare_ToleratesLeadingV()
    {
        Assert.True(UpdateChecker.Compare("v1.8.15", "1.8.14") > 0);
        Assert.Equal(0, UpdateChecker.Compare("v1.8.14.1", "1.8.14.1"));
    }
}
