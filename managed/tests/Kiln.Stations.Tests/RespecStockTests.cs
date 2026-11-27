// SPDX-License-Identifier: MIT
// GitHub #150 (0.2.0-dev.31.18): CraftSourceOptions.Respec on the real managed API against the kit's kiln.stations fake:
// the flag, its counts, the respec snapshot a Kiln client gets (RespecStock: message 11 on kiln:craftstock) and the
// client's hand-off to kiln-items. The payment and the client's checks themselves are native
// (native/services/items/tests/respec_sources_test.cpp).
using Kiln;
using Kiln.TestKit;
using Xunit;
namespace Kiln.StationsTests;

public sealed class RespecStockTests : IDisposable
{
    private const uint Rune = 0x41a792dc;

    public void Dispose() => RespecStock.ResetForTests();

    [Fact]
    public void Respec_is_off_by_default_and_recorded_when_asked_for()
    {
        Assert.False(new CraftSourceOptions().Respec);
        using var h = new KilnHarness();
        var player = h.Players.Join("Mage", rights: PlayerPermissions.AccessInventories);
        var visitor = h.Players.Join("Visitor");
        h.Step();
        var rune = h.Items.AddItem("rune", 100);
        var wood = h.Items.AddItem("wood", 100);
        var a = h.Entities.Create(); h.Items.AddContainer(a).Put(rune, 2);
        var b = h.Entities.Create(); var inv = h.Items.AddContainer(b); inv.Put(rune, 7); inv.Put(wood, 3);
        using var crafting = h.Run(() => Crafting.AddSource(player.Entity, a));
        using var respec = h.Run(() => Crafting.AddSource(player.Entity, b,
            new CraftSourceOptions { Respec = true, Filter = CraftSourceFilter.Allow, Items = [rune] }));
        using var refused = h.Run(() => Crafting.AddSource(visitor.Entity, b, new CraftSourceOptions { Respec = true }));
        Assert.Equal([false, true, true], h.Crafting.Sources.Select(s => s.Respec));
        Assert.Equal([false, false, false], h.Crafting.Sources.Select(s => s.Locks || s.Fills || s.Building));
        // crafting counts every link; the respec only the opted-in one, through its filter
        Assert.Equal(9, h.Crafting.Stock(player.Entity)[rune]);
        Assert.Equal(7, Assert.Single(h.Crafting.RespecStock(player.Entity)).Value);
        Assert.Empty(h.Crafting.RespecStock(visitor.Entity));  // no AccessInventories
        // the kit has no respec payer: source_stock's respec row is unavailable, as where crafting.respec_sources is off
        Assert.Equal(StationsNative.Unavailable, h.Run(() => CraftingCore.RespecStockRows(player.Entity)).Rc);
        Assert.False(h.Run(() => Capabilities.Is(Crafting.RespecSourcesCapability)));
        // the snapshot follows the player's respec links
        Assert.Equal((true, 1), RespecStock.SessionOf(player.Entity));
        respec.Dispose();
        Assert.Equal((true, 0), RespecStock.SessionOf(player.Entity));  // closes at the next tick, after "unsupported" goes out
        Assert.Empty(h.Crafting.RespecStock(player.Entity));
    }

