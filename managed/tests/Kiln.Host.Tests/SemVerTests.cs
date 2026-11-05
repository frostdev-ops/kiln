// SPDX-License-Identifier: MIT
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("1", 1, 0, 0, "")]
    [InlineData("0.1.0-dev", 0, 1, 0, "dev")]
    [InlineData("1.0.0-rc.1+build.5", 1, 0, 0, "rc.1")]
    public void Parses(string text, int major, int minor, int patch, string pre)
    {
        var v = SemVersion.Parse(text);
        Assert.Equal((major, minor, patch), (v.Major, v.Minor, v.Patch));
        Assert.Equal(pre, string.Join('.', v.Pre));
    }

    [Theory]
    [InlineData("")]
    [InlineData("one")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-01")]
    [InlineData("1.x")]
    public void Rejects(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void Orders_by_semver_precedence()
    {
        // semver.org §11 example
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        for (var i = 0; i < ordered.Length - 1; i++)
            Assert.True(SemVersion.Parse(ordered[i]) < SemVersion.Parse(ordered[i + 1]), $"{ordered[i]} < {ordered[i + 1]}");
        Assert.Equal(SemVersion.Parse("1.0.0+a"), SemVersion.Parse("1.0.0+b"));  // build metadata is ignored
    }

    [Theory]
    // caret (and bare = caret)
    [InlineData("^1.2", "1.2.0", true)]
    [InlineData("^1.2", "1.9.9", true)]
    [InlineData("^1.2", "2.0.0", false)]
    [InlineData("^1.2", "2.0.0-beta", false)]
    [InlineData("^1.2", "1.1.9", false)]
    [InlineData("1.2", "1.5.0", true)]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.3.0", true)]
    [InlineData("^0.2", "0.2.5", true)]
    [InlineData("^0.2", "0.3.0", false)]
    [InlineData("^0.0.3", "0.0.4", false)]
    // tilde
    [InlineData("~1.2", "1.2.9", true)]
    [InlineData("~1.2", "1.3.0", false)]
    [InlineData("~1.2.3", "1.2.2", false)]
    [InlineData("~1", "1.9.0", true)]
    // comparators, AND, OR
    [InlineData(">=1.0 <2", "1.5.0", true)]
    [InlineData(">=1.0 <2", "2.0.0", false)]
    [InlineData(">=1.0, <2", "0.9.0", false)]
    [InlineData(">= 1.0", "1.0.0", true)]
    [InlineData(">1.2", "1.2.9", false)]
    [InlineData(">1.2", "1.3.0", true)]
    [InlineData(">1.2.3", "1.2.4", true)]
    [InlineData("<=1.2", "1.2.7", true)]
    [InlineData("<=1.2.0", "1.2.1", false)]
    [InlineData("=1.2.3", "1.2.4", false)]
    [InlineData("^1 || ^3", "3.1.0", true)]
    [InlineData("^1 || ^3", "2.1.0", false)]
    [InlineData("1.0 - 2.0", "2.0.9", true)]
    [InlineData("1.0 - 2.0", "2.1.0", false)]
    [InlineData("1.0 - 2", "2.9.0", true)]
    [InlineData("1.0.0 - 2.0.0", "2.0.0", true)]
    // wildcards
    [InlineData("*", "0.0.1", true)]
    [InlineData("1.x", "1.4.2", true)]
    [InlineData("1.2.*", "1.3.0", false)]
    // Maven / Forge intervals
    [InlineData("[1.0,2.0)", "1.0.0", true)]
    [InlineData("[1.0,2.0)", "2.0.0", false)]
    [InlineData("(1.0,2.0]", "1.0.0", false)]
    [InlineData("(1.0,2.0]", "2.0.0", true)]
    [InlineData("[1.0,)", "9.0.0", true)]
    [InlineData("(,2.0]", "0.1.0", true)]
    [InlineData("[1.2.3]", "1.2.3", true)]
    [InlineData("[1.2.3]", "1.2.4", false)]
    [InlineData("[1.0,1.5),[2.0,3.0)", "2.5.0", true)]
    [InlineData("[1.0,1.5),[2.0,3.0)", "1.7.0", false)]
    public void Range_satisfies(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Parse(range).Satisfies(SemVersion.Parse(version)));

    [Theory]
    [InlineData(">=")]
    [InlineData("^one")]
    [InlineData("[1.0")]
    [InlineData("[1.0,2.0,3.0]")]
    [InlineData("1.0 ||")]
    public void Range_rejects(string range) => Assert.False(VersionRange.TryParse(range, out _, out _));

    [Theory]
    [InlineData("0.1", "0.1.0", true)]
    [InlineData("0.1", "0.2.0", true)]   // the API is additive within a major, 0.x included
    [InlineData("0.2", "0.1.0", false)]
    [InlineData("0.1", "1.0.0", false)]
    public void ApiVersion_bare_means_at_least_same_major(string api, string current, bool ok) =>
        Assert.Equal(ok, VersionRange.AtLeastSameMajor(SemVersion.Parse(api)).Satisfies(SemVersion.Parse(current)));
}
