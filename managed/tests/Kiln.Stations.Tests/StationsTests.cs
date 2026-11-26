// SPDX-License-Identifier: MIT
// Kiln.Stations offline, on a fake kiln.stations: the native mirrors' layouts, the requests built from the options (fill,
// stop, resume, select), results and plans from native records, the bases and stations read, the event pump (the watch
// follows the subscriptions, records become events, a mod's own write is named), a finished cycle and a player's station
// button typed, and the argument, thread and capability errors. What the game does is the lab test
// (mods/tests/stations-test); the native logic is native/services/items/tests/stations_test.cpp.
using System.Runtime.InteropServices;
using Kiln;
using Kiln.TestKit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Kiln.StationsTests;

public sealed unsafe class StationsTests : IDisposable
{
    internal static readonly Guid AltarGuid = new("0b0c0d0e-0102-0304-0506-0708090a0b0c");

    // A fake kiln.stations: what Kiln asked, and what it answers.
    internal sealed class Fake : IStationsBackend
    {
        public (bool Read, bool Fill, bool Control, bool Events, string Detail)? State = (true, true, true, true, "");
        public readonly List<StationsNative.Base> BaseRows = [];
        public readonly Dictionary<uint, StationsNative.Station> StationRows = new();
        public readonly Dictionary<uint, StationsNative.Recipe> Recipes = new();
        public readonly Dictionary<uint, uint[]> Storage = new(), Water = new();
        public StationsNative.Stack[] Stock = [];
        public readonly List<(StationsNative.Request Request, StationsNative.Stack[] Keep, ulong Owner, long Key)> Submitted = [];
        public readonly List<bool> Watches = [];
        public readonly List<StationsNative.Event> Ring = [];
        public StationsNative.Plan NextPlan;
        public StationsNative.Request LastPlanned;
        public int SubmitResult;

        public (bool Read, bool Fill, bool Control, bool Events, string Detail)? Status() => State;

        public bool NoWorld;
        public int BaseCount(out uint n)
        {
            n = (uint)BaseRows.Count;
            return NoWorld ? StationsNative.Unavailable : 0;
        }

        public int BaseAt(uint index, out StationsNative.Base b)
        {
            b = index < BaseRows.Count ? BaseRows[(int)index] : default;
            return index < BaseRows.Count ? 0 : StationsNative.NotFound;
        }

        /// <summary>The base the game's position lookup puts an entity that is no station in (Bases.Of; -1: none).</summary>
        public readonly Dictionary<uint, int> EntityBase = new();

        public int BaseOf(uint entity, out StationsNative.Base b)
        {
            b = default;
            if (EntityBase.TryGetValue(entity, out var index)) return index < 0 ? StationsNative.NotFound : BaseAt((uint)index, out b);
            if (!StationRows.TryGetValue(entity, out var s) || s.BaseIndex < 0) return StationsNative.NotFound;
            return BaseAt((uint)s.BaseIndex, out b);
        }

        public int BaseAtPosition(WorldPosition p, out StationsNative.Base b)
        {
            foreach (var x in BaseRows)
                if (p.X >= x.Min.X && p.X < x.Max.X && p.Z >= x.Min.Z && p.Z < x.Max.Z)
                {
                    b = x;
                    return 0;
                }
            b = default;
            return StationsNative.NotFound;
        }

        public (int Rc, uint[] Ids) BaseList(uint index, bool water) =>
            !Rebuilt && (water ? Water : Storage).TryGetValue(index, out var ids) ? (0, ids) : (StationsNative.NotFound, []);

        /// <summary>The game rebuilt its bases between Kiln's reads: a record index read a moment ago is gone.</summary>
        public bool Rebuilt;

        public (int Rc, StationsNative.Stack[] Rows) BaseStock(uint index) => Rebuilt ? (StationsNative.NotFound, []) : (0, Stock);

        public (int Rc, uint[] Ids) StationIds(int baseIndex) =>
            Rebuilt && baseIndex >= 0 ? (StationsNative.NotFound, [])
                : (0, StationRows.Values.Where(s => baseIndex < 0 || s.BaseIndex == baseIndex).Select(s => s.Entity).ToArray());

        public int StationRead(uint entity, out StationsNative.Station s) =>
            StationRows.TryGetValue(entity, out s) ? 0 : StationsNative.NotFound;

        public int Recipe(uint id, out StationsNative.Recipe r) => Recipes.TryGetValue(id, out r) ? 0 : StationsNative.NotFound;

        public (int Rc, StationsNative.Stack[] Rows) RecipeInputs(uint id, StationsNative.Choice[] choices)
        {
            if (!Recipes.TryGetValue(id, out var r)) return (StationsNative.NotFound, []);
            var rows = new List<StationsNative.Stack>();
            for (var i = 0; i < r.InputCount; i++)
            {
                var cat = r.Inputs[i * 4 + 2];
                var item = cat == 0 ? r.Inputs[i * 4] : 0x7000u + choices.FirstOrDefault(c => c.Category == cat).Index;
                rows.Add(new StationsNative.Stack { Item = item, Count = cat == 0 ? r.Inputs[i * 4 + 1] : r.Inputs[i * 4 + 3] });
            }
            return (0, rows.ToArray());
        }

        public (int Rc, (uint Item, float Scaling)[] Options) Category(uint id) =>
            id == 77 ? (0, [(0x7000u, 1f), (0x7001u, 0.5f)]) : (StationsNative.NotFound, []);

        public int CategoryAt(uint index, out uint id)
        {
            id = index == 0 ? 77u : 0;
            return index == 0 ? 0 : StationsNative.NotFound;
        }

        public (int Rc, uint[] Ids) WorkshopRecipes(uint workshop) => (0, Recipes.Values.Where(r => r.Workshop == workshop).Select(r => r.Id).ToArray());

        /// <summary>When set, the plan for a request (else <see cref="NextPlan"/>): a fill with and without station outputs.</summary>
        public Func<StationsNative.Request, StationsNative.Plan>? PlanFor;
        public readonly List<StationsNative.Request> Planned = [];

        public int Plan(in StationsNative.Request r, StationsNative.Stack[] keep, uint[] chests, out StationsNative.Plan p)
        {
            LastPlanned = r;
            LastChests = chests;
            Planned.Add(r);
            p = PlanFor is { } f ? f(r) : NextPlan;
            return 0;
        }

        public uint[] LastChests = [];
        public bool HasChests { get; set; } = true;
        public bool HasLinkedFills { get; set; } = true;  // GitHub #132
        public bool HasItemFills { get; set; } = true;    // GitHub #132, 0.2.0-dev.31.16: the per-ingredient transfer
        // base_stock_with_stations (0.2.0-dev.29.17): off unless a test sets it
        public bool HasStockWithStations { get; set; }
        public StationsNative.Stack[] StockWithStations = [];
        public readonly List<uint> StockWithStationsAsked = [];
        public (int Rc, StationsNative.Stack[] Rows) BaseStockWithStations(uint index)
        {
            StockWithStationsAsked.Add(index);
            return index < BaseRows.Count ? (0, StockWithStations) : (StationsNative.NotFound, []);
        }

        // station outputs as crafting stock (0.2.0-dev.29.17)
        public bool HasOutputs = true;
        public readonly SortedSet<uint> Sources = [];
        public bool HasOutputSources => HasOutputs;
        public bool HasCraftStock => HasOutputs;
        public StationsNative.Stack[] CraftStock = [];
        public int SetCraftStock(StationsNative.Stack[] rows)
        {
            if (!HasCraftStock) return StationsNative.Unavailable;
            CraftStock = rows;
            return 0;
        }

        // the station screen's fill buttons (0.2.0-dev.29.19)
        public bool HasFill = true;
        public bool HasFillUi => HasFill;
        public StationsNative.FillAnswer[] FillAnswers = [];
        public StationsNative.FillKey[] Asks = [];
        public int SetFillAnswers(StationsNative.FillAnswer[] rows)
        {
            if (!HasFillUi) return StationsNative.Unavailable;
            FillAnswers = rows;
            return 0;
        }
        public (int Rc, StationsNative.FillKey[] Keys) FillAsks() => HasFillUi ? (0, Asks) : (StationsNative.Unavailable, []);

        /// <summary>craft_items: per lent item, the other inputs of the recipes that take it.</summary>
        public readonly Dictionary<uint, uint[]> CraftInputs = new();
        public (int Rc, uint[] Items) CraftItems(uint[] lent)
        {
            var set = new SortedSet<uint>(lent);
            foreach (var l in lent)
                if (CraftInputs.TryGetValue(l, out var more)) set.UnionWith(more);
            return (0, set.ToArray());
        }

        public int SetOutputSource(uint entity, bool on)
        {
            if (!HasOutputs) return StationsNative.Unavailable;
            if (on) Sources.Add(entity);
            else Sources.Remove(entity);
            return 0;
        }

        public (int Rc, uint[] Ids) OutputSources() => HasOutputs ? (0, Sources.ToArray()) : (StationsNative.Unavailable, []);

        public int Submit(in StationsNative.Request r, StationsNative.Stack[] keep, uint[] chests, ulong owner, long key)
        {
            if (SubmitResult != 0) return SubmitResult;
            Submitted.Add((r, keep, owner, key));
            return 0;
        }

        public int Watch(bool on)
        {
            Watches.Add(on);
            return 0;
        }

        public ulong LastSeq() => Ring.Count == 0 ? 0 : Ring[^1].Seq;

        public bool ScannedFlag = true;
        public bool Scanned() => ScannedFlag;

        public int Events(ulong after, Span<StationsNative.Event> into, out uint count)
        {
            var rest = Ring.Where(e => e.Seq > after).ToArray();
            var n = Math.Min(rest.Length, into.Length);
            for (var i = 0; i < n; i++) into[i] = rest[i];
            count = (uint)n;
            return rest.Length > n ? StationsNative.BufferTooSmall : 0;
        }

        public void Push(uint kind, uint station, byte before = 0, byte after = 0, ulong by = 0, int baseIndex = 0)
        {
            var seq = (ulong)Ring.Count + 1;
            Ring.Add(new StationsNative.Event
            {
                Size = (uint)sizeof(StationsNative.Event), Kind = kind, Seq = seq, Step = 100 + seq, Station = station, Workshop = 0x51,
                StateBefore = before, StateAfter = after, StoppedAfter = (byte)(after == (byte)StationState.Stopped ? 1 : 0),
                SelectedBefore = 0x1234, SelectedAfter = 0x1234, BaseIndex = baseIndex, By = by, Base = StationsNative.IdOf(AltarGuid),
            });
        }
    }

    private readonly Fake _fake = new();

