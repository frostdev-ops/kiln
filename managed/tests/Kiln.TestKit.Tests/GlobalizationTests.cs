// SPDX-License-Identifier: MIT
using System.Globalization;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class GlobalizationTests
{
    [Fact]
    public void TheTickThreadUsesTheGamesInvariantGlobalization()
    {
        using var kiln = new KilnHarness();
        var (culture, names, number) = kiln.Run(() =>
        {
            var names = new[] { "apple", "Banana", "cherry" };
            Array.Sort(names);
            return (CultureInfo.CurrentCulture.Name, names, 12.5.ToString());
        });
        Assert.Equal(new[] { "Banana", "apple", "cherry" }, names);
        Assert.Equal("12.5", number);
        Assert.Equal("", culture);
    }
}