    [Fact]
    public void The_respec_snapshot_goes_to_the_players_Kiln_client_while_a_link_is_opted_in()
    {
        var sent = new List<(EntityId Player, RespecStockRows Message)>();
        RespecStock.SendHook = (p, m) => { sent.Add((p, m)); return true; };
        RespecStock.SupportedHook = () => true;
        var rows = new[] { new StationsNative.Stack { Item = Rune, Count = 0 } };
        RespecStock.RowsHook = _ => (StationsNative.Ok, rows);
        var player = new EntityId(5);
        var s = RespecStock.Track(player);
        Assert.Equal((true, 1), RespecStock.SessionOf(player));
        RespecStock.Pump(100);
        // a row of 0 is sent too: the client then knows the item and counts its own bag and chests alone
        Assert.Equal([new CraftStockRow(Rune, 0)], Assert.Single(sent).Message.Rows);
        Assert.True(sent[0].Message.Supported);
        RespecStock.Pump(110);
        Assert.Single(sent);                                    // unchanged, not due: nothing again
        rows[0].Count = 20;
        RespecStock.Pump(131);
        Assert.Equal(2, sent.Count);                            // the refresh saw the links' count change
        Assert.Equal([new CraftStockRow(Rune, 20)], sent[^1].Message.Rows);
        RespecStock.RowsHook = _ => (StationsNative.Unavailable, []);
        RespecStock.Pump(162);
        Assert.False(sent[^1].Message.Supported);               // no row from kiln-items: the client's own counts again
        RespecStock.RowsHook = _ => (StationsNative.Ok, rows);
        RespecStock.SupportedHook = () => false;
        RespecStock.Pump(193);
        Assert.False(sent[^1].Message.Supported);               // the capability went off
        RespecStock.SupportedHook = () => true;
        // a join into the slot sends the current message again; a handle of an earlier session never ends a later one
        RespecStock.Joined(player);
        RespecStock.Pump(194);
        Assert.True(sent[^1].Message.Supported);
        RespecStock.Gone(player);
        var s2 = RespecStock.Track(player);
        RespecStock.Untrack(player, s);
        Assert.Equal((true, 1), RespecStock.SessionOf(player));
        RespecStock.Untrack(player, s2);
        RespecStock.Pump(195);
        Assert.False(sent[^1].Message.Supported);
        Assert.Equal((false, 0), RespecStock.SessionOf(player)); // the last link went: the session ended
    }

    [Fact]
    public void The_wire_message_round_trips_and_the_client_hands_it_to_kiln_items_only_with_a_Kiln_server()
    {
        Assert.Equal(11, RespecStockRows.Id);  // 1 to 10 are taken on kiln:craftstock
        var w = new NetWriter();
        RespecStockRows.Encode(new RespecStockRows(true, [new(Rune, 20)]), w);
        var reader = new NetReader(w.WrittenSpan);
        var back = RespecStockRows.Decode(ref reader);
        Assert.True(back.Supported && back.Rows.Single() == new CraftStockRow(Rune, 20));
        var bad = new NetWriter();
        bad.Bool(true).UInt16(2).UInt32(Rune).UInt32(1).UInt32(Rune).UInt32(2);
        Assert.Throws<InvalidDataException>(() => { var rr = new NetReader(bad.WrittenSpan); RespecStockRows.Decode(ref rr); });
        var many = new NetWriter();
        many.Bool(true).UInt16((ushort)(RespecStock.MaxRows + 1));
        Assert.Throws<InvalidDataException>(() => { var rr = new NetReader(many.WrittenSpan); RespecStockRows.Decode(ref rr); });
        var off = new NetWriter();
        RespecStockRows.Encode(RespecStock.Off, off);
        var ro = new NetReader(off.WrittenSpan);
        Assert.False(RespecStockRows.Decode(ref ro).Supported);
        var applied = new List<(bool On, int Rows)>();
        RespecStock.ApplyHook = (on, r) => { applied.Add((on, r.Length)); return StationsNative.Ok; };
        RespecStock.ServerSideHook = () => true;
        Assert.True(RespecStock.ClientApply(back));
        RespecStock.ServerSideHook = () => false;              // a vanilla or older server never turns the client's counts on
        Assert.True(RespecStock.ClientApply(back));
        Assert.Equal([(true, 1), (false, 0)], applied);
    }

    [Fact]
    public void The_respec_flags_reach_kiln_stations()
    {
        Assert.Equal((8u, 16u, 16), (StationsNative.SourceRespec, StationsNative.StockRespec, StationsNative.MaxRespecStock));
        Assert.False(CraftingCore.HasRespecMembers((uint)StationsNative.TableItemFills));
        Assert.True(CraftingCore.HasRespecMembers((uint)StationsNative.TableRespec));
        Assert.Equal(StationsNative.TableItemFills + sizeof(long), StationsNative.TableRespec);
        Assert.Equal("crafting.respec_sources", Crafting.RespecSourcesCapability);
    }
}
