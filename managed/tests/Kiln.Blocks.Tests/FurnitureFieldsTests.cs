// SPDX-License-Identifier: MIT
// What a container's default loot and lid, a door's swing and sides, several colliders and crafting props lower to, with no game: the clone's fields and what it adds or does without,
// what is refused by block and field, and how the runtime names a container. The placed things, the loot given and the restart are the lab's (mods/tests/furniture-test).
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class FurnitureFieldsTests : IDisposable
{
    private static ModBlocks Mod(string id) => new(id, 1) { ManifestClients = "mod" };

    private static JsonObject Lower<T>(T b, string name = "thing", ModBlocks? mod = null) where T : class, IBlock => Compiler.Lower(mod ?? Mod("furniture-fields"), name, b).Low.Json;

    private static JsonObject Template(JsonObject j) => Assert.Single(j["templates"]!.AsArray())!.AsObject();
    private static JsonObject Fields(JsonObject j) => Template(j)["fields"]!.AsObject();
    private static double D(JsonNode? n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);

    // a handle for a block of the mod, made as Define would, without handing anything to the game
    private static BlockHandle Add<T>(ModBlocks mod, string name, T block) where T : class, IBlock
    {
        var handle = Compiler.Lower(mod, name, block).Handle;
        mod.Handles[name] = handle;
        return handle;
    }

    private static string?[] Names(JsonNode? list) => list is JsonArray a ? [.. a.Select(x => (string?)x)] : [];

    public void Dispose()
    {
        WorldTablesNative.TestDefine = null;
        WorldTablesNative.TestCraftingPropIndex = null;
    }

    [Fact]
    public void Reset_forgets_interaction_callbacks_and_preserves_other_mods()
    {
        var mod = Mod("interaction-reset");
        var other = Mod("interaction-other");
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        try
        {
            InteractRuntime.Add(mod, mine, new(mod.Id, "mine", null, null, null, null, _ => false));
            InteractRuntime.Add(other, theirs, new(other.Id, "theirs", null, null, null, null, _ => false));
            Assert.False(InteractRuntime.Allows(EntityId.None, mine));
            mod.Reset();
            Assert.Null(InteractRuntime.EntryOf(mine));
            Assert.Null(InteractRuntime.Allows(EntityId.None, mine));
            Assert.NotNull(InteractRuntime.EntryOf(theirs));
        }
        finally { mod.Reset(); other.Reset(); }
    }

    // ---- default loot ----

    private static readonly LootTableBlock Scrap = new() { Entries = [new LootEntry { Item = "Material_T1_Metal_Scraps", CountMin = 2, CountMax = 5 }] };

    [Fact]
    public void A_default_loot_written_in_place_is_in_what_the_container_became()
    {
        var f = Lower(new ContainerBlock { Name = "Chest", DefaultLoot = Scrap })["furniture"]!;
        Assert.Equal("Material_T1_Metal_Scraps", (string?)f["defaultLoot"]!["entries"]![0]!["item"]);
        Assert.Equal(5, (int)f["defaultLoot"]!["entries"]![0]!["countMax"]!);
    }

    [Fact]
    public void A_default_loot_names_a_loot_table_block_of_the_mod()
    {
        var mod = Mod("furniture-loot-name");
        Add(mod, "scrap-pile", Scrap);
        var f = Lower(new ContainerBlock { Name = "Chest", DefaultLoot = "scrap-pile" }, mod: mod)["furniture"]!;
        Assert.Equal("Material_T1_Metal_Scraps", (string?)f["defaultLoot"]!["entries"]![0]!["item"]);
    }

    [Fact]
    public void A_default_loot_that_is_no_table_is_refused_by_field()
    {
        var mod = Mod("furniture-loot-bad");
        var e = Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "Chest", DefaultLoot = new LootTableBlock { Entries = [] } }, "bad", mod));
        Assert.Equal("defaultLoot.entries", e.Field);
        Assert.Contains("bad", e.Message, StringComparison.Ordinal);
        var gone = Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "Chest", DefaultLoot = "no-such-table" }, "gone", mod));
        Assert.Equal("defaultLoot", gone.Field);
    }

    [Fact]
    public void Containers_are_named_by_template_and_spot_in_sixteenths_of_a_metre()
    {
        var t = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var a = ContainerRuntime.Spot(t, new WorldPosition(10.0, 20.0, 30.0));
        Assert.Equal(a, ContainerRuntime.Spot(t, new WorldPosition(10.01, 20.0, 29.99)));     // a position read back from a save is the same spot
        Assert.NotEqual(a, ContainerRuntime.Spot(t, new WorldPosition(10.5, 20.0, 30.0)));
        Assert.NotEqual(a, ContainerRuntime.Spot(Guid.Parse("11111111-2222-3333-4444-555555555556"), new WorldPosition(10.0, 20.0, 30.0)));
    }

    private static ItemsResult Given(ItemsStatus status, int requested = 2, int done = 2) =>
        new(status, EntityId.None, ItemId.None, requested, done, [], "", 0, 1, false);

    [Fact]
    public async Task A_delayed_inventory_result_never_submits_the_next_stack_from_a_continuation()
    {
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2), new("twigs", 2)] };
        book.Pending["spot"] = progress;
        var first = new TaskCompletionSource<ItemsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ItemsResult>? transaction = null;
        var thread = Environment.CurrentManagedThreadId;
        var submitted = new List<string>();
        Task<ItemsResult> Give(LootStack stack)
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            submitted.Add(stack.Item);
            return first.Task;
        }
        Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, Give, true, out _));
        await Task.Run(() => first.SetResult(Given(ItemsStatus.Done)), TestContext.Current.CancellationToken);
        // Completing a transaction on another thread changes neither the journal nor the next native request.
        Assert.Equal(0, progress.Next);
        Assert.Equal(["scraps"], submitted);
        // The next call stands for the next game tick; its own thread is the one native calls must use.
        thread = Environment.CurrentManagedThreadId;
        Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, Give, true, out _));
        Assert.Equal(["scraps", "twigs"], submitted);
        Assert.Equal(1, progress.Next);
    }

    [Fact]
    public void A_save_records_completed_stacks_without_submitting_and_a_restart_resumes_only_the_remainder()
    {
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2), new("twigs", 2)] };
        book.Pending["spot"] = progress;
        var completion = new TaskCompletionSource<ItemsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ItemsResult>? transaction = completion.Task;
        // The final shutdown snapshot can come after the host stopped service ticks. WorldData's serialization callback must harvest that result itself.
        book.BeforeSnapshot = () => Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, _ => throw new Exception("a snapshot must submit no request"), false, out _));
        Assert.Equal(0, JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(book))!.Pending["spot"].Next);
        completion.SetResult(Given(ItemsStatus.Done)); // arrives after the earlier snapshot/announcement, with no intervening service tick
        var restored = JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(book))!;
        var roll = restored.Pending["spot"];
        Assert.Equal(1, roll.Next);
        var given = new List<string>();
        Assert.False(ContainerRuntime.Advance(restored, "spot", roll, ref transaction, stack => { given.Add(stack.Item); return Task.FromResult(Given(ItemsStatus.Done)); }, true, out _));
        Assert.True(ContainerRuntime.Advance(restored, "spot", roll, ref transaction, _ => throw new Exception("all stacks finished"), false, out _));
        Assert.Equal(["twigs"], given);
        Assert.Contains("spot", restored.Filled.Keys);
        Assert.Empty(restored.Pending);
    }

    [Theory]
    [InlineData(ItemsStatus.Unavailable)]
    [InlineData(ItemsStatus.NoInventory)]
    [InlineData(ItemsStatus.Refused)]
    public void A_temporary_failure_keeps_the_same_rolled_stack_for_retry(ItemsStatus status)
    {
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2)] };
        book.Pending["spot"] = progress;
        Task<ItemsResult>? transaction = Task.FromResult(Given(status, done: 0));
        Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, _ => throw new Exception("retry is later"), true, out var retry));
        Assert.NotNull(retry);
        Assert.Equal(0, progress.Next);
        Assert.Empty(book.Filled);
        Assert.Same(progress, book.Pending["spot"]);
    }

    // The real managed Items submission/callback path, with the native transaction committed before its Pre callback.
    // The trailing slot is the append-only result peek; using V3 as the prefix also checks compatibility with WP13's rows.
    private sealed unsafe class InventoryStub : IDisposable
    {
        private sealed class Request
        {
            public ItemsNative.Result Result;
            public delegate* unmanaged[Cdecl]<ItemsNative.Result*, void*, void> Callback;
            public void* User;
            public bool Committed;
        }
        private static readonly Dictionary<ulong, Request> Requests = new();
        private static ulong _ticket;
        private readonly ItemsNative.TableV3* _table;
        private readonly FieldInfo _field = typeof(Items).GetField("_table", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _previous;
        private static nint _catalogName;
        private static Guid _catalogGuid;
        private readonly (FieldInfo Field, object? Value)[] _catalogBefore = typeof(ItemCatalog).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.Name is "_all" or "_byId" or "_byName" or "_byNameCi" or "_byDisplay" or "_loadedCount").Select(field => (field, field.GetValue(null))).ToArray();

        public InventoryStub(bool peekAvailable = true)
        {
            Requests.Clear();
            _table = (ItemsNative.TableV3*)NativeMemory.AllocZeroed((nuint)(sizeof(ItemsNative.TableV3) + sizeof(nint)));
            ((uint*)_table)[0] = (uint)(sizeof(ItemsNative.TableV3) + (peekAvailable ? sizeof(nint) : 0));
            ((uint*)_table)[1] = 3;
            _table->V2.V1.Give = &Give;
            _table->V2.V1.ItemCount = &ItemCount;
            _table->V2.V1.ItemAt = &ItemAt;
            *(delegate* unmanaged[Cdecl]<ulong, ItemsNative.Result*, int>*)((byte*)_table + sizeof(ItemsNative.TableV3)) = &Peek;
            _previous = _field.GetValue(null);
            _field.SetValue(null, Pointer.Box(_table, typeof(ItemsNative.Table*)));
        }

        public void Catalog(string name, Guid guid)
        {
            if (_catalogName != 0) Marshal.FreeCoTaskMem(_catalogName);
            _catalogName = Marshal.StringToCoTaskMemUTF8(name);
            _catalogGuid = guid;
            typeof(ItemCatalog).GetField("_loadedCount", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, 0u);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static uint ItemCount() => _catalogName == 0 ? 0u : 1u;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int ItemAt(uint index, ItemsNative.ItemInfo* info)
        {
            if (index != 0 || _catalogName == 0) return ItemsNative.NotFound;
            info->Id = BlockIds.Fnv(_catalogGuid);
            _catalogGuid.TryWriteBytes(new Span<byte>(info->Guid, 16));
            var name = (byte*)info + (int)Marshal.OffsetOf<ItemsNative.ItemInfo>("Name");
            *(nint*)name = _catalogName;
            ((nuint*)name)[1] = (nuint)System.Text.Encoding.UTF8.GetByteCount(Marshal.PtrToStringUTF8(_catalogName)!);
            return ItemsNative.Ok;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Give(ulong owner, ItemsNative.Request* request, delegate* unmanaged[Cdecl]<ItemsNative.Result*, void*, void> callback, void* user, ulong* ticket)
        {
            *ticket = ++_ticket;
            Requests[*ticket] = new Request { Callback = callback, User = user, Result = new ItemsNative.Result
            {
                Size = (uint)sizeof(ItemsNative.Result), Ticket = *ticket, Entity = request->Entity, Item = request->Item,
                Requested = request->Count, Done = request->Count, Status = 0,
            } };
            return ItemsNative.Ok;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Peek(ulong ticket, ItemsNative.Result* result)
        {
            if (!Requests.TryGetValue(ticket, out var request) || !request.Committed) return ItemsNative.NotFound;
            *result = request.Result;
            return ItemsNative.Ok;
        }

        public void Commit() { foreach (var request in Requests.Values) request.Committed = true; }
        public int Submitted => Requests.Count;
        public void Deliver()
        {
            foreach (var request in Requests.Values.ToArray())
            {
                var result = request.Result;
                request.Callback(&result, request.User);
            }
            Requests.Clear();
        }
        public void Dispose()
        {
            Deliver();
            _field.SetValue(null, _previous);
            NativeMemory.Free(_table);
            if (_catalogName != 0) Marshal.FreeCoTaskMem(_catalogName);
            _catalogName = 0;
            foreach (var before in _catalogBefore) before.Field.SetValue(null, before.Value);
        }
    }

    [Fact]
    public void A_pending_roll_keeps_a_mod_items_identity_when_its_debug_name_changes_with_was()
    {
        using var native = new InventoryStub();
        var mod = Mod("pending-rename");
        var old = Compiler.Lower(mod, "old-name", new ItemBlock { Name = "Shard" }).Handle;
        var renamed = Compiler.Lower(mod, "new-name", new ItemBlock { Name = "Shard", Was = "old-name" }).Handle;
        Assert.Equal(old.Guid, renamed.Guid);
        native.Catalog(old.GameName, old.Guid);
        var type = typeof(ContainerRuntime);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var active = (System.Collections.IDictionary)type.GetField("Active", flags)!.GetValue(null)!;
        var plans = (System.Collections.IDictionary)type.GetField("Plans", flags)!.GetValue(null)!;
        var pump = type.GetMethod("Pump", flags)!;
        var template = Guid.NewGuid();
        var plan = Activator.CreateInstance(type.GetNestedType("Plan", BindingFlags.NonPublic)!, [mod.Id, "cache", template, new LootPlan { Label = "cache", Entries = [] }])!;
        var progress = new FilledContainers.FillProgress { Stacks = [new(old.GameName, 2)] };
        var book = new FilledContainers();
        book.Pending["spot"] = progress;
        void Work(FilledContainers.FillProgress roll) => active["spot"] = Activator.CreateInstance(type.GetNestedType("Work", BindingFlags.NonPublic)!, [plan, new EntityId(42), roll]);
        try
        {
            plans[template] = plan;
            Work(progress);
            book.BeforeSnapshot = () => pump.Invoke(null, [book, false]);
            var restored = JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(book))!;
            active.Clear();
            native.Catalog(renamed.GameName, renamed.Guid);
            Work(restored.Pending["spot"]);
            pump.Invoke(null, [restored, true]);
            native.Commit(); native.Deliver();
            pump.Invoke(null, [restored, false]);
            Assert.Contains("spot", restored.Filled.Keys);
            Assert.Empty(restored.Pending);
        }
        finally { active.Clear(); plans.Remove(template); }
    }

    [Fact]
    public void Legacy_pending_labels_are_backfilled_with_the_current_native_item_guid()
    {
        using var native = new InventoryStub();
        var guid = Guid.NewGuid();
        native.Catalog("legacy.current", guid);
        var progress = JsonSerializer.Deserialize<FilledContainers.FillProgress>("""{"Stacks":[{"Item":"legacy.current","Count":2}],"Next":0}""")!;
        ContainerRuntime.CaptureItemGuids(progress);
        Assert.Equal(guid, progress.ItemGuids["legacy.current"]);
    }

    [Fact]
    public void A_stored_guid_cannot_be_replaced_by_a_different_item_reusing_its_old_label()
    {
        using var native = new InventoryStub();
        native.Catalog("reused.label", Guid.NewGuid());
        var progress = new FilledContainers.FillProgress { Stacks = [new("reused.label", 2)], ItemGuids = { ["reused.label"] = Guid.NewGuid() } };
        var give = typeof(ContainerRuntime).GetMethod("Give", BindingFlags.Static | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => give.Invoke(null, [new EntityId(42), progress, progress.Stacks[0]]));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(0, native.Submitted);
        Assert.Equal(0, progress.Next);
    }

    [Fact]
    public void A_legacy_record_with_no_native_guid_is_retained_without_a_guessed_identity()
    {
        using var native = new InventoryStub();
        native.Catalog("missing.guid", Guid.Empty);
        var progress = new FilledContainers.FillProgress { Stacks = [new("missing.guid", 2)] };
        Assert.Throws<InvalidOperationException>(() => ContainerRuntime.CaptureItemGuids(progress));
        Assert.Empty(progress.ItemGuids);
        Assert.Equal(0, native.Submitted);
    }

    private static void RemoveAliasMod(ModBlocks mod)
    {
        ContainerRuntime.Forget(mod.Id);
        var mods = (System.Collections.Concurrent.ConcurrentDictionary<string, ModBlocks>)BlocksType.GetField("Mods_", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        mods.TryRemove(mod.Id, out _);
    }

    // SchemaDriftTests declares a Kiln.Blocks namespace, which shadows the imported Blocks type in this test assembly.
    private static Type BlocksType => typeof(Compiler).Assembly.GetType("Kiln.Blocks")!;
    private static ModBlocks AliasMod(string id) => (ModBlocks)BlocksType.GetMethod("ModFor", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [id, (Func<ulong>)(() => 1)])!;

    [Fact]
    public void Legacy_pending_renames_use_only_an_explicit_was_alias_confirmed_by_the_native_registry()
    {
        using var native = new InventoryStub();
        var mod = AliasMod("legacy-rename-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handle = Compiler.Lower(mod, "new-name", new ItemBlock { Name = "Shard", Was = "old-name" }).Handle;
            mod.Handles[handle.Name] = handle;
            native.Catalog(handle.GameName, handle.Guid);
            ContainerRuntime.Forget(mod.Id); // a mod generation invalidates the bounded alias index
            var progress = new FilledContainers.FillProgress { Stacks = [new(mod.Id + ".old-name", 2)] };
            ContainerRuntime.CaptureItemGuids(progress);
            Assert.Equal(handle.Guid, progress.ItemGuids[mod.Id + ".old-name"]);
        }
        finally { RemoveAliasMod(mod); }
    }

    [Fact]
    public void Ambiguous_legacy_was_aliases_keep_the_roll_pending_without_guessing_an_item()
    {
        using var native = new InventoryStub();
        var id = "ambiguous-" + Guid.NewGuid().ToString("N");
        var a = AliasMod(id + ".a");
        var b = AliasMod(id);
        try
        {
            var one = Compiler.Lower(a, "one", new ItemBlock { Name = "One", Was = "old" }).Handle;
            var two = Compiler.Lower(b, "two", new ItemBlock { Name = "Two", Was = "a.old" }).Handle;
            a.Handles[one.Name] = one; b.Handles[two.Name] = two;
            Assert.NotEqual(one.Guid, two.Guid);
            native.Catalog(one.GameName, one.Guid);
            ContainerRuntime.Forget(a.Id);
            var label = id + ".a.old";
            var progress = new FilledContainers.FillProgress { Stacks = [new(label, 2)] };
            var error = Assert.Throws<InvalidOperationException>(() => ContainerRuntime.CaptureItemGuids(progress));
            Assert.Contains("unambiguous native GUID", error.Message, StringComparison.Ordinal);
            Assert.Empty(progress.ItemGuids);
            Assert.Equal(0, progress.Next);
            Assert.Equal(label, Assert.Single(progress.Stacks).Item);
        }
        finally { RemoveAliasMod(a); RemoveAliasMod(b); }
    }

    [Fact]
    public void A_save_after_native_commit_before_callback_does_not_grant_the_stack_again()
    {
        using var native = new InventoryStub();
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2)] };
        book.Pending["spot"] = progress;
        Task<ItemsResult>? transaction = Items.Give(new EntityId(42), new ItemId(24), 2);
        var task = transaction;
        native.Commit();
        Assert.False(transaction.IsCompleted);
        book.BeforeSnapshot = () => ContainerRuntime.Advance(book, "spot", progress, ref transaction, _ => throw new Exception("snapshot submitted a request"), false, out _);
        var restored = JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(book))!;
        Assert.Contains("spot", restored.Filled.Keys);
        Assert.Empty(restored.Pending);
        Assert.False(task.IsCompleted); // observing a commit never dispatches callbacks or completes tasks from serialization
        native.Deliver();
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void A_save_inside_an_item_event_sees_the_other_committed_results_in_its_callback_batch()
    {
        using var native = new InventoryStub();
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2)] };
        book.Pending["spot"] = progress;
        var first = Items.Give(new EntityId(41), new ItemId(24), 2);
        Task<ItemsResult>? transaction = Items.Give(new EntityId(42), new ItemId(24), 2);
        book.BeforeSnapshot = () => ContainerRuntime.Advance(book, "spot", progress, ref transaction, _ => throw new Exception("snapshot submitted a request"), false, out _);
        FilledContainers? restored = null;
        using var subscription = Events.On<ItemsGivenEvent>(e =>
        {
            if (e.Target.Value != 41) return;
            Assert.False(first.IsCompleted);
            Assert.True(Items.TryGetResult(first, out var own)); // InFlight has already removed this request before posting its event
            Assert.Equal(2, own.Done);
            restored = JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(book));
        });
        native.Commit();
        native.Deliver();
        Assert.NotNull(restored);
        Assert.Contains("spot", restored.Filled.Keys);
        Assert.Empty(restored.Pending);
    }

    [Fact]
    public void Older_v3_tables_are_detected_before_default_loot_can_award_any_items()
    {
        using var native = new InventoryStub(peekAvailable: false);
        Assert.False(Items.ResultPeekAvailable);
        var transaction = Items.Give(new EntityId(42), new ItemId(24), 2);
        native.Commit();
        Assert.False(Items.TryGetResult(transaction, out _));
        native.Deliver();
        Assert.True(Items.TryGetResult(transaction, out var completed));
        Assert.Equal(2, completed.Done); // normal give/take and WP13's prefix remain usable on that older runtime
    }

    [Fact]
    public void Uncommitted_requests_are_never_journaled_as_inventory_changes()
    {
        using var native = new InventoryStub();
        var transaction = Items.Give(new EntityId(42), new ItemId(24), 2);
        Assert.True(Items.ResultPeekAvailable);
        Assert.False(Items.TryGetResult(transaction, out _));
        Assert.False(transaction.IsCompleted);
    }

    [Fact]
    public void Reopening_the_same_world_rebinds_pending_progress_instead_of_leaving_old_work_active()
    {
        var type = typeof(ContainerRuntime);
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var active = (System.Collections.IDictionary)type.GetField("Active", flags)!.GetValue(null)!;
        var plans = (System.Collections.IDictionary)type.GetField("Plans", flags)!.GetValue(null)!;
        var bookMethod = type.GetMethod("Book", flags)!;
        var template = Guid.NewGuid();
        var mod = Mod("pending-session");
        var planType = type.GetNestedType("Plan", BindingFlags.NonPublic)!;
        var workType = type.GetNestedType("Work", BindingFlags.NonPublic)!;
        var oldBook = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2)] };
        oldBook.Pending["spot"] = progress;
        try
        {
            ContainerRuntime.TestBook = oldBook;
            bookMethod.Invoke(null, [null]);
            var plan = Activator.CreateInstance(planType, [mod.Id, "cache", template, new LootPlan { Label = "cache", Entries = [] }])!;
            plans[template] = plan;
            active["spot"] = Activator.CreateInstance(workType, [plan, new EntityId(42), progress]);
            ContainerRuntime.TestBook = JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(oldBook))!;
            bookMethod.Invoke(null, [null]);
            Assert.Empty(active);
            Assert.Equal(0, ContainerRuntime.TestBook.Pending["spot"].Next);
        }
        finally { active.Clear(); plans.Remove(template); ContainerRuntime.TestBook = null; }
    }

    [Fact]
    public void Previously_filled_containers_do_not_permanently_hide_matches_after_the_first_4096()
    {
        var template = Guid.NewGuid();
        var all = Enumerable.Range(1, 4097).Select(i => new EntityId((uint)i)).ToArray();
        var seen = new HashSet<EntityId>(all.Take(4096));
        for (var search = 0; search < 12 && !seen.Contains(all[^1]); search++)
            foreach (var entity in ContainerRuntime.Find(template, (_, max) => all.Take(max).ToArray())) seen.Add(entity);
        Assert.Contains(all[^1], seen);
    }

    [Fact]
    public void Discovery_growth_stays_within_the_native_limit_and_makes_only_one_query_per_search()
    {
        var template = Guid.NewGuid();
        var calls = 0;
        var sizes = new List<int>();
        var full = new RepeatedEntities();
        for (var search = 0; search < 20; search++)
            ContainerRuntime.Find(template, (_, max) => { calls++; sizes.Add(max); full.Count = max; return full; });
        Assert.Equal(20, calls);
        Assert.Equal(4096, sizes[0]);
        Assert.Equal(1 << 20, sizes[^1]);
        Assert.All(sizes, size => Assert.InRange(size, 4096, 1 << 20));
    }

    private sealed class RepeatedEntities : IReadOnlyList<EntityId>
    {
        public int Count { get; set; }
        public EntityId this[int index] => new((uint)index + 1);
        public IEnumerator<EntityId> GetEnumerator() => Enumerable.Range(1, Count).Select(i => new EntityId((uint)i)).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // ---- the lid ----

    [Fact]
    public void A_lid_that_does_not_move_takes_the_toggle_animation_off()
    {
        var j = Lower(new ContainerBlock { Name = "Barrel", Lid = new LidBlock { Moves = false } });
        Assert.Collection(Names(Template(j)["removeComponents"]), n => Assert.Equal("ToggleAnimation", n));
        Assert.False((bool)j["furniture"]!["lid"]!["moves"]!);
    }

    [Fact]
    public void A_lid_with_a_graph_and_states_sets_the_toggle_animation_on_the_clone()
    {
        var graph = Guid.Parse("be434836-4c77-4c86-bee0-a49bc5992800");
        var open = Guid.Parse("11111111-0000-4000-8000-000000000001");
        var j = Lower(new ContainerBlock { Name = "Chest", Lid = new LidBlock { Graph = graph, OpenState = open } });
        var f = Fields(j);
        Assert.Equal(graph.ToString(), (string?)f["AnimationGraphResourceComponent2.animationGraph"]);
        Assert.Equal(open.ToString(), (string?)f["ToggleAnimation.animationStateOn"]);
        Assert.Equal(LidBlock.Shut.ToString(), (string?)f["ToggleAnimation.animationStateOff"]);       // a state left out is the game's
        Assert.Contains("ToggleAnimation", Names(Template(j)["addComponents"]));
    }

    [Fact]
    public void A_lid_that_says_nothing_leaves_the_starters_alone()
    {
        var j = Lower(new ContainerBlock { Name = "Chest", Lid = new LidBlock() });
        Assert.DoesNotContain("ToggleAnimation.animationStateOn", Fields(j).Select(x => x.Key));
        Assert.Null(Template(j)["removeComponents"]);
    }

    [Fact]
    public void A_lid_that_makes_no_sense_is_refused_by_field()
    {
        Assert.Equal("lid", Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "x", Lid = new LidBlock { Moves = false, Graph = Guid.NewGuid() } })).Field);
        Assert.Equal("lid.openState", Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "x", Lid = new LidBlock { OpenState = Guid.Empty } })).Field);
    }

    // ---- the door ----

    [Theory]
    [InlineData(DoorSide.Both, "bcf39dc8-d81e-450a-a77d-3b626d4106c4", "03deda2f-3d10-4127-96bb-12d438533e4f")]
    [InlineData(DoorSide.Front, "bcf39dc8-d81e-450a-a77d-3b626d4106c4", "bcf39dc8-d81e-450a-a77d-3b626d4106c4")]
    [InlineData(DoorSide.Back, "03deda2f-3d10-4127-96bb-12d438533e4f", "03deda2f-3d10-4127-96bb-12d438533e4f")]
    public void A_door_swings_the_way_it_says(DoorSide opens, string front, string back)
    {
        var f = Fields(Lower(new DoorBlock { Name = "Door", Opens = opens }));
        Assert.Equal(front, (string?)f["DirectionalInteraction.frontInteraction.animationState"]);
        Assert.Equal(back, (string?)f["DirectionalInteraction.backInteraction.animationState"]);
    }

    [Fact]
    public void A_door_that_says_nothing_of_its_swing_keeps_the_starters()
    {
        var f = Fields(Lower(new DoorBlock { Name = "Door", OnInteract = new InteractBlock { Verb = InteractVerb.Use } }));
        Assert.DoesNotContain("DirectionalInteraction.frontInteraction.animationState", f.Select(x => x.Key));
    }

    [Fact]
    public void A_doors_sides_are_in_what_it_became_and_wrong_values_are_refused()
    {
        var j = Lower(new DoorBlock { Name = "Door", Sides = DoorSide.Front, Opens = DoorSide.Back })["furniture"]!;
        Assert.Equal("front", (string?)j["sides"]);
        Assert.Equal("back", (string?)j["opens"]);
        Assert.Equal("sides", Assert.Throws<BlockError>(() => Lower(new DoorBlock { Name = "x", Sides = (DoorSide)9 })).Field);
        Assert.Equal("opens", Assert.Throws<BlockError>(() => Lower(new DoorBlock { Name = "x", Opens = (DoorSide)9 })).Field);
    }

    [Fact]
    public void The_side_of_a_door_follows_the_doors_turn()
    {
        // the side a guest is on is decided by the door's yaw: a door turned half a circle swaps front and back (Spawn.Inspect needs a game, so the geometry is checked here)
        static DoorSide Side(double yawDeg, double gx, double gz)
        {
            var yaw = yawDeg * Math.PI / 180;
            return gx * Math.Sin(yaw) + gz * Math.Cos(yaw) >= 0 ? DoorSide.Front : DoorSide.Back;
        }
        Assert.Equal(DoorSide.Front, Side(0, 0, 1));
        Assert.Equal(DoorSide.Back, Side(0, 0, -1));
        Assert.Equal(DoorSide.Back, Side(180, 0, 1));
        Assert.Equal(DoorSide.Front, Side(90, 1, 0));
    }

    // ---- several colliders ----

    private static PropBlock Table(params BoxCollider[] boxes) => new() { Name = "Table", Visual = new VisualBlock { Model = Guid.Empty.ToString() }, Colliders = boxes };

    [Fact]
    public void Two_boxes_are_two_shapes_of_the_templates_collider_and_the_template_grows_its_list()
    {
        var j = Lower(Table(new BoxCollider { Size = new Vector3(2, 0.2f, 1), Center = new Vector3(0, 1, 0) }, new BoxCollider { Size = new Vector3(0.2f, 1, 0.2f), Center = new Vector3(0.8f, 0.5f, 0.6f) }));
        var f = Fields(j);
        const string g = "ColliderResourceComponent.colliders[0].dataArray";
        Assert.Equal(1.0, (double)f[$"{g}[0].shape.halfSize.x"]!, 5);
        Assert.Equal(0.1, (double)f[$"{g}[1].shape.halfSize.x"]!, 5);
        Assert.Equal(0.8, (double)f[$"{g}[1].offset.x"]!, 5);
        Assert.Equal(2, (int)Template(j)["growArrays"]![g]!);
        Assert.Equal(2, (int)Template(j)["growArrays"]!["ColliderResourceComponent.colliders[0].joltObjects[0].shapeIndexList"]!);
        Assert.Equal(0, (int)f["ColliderResourceComponent.colliders[0].joltObjects[0].shapeIndexList[0]"]!);
        Assert.Equal(1, (int)f["ColliderResourceComponent.colliders[0].joltObjects[0].shapeIndexList[1]"]!);
        // one hash for the compound shape, and a different one for the cursor's bounding box
        var hash = (double)f["ColliderResourceComponent.colliders[0].joltObjects[0].hash"]!;
        Assert.NotEqual(hash, (double)j["itemDataRules"]!["equipment.placementColliders[0].joltObjects[0].hash"]!);
        Assert.Equal(2, j["collider"]!["boxes"]!.AsArray().Count);
        Assert.Equal(1.0, D(j["collider"]!["half"]![0]), 5);
        Assert.Equal(0.6, D(j["collider"]!["half"]![2]), 5);       // z from -0.5 to 0.7
    }

    [Fact]
    public void One_box_in_a_list_makes_no_longer_list_and_the_hash_follows_the_geometry()
    {
        var one = Lower(Table(new BoxCollider { Size = Vector3.One }));
        Assert.Null(Template(one)["growArrays"]);
        var a = Lower(Table(new BoxCollider { Size = Vector3.One }, new BoxCollider { Size = new Vector3(2, 1, 1) }));
        var b = Lower(Table(new BoxCollider { Size = Vector3.One }, new BoxCollider { Size = new Vector3(3, 1, 1) }));
        const string h = "ColliderResourceComponent.colliders[0].joltObjects[0].hash";
        Assert.NotEqual((double)Fields(a)[h]!, (double)Fields(b)[h]!);
        var twin = Lower(Table(new BoxCollider { Size = Vector3.One }, new BoxCollider { Size = new Vector3(2, 1, 1) }), "twin");
        Assert.NotEqual((double)Fields(a)[h]!, (double)Fields(twin)[h]!); // identical geometry still belongs to a different template
    }

    [Theory]
    [InlineData("both", "colliders")]
    [InlineData("none", "colliders")]
    [InlineData("many", "colliders")]
    [InlineData("size", "colliders[1].size")]
    [InlineData("far", "colliders[0].size")]
    [InlineData("center", "colliders[0].center")]
    [InlineData("ceiling", "colliders")]
    [InlineData("together", "colliders")]
    public void Wrong_colliders_are_refused_by_field(string what, string field)
    {
        var box = new BoxCollider { Size = Vector3.One };
        PropBlock b = what switch
        {
            "both" => Table(box) with { Collider = box },
            "none" => Table(),
            "many" => Table([.. Enumerable.Repeat(box, 17)]),
            "size" => Table(box, new BoxCollider { Size = new Vector3(1, 0, 1) }),
            "far" => Table(new BoxCollider { Size = new Vector3(40, 1, 1) }),
            "center" => Table(new BoxCollider { Size = Vector3.One, Center = new Vector3(float.NaN, 0, 0) }),
            "ceiling" => Table(box, box) with { Placement = new PlacementBlock { On = Surface.Ceiling } },
            "together" => Table(new BoxCollider { Size = new Vector3(30, 1, 1), Center = new Vector3(-10, 0, 0) }, new BoxCollider { Size = new Vector3(30, 1, 1), Center = new Vector3(10, 0, 0) }),
            _ => throw new ArgumentException(what),
        };
        var e = Assert.Throws<BlockError>(() => Lower(b, "bad"));
        Assert.Equal(field, e.Field);
    }

    [Fact]
    public void Colliders_of_a_station_or_a_game_items_look_are_refused_as_a_collider_is()
    {
        var box = new BoxCollider { Size = Vector3.One };
        Assert.Equal("colliders", Assert.Throws<BlockError>(() => Lower(new StationBlock { Name = "x", Colliders = [box] })).Field);
        Assert.Equal("colliders", Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "x", Colliders = [box] })).Field);
        Assert.Equal("colliders", Assert.Throws<BlockError>(() => Lower(new PropBlock { Name = "x", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" }, Colliders = [box] })).Field);
    }

    // ---- crafting props ----

    private static string Report(string name, string status, int index = -1) =>
        new JsonObject { ["blocks"] = new JsonArray(new JsonObject { ["name"] = name, ["kind"] = "craftingProp", ["status"] = status, ["index"] = index }) }.ToJsonString();

    [Fact]
    public void A_prop_in_the_table_is_a_crafting_prop_by_the_id_of_its_guid()
    {
        var mod = Mod("furniture-provides");
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "registered", 51);
        var j = Lower(new StationBlock { Name = "Cold Forge", Provides = true }, "cold-forge", mod);
        var guid = Compiler.CraftingPropGuid("furniture-provides", "cold-forge");
        Assert.Equal(BlockIds.Fnv(guid), (uint)Fields(j)["RequiredCraftingProp.craftingPropId"]!);
        Assert.Contains("RequiredCraftingProp", Names(Template(j)["addComponents"]));
        Assert.Equal(guid.ToString(), (string?)j["provides"]!["guid"]);
        Assert.Equal(51, (int)j["provides"]!["index"]!);
    }

    [Fact]
    public void A_prop_the_table_does_not_have_yet_is_made_from_the_next_start()
    {
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "pending");
        var j = Lower(new StationBlock { Name = "Cold Forge", Provides = true }, "cold-forge", Mod("furniture-pending"));
        Assert.Null(j["templates"]);       // no template, no item: nothing the game could place with an id it can't read
        Assert.Equal("pending", (string?)j["provides"]!["status"]);
    }

    [Fact]
    public void A_service_that_keeps_no_crafting_props_makes_nothing_and_says_so()
    {
        var j = Lower(new PropBlock { Name = "Lamp", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" }, Provides = true }, "lamp", Mod("furniture-off"));
        Assert.Null(j["templates"]);
        Assert.Equal("off", (string?)j["provides"]!["status"]);
    }

    [Fact]
    public void A_crafting_prop_asks_for_clients_with_the_mod()
    {
        var mod = Mod("furniture-clients");
        mod.ManifestClients = "kiln";
        var e = Assert.Throws<BlockError>(() => Lower(new StationBlock { Name = "x", Provides = true }, "x", mod));
        Assert.Equal("provides", e.Field);
    }

    [Fact]
    public void The_table_is_told_the_blocks_key_item_and_name()
    {
        string? sent = null;
        WorldTablesNative.TestDefine = d => { sent = d; return Report("cold-forge", "registered", 51); };
        Lower(new StationBlock { Name = "Cold Forge", Provides = true, Was = "old-forge" }, "cold-forge", Mod("furniture-sent"));
        var doc = JsonNode.Parse(sent!)!;
        var block = doc["craftingProps"]!["cold-forge"]!;
        Assert.Equal("old-forge", (string?)block["key"]);                                         // ids follow from the key, so a rename keeps them
        Assert.Equal(BlockIds.Derive("furniture-sent", "item", "old-forge").ToString(), (string?)block["item"]);
        Assert.Equal("Cold Forge", (string?)block["name"]);
    }

    [Fact]
    public void A_recipe_that_needs_a_prop_carries_its_id_and_place()
    {
        var mod = Mod("furniture-needs");
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "registered", 51);
        WorldTablesNative.TestCraftingPropIndex = g => g == Compiler.CraftingPropGuid("furniture-needs", "cold-forge") ? 51 : -1;
        var forge = Add(mod, "cold-forge", new StationBlock { Name = "Cold Forge", Provides = true });
        var j = Lower(new ItemBlock { Name = "Frost Ingot", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 2 }, NeedsProps = [forge] } }, "frost-ingot", mod);
        var need = j["needsProps"]![0]!;
        Assert.Equal($"0x{BlockIds.Fnv(Compiler.CraftingPropGuid("furniture-needs", "cold-forge")):x8}", (string?)need["id"]);
        Assert.Equal(51, (int)need["index"]!);
        Assert.NotNull(j["recipes"]);
    }

    [Fact]
    public void An_automated_station_refuses_nearby_props_before_a_recipe_can_be_made()
    {
        var recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 }, Seconds = 3, Output = "Material_T1_Wood_Logs", NeedsProps = ["table"] };
        var error = Assert.Throws<BlockError>(() => Lower(new StationBlock { Name = "Forge", Kind = StationKind.Forge, Recipes = [recipe] }));
        Assert.Equal("recipes[0].needsProps", error.Field);
        Assert.Contains("manual station", error.Message, StringComparison.Ordinal);
        var item = Assert.Throws<BlockError>(() => Lower(new ItemBlock { Name = "Logs", Recipe = recipe with { Output = null, Station = StationKind.Forge.Workshop } }));
        Assert.Equal("recipe.needsProps", item.Field);
        var mod = Mod("factory-prop-recipe");
        var forge = Add(mod, "forge", new StationBlock { Name = "Forge", Kind = StationKind.Forge });
        Assert.Equal("recipe.needsProps", Assert.Throws<BlockError>(() => Lower(new ItemBlock { Name = "Logs", Recipe = recipe with { Output = null, Station = forge.ToString() } }, mod: mod)).Field);
    }

    [Fact]
    public void A_manual_workbench_recipe_accepts_the_props_mask()
    {
        var mod = Mod("manual-prop-recipe");
        WorldTablesNative.TestDefine = _ => Report("table", "registered", 51);
        WorldTablesNative.TestCraftingPropIndex = _ => 51;
        var table = Add(mod, "table", new PropBlock { Name = "Table", Visual = new VisualBlock { Like = "Prop_Decoration_Comfort1_T0_Table_Medium" }, Provides = true });
        var j = Lower(new StationBlock { Name = "Bench", Kind = StationKind.Workbench, Recipes = [new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 }, Output = "Material_T1_Wood_Logs", NeedsProps = [table] }] }, mod: mod);
        Assert.Equal(51, (int)j["needsProps"]![0]!["index"]!);
        Assert.Single(j["recipes"]!.AsArray());
        Assert.Equal(51, (int)j["recipes"]![0]!["props"]![0]!["index"]!);
    }

    [Fact]
    public void A_recipe_that_needs_a_prop_the_table_does_not_have_waits_for_it()
    {
        var mod = Mod("furniture-needs-wait");
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "registered", 51);
        WorldTablesNative.TestCraftingPropIndex = _ => -1;
        Add(mod, "cold-forge", new StationBlock { Name = "Cold Forge", Provides = true });
        var j = Lower(new ItemBlock { Name = "Frost Ingot", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 2 }, NeedsProps = ["cold-forge"] } }, "frost-ingot", mod);
        Assert.Null(j["recipes"]);
        Assert.Contains("no place", (string?)j["recipeWaits"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_recipe_that_needs_what_is_no_crafting_prop_is_refused_by_field()
    {
        var mod = Mod("furniture-needs-bad");
        Add(mod, "plain-lamp", new PropBlock { Name = "Lamp", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" } });
        Add(mod, "scrap-pile", Scrap);
        ItemBlock Item(PropRef p) => new() { Name = "Thing", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 1 }, NeedsProps = [p] } };
        var plain = Assert.Throws<BlockError>(() => Lower(Item("plain-lamp"), "a", mod));
        Assert.Equal("recipe.needsProps[0]", plain.Field);
        Assert.Contains("provides", plain.Message, StringComparison.Ordinal);
        Assert.Equal("recipe.needsProps[0]", Assert.Throws<BlockError>(() => Lower(Item("scrap-pile"), "b", mod)).Field);
        Assert.Equal("recipe.needsProps[0]", Assert.Throws<BlockError>(() => Lower(Item("nothing-here"), "c", mod)).Field);
        var many = new ItemBlock { Name = "Thing", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 1 }, NeedsProps = [.. Enumerable.Repeat((PropRef)"plain-lamp", 9)] } };
        Assert.Equal("recipe.needsProps", Assert.Throws<BlockError>(() => Lower(many, "d", mod)).Field);
    }
}