    public StationsTests()
    {
        StationsCore.UseBackend(_fake);
        var b = new StationsNative.Base
        {
            Size = (uint)sizeof(StationsNative.Base), Index = 0, AltarCount = 2, StorageCount = 2, WaterCount = 1, ZoneCount = 2, StationCount = 1,
            Min = new StationsNative.Vec3 { X = -10, Y = -5, Z = -10 }, Max = new StationsNative.Vec3 { X = 10, Y = 30, Z = 10 },
        };
        b.Id = StationsNative.IdOf(AltarGuid);
        b.Altars[0] = 900;
        b.Altars[1] = 901;
        var altar2 = Guid.Parse("ffffffff-0102-0304-0506-0708090a0b0c").ToByteArray();
        var altar1 = AltarGuid.ToByteArray();
        for (var i = 0; i < 16; i++)
        {
            b.AltarIds[i] = altar1[i];
            b.AltarIds[16 + i] = altar2[i];
        }
        _fake.BaseRows.Add(b);
        _fake.Storage[0] = [700, 701];
        _fake.Water[0] = [702];
        _fake.Stock = [new StationsNative.Stack { Item = 0x10, Count = 40 }, new StationsNative.Stack { Item = 0x11, Count = 3 }];
        var s = new StationsNative.Station
        {
            Size = (uint)sizeof(StationsNative.Station), Entity = 3300, Workshop = 0x51, BaseIndex = 0, State = (byte)StationState.Running,
            Needs = 0, RunningRecipe = 0x1234, SelectedRecipe = 0x1234, CraftsLoaded = 3, DurationNs = 30_000_000_000, Progress = 0.5f,
            Position = new StationsNative.Vec3 { X = 1, Y = 2, Z = 3 }, S0 = new StationsNative.Choice { Category = 77, Index = 1 },
        };
        s.Base = b.Id;
        _fake.StationRows[3300] = s;
        var r = new StationsNative.Recipe
        {
            Size = (uint)sizeof(StationsNative.Recipe), Id = 0x1234, Workshop = 0x51, InputCount = 2, OutputCount = 1, WaterCharges = 0,
            DurationNs = 30_000_000_000,
        };
        r.Inputs[0] = 0x10;
        r.Inputs[1] = 2;
        r.Inputs[4 + 2] = 77;
        r.Inputs[4 + 3] = 1;
        r.Outputs[0] = 0x20;
        r.Outputs[1] = 1;
        _fake.Recipes[0x1234] = r;
    }

    public void Dispose()
    {
        StationsCore.UseBackend(null);
        StationOutputKeeper.Reset();
        BuildStock.ResetForTests();
        RespecStock.ResetForTests();
        StationsCore.CanAccessInventories = static p => p.PermissionsKnown ? p.Can(PlayerPermissions.AccessInventories) : null;
    }

    // The calls return a task but throw before one exists: run them as a plain call.
    private static void Call(Func<Task> f) => _ = f();

    private static KilnHarness Kiln(bool caps = true)
    {
        var k = new KilnHarness();
        if (caps)
            foreach (var c in new[] { Stations.ReadCapability, Stations.FillCapability, Stations.ControlCapability, Stations.EventsCapability,
                         Stations.OutputSourcesCapability })
                k.Capabilities.Override(c, true);
        return k;
    }

    [Fact]
    public void TheMirrorsKeepTheNativeLayouts()
    {
        // native/core/abi/services/kiln_stations.h; stations_test.cpp asserts the same sizes and offsets
        Assert.Equal(16, sizeof(StationsNative.BaseId));
        Assert.Equal(8, sizeof(StationsNative.Choice));
        Assert.Equal(432, sizeof(StationsNative.Base));
        Assert.Equal(92, (int)Marshal.OffsetOf<StationsNative.Base>("AltarIds"));
        Assert.Equal(368, (int)Marshal.OffsetOf<StationsNative.Base>("Min"));
        Assert.Equal(200, sizeof(StationsNative.Station));
        Assert.Equal(180, (int)Marshal.OffsetOf<StationsNative.Station>("Flags"));
        Assert.Equal(16, sizeof(StationsNative.Input));
        Assert.Equal(624, sizeof(StationsNative.Recipe));
        Assert.Equal(104, sizeof(StationsNative.Request));
        Assert.Equal(24, (int)Marshal.OffsetOf<StationsNative.Request>("C0"));
        Assert.Equal(64, (int)Marshal.OffsetOf<StationsNative.Request>("Keep"));
        Assert.Equal(88, (int)Marshal.OffsetOf<StationsNative.Request>("ChestCount"));
        Assert.Equal(96, (int)Marshal.OffsetOf<StationsNative.Request>("Chests"));
        Assert.Equal(72, (int)Marshal.OffsetOf<StationsNative.Request>("Item"));      // 0.2.0-dev.31.16: reserved[0] before
        Assert.Equal(76, (int)Marshal.OffsetOf<StationsNative.Request>("Reserved"));
        Assert.Equal(8 + 40 * sizeof(nint), sizeof(StationsNative.Table));          // fill_flags after set_build_stock; #150 set_respec_stock
        Assert.Equal(StationsNative.TableBuild + sizeof(nint), StationsNative.TableItemFills);
        Assert.Equal(0x200u, StationsNative.FillItem);
        Assert.Equal(32, sizeof(StationsNative.MoveChange));
        Assert.Equal(1248, sizeof(StationsNative.Result));  // GitHub #132: FromLinked, ReservedLinked appended
        Assert.Equal(1240, (int)Marshal.OffsetOf<StationsNative.Result>("FromLinked"));
        Assert.Equal(1232, (int)Marshal.OffsetOf<StationsNative.Result>("FromChests"));
        Assert.Equal(52, (int)Marshal.OffsetOf<StationsNative.Result>("LimitedBy"));
        Assert.Equal(56, (int)Marshal.OffsetOf<StationsNative.Result>("ShortItem"));
        Assert.Equal(64, (int)Marshal.OffsetOf<StationsNative.Result>("Ticket"));
        Assert.Equal(80, (int)Marshal.OffsetOf<StationsNative.Result>("Changes"));
        Assert.Equal(1104, (int)Marshal.OffsetOf<StationsNative.Result>("Message"));
        Assert.Equal(1080, sizeof(StationsNative.Plan));  // GitHub #132: Linked appended
        Assert.Equal(952, (int)Marshal.OffsetOf<StationsNative.Plan>("Linked"));
        Assert.Equal(952, StationsNative.PlanLinked);
        Assert.Equal(696, (int)Marshal.OffsetOf<StationsNative.Plan>("Outputs"));
        Assert.Equal(824, (int)Marshal.OffsetOf<StationsNative.Plan>("Chests"));
        Assert.Equal(696, StationsNative.PlanV1);
        Assert.Equal(824, StationsNative.PlanChests);
        Assert.Equal(60, (int)Marshal.OffsetOf<StationsNative.Result>("FromOutputs"));
        Assert.Equal(36, (int)Marshal.OffsetOf<StationsNative.Plan>("ShortItem"));
        Assert.Equal(44, (int)Marshal.OffsetOf<StationsNative.Plan>("WaterPerCraft"));
        Assert.Equal(56, (int)Marshal.OffsetOf<StationsNative.Plan>("Inputs"));
        Assert.Equal(112, sizeof(StationsNative.Event));
        Assert.Equal(64, (int)Marshal.OffsetOf<StationsNative.Event>("By"));
        Assert.Equal(72, (int)Marshal.OffsetOf<StationsNative.Event>("Position"));
        Assert.Equal(96, (int)Marshal.OffsetOf<StationsNative.Event>("Base"));
        Assert.Equal(188, sizeof(StationsNative.Status));
        Assert.Equal(328, sizeof(StationsNative.Table));  // GitHub #147: SetLockStock, LockAsks; #132: SetBuildStock, FillFlags; #150: SetRespecStock
        Assert.Equal(320, (int)Marshal.OffsetOf<StationsNative.Table>("SetRespecStock"));
        Assert.Equal(312, (int)Marshal.OffsetOf<StationsNative.Table>("FillFlags"));
        Assert.Equal(304, (int)Marshal.OffsetOf<StationsNative.Table>("SetBuildStock"));
        Assert.Equal(312, StationsNative.TableBuild);
        Assert.Equal(40, sizeof(StationsNative.SourceRequest));  // GitHub #147: Flags, Reserved after the 32-byte V1
        Assert.Equal(32, (int)Marshal.OffsetOf<StationsNative.SourceRequest>("Flags"));
        Assert.Equal(288, (int)Marshal.OffsetOf<StationsNative.Table>("SetLockStock"));
        Assert.Equal(296, (int)Marshal.OffsetOf<StationsNative.Table>("LockAsks"));
        Assert.Equal(288, StationsNative.TableDyes);
        Assert.Equal(304, StationsNative.TableLocks);
        Assert.Equal(248, (int)Marshal.OffsetOf<StationsNative.Table>("AddSource"));
        Assert.Equal(256, (int)Marshal.OffsetOf<StationsNative.Table>("RemoveSource"));
        Assert.Equal(264, (int)Marshal.OffsetOf<StationsNative.Table>("CancelSources"));
        Assert.Equal(272, (int)Marshal.OffsetOf<StationsNative.Table>("SourceStock"));
        Assert.Equal(280, (int)Marshal.OffsetOf<StationsNative.Table>("SetDyeStock"));
        Assert.Equal(8 + 24 * 8, StationsNative.TableChests);
        Assert.Equal(8 + 25 * 8, StationsNative.TableCollect);
        Assert.Equal(8 + 26 * 8, StationsNative.TableStock);
        Assert.Equal(8 + 28 * 8, StationsNative.TableCraftStock);
        Assert.Equal(8 + 30 * 8, StationsNative.TableFillUi);
        Assert.Equal(40, sizeof(StationsNative.FillKey));
        Assert.Equal(8, (int)Marshal.OffsetOf<StationsNative.FillKey>("C0"));
        Assert.Equal(48, sizeof(StationsNative.FillAnswer));
        Assert.Equal(40, (int)Marshal.OffsetOf<StationsNative.FillAnswer>("Crafts"));
        Assert.Equal(24, sizeof(StationsNative.Collect));
        Assert.Equal(8 + 21 * 8, StationsNative.TableV1);
        Assert.Equal(8 + 23 * 8, StationsNative.TableOutputs);
        Assert.Equal(1024, StationsNative.MaxCraftStock);
    }

