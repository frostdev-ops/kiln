// SPDX-License-Identifier: MIT
// Issue #44 Phase 1c: managed search panel bounds and stale/corrupt snapshots.
using Xunit;

namespace Kiln.UiTests;

public sealed class ChatSearchDisplayTests
{
    private static UiNative.ChatSearch Snapshot() => new()
    {
        Active = 1, Field = 1, Revision = 3,
        X = 100, Y = 700, Width = 410, Height = 36,
        QueryLen = 4, First = 0, Selected = 0, Count = 2, Total = 2,
    };

    [Fact]
    public void FocusedSnapshotWithVisibleSelectionDraws()
    {
        var s = Snapshot();
        Assert.True(ChatSearchDisplayRules.Valid(s));
        s.Total = 30;
        s.First = 17;
        s.Selected = 26;
        s.Count = 10;
        Assert.True(ChatSearchDisplayRules.Valid(s));
    }

    [Fact]
    public void InactiveMalformedOrStaleSnapshotIsRejected()
    {
        var s = Snapshot();
        s.Active = 0;
        Assert.False(ChatSearchDisplayRules.Valid(s));
        s = Snapshot();
        s.QueryLen = 257;
        Assert.False(ChatSearchDisplayRules.Valid(s));
        s = Snapshot();
        s.Count = 11;
        Assert.False(ChatSearchDisplayRules.Valid(s));
        s = Snapshot();
        s.First = 3;
        Assert.False(ChatSearchDisplayRules.Valid(s));
        s = Snapshot();
        s.Selected = 2;
        Assert.False(ChatSearchDisplayRules.Valid(s));
        s = Snapshot();
        s.Width = float.NaN;
        Assert.False(ChatSearchDisplayRules.Valid(s));
        s = Snapshot();
        s.Height = -1;
        Assert.False(ChatSearchDisplayRules.Valid(s));
    }

    [Fact]
    public void EmptySearchIsStillVisible()
    {
        var s = Snapshot();
        s.Total = 0;
        s.Count = 0;
        Assert.True(ChatSearchDisplayRules.Valid(s));
    }
}
