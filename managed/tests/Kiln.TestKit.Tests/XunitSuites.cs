// SPDX-License-Identifier: MIT
using Kiln.TestKit.Xunit;
using SampleTests;
using TkOrdered;

namespace Kiln.TestKit.Tests;

/// <summary>The smallest suite: TkOrdered's tests, its own manifest file. Each test fails when one with a higher order
/// ran before it on the same instance, so xunit must run them in the runner's order, all or a filtered subset
/// (<c>dotnet test --filter "DisplayName~B_second|DisplayName~A_third"</c>).</summary>
public sealed class TkOrderedSuite : KilnTestSuite<TkOrderedMod>;

/// <summary>Kiln's sample mod of in-game tests, offline: it depends on sample-lib and optionally on sample-consumer, both
/// loaded first.</summary>
public sealed class SampleTestsSuite : KilnTestSuite<SampleTestsMod>
{
    protected override void Setup(KilnHarness kiln)
    {
        kiln.LoadFolder(TestMods.Folder("SampleLib"));
        kiln.LoadFolder(TestMods.Folder("SampleConsumer"));
    }

    protected override ModHandle LoadMod(KilnHarness kiln) => kiln.LoadFolder(TestMods.Folder("SampleTests"));
}