    [Fact]
    public void TheEnumsKeepTheNativeValues()
    {
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Enum.GetValues<StationState>().Select(v => (int)v));
        Assert.Equal(12, (int)StationActionStatus.Dropped);
        Assert.Equal(9, (int)StationActionStatus.Locked);
        Assert.Equal((13, 14, 15), ((int)StationActionStatus.TimedOut, (int)StationActionStatus.QueueFull, (int)StationActionStatus.Canceled));
        Assert.Equal(4, (int)FillLimit.Water);
        Assert.Equal(FillLimit.None, StationsCore.LimitOf(77));
        Assert.Equal(StationActionStatus.Refused, StationsCore.StatusOf(99));
        Assert.Equal(StopWhen.Now, default(StopWhen));
    }

    [Fact]
    public void TheBaseIdKeepsTheGuidBytes()
    {
        var id = StationsNative.IdOf(AltarGuid);
        Assert.Equal(AltarGuid, StationsNative.GuidOf(id));
        Assert.True(BaseId.None.IsNone);
        Assert.Equal("none", BaseId.None.ToString());
    }

    [Fact]
    public void BasesAreReadWithTheirAltarsChestsWaterAndStock()
    {
        using var kiln = Kiln();
        var all = kiln.Run(() => Bases.All);
        var b = Assert.Single(all);
        Assert.Equal(new BaseId(AltarGuid), b.Id);
        Assert.Equal([new EntityId(900), new EntityId(901)], b.Altars.Select(a => a.Entity));
        Assert.Equal(new BaseId(AltarGuid), b.Altars[0].Id);  // each altar's own id: the stable key
        Assert.Equal(Guid.Parse("ffffffff-0102-0304-0506-0708090a0b0c"), b.Altars[1].Id.Value);
        Assert.True(kiln.Run(() => Bases.TryGet(b.Id, out var again) && again.Index == 0));
        Assert.False(kiln.Run(() => Bases.TryGet(new BaseId(Guid.NewGuid()), out _)));
        Assert.Equal((2, 1, 2, 1), (b.StorageCount, b.WaterCount, b.ZoneCount, b.StationCount));
        Assert.True(b.InBox(new WorldPosition(0, 0, 0)));
        Assert.False(b.InBox(new WorldPosition(20, 0, 0)));
        Assert.Equal([new EntityId(700), new EntityId(701)], kiln.Run(() => Bases.Storage(b.Id)));
        Assert.Equal([new EntityId(702)], kiln.Run(() => Bases.WaterSources(b.Id)));
        var stock = kiln.Run(() => Bases.Stock(b.Id));
        Assert.Equal(40, stock[new ItemId(0x10)]);
        Assert.Equal(3, kiln.Run(() => Bases.Stock(b.Id, new ItemId(0x11))));
        Assert.Equal(0, kiln.Run(() => Bases.Stock(b.Id, new ItemId(0x99))));
        Assert.Equal(b.Id, kiln.Run(() => Bases.Of(new EntityId(3300)))!.Id);
        Assert.Equal(b.Id, kiln.Run(() => Bases.At(new WorldPosition(1, 0, 1)))!.Id);
        Assert.Null(kiln.Run(() => Bases.At(new WorldPosition(100, 0, 1))));
        Assert.Null(kiln.Run(() => Bases.Get(new BaseId(Guid.NewGuid()))));
        Assert.Empty(kiln.Run(() => Bases.Storage(new BaseId(Guid.NewGuid()))));  // since 0.2.0-dev.29.15: no base, nothing in it
    }

    [Fact]
    public void BasesChestsAreItsStorageAndThePlainContainersTheGamePutsInIt()
    {
        // GitHub issue #138 (0.2.0-dev.31): the base's Magic chests (its crafting stock) and its ordinary chests (containers
        // of no proven kind the game's lookup puts in the base); never a station, a player, another base's storage
        using var kiln = Kiln();
        EntityId Container(double x, InventoryOwnerKind kind, int? baseIndex)
        {
            var e = kiln.Entities.Create(new WorldPosition(x, 0, 1));
            kiln.Items.AddContainer(e).Kind = kind;
            if (baseIndex is { } i) _fake.EntityBase[e.Value] = i;
            return e;
        }
        var magic = Container(1, InventoryOwnerKind.BaseStorage, null);
        var chest = Container(2, InventoryOwnerKind.Unknown, 0);
        var away = Container(50, InventoryOwnerKind.Unknown, -1);              // in no base
        var forge = Container(3, InventoryOwnerKind.ProductionStation, 0);     // a station in the base: not a chest
        var otherMagic = Container(4, InventoryOwnerKind.BaseStorage, 0);      // storage the game lists for no base here
        var player = kiln.Players.Join("Ada", 76561198000000001, position: new WorldPosition(1, 0, 1));
        kiln.Step();
        _fake.EntityBase[player.Entity.Value] = 0;
        _fake.Storage[0] = [magic.Value];
        var id = new BaseId(AltarGuid);
        var chests = kiln.Run(() => Bases.Chests(id));
        Assert.Equal(new[] { magic, chest }.OrderBy(e => e.Value), chests.OrderBy(e => e.Value));
        Assert.DoesNotContain(away, chests);
        Assert.DoesNotContain(forge, chests);
        Assert.DoesNotContain(otherMagic, chests);
        Assert.DoesNotContain(player.Entity, chests);
        Assert.Empty(kiln.Run(() => Bases.Chests(new BaseId(Guid.NewGuid()))));  // no base has this id: empty
        // Bases.Storage keeps its contract: the crafting stock only
        Assert.Equal([magic], kiln.Run(() => Bases.Storage(id)));
        Assert.Throws<InvalidOperationException>(() => Bases.Chests(id));  // the tick thread only
    }

    [Fact]
    public void ABaseRebuiltBetweenTwoReadsIsEmptyNotAnException()
    {
        // The game rebuilds its bases every server frame: an id read from Bases.All, or a record index found a moment ago, can be
        // gone by the next read (an altar placed or removed). Consumers polled Bases.Stock and Stations.InBase and got
        // KeyNotFoundException (Busy Hands, 0.2.0-dev.29.14).
        using var kiln = Kiln();
        var id = new BaseId(AltarGuid);
        _fake.Rebuilt = true;  // found by id, then its record is gone
        Assert.Empty(kiln.Run(() => Bases.Stock(id)));
        Assert.Equal(0, kiln.Run(() => Bases.Stock(id, new ItemId(0x10))));
        Assert.Empty(kiln.Run(() => Bases.Storage(id)));
        Assert.Empty(kiln.Run(() => Bases.Storage(id, deposit: true)));
        Assert.Empty(kiln.Run(() => Bases.WaterSources(id)));
        Assert.Empty(kiln.Run(() => Stations.InBase(id)));
        Assert.Null(kiln.Run(() => Bases.FindRoomFor(id, new ItemId(0x10))));
        // an id no base has any more (its altar's zone merged into another base)
        _fake.Rebuilt = false;
        var gone = new BaseId(Guid.NewGuid());
        Assert.Empty(kiln.Run(() => Bases.Stock(gone)));
        Assert.Empty(kiln.Run(() => Stations.InBase(gone)));
        Assert.Empty(kiln.Run(() => Bases.WaterSources(gone)));
        Assert.Null(kiln.Run(() => Bases.FindRoomFor(gone, new ItemId(0x10))));
        // the base still there: answered as before
        Assert.Equal(40, kiln.Run(() => Bases.Stock(id, new ItemId(0x10))));
        Assert.Equal([new EntityId(3300)], kiln.Run(() => Stations.InBase(id)));
    }

    [Fact]
    public void AStationIsReadWithItsRecipeAndChoices()
    {
        using var kiln = Kiln();
        var s = kiln.Run(() => Stations.Read(new EntityId(3300)))!;
        Assert.Equal((0x51u, StationState.Running, 3, 0x1234u), (s.Workshop, s.State, s.CraftsLoaded, s.SelectedRecipe));
        Assert.Equal(new BaseId(AltarGuid), s.Base);
        Assert.Equal(TimeSpan.FromSeconds(30), s.Duration);
        Assert.Equal([new CategoryChoice(77, 1)], s.SelectedChoices);
        Assert.Null(s.Water);
        Assert.True(s.IsRunning);
        Assert.False(s.StopPending);
        var pending = _fake.StationRows[3300];
        pending.Flags = StationsNative.FlagStopPending;
        _fake.StationRows[3300] = pending;
        Assert.True(kiln.Run(() => Stations.Read(new EntityId(3300)))!.StopPending);
        Assert.Null(kiln.Run(() => Stations.Read(new EntityId(5))));
        Assert.True(kiln.Run(() => Stations.IsStation(new EntityId(3300))));
        Assert.False(kiln.Run(() => Stations.IsStation(new EntityId(5))));
        Assert.Equal([new EntityId(3300)], kiln.Run(() => Stations.All()));
        Assert.Equal([new EntityId(3300)], kiln.Run(() => Stations.InBase(new BaseId(AltarGuid))));
        Assert.Equal([0x1234u], kiln.Run(() => Stations.RecipesOf(new EntityId(3300))));

        var r = kiln.Run(() => Stations.Recipe(0x1234))!;
        Assert.Equal(2, r.Inputs.Count);
        Assert.False(r.Inputs[0].IsCategory);
        Assert.True(r.Inputs[1].IsCategory);
        Assert.Equal(new StationStack(new ItemId(0x20), 1), Assert.Single(r.Outputs));
        Assert.True(r.AlwaysUnlocked);
        Assert.Null(kiln.Run(() => Stations.Recipe(0)));
        Assert.Null(kiln.Run(() => Stations.Recipe(0x9999)));

        var cat = Assert.Single(kiln.Run(() => Stations.Categories));
        Assert.Equal(77u, cat.Id);
        Assert.Equal(new IngredientOption(new ItemId(0x7001), 0.5f), cat.Options[1]);
        var inputs = kiln.Run(() => Stations.ResolveInputs(0x1234, [new CategoryChoice(77, 1)]));
        Assert.Equal([new StationStack(new ItemId(0x10), 2), new StationStack(new ItemId(0x7001), 1)], inputs);
        Assert.Throws<KeyNotFoundException>(() => kiln.Run(() => Stations.ResolveInputs(0x9999)));
    }

    [Fact]
    public void StockWithStationsCountsFinishedItemsStillInTheBasesStations()
    {
        using var k = Kiln();
        var charcoal = k.Items.AddItem("Material_T1_Charcoal", 50);
        var logs = k.Items.AddItem("Material_T1_Wood_Logs", 50);
        var r = _fake.Recipes[0x1234];
        r.Outputs[0] = charcoal.Value;                                    // the kiln's recipe makes charcoal from logs (0x10)...
        _fake.Recipes[0x1234] = r;
        var kilnEntity = k.Entities.Create(new WorldPosition(1, 2, 3));     // the fake world's entity stands for the station
        var row = _fake.StationRows[3300];
        _fake.StationRows.Remove(3300);
        row.Entity = kilnEntity.Value;
        _fake.StationRows[kilnEntity.Value] = row;
        var station = k.Items.AddContainer(kilnEntity);
        station.Put(charcoal, 7);                                         // finished, not collected
        station.Put(logs, 4);                                             // a leftover: not a product
        _fake.Stock = [new StationsNative.Stack { Item = charcoal.Value, Count = 40 }];
        var id = new BaseId(AltarGuid);
        Assert.Equal(40, k.Run(() => Bases.Stock(id))[charcoal]);         // the chests only, as before
        var all = k.Run(() => Bases.StockWithStations(id));
        Assert.Equal(47, all[charcoal]);
        Assert.False(all.ContainsKey(logs));
        Assert.Empty(k.Run(() => Bases.StockWithStations(new BaseId(Guid.NewGuid()))));
    }

    [Fact]
    public void StationsAndRecipesHaveNamesForPlayers()
    {
        // the game's data as Kiln.Items reads it: the kiln's workshop placed by Prop_Production_T1_Charcoal_Kiln, a recipe with no
        // name of its own whose output is charcoal, and the game's German texts
        var kiln = new ItemId(0x15e4e52e);
        GameNames.UseForTests(GameNameLayout.Measured, new() { [kiln.Value] = Guid.NewGuid() }, new() { [0x51] = (5u, kiln.Value), [0x52] = (6u, 0u) },
            new() { [0x1234] = (0u, 0x162cb6d2u), [0x99] = (7u, 0u) }, "de-de", new() { [6] = "Werkbank", [7] = "Sonderrezept", [8] = "Köhlerofen" },
            itemTags: new() { [kiln.Value] = 8 });
        try
        {
            using var k = Kiln();
            var r = _fake.Recipes[0x1234];
            r.Outputs[0] = 0x162cb6d2;                                      // Material_T1_Charcoal
            _fake.Recipes[0x1234] = r;
            Assert.Equal("Köhlerofen", k.Run(() => Stations.Read(new EntityId(3300)))!.DisplayName);  // the placing item, in German
            Assert.Equal("Charcoal", k.Run(() => Stations.Recipe(0x1234))!.DisplayName);               // the first output (no German text here)
            // any side, any thread: from the game's data alone (a joined client names the ids its server half sends)
            Assert.Equal("Köhlerofen", Stations.WorkshopName(0x51));
            Assert.Equal("Werkbank", Stations.WorkshopName(0x52));         // no item: the workshop's own text
            Assert.Equal("Charcoal", Stations.RecipeName(0x1234));
            Assert.Equal("Sonderrezept", Stations.RecipeName(0x99));       // no output: the recipe's own text
            Assert.Null(Stations.WorkshopName(0x77));                       // not in the game's data (a mod's own workshop)
            Assert.Null(Stations.RecipeName(0));
        }
        finally
        {
            GameNames.ResetForTests();
        }
    }

    [Fact]
    public void SlotsAreOutputsLeftoversOrInputs()
    {
        var recipe = new StationRecipe(1, 0x51, [new StationInput(new ItemId(0x10), 2, 0, 0), new StationInput(default, 0, 77, 1)],
            [new StationStack(new ItemId(0x20), 1), new StationStack(new ItemId(0x10), 1), new StationStack(new ItemId(0x7001), 1)],
            TimeSpan.FromSeconds(1), 0, true);
        var holder = new EntityId(3300);
        InventorySlot Slot(int i, uint item, int count) => new(SlotId.Of(holder, i), new ItemId(item), count, default, 1, ItemRarity.Common, default, 0, true, false, false, 0);
        // 0x10 input, 0x20 output, 0x7001 an option of category 77 (an input, though also an output), 0x55 an old recipe's
        var slots = new[] { Slot(0, 0x10, 4), Slot(1, 0x20, 5), Slot(2, 0, 0), Slot(3, 0x7001, 2), Slot(4, 0x55, 9) };
        IEnumerable<ItemId> Options(uint c) => c == 77 ? [new ItemId(0x7000), new ItemId(0x7001)] : [];
        var (outs, left) = StationsCore.SlotRoles(slots, recipe, null, Options);
        Assert.Equal(new ItemId(0x20), Assert.Single(outs).Item);
        Assert.Equal(new ItemId(0x55), Assert.Single(left).Item);
        var (none, noLeft) = StationsCore.SlotRoles(slots, null, null, Options);
        Assert.Empty(none);
        Assert.Empty(noLeft);  // no recipe: nothing is called a leftover
    }

    [Fact]
    public void BaseChangesPairOldAndNewIdsByTheirAltars()
    {
        Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid();
        BaseId Id(Guid g) => new(g);
        var before = new List<(BaseId, HashSet<Guid>)> { (Id(A), [A]), (Id(C), [C, D]) };
        // a new altar B overlaps A's zone (the merged base takes the smaller id: here B), D is gone, a new base E
        Guid E = Guid.NewGuid();
        var after = new List<(BaseId, HashSet<Guid>)> { (Id(B), [A, B]), (Id(C), [C]), (Id(E), [E]) };
        var (added, removed, changed, kept) = StationsCore.DiffBases(before, after);
        Assert.Equal([Id(E)], added);
        Assert.Empty(removed);
        Assert.Equal([new BaseIdChange(Id(A), Id(B))], changed);
        Assert.Equal([Id(C)], kept);
        var (_, gone, _, _) = StationsCore.DiffBases(before, [(Id(A), [A])]);
        Assert.Equal([Id(C)], gone);
        // a split keeps the old id for one part: Kept tells it from a rename
        Guid X = Guid.NewGuid();
        var (_, _, splitChanged, splitKept) = StationsCore.DiffBases([(Id(A), [A, X])], [(Id(A), [A]), (Id(X), [X])]);
        Assert.Equal([new BaseIdChange(Id(A), Id(X))], splitChanged);
        Assert.Equal([Id(A)], splitKept);
        var (_, _, renamed, renameKept) = StationsCore.DiffBases([(Id(A), [A])], [(Id(B), [A, B])]);
        Assert.Equal([new BaseIdChange(Id(A), Id(B))], renamed);
        Assert.Empty(renameKept);
        // a base whose altars were not found (id none) is in no list
        var (noneAdded, noneRemoved, _, _) = StationsCore.DiffBases([(BaseId.None, [])], [(BaseId.None, []), (Id(E), [E])]);
        Assert.Equal([Id(E)], noneAdded);
        Assert.Empty(noneRemoved);
    }

    [Fact]
    public void FillOptionsBecomeTheRequest()
    {
        var (r, keep, chests) = StationsCore.FillRequest(new EntityId(3300), new FillOptions
        {
            Crafts = 10, Mode = FillMode.Add, Exact = true, DryRun = true, Water = false, Recipe = 0x1234,
            Choices = [new CategoryChoice(77, 1)], Player = new EntityId(1),
            Keep = new Dictionary<ItemId, long> { [new ItemId(0x10)] = 20, [new ItemId(0x11)] = 0 },
        });
        Assert.Equal(StationsNative.Fill, r.Action);
        Assert.Equal(StationsNative.FillAdd | StationsNative.FillExact | StationsNative.DryRun | StationsNative.NoWater, r.Flags);
        Assert.Equal((3300u, 10u, 0x1234u, 1u), (r.Station, r.Crafts, r.RecipeId, r.Player));
        Assert.Equal((77u, (byte)1), (r.C0.Category, r.C0.Index));
        Assert.Equal(0u, r.C1.Category);
        var k = Assert.Single(keep);  // a reserve of 0 is no reserve
        Assert.Equal((0x10u, 20u), (k.Item, k.Count));
        Assert.Equal(1u, r.KeepCount);
        Assert.Empty(chests);
        Assert.Equal(0u, r.ChestCount);

        var (d, _, _) = StationsCore.FillRequest(new EntityId(3300), new FillOptions());
        Assert.Equal((0u, 1u, 0u, 0u), (d.Flags, d.Crafts, d.RecipeId, d.Player));  // up to 1 craft, water on, no player's bag

        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions { Crafts = 0 }));
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions { Crafts = 1001 }));
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(EntityId.None, new FillOptions()));
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions { Choices = [new CategoryChoice(77, 0)] }));
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions
        {
            Keep = Enumerable.Range(1, 65).ToDictionary(i => new ItemId((uint)i), _ => 1L),
        }));
        Assert.Throws<ArgumentException>(() => StationsCore.ChoicesOf([new(1, 0), new(2, 0), new(3, 0), new(4, 0), new(5, 0)]));
        Assert.Throws<ArgumentException>(() => StationsCore.ChoicesOf([new(1, 256)]));
    }

    [Fact]
    public void APlanIsReadFromTheNativeRecord()
    {
        using var kiln = Kiln();
        var p = new StationsNative.Plan
        {
            Size = (uint)sizeof(StationsNative.Plan), Status = (int)StationActionStatus.Partial, Station = 3300, RecipeId = 0x1234, CraftsLoaded = 3,
            CraftsAffordable = 4, CraftsPlanned = 4, InputCount = 1,
        };
        p.Inputs[0] = 0x10;
        p.Inputs[1] = 2;
        p.Have[0] = 6;
        p.Stock[0] = 8;
        p.WaterPerCraft = 64;
        p.WaterHave = 64;
        p.WaterStock = 640;
        p.LimitedBy = 1;
        p.ShortItem = 0x10;
        p.UseAllOptions = 1;
        _fake.NextPlan = p;
        var plan = kiln.Run(() => Stations.Plan(new EntityId(3300), new FillOptions { Crafts = 10 }));
        Assert.Equal(10u, _fake.LastPlanned.Crafts);
        Assert.True(plan.Ok);
        Assert.Null(plan.Reason);
        Assert.Equal((3, 4, 4), (plan.CraftsLoaded, plan.CraftsAffordable, plan.CraftsPlanned));
        Assert.Equal(new FillInput(new ItemId(0x10), 2, 6, 8), Assert.Single(plan.Inputs));
        Assert.Equal((64, 64, 640, true), (plan.WaterPerCraft, plan.WaterHave, plan.WaterStock, plan.UseAllOptions));
        Assert.Equal((FillLimit.Stock, (ItemId?)new ItemId(0x10)), (plan.LimitedBy, plan.ShortItem));
        var none = p;
        none.Status = (int)StationActionStatus.NotEnough;
        var why = System.Text.Encoding.UTF8.GetBytes("2 of 9 x 0x00000011 for one craft");
        for (var i = 0; i < why.Length; i++) none.Message[i] = why[i];
        _fake.NextPlan = none;
        var refused = kiln.Run(() => Stations.Plan(new EntityId(3300)));
        Assert.False(refused.Ok);
        Assert.Equal("2 of 9 x 0x00000011 for one craft", refused.Reason);
    }

    [Fact]
    public void StopResumeAndSelectSendTheButtonsRequests()
    {
        using var kiln = Kiln();
        var station = new EntityId(3300);
        Call(() => kiln.Run(() => Stations.Stop(station)));
        Call(() => kiln.Run(() => Stations.Stop(station, StopWhen.AfterCurrentCraft)));
        Call(() => kiln.Run(() => Stations.Resume(station)));
        Call(() => kiln.Run(() => Stations.Select(station, 0x1234, [new CategoryChoice(77, 1)], keepStopped: true)));
        Call(() => kiln.Run(() => Stations.Select(station, 0x5678, allowLocked: true)));
        var r = _fake.Submitted.Select(x => x.Request).ToArray();
        Assert.Equal(5, r.Length);
        Assert.Equal((StationsNative.Stop, 0u), (r[0].Action, r[0].Flags));  // the button's meaning: now
        Assert.Equal((StationsNative.Stop, StationsNative.AfterCraft), (r[1].Action, r[1].Flags));
        Assert.Equal((StationsNative.Resume, 0u), (r[2].Action, r[2].Flags));
        Assert.Equal((StationsNative.Select, StationsNative.KeepStopped, 0x1234u), (r[3].Action, r[3].Flags, r[3].RecipeId));
        Assert.Equal((77u, (byte)1), (r[3].C0.Category, r[3].C0.Index));
        Assert.Equal((StationsNative.Select, StationsNative.AllowLocked, 0x5678u), (r[4].Action, r[4].Flags, r[4].RecipeId));
        Assert.All(r, x => Assert.Equal(3300u, x.Station));
        Assert.Equal(_fake.Submitted.Count, _fake.Submitted.Select(x => x.Key).Distinct().Count());
    }

    [Fact]
    public void TheCallsCheckTheirArgumentsTheThreadAndTheCapability()
    {
        using (var off = Kiln(caps: false))
        {
            var e = off.Run(() => Assert.Throws<KilnUnavailableException>(() => Call(() => Stations.Fill(new EntityId(3300)))));
            Assert.Equal("stations.fill", e.Capability);
            e = off.Run(() => Assert.Throws<KilnUnavailableException>(() => Call(() => Stations.Stop(new EntityId(3300)))));
            Assert.Equal("stations.control", e.Capability);
            e = off.Run(() => Assert.Throws<KilnUnavailableException>(() => _ = Bases.All));
            Assert.Equal("stations.read", e.Capability);
        }
        using var kiln = Kiln();
        kiln.Run(() => Assert.Throws<ArgumentException>(() => Call(() => Stations.Fill(EntityId.None))));
        kiln.Run(() => Assert.Throws<ArgumentException>(() => Call(() => Stations.Stop(EntityId.None))));
        kiln.Run(() => Assert.Throws<ArgumentException>(() => Call(() => Stations.Select(new EntityId(3300), 1, [new CategoryChoice(1, 300)]))));
        Assert.Throws<InvalidOperationException>(() => Call(() => Stations.Resume(new EntityId(3300))));  // not the tick thread
        Assert.Throws<InvalidOperationException>(() => _ = Bases.All);
        // kiln.stations says why it is off
        _fake.State = (true, false, false, true, "the program kiln_items_stations was refused");
        var why = kiln.Run(() => Assert.Throws<KilnUnavailableException>(() => Call(() => Stations.Fill(new EntityId(3300)))));
        Assert.Contains("refused", why.Message);
        _fake.State = null;
        Assert.False(Stations.Available);
        Assert.False(Bases.Available);
        Assert.True(Stations.Scanned);
        kiln.Run(() => Assert.Throws<KilnUnavailableException>(() => _ = Bases.All));
    }

    [Fact]
    public void TheWatchFollowsTheSubscriptionsAndRecordsBecomeEvents()
    {
        using var kiln = Kiln();
        _fake.Push(StationsNative.EvState, 3300, 4, 3);  // before the watch: not this handler's
        var states = new List<StationStateChangedEvent>();
        var placed = new List<StationPlacedEvent>();
        var removed = new List<StationRemovedEvent>();
        var bases = new List<BasesChangedEvent>();
        IDisposable? sub = null;
        kiln.Run(() =>
        {
            sub = Events.On<StationStateChangedEvent>(states.Add);
            Events.On<StationPlacedEvent>(placed.Add);
            Events.On<StationRemovedEvent>(removed.Add);
            Events.On<BasesChangedEvent>(bases.Add);
        });
        kiln.Run(StationsCore.PumpOnce);
        Assert.Equal([true], _fake.Watches);
        Assert.Empty(states);
        _fake.Push(StationsNative.EvState, 3300, (byte)StationState.Running, (byte)StationState.Stopped);
        _fake.Push(StationsNative.EvPlaced, 3301, baseIndex: 0);
        _fake.Push(StationsNative.EvRemoved, 3302, baseIndex: -1);
        _fake.Push(StationsNative.EvBases, 0, baseIndex: 2);
        kiln.Run(StationsCore.PumpOnce);
        var s = Assert.Single(states);
        Assert.Equal((StationState.Running, StationState.Stopped, true), (s.Before, s.After, s.Stopped));
        Assert.Equal(new BaseId(AltarGuid), s.Base);
        Assert.Null(s.ByMod);
        Assert.True(s.Involves(new EntityId(3300)));
        Assert.Equal(new EntityId(3301), Assert.Single(placed).Station);
        Assert.Equal(-1, Assert.Single(removed).BaseIndex);
        var changedBases = Assert.Single(bases);
        Assert.Equal(2, changedBases.Count);
        Assert.Empty(changedBases.Added);  // the same base as at the watch's start
        Assert.Equal(new BaseId(AltarGuid), Assert.Single(placed).Base);
        kiln.Run(StationsCore.PumpOnce);  // each record once
        Assert.Single(states);
    }

    [Fact]
    public void AWatchFromBeforeTheWorldTakesTheFirstRebuildAsItsBaseline()
    {
        using var kiln = Kiln();
        _fake.NoWorld = true;  // subscribed in OnLoad: no bases to read yet
        var bases = new List<BasesChangedEvent>();
        kiln.Run(() => Events.On<BasesChangedEvent>(bases.Add));
        kiln.Run(StationsCore.PumpOnce);
        _fake.NoWorld = false;  // the world runs; its first rebuild
        _fake.Push(StationsNative.EvBases, 0, baseIndex: 1);
        kiln.Run(StationsCore.PumpOnce);
        Assert.Empty(bases.SelectMany(b => b.Added));  // not every base "added"
        _fake.Push(StationsNative.EvBases, 0, baseIndex: 1);
        kiln.Run(StationsCore.PumpOnce);
        Assert.Empty(bases.SelectMany(b => b.Added));
    }

    [Fact]
    public void APlayersPressNamesOnlyTheChangeItLeadsTo()
    {
        using var kiln = Kiln();
        var states = new List<StationStateChangedEvent>();
        kiln.Run(() => Events.On<StationStateChangedEvent>(states.Add));
        kiln.Run(StationsCore.PumpOnce);
        // "use all options" pressed, then an unrelated stop flag change: not the player's
        kiln.Run(() => StationsCore.Press(new EntityId(3300), new EntityId(1), StationPlayerAction.UseAllOptions));
        _fake.Push(StationsNative.EvState, 3300, (byte)StationState.Running, (byte)StationState.Stopped);
        kiln.Run(StationsCore.PumpOnce);
        Assert.Null(Assert.Single(states).Player);
        // a stop pressed: the stop flag going on is the player's
        states.Clear();
        kiln.Run(() => StationsCore.Press(new EntityId(3300), new EntityId(1), StationPlayerAction.Stop));
        _fake.Push(StationsNative.EvState, 3300, (byte)StationState.Running, (byte)StationState.Stopped);
        kiln.Run(StationsCore.PumpOnce);
        Assert.Equal(new EntityId(1), Assert.Single(states).Player);
        // a select pressed, then a state change with no selection change and no resume: not the player's
        states.Clear();
        kiln.Run(() => StationsCore.Press(new EntityId(3300), new EntityId(2), StationPlayerAction.Select));
        _fake.Push(StationsNative.EvState, 3300, (byte)StationState.Running, (byte)StationState.MissingMaterials);
        kiln.Run(StationsCore.PumpOnce);
        Assert.Null(Assert.Single(states).Player);
    }

    [Fact]
    public void ManyRecordsAreDrainedInOnePump()
    {
        using var kiln = Kiln();
        var n = 0;
        kiln.Run(() => Events.On<StationStateChangedEvent>(_ => n++));
        kiln.Run(StationsCore.PumpOnce);
        for (var i = 0; i < 100; i++) _fake.Push(StationsNative.EvState, 3300, 4, 1);
        kiln.Run(StationsCore.PumpOnce);
        Assert.Equal(100, n);
    }

    [Fact]
    public void ASubscriptionIsRefusedByNameWhileTheEventsAreOff()
    {
        using var kiln = Kiln();
        _fake.State = (true, true, true, false, "kiln.world is too old for the watch");
        var off = kiln.Run(() => Assert.Throws<KilnUnavailableException>(() => Events.On<StationPlacedEvent>(_ => { })));
        Assert.Equal("stations.events", off.Capability);
        Assert.Contains("too old", off.Message);
    }

    [Fact]
    public void OnlyAStationsOwnSuccessfulCycleIsAStationCraft()
    {
        var station = new EntityId(3300);
        var ev = StationsCore.CraftedOf(station, station, true, 0x1234, 0x51, [(0x20u, 2)], new BaseId(AltarGuid));
        Assert.NotNull(ev);
        Assert.Equal(new BaseId(AltarGuid), ev!.Base);
        Assert.Equal(new StationStack(new ItemId(0x20), 2), Assert.Single(ev!.Outputs));
        Assert.Null(StationsCore.CraftedOf(station, station, false, 0x1234, 0x51, []));  // a failed attempt
        Assert.Null(StationsCore.CraftedOf(new EntityId(1), station, true, 0x1234, 0x51, []));  // a player's craft at a bench
        Assert.Null(StationsCore.CraftedOf(EntityId.None, EntityId.None, true, 0x1234, 0x51, []));
    }

    // ---- station outputs as crafting stock (GitHub #68) ----

    private static byte[] FillPress(uint station = 3300, uint recipe = 0x1234, ushort amount = 3)
    {
        var p = new byte[68];
        BitConverter.TryWriteBytes(p.AsSpan(0), station);
        BitConverter.TryWriteBytes(p.AsSpan(4), recipe);
        BitConverter.TryWriteBytes(p.AsSpan(0x0c), amount);
        BitConverter.TryWriteBytes(p.AsSpan(0x18), 77u);
        p[0x18 + 4] = 1;
        p[0x40] = (byte)StationPlayerAction.Fill;
        return p;
    }

    // the station screen's per-ingredient "+1" / "+n": AutomaticTransfer with an item and no recipe (docs/re/stations.md §10.9)
    private static byte[] ItemPress(uint station = 3300, uint item = 0x10, ushort amount = 7)
    {
        var p = new byte[68];
        BitConverter.TryWriteBytes(p.AsSpan(0), station);
        BitConverter.TryWriteBytes(p.AsSpan(8), item);
        BitConverter.TryWriteBytes(p.AsSpan(0x0c), amount);
        p[0x40] = (byte)StationPlayerAction.Fill;
        return p;
    }

    private void AddStation(uint entity, int baseIndex, double x = 2)
    {
        var s = _fake.StationRows[3300];
        s.Entity = entity;
        s.BaseIndex = baseIndex;
        s.Position = new StationsNative.Vec3 { X = x, Y = 0, Z = 0 };
        if (baseIndex < 0) s.Base = default;
        _fake.StationRows[entity] = s;
    }

    private static StationsNative.Plan PlanWith(StationActionStatus status, uint outputs = 0)
    {
        var p = new StationsNative.Plan { Size = (uint)sizeof(StationsNative.Plan), Status = (int)status, Station = 3300, RecipeId = 0x1234, InputCount = 1 };
        p.Inputs[0] = 0x10;
        p.Inputs[1] = 2;
        p.Stock[0] = 4;
        p.Outputs[0] = outputs;
        return p;
    }

    [Fact]
    public void AFillUsesStationOutputsUnlessTheOptionsSayNo()
    {
        var (r, _, _) = StationsCore.FillRequest(new EntityId(3300), new FillOptions());
        Assert.Equal(0u, r.Flags & StationsNative.FillNoOutputs);
        var (n, _, _) = StationsCore.FillRequest(new EntityId(3300), new FillOptions { UseStationOutputs = false });
        Assert.Equal(StationsNative.FillNoOutputs, n.Flags & StationsNative.FillNoOutputs);
        // an older kiln.stations knows no such flag (it would refuse the request): never sent there
        _fake.HasOutputs = false;
        var (o, _, _) = StationsCore.FillRequest(new EntityId(3300), new FillOptions { UseStationOutputs = false });
        Assert.Equal(0u, o.Flags & StationsNative.FillNoOutputs);
    }

    [Fact]
    public void APlanAndAResultSayWhatTheOutputsLend()
    {
        using var kiln = Kiln();
        _fake.NextPlan = PlanWith(StationActionStatus.Done, outputs: 20);
        var plan = kiln.Run(() => Stations.Plan(new EntityId(3300)));
        var input = Assert.Single(plan.Inputs);
        Assert.Equal((4L, 20L), (input.Stock, input.Outputs));
        var r = NewResult(StationActionStatus.Done, "");
        r.FromOutputs = 5;
        Assert.Equal(5, StationsCore.ResultOf(r, StationAction.Fill).FromOutputs);
    }

    [Fact]
    public void AFillCanNameOrdinaryChests()
    {
        var chest = new EntityId(9000);
        var (r, _, ids) = StationsCore.FillRequest(new EntityId(3300), new FillOptions { Chests = [chest, new EntityId(9001)] });
        Assert.Equal(2u, r.ChestCount);
        Assert.Equal([9000u, 9001u], ids);
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions { Chests = [new EntityId(1)] }));
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions { Chests = [new EntityId(3300)] }));
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions
        {
            Chests = Enumerable.Range(2000, StationsNative.MaxChests + 1).Select(i => new EntityId((uint)i)).ToArray(),
        }));
        _fake.HasChests = false;
        Assert.Throws<InvalidOperationException>(() => StationsCore.FillRequest(new EntityId(3300), new FillOptions { Chests = [chest] }));
        _fake.HasChests = true;

        using var kiln = Kiln();
        var planned = PlanWith(StationActionStatus.Done);
        planned.Chests[0] = 7;
        _fake.NextPlan = planned;
        var plan = kiln.Run(() => Stations.Plan(new EntityId(3300), new FillOptions { Chests = [chest] }));
        Assert.Equal([9000u], _fake.LastChests);
        var input = Assert.Single(plan.Inputs);
        Assert.Equal((4L, 7L, 0L), (input.Stock, input.Chests, input.Outputs));
        var done = NewResult(StationActionStatus.Done, "");
        done.FromChests = 4;
        Assert.Equal(4, StationsCore.ResultOf(done, StationAction.Fill).FromChests);
    }

    [Fact]
    public void CraftStockKeyChangesWhenACountChanges()
    {
        var a = new CraftStockRows([new CraftStockRow(0x10, 2), new CraftStockRow(0x20, 4)]);
        var b = new CraftStockRows([new CraftStockRow(0x10, 2), new CraftStockRow(0x20, 5)]);
        Assert.NotEqual(CraftStock.Key(a), CraftStock.Key(b));
        Assert.Equal(CraftStock.Key(a), CraftStock.Key(new CraftStockRows([new CraftStockRow(0x10, 2), new CraftStockRow(0x20, 4)])));
    }

    [Fact]
    public void AStationIsOptedInAndOutAsCraftingStock()
    {
        using var kiln = Kiln();
        AddStation(3301, 0);
        Assert.False(kiln.Run(() => Stations.IsOutputSource(new EntityId(3301))));
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3301)));
        Assert.Equal([3301u], _fake.Sources);
        Assert.True(kiln.Run(() => Stations.IsOutputSource(new EntityId(3301))));
        Assert.Equal([new EntityId(3301)], kiln.Run(() => Stations.OutputSources(new BaseId(AltarGuid))));
        Assert.Empty(kiln.Run(() => Stations.OutputSources(new BaseId(Guid.NewGuid()))));  // another base's list
        Assert.Equal([new EntityId(3301)], kiln.Run(() => Stations.OutputSources()));
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3301), on: false));
        Assert.Empty(_fake.Sources);
        Assert.False(kiln.Run(() => Stations.IsOutputSource(new EntityId(3301))));
        // mistakes
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Stations.SetOutputSource(EntityId.None)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => Stations.SetOutputSource(new EntityId(4444))));  // no station
        Assert.Throws<InvalidOperationException>(() => Stations.SetOutputSource(new EntityId(3301)));  // not the tick thread
        _fake.HasOutputs = false;  // an older kiln-items.dll
        Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Stations.SetOutputSource(new EntityId(3301))));
        Assert.False(kiln.Run(() => Stations.IsOutputSource(new EntityId(3301))));
    }

    [Fact]
    public void WithoutTheCapabilityNothingIsOptedIn()
    {
        using var kiln = Kiln(caps: false);
        kiln.Capabilities.Override(Stations.ReadCapability, true);
        kiln.Capabilities.Override(Stations.FillCapability, true);
        kiln.Capabilities.Override(Stations.OutputSourcesCapability, false);
        Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => Stations.SetOutputSource(new EntityId(3300))));
        Assert.Empty(_fake.Sources);
    }

    [Fact]
    public void AGoneStationNoLongerLends()
    {
        using var kiln = Kiln();
        AddStation(3301, 0);
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3301)));
        Assert.Equal(1, StationOutputKeeper.Count);
        _fake.StationRows.Remove(3301);  // destroyed
        kiln.Run(StationOutputKeeper.Prune);
        Assert.Empty(_fake.Sources);
        Assert.Equal(0, StationOutputKeeper.Count);
    }

    [Fact]
    public void TheFillButtonRunsAsKilnsFillOnlyWhenTheOutputsAreNeededAndCoverIt()
    {
        using var kiln = Kiln();
        AddStation(3301, 0);       // the charcoal kiln, opted in
        AddStation(5, 0, x: 3);    // the player, standing in the same base (BaseOf reads the fake's rows)
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3301)));
        var player = new EntityId(5);
        string? why = null;
        // the player and the chests cover it: the game's own fill
        _fake.PlanFor = _ => PlanWith(StationActionStatus.Done);
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));
        Assert.Contains("cover", why);
        // they do not, the outputs do: Kiln's fill, the button's meaning (n more, all or nothing, the player's bag first)
        _fake.PlanFor = r => PlanWith((r.Flags & StationsNative.FillNoOutputs) != 0 ? StationActionStatus.NotEnough : StationActionStatus.Done, 20);
        var o = kiln.Run(() => StationsCore.SourceFill(player, FillPress(amount: 3), true, out _, out why));
        Assert.NotNull(o);
        Assert.Equal((3, FillMode.Add, true, (uint?)0x1234u, player, true), (o!.Crafts, o.Mode, o.Exact, o.Recipe, o.Player, o.UseStationOutputs));
        Assert.Equal([new CategoryChoice(77, 1)], o.Choices);
        // not even with the outputs: the game's own fill (it refuses, nothing moves)
        _fake.PlanFor = _ => PlanWith(StationActionStatus.NotEnough);
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));
        Assert.Contains("not even", why);
        _fake.PlanFor = r => PlanWith((r.Flags & StationsNative.FillNoOutputs) != 0 ? StationActionStatus.NotEnough : StationActionStatus.Done, 20);
        // the game's own checks: CanAccessInventories, the player's base
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), false, out _, out why)));
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), null, out _, out why)));
        AddStation(6, -1);  // a player in no base
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(new EntityId(6), FillPress(), true, out _, out why)));
        Assert.Contains("another base", why);
        // other presses, item and water fills: not this
        var stop = FillPress();
        stop[0x40] = (byte)StationPlayerAction.Stop;
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, stop, true, out _, out why)));
        var water = FillPress();
        BitConverter.TryWriteBytes(water.AsSpan(0x10), 64u);
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, water, true, out _, out why)));
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(recipe: 0), true, out _, out why)));
        // the only opted-in station is the one being filled: nothing to lend
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3301), on: false));
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3300)));
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));
        // opted out everywhere: never looked at (vanilla)
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3300), on: false));
        _fake.Planned.Clear();
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));
        Assert.Empty(_fake.Planned);
    }

    [Fact]
    public void TheFillButtonTakesFromAPlayersFillOptedLinks()
    {
        // GitHub #132 (0.2.0-dev.31.12): no station opted in, the player has a link registered with Fills
        using var kiln = Kiln();
        AddStation(5, 0, x: 3);    // the player, in the forge's base
        var player = new EntityId(5);
        var forge = new EntityId(3300);
        string? why = null;
        _fake.PlanFor = r => PlanWith((r.Flags & StationsNative.FillLinked) != 0 ? StationActionStatus.Done : StationActionStatus.NotEnough, 0);
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));  // no link: vanilla, never planned
        Assert.Empty(_fake.Planned);
        var session = LinkPurposes.Fills.Track(player);
        Assert.True(LinkPurposes.Fills.Has(player) && LinkPurposes.Fills.Any && StationsCore.FillLinksOf(player));
        var o = kiln.Run(() => StationsCore.SourceFill(player, FillPress(amount: 2), true, out _, out why));
        Assert.NotNull(o);
        Assert.True(o!.UseLinkedSources && o.Player == player && o.Exact && o.Crafts == 2);
        Assert.True((_fake.Planned[0].Flags & (StationsNative.FillLinked | StationsNative.FillNoOutputs)) == StationsNative.FillNoOutputs);  // plain first
        Assert.True((_fake.Planned[^1].Flags & StationsNative.FillLinked) != 0 && _fake.Planned[^1].Player == 5);
        // the links cannot cover it either: the game's own fill, said so
        _fake.PlanFor = _ => PlanWith(StationActionStatus.NotEnough);
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));
        Assert.Contains("linked sources", why);
        // a Kiln client's "+1" / "+10": the answer counts the links too
        _fake.PlanFor = r =>
        {
            var p = PlanWith((r.Flags & StationsNative.FillLinked) != 0 ? StationActionStatus.Partial : StationActionStatus.NotEnough);
            p.CraftsPlanned = (r.Flags & StationsNative.FillLinked) != 0 ? 7u : 0u;
            return p;
        };
        Assert.Equal(7u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, [], true)));
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, [], false)));  // the inventory right still
        // an older kiln-items.dll (no FILL_LINKED): the button stays the game's, a mod's fill that asks is refused
        _fake.HasLinkedFills = false;
        _fake.Planned.Clear();
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));
        Assert.Empty(_fake.Planned);
        Assert.Throws<InvalidOperationException>(() => StationsCore.FillRequest(forge, new FillOptions { Player = player, UseLinkedSources = true }));
        _fake.HasLinkedFills = true;
        Assert.Throws<ArgumentException>(() => StationsCore.FillRequest(forge, new FillOptions { UseLinkedSources = true }));  // no Player
        var (req, _, _) = StationsCore.FillRequest(forge, new FillOptions { Player = player, UseLinkedSources = true });
        Assert.True((req.Flags & StationsNative.FillLinked) != 0);
        var (plain, _, _) = StationsCore.FillRequest(forge, new FillOptions { Player = player });
        Assert.True((plain.Flags & StationsNative.FillLinked) == 0);  // a mod's fill never takes from links unless it asks
        // the link went (disposed, or the player left): vanilla again
        LinkPurposes.Fills.Untrack(player, session);
        Assert.False(LinkPurposes.Fills.Has(player));
        LinkPurposes.Fills.Track(player);
        BuildStock.Gone(player);
        Assert.False(LinkPurposes.Fills.Has(player));
        _fake.Planned.Clear();
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, FillPress(), true, out _, out why)));
        Assert.Empty(_fake.Planned);
    }

    [Fact]
    public void TheIngredientRowsTransferTakesFromAPlayersFillOptedLinks()
    {
        // GitHub #132 (0.2.0-dev.31.16): the per-ingredient "+1" / "+n" of a production station's screen
        using var kiln = Kiln();
        AddStation(5, 0, x: 3);    // the player, in the forge's base
        var player = new EntityId(5);
        string? why = null;
        _fake.PlanFor = r => PlanWith((r.Flags & StationsNative.FillLinked) != 0 ? StationActionStatus.Done : StationActionStatus.NotEnough);
        // no fill-opted link: the game's own transfer, never planned
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(), true, out _, out why)));
        Assert.Empty(_fake.Planned);
        var session = LinkPurposes.Fills.Track(player);
        // the bag and the Magic chests do not cover it, the links do: Kiln's item fill, exactly the press's item and amount
        var press = kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(amount: 7), true, out _, out why));
        Assert.NotNull(press);
        Assert.Equal((new EntityId(3300), 0x10u, 7, player), (press!.Station, press.Item, press.Amount, press.Player));
        var plain = _fake.Planned[0];
        var linked = _fake.Planned[^1];
        Assert.Equal(StationsNative.FillItem, plain.Flags & (StationsNative.FillItem | StationsNative.FillLinked));  // plain first
        Assert.Equal(StationsNative.FillItem | StationsNative.FillLinked, linked.Flags & (StationsNative.FillItem | StationsNative.FillLinked));
        Assert.Equal((0x10u, 7u, 0u, 5u, StationsNative.Fill), (linked.Item, linked.Crafts, linked.RecipeId, linked.Player, linked.Action));
        var (req, _, _) = StationsCore.ItemFillRequest(new EntityId(3300), 0x10, 7, player, true, false);
        Assert.Equal(linked.Flags, req.Flags);
        // the bag and the chests cover it: the game's own transfer
        _fake.PlanFor = _ => PlanWith(StationActionStatus.Done);
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(), true, out _, out why)));
        Assert.Contains("cover", why);
        // not even with the links: the game's (it refuses, nothing moves), said so
        _fake.PlanFor = _ => PlanWith(StationActionStatus.NotEnough);
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(), true, out _, out why)));
        Assert.Contains("linked sources", why);
        _fake.PlanFor = r => PlanWith((r.Flags & StationsNative.FillLinked) != 0 ? StationActionStatus.Done : StationActionStatus.NotEnough);
        // the press's own rules: CanAccessInventories, the station's base
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(), false, out _, out why)));
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(), null, out _, out why)));
        AddStation(6, -1);
        var away = LinkPurposes.Fills.Track(new EntityId(6));
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(new EntityId(6), ItemPress(), true, out _, out why)));
        Assert.Contains("another base", why);
        LinkPurposes.Fills.Untrack(new EntityId(6), away);
        // a recipe fill, a water fill, an empty one: not this path
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, FillPress(), true, out _, out why)));
        var water = ItemPress();
        BitConverter.TryWriteBytes(water.AsSpan(0x10), 64u);
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, water, true, out _, out why)));
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(amount: 0), true, out _, out why)));
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(item: 0), true, out _, out why)));
        // and the recipe fill's path leaves the item press alone
        Assert.Null(kiln.Run(() => StationsCore.SourceFill(player, ItemPress(), true, out _, out why)));
        // an older kiln-items.dll (no FILL_ITEM): the game's own transfer, never planned
        _fake.HasItemFills = false;
        _fake.Planned.Clear();
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(), true, out _, out why)));
        Assert.Empty(_fake.Planned);
        Assert.Throws<InvalidOperationException>(() => StationsCore.ItemFillRequest(new EntityId(3300), 0x10, 7, player, true, false));
        _fake.HasItemFills = true;
        // the request's own limits: an item, 1..65535, a player for the links
        Assert.Throws<ArgumentException>(() => StationsCore.ItemFillRequest(new EntityId(3300), 0, 7, player, true, false));
        Assert.Throws<ArgumentException>(() => StationsCore.ItemFillRequest(new EntityId(3300), 0x10, 0, player, true, false));
        Assert.Throws<ArgumentException>(() => StationsCore.ItemFillRequest(new EntityId(3300), 0x10, 65536, player, true, false));
        Assert.Throws<ArgumentException>(() => StationsCore.ItemFillRequest(new EntityId(3300), 0x10, 7, EntityId.None, true, false));
        Assert.Equal(StationsNative.FillItem | StationsNative.DryRun,
            StationsCore.ItemFillRequest(new EntityId(3300), 0x10, 7, player, false, true).Request.Flags);
        // the link went: vanilla again
        LinkPurposes.Fills.Untrack(player, session);
        _fake.Planned.Clear();
        Assert.Null(kiln.Run(() => StationsCore.SourceItemFill(player, ItemPress(), true, out _, out why)));
        Assert.Empty(_fake.Planned);
    }

    [Fact]
    public void AnIngredientPressThatKilnRunsIsCanceledAndSubmittedAsAnItemFill()
    {
        using var kiln = Kiln();
        AddStation(5, 0, x: 3);
        var player = new EntityId(5);
        var session = LinkPurposes.Fills.Track(player);
        _fake.PlanFor = r => PlanWith((r.Flags & StationsNative.FillLinked) != 0 ? StationActionStatus.Done : StationActionStatus.NotEnough);
        _fake.Submitted.Clear();
        var ran = kiln.Run(() => StationsCore.RunItemPress(new ItemPress(new EntityId(3300), 0x10, 7, player)));
        Assert.True(ran);
        var (r, keep, _, _) = Assert.Single(_fake.Submitted);
        Assert.Equal((StationsNative.Fill, 0x10u, 7u, 5u, 0u), (r.Action, r.Item, r.Crafts, r.Player, r.RecipeId));
        Assert.Equal(StationsNative.FillItem | StationsNative.FillLinked, r.Flags);
        Assert.Empty(keep);
        LinkPurposes.Fills.Untrack(player, session);
    }

    [Fact]
    public void AnIngredientPressKilnCannotQueueIsLeftToTheGame()
    {
        // GitHub #132 (0.2.0-dev.31.16): RunItemPress false = the press is not canceled (the game's own transfer runs and
        // refuses as before); never a canceled press with nothing queued
        using var kiln = Kiln();
        var press = new ItemPress(new EntityId(3300), 0x10, 7, new EntityId(5));
        _fake.Submitted.Clear();
        foreach (var rc in new[] { StationsNative.BufferTooSmall, StationsNative.Unavailable, StationsNative.InvalidArgument })
        {
            _fake.SubmitResult = rc;
            Assert.False(kiln.Run(() => StationsCore.RunItemPress(press)));
        }
        _fake.SubmitResult = 0;
        _fake.HasItemFills = false;  // an older kiln-items.dll
        Assert.False(kiln.Run(() => StationsCore.RunItemPress(press)));
        _fake.HasItemFills = true;
        _fake.State = (true, false, true, true, "stations.fill is off");
        Assert.False(kiln.Run(() => StationsCore.RunItemPress(press)));
        Assert.Empty(_fake.Submitted);
        _fake.State = (true, true, true, true, "");
        Assert.True(kiln.Run(() => StationsCore.RunItemPress(press)));
        Assert.Single(_fake.Submitted);
    }

    [Fact]
    public void LinkedCountsComeBackInThePlanAndTheResult()
    {
        using var kiln = Kiln();
        var p = PlanWith(StationActionStatus.Done, 3);
        p.Linked[0] = 9;
        _fake.NextPlan = p;
        var plan = kiln.Run(() => Stations.Plan(new EntityId(3300), new FillOptions { Player = new EntityId(5), UseLinkedSources = true }));
        Assert.Equal((9L, 3L), (plan.Inputs[0].Linked, plan.Inputs[0].Outputs));
        var r = new StationsNative.Result { Size = (uint)sizeof(StationsNative.Result), Status = 0, FromLinked = 12, FromOutputs = 1 };
        Assert.Equal((12, 1), (StationsCore.ResultOf(r, StationAction.Fill).FromLinked, StationsCore.ResultOf(r, StationAction.Fill).FromOutputs));
    }

    [Fact]
    public void TheBuildingSnapshotGoesToThePlayersKilnClientWhileALinkIsOptedIn()
    {
        // GitHub #132: the server sends a player with a Building link what those links hold; off after the last one goes
        var sent = new List<(EntityId Player, BuildStockRows Message)>();
        BuildStock.SendHook = (p, m) => { sent.Add((p, m)); return true; };
        BuildStock.SupportedHook = () => true;
        var rows = new[] { new StationsNative.Stack { Item = 0x88, Count = 4 }, new StationsNative.Stack { Item = 0x77, Count = 35 },
                           new StationsNative.Stack { Item = 0x99, Count = 0 } };
        BuildStock.RowsHook = _ => (StationsNative.Ok, rows);
        var player = new EntityId(5);
        var s = BuildStock.Track(player);
        Assert.Equal((true, 1), BuildStock.SessionOf(player));
        BuildStock.Pump(100);
        Assert.Single(sent);
        Assert.True(sent[0].Message.Supported);
        Assert.Equal([new CraftStockRow(0x77, 35), new CraftStockRow(0x88, 4)], sent[0].Message.Rows);  // sorted, empty rows left out
        BuildStock.Pump(110);
        Assert.Single(sent);                                     // unchanged, not due: nothing again
        rows[0].Count = 3;
        BuildStock.Pump(131);
        Assert.Equal(2, sent.Count);                             // the refresh saw a change
        BuildStock.SupportedHook = () => false;
        BuildStock.Pump(162);
        Assert.False(sent[^1].Message.Supported);                // the capability went off: the client's own checks again
        BuildStock.SupportedHook = () => true;
        BuildStock.Untrack(player, s);
        BuildStock.Pump(163);
        Assert.False(sent[^1].Message.Supported);
        Assert.Equal((false, 0), BuildStock.SessionOf(player));  // the last link went: the session ended
        // the wire message round-trips and refuses malformed rows
        var w = new NetWriter();
        BuildStockRows.Encode(new BuildStockRows(true, [new(0x77, 35)]), w);
        var reader = new NetReader(w.WrittenSpan);
        var back = BuildStockRows.Decode(ref reader);
        Assert.True(back.Supported && back.Rows.Single() == new CraftStockRow(0x77, 35));
        var bad = new NetWriter();
        bad.Bool(true).UInt16(2).UInt32(0x77).UInt32(1).UInt32(0x77).UInt32(2);
        Assert.Throws<InvalidDataException>(() => { var rr = new NetReader(bad.WrittenSpan); BuildStockRows.Decode(ref rr); });
        // the client hands it to kiln-items only with a Kiln server
        var applied = new List<(bool On, int Rows)>();
        BuildStock.ApplyHook = (on, r) => { applied.Add((on, r.Length)); return StationsNative.Ok; };
        BuildStock.ServerSideHook = () => true;
        Assert.True(BuildStock.ClientApply(back));
        BuildStock.ServerSideHook = () => false;
        Assert.True(BuildStock.ClientApply(back));
        Assert.Equal([(true, 1), (false, 0)], applied);
    }

    [Fact]
    public void TheStationScreensFillIsAnsweredByTheButtonsOwnRules()
    {
        // GitHub #68: a Kiln client's "+1" / "+10" fill is offered for as many crafts as the press would run as Kiln's fill
        using var kiln = Kiln();
        AddStation(3301, 0);       // the charcoal kiln, opted in
        AddStation(5, 0, x: 3);    // the player, in the same base
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3301)));
        var player = new EntityId(5);
        var forge = new EntityId(3300);
        CategoryChoice[] choices = [new(77, 1)];
        _fake.PlanFor = r =>
        {
            var p = PlanWith((r.Flags & StationsNative.FillNoOutputs) != 0 ? StationActionStatus.NotEnough : StationActionStatus.Partial, 20);
            p.CraftsPlanned = 10;
            return p;
        };
        Assert.Equal(10u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, choices, true)));
        // the plan the press would run: n more, as many as covered, the player's bag first, station outputs on
        var asked = _fake.Planned[^1];
        Assert.Equal((uint)StationsCore.MaxFillCrafts, asked.Crafts);
        Assert.True((asked.Flags & StationsNative.FillAdd) != 0);
        Assert.True((asked.Flags & StationsNative.FillExact) == 0);
        Assert.True((asked.Flags & StationsNative.FillNoOutputs) == 0);
        Assert.Equal((0x1234u, 5u, 77u, (byte)1), (asked.RecipeId, asked.Player, asked.C0.Category, asked.C0.Index));
        // not even one craft with the outputs
        _fake.PlanFor = _ => PlanWith(StationActionStatus.NotEnough);
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, choices, true)));
        _fake.PlanFor = r =>
        {
            var p = PlanWith(StationActionStatus.Done, 20);
            p.CraftsPlanned = 4;
            return p;
        };
        Assert.Equal(4u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, [], true)));
        // the button's rules: the inventory right, the player's base, another opted-in station; nothing is planned then
        _fake.Planned.Clear();
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, choices, false)));
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, choices, null)));
        AddStation(6, -1);  // a player in no base
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(new EntityId(6), forge, 0x1234, choices, true)));
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0, choices, true)));
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(EntityId.None, forge, 0x1234, choices, true)));
        Assert.Empty(_fake.Planned);
        // the only opted-in station is the one asked about
        kiln.Run(() => Stations.SetOutputSource(new EntityId(3301), on: false));
        kiln.Run(() => Stations.SetOutputSource(forge));
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, choices, true)));
        // opted out everywhere (vanilla): never planned
        kiln.Run(() => Stations.SetOutputSource(forge, on: false));
        Assert.Equal(0u, kiln.Run(() => StationsCore.FillCrafts(player, forge, 0x1234, choices, true)));
        Assert.Empty(_fake.Planned);
    }

    [Fact]
    public void APlayersCraftingActionIsDecoded()
    {
        var p = new byte[68];
        BitConverter.TryWriteBytes(p.AsSpan(0), 3300u);
        BitConverter.TryWriteBytes(p.AsSpan(4), 0x1234u);
        BitConverter.TryWriteBytes(p.AsSpan(0x0c), (ushort)7);
        BitConverter.TryWriteBytes(p.AsSpan(0x18), 77u);
        p[0x18 + 4] = 1;
        p[0x38] = 1;
        BitConverter.TryWriteBytes(p.AsSpan(0x3c), 9u);
        p[0x40] = (byte)StationPlayerAction.Fill;
        var ev = StationsCore.Decode(new EntityId(1), p, canCancel: true)!;
        Assert.Equal((new EntityId(3300), StationPlayerAction.Fill, 0x1234u, 7), (ev.Station, ev.Action, ev.Recipe, ev.Amount));
        Assert.Equal([new CategoryChoice(77, 1)], ev.Choices);
        Assert.True(ev.UseAllOptions && ev.CanCancel);
        Assert.True(ev.Involves(new EntityId(1)) && ev.Involves(new EntityId(3300)));
        p[0x40] = 6;
        Assert.Null(StationsCore.Decode(new EntityId(1), p, true));  // not a type the game has
        Assert.Null(StationsCore.Decode(new EntityId(1), p.AsSpan(0, 0x40), true));
    }

    [Fact]
    public void AResultIsReadFromTheNativeRecord()
    {
        var r = NewResult(StationActionStatus.Partial, "the stock covered 4 of 10 crafts");
        r.ChangeCount = 1;
        var ch = (StationsNative.MoveChange*)r.Changes;
        ch[0] = new StationsNative.MoveChange { FromSlot = SlotId.Of(new EntityId(700), 3).Value, ToSlot = 0, Item = 0x10, Amount = 8 };
        var x = StationsCore.ResultOf(r, StationAction.Fill);
        Assert.True(x.Ok && x.Changed);
        Assert.Null(x.Reason);
        Assert.Equal((10, 3, 7, 4), (x.CraftsRequested, x.CraftsBefore, x.CraftsAfter, x.CraftsAdded));
        var c = Assert.Single(x.Changes);
        Assert.Equal((new ItemId(0x10), 8), (c.Item, c.Amount));
        Assert.Equal("the stock covered 4 of 10 crafts", x.Message);
        var locked = StationsCore.ResultOf(NewResult(StationActionStatus.Locked, "no one on this server has unlocked recipe 0x00005678", read: false), StationAction.Select);
        Assert.False(locked.Ok);
        Assert.Contains("unlocked", locked.Reason);
        Assert.Null(locked.StateBefore);  // nothing was read
        var limited = NewResult(StationActionStatus.Partial, "2 of 9");
        limited.LimitedBy = 2;
        limited.ShortItem = 0x10;
        var lx = StationsCore.ResultOf(limited, StationAction.Fill);
        Assert.Equal((FillLimit.Reserve, (ItemId?)new ItemId(0x10)), (lx.LimitedBy, lx.ShortItem));
        Assert.True(lx.CanRetry);
        Assert.False(StationsCore.ResultOf(NewResult(StationActionStatus.WrongWorkshop, "x"), StationAction.Select).CanRetry);
        Assert.True(StationsCore.ResultOf(NewResult(StationActionStatus.Unchanged, ""), StationAction.Stop) is { Ok: true, Changed: false });
    }

    [Fact]
    public void StockWithStationsReadsTheNativeSumWhenTheTableHasIt()
    {
        using var k = Kiln();
        _fake.HasStockWithStations = true;
        _fake.Stock = [new StationsNative.Stack { Item = 0x10, Count = 40 }];
        _fake.StockWithStations = [new StationsNative.Stack { Item = 0x10, Count = 47 }, new StationsNative.Stack { Item = 0x20, Count = 5 }];
        var id = new BaseId(AltarGuid);
        var all = k.Run(() => Bases.StockWithStations(id));
        Assert.Equal(47, all[new ItemId(0x10)]);  // kiln.stations' sum, not Stock plus a managed read of each station
        Assert.Equal(5, all[new ItemId(0x20)]);
        Assert.Equal([0u], _fake.StockWithStationsAsked);
        Assert.Empty(k.Run(() => Bases.StockWithStations(new BaseId(Guid.NewGuid()))));  // no base with that id now
        Assert.Single(_fake.StockWithStationsAsked);  // an unknown id never reaches the table
        _fake.State = (false, false, false, false, "stations.read is off");
        k.Capabilities.Override(Stations.ReadCapability, false);
        Assert.Throws<KilnUnavailableException>(() => k.Run(() => Bases.StockWithStations(id)));
    }

    [Fact]
    public void ACollectResultIsNeverADryRun()
    {
        // native collect's flags (bits 16 and up) share no bit with a request flag
        const uint request = StationsNative.FillAdd | StationsNative.FillExact | StationsNative.DryRun | StationsNative.NoWater |
                             StationsNative.AfterCraft | StationsNative.KeepStopped | StationsNative.AllowLocked | StationsNative.FillNoOutputs;
        Assert.Equal(0u, (StationsNative.CollectOutputs | StationsNative.CollectLeftovers | StationsNative.CollectToBase) & request);
        var collect = NewResult(StationActionStatus.Done, "", StationsNative.CollectOutputs | StationsNative.CollectToBase);
        collect.Action = StationsNative.CollectAction;
        Assert.False(StationsCore.ResultOf(collect, StationAction.Collect).DryRun);
        // even a result whose flags carry 0x4 is a dry run only for a fill
        var odd = NewResult(StationActionStatus.Done, "", StationsNative.DryRun);
        Assert.False(StationsCore.ResultOf(odd, StationAction.Collect).DryRun);
        Assert.True(StationsCore.ResultOf(odd, StationAction.Fill).DryRun);
    }

    internal static StationsNative.Result NewResult(StationActionStatus status, string message, uint flags = 0, bool read = true)
    {
        var r = new StationsNative.Result
        {
            Size = (uint)sizeof(StationsNative.Result), Status = (int)status, Action = StationsNative.Fill, Flags = flags, Station = 3300,
            RecipeId = 0x1234, CraftsRequested = 10, CraftsBefore = 3, CraftsAfter = 7, CraftsAdded = 4,
            StateBefore = read ? (byte)StationState.MissingMaterials : (byte)0, StateAfter = read ? (byte)StationState.Running : (byte)0,
        };
        var b = System.Text.Encoding.UTF8.GetBytes(message);
        for (var i = 0; i < b.Length && i < 127; i++) r.Message[i] = b[i];
        return r;
    }

    internal Fake Backend => _fake;

    // The station's recipe 0x1234 makes this item (a fixed buffer: only an unsafe class can write it).
    internal void SetRecipeOutput(uint item)
    {
        var r = _fake.Recipes[0x1234];
        r.Outputs[0] = item;
        _fake.Recipes[0x1234] = r;
    }
}

