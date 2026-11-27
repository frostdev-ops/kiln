// SPDX-License-Identifier: MIT
// dev.32 api-shape (docs lane A's A15): a give's or take's result reads like every other Kiln result (IKilnResult): Ok is
// everything asked for (Done), Reason says why not; Succeeded still counts a partial give.
using Kiln;
using Xunit;

namespace Kiln.ItemsTests;

public sealed class ItemsResultShapeTests
{
    private static ItemsResult Of(ItemsStatus status, int done, string message = "") =>
        new(status, new EntityId(1), new ItemId(7), 10, done, [], message, 0, 0, false);

    [Fact]
    public void An_items_result_is_a_kiln_result()
    {
        IKilnResult done = Of(ItemsStatus.Done, 10);
        Assert.True(done.Ok);
        Assert.Null(done.Reason);
        var partial = Of(ItemsStatus.Partial, 4, "only 4 fit");
        Assert.False(partial.Ok);
        Assert.True(partial.Succeeded);
        Assert.Equal("Partial: only 4 fit", partial.Reason);
        Assert.Equal("NoRoom", Of(ItemsStatus.NoRoom, 0).Reason);
    }
}
