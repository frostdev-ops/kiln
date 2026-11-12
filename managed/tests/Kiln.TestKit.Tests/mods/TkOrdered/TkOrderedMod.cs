// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace TkOrdered;

/// <summary>Each test checks that every test that ran before it (on this one instance) has a lower order, so a run in
/// any other order, of all or of a subset, fails. Names sort the other way round on purpose.</summary>
public sealed class TkOrderedMod : Mod
{
    private readonly List<int> _ran = [];

    private void Ran(int order)
    {
        Assert.True(_ran.TrueForAll(o => o < order), $"test {order} ran after {string.Join(", ", _ran)}");
        _ran.Add(order);
    }

    [KilnTest(Order = 3)]
    public void A_third() => Ran(3);

    [KilnTest(Order = 2)]
    public async Task B_second(TestContext t)
    {
        await t.Ticks(2);
        Ran(2);
    }

    [KilnTest(Order = 1)]
    public void C_first() => Ran(1);

    [KilnTest(Order = 4, Skip = "a skip in an xunit suite")]
    public void D_skipped() => Assert.Fail("never runs");

    [KilnTest(Order = 5)]
    public void E_fifth() => Ran(5);
}