// Submissions complete their tasks (await needs a safe context: this class is not unsafe).
public sealed class StationsSubmitTests : IDisposable
{
    private readonly StationsTests _fixture = new();
    private StationsTests.Fake Fake => _fixture.Backend;

    public void Dispose() => _fixture.Dispose();

    private static KilnHarness Kiln()
    {
        var k = new KilnHarness();
        foreach (var c in new[] { Stations.ReadCapability, Stations.FillCapability, Stations.ControlCapability, Stations.EventsCapability })
            k.Capabilities.Override(c, true);
        return k;
    }

    [Fact]
    public async Task AFillCompletesWithItsResultAndPostsTheChange()
    {
        using var kiln = Kiln();
        var changed = new List<StationChangedEvent>();
        kiln.Run(() => Events.On<StationChangedEvent>(changed.Add));
        var task = kiln.Run(() => Stations.Fill(new EntityId(3300), new FillOptions { Crafts = 10 }));
        var (r, _, _, key) = Assert.Single(Fake.Submitted);
        Assert.Equal((StationsNative.Fill, 10u), (r.Action, r.Crafts));
        Assert.False(task.IsCompleted);
        kiln.Run(() => StationsCore.Complete(key, StationsTests.NewResult(StationActionStatus.Partial, "4 of 10")));
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(StationActionStatus.Partial, result.Status);
        Assert.Equal(StationAction.Fill, result.Action);
        kiln.Run(() => { });  // the notices go out at the step's end
        Assert.Equal(StationAction.Fill, Assert.Single(changed).Action);
        // a second completion of the same key does nothing
        kiln.Run(() => StationsCore.Complete(key, StationsTests.NewResult(StationActionStatus.Done, "")));
    }

    [Fact]
    public async Task ADryRunOrAnUnchangedResultPostsNoChange()
    {
        using var kiln = Kiln();
        var changed = new List<StationChangedEvent>();
        kiln.Run(() => Events.On<StationChangedEvent>(changed.Add));
        var dry = kiln.Run(() => Stations.Fill(new EntityId(3300), new FillOptions { DryRun = true }));
        var stop = kiln.Run(() => Stations.Stop(new EntityId(3300)));
        kiln.Run(() => StationsCore.Complete(Fake.Submitted[0].Key, StationsTests.NewResult(StationActionStatus.Done, "", StationsNative.DryRun)));
        kiln.Run(() => StationsCore.Complete(Fake.Submitted[1].Key, StationsTests.NewResult(StationActionStatus.Unchanged, "already stopped")));
        Assert.True((await dry).DryRun);
        Assert.Equal(StationActionStatus.Unchanged, (await stop).Status);
        kiln.Run(() => { });
        Assert.Empty(changed);
    }

    [Fact]
    public async Task AFullQueueIsARefusalAndAnOffServiceThrows()
    {
        using var kiln = Kiln();
        Fake.SubmitResult = StationsNative.BufferTooSmall;
        var full = await kiln.Run(() => Stations.Resume(new EntityId(3300)));
        Assert.Equal(StationActionStatus.QueueFull, full.Status);
        Assert.True(full.CanRetry);
        Assert.Null(full.StateBefore);
        Assert.Contains("1024", full.Message);
        Fake.SubmitResult = StationsNative.Unavailable;
        var off = kiln.Run(() => Assert.Throws<KilnUnavailableException>(() => { _ = Stations.Resume(new EntityId(3300)); }));
        Assert.Equal("stations.control", off.Capability);
        Fake.SubmitResult = StationsNative.InvalidArgument;
        kiln.Run(() => Assert.Throws<ArgumentException>(() => { _ = Stations.Fill(new EntityId(3300)); }));
    }

    [Fact]
    public async Task CollectWithNothingToCollectIsUnchanged()
    {
        using var kiln = Kiln();
        var r = await kiln.Run(() => Stations.Collect(new EntityId(3300), new EntityId(700)));
        Assert.Equal(StationActionStatus.Unchanged, r.Status);
        Assert.True(r.Ok);
        var none = await kiln.Run(() => Stations.Collect(new EntityId(5), new EntityId(700)));
        Assert.Equal(StationActionStatus.NotAStation, none.Status);
    }

    // Collect and CollectToBase keep their 29.16 path: Kiln.Items' Items.Move per stack, chosen by FindRoomFor; the native
    // collect of kiln.stations is for native mods only. A player's bag is a target; with no move function the exception
    // names items.move.
    [Fact]
    public async Task CollectKeepsItsItemsMovePathAndTakesAPlayersBag()
    {
        using var kiln = Kiln();
        var coal = kiln.Items.AddItem("Material_T1_Charcoal", 50);
        _fixture.SetRecipeOutput(coal.Value);
        var station = kiln.Entities.Create(new WorldPosition(1, 2, 3));
        var row = Fake.StationRows[3300];
        Fake.StationRows.Remove(3300);
        row.Entity = station.Value;
        Fake.StationRows[station.Value] = row;
        kiln.Items.AddContainer(station).Put(coal, 7);
        var changed = new List<StationChangedEvent>();
        kiln.Run(() => Events.On<StationChangedEvent>(changed.Add));

        var toPlayer = await Assert.ThrowsAsync<KilnUnavailableException>(() => kiln.Run(() => Stations.Collect(station, new EntityId(5))));
        Assert.Equal("items.move", toPlayer.Capability);
        Assert.Empty(Fake.Submitted);

        // no Magic chest has room: NoRoom, as in 29.16; not a dry run, nothing changed, no event
        var none = await kiln.Run(() => Stations.CollectToBase(station));
        Assert.Equal(StationActionStatus.NoRoom, none.Status);
        Assert.False(none.DryRun);
        Assert.Contains("no container with room", none.Message);
        kiln.Run(() => { });
        Assert.Empty(changed);
    }
}
