// SPDX-License-Identifier: MIT
// The review round of dev.27's recording fakes: each test pins a point where a fake differed from what its native table
// does for the same call (native/services/<service>/), named after the finding it closes.
using System.Runtime.InteropServices;
using TkFakes;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed unsafe class FakesReviewTests
{
    private const ulong AdaSteam = 76561198000001001, BoSteam = 76561198000001002;

    private static string Said(CommandResult r) => string.Join("\n", r.Error is null ? r.Output : r.Output.Append(r.Error));

    private static T* Table<T>(string service) where T : unmanaged => (T*)KitBackend.Instance.Service(service, 1);

    private static (KilnHarness Kiln, FakePlayer Ada, FakePlayer Bo) Start(bool fakesMod = true)
    {
        var kiln = new KilnHarness();
        try
        {
            if (fakesMod) kiln.Load<TkFakesMod>();
            var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan, position: new WorldPosition(0, 0, 0));
            var bo = kiln.Players.Join("Bo", BoSteam, position: new WorldPosition(1, 0, 0));
            kiln.Step(2);
            return (kiln, ada, bo);
        }
        catch
        {
            kiln.Dispose();
            throw;
        }
    }

    // 1 (HIGH): kiln.combat's status takes a caller's older, shorter record (native t_status: at least up to `falls`,
    // and it copies only the size asked for).
    [Fact]
    public void Finding01_CombatStatusWritesNoMoreThanTheCallersRecord()
    {
        var (kiln, _, _) = Start(fakesMod: false);
        using var _k = kiln;
        const int shortSize = 144;  // offsetof(KilnCombatStatus, falls)
        Assert.Equal(shortSize, (int)Marshal.OffsetOf<CombatAbi.Status>(nameof(CombatAbi.Status.Falls)));
        var length = Marshal.SizeOf<CombatAbi.Status>() + 16;
        var mem = (nint)NativeMemory.Alloc((nuint)length);
        var tinyMem = (nint)NativeMemory.AllocZeroed((nuint)Marshal.SizeOf<CombatAbi.Status>());
        byte[] buffer;
        int rc, small;
        try
        {
            new Span<byte>((void*)mem, length).Fill(0xCD);
            ((CombatAbi.Status*)mem)->Size = shortSize;
            ((CombatAbi.Status*)tinyMem)->Size = shortSize - 8;
            rc = kiln.Run(() => Table<CombatAbi.Table>(CombatAbi.Service)->GetStatus((CombatAbi.Status*)mem));
            small = kiln.Run(() => Table<CombatAbi.Table>(CombatAbi.Service)->GetStatus((CombatAbi.Status*)tinyMem));
            buffer = new ReadOnlySpan<byte>((void*)mem, length).ToArray();
        }
        finally
        {
            NativeMemory.Free((void*)mem);
            NativeMemory.Free((void*)tinyMem);
        }
        Assert.Equal(FakeTables.Ok, rc);
        Assert.Equal((uint)shortSize, BitConverter.ToUInt32(buffer, 0));
        Assert.All(buffer.Skip(shortSize), b => Assert.Equal(0xCD, b));  // nothing past the caller's record
        Assert.Equal(FakeTables.InvalidArgument, small);
    }

    // 2 (HIGH): KilnItemInfo.name stays valid for the process: a harness that ends frees no name it handed out.
    [Fact]
    public void Finding02_AnItemNameStaysValidAfterItsHarnessEnded()
    {
        // KilnItemInfo.name, a (ptr, len) pair at its end
        var nameAt = (int)Marshal.OffsetOf<ItemsAbi.ItemInfo>(nameof(ItemsAbi.ItemInfo.Name));
        (nint Ptr, int Len) ReadName(KilnHarness kiln)
        {
            var info = (nint)NativeMemory.AllocZeroed((nuint)sizeof(ItemsAbi.ItemInfo));
            try
            {
                ((ItemsAbi.ItemInfo*)info)->Size = (uint)sizeof(ItemsAbi.ItemInfo);
                Assert.Equal(FakeTables.Ok, kiln.Run(() => Table<ItemsAbi.Table>(ItemsAbi.Service)->ItemAt(0, (ItemsAbi.ItemInfo*)info)));
                return (*(nint*)(info + nameAt), (int)*(nuint*)(info + nameAt + 8));
            }
            finally
            {
                NativeMemory.Free((void*)info);
            }
        }
        (nint Ptr, int Len) name;
        using (var kiln = new KilnHarness())
        {
            kiln.Items.AddItem("Material_T1_Twigs_Review", 50);
            name = ReadName(kiln);
        }
        // reuse every freed block of that size: a freed name would now read as 'X'
        var blocks = new List<nint>();
        for (var i = 0; i < 256; i++)
        {
            var b = (byte*)NativeMemory.Alloc((nuint)name.Len);
            new Span<byte>(b, name.Len).Fill((byte)'X');
            blocks.Add((nint)b);
        }
        try
        {
            Assert.Equal("Material_T1_Twigs_Review", System.Text.Encoding.UTF8.GetString((byte*)name.Ptr, name.Len));
        }
        finally
        {
            foreach (var b in blocks) NativeMemory.Free((void*)b);
        }
        // the same name in the next harness is the same interned text
        using var next = new KilnHarness();
        next.Items.AddItem("Material_T1_Twigs_Review", 50);
        Assert.Equal(name.Ptr, ReadName(next).Ptr);
    }

    // 3 (MEDIUM): a cancel drops the callback, not the transaction (native t_cancel erases g_callbacks only).
    [Fact]
    public void Finding03_AGiveOfAModThatUnloadedStillCommits()
    {
        var (kiln, ada, _) = Start();
        using var _k = kiln;
        var twigs = kiln.Items.AddItem("Material_T1_Twigs", 50);
        kiln.As(kiln.Mod("tk-fakes"), () => _ = Items.Give(ada.Entity, twigs, 5));
        kiln.Unload(kiln.Mod("tk-fakes"), checkCollected: false);  // Kiln.Items cancels the mod's tickets
        kiln.Step();
        Assert.Equal(5, ada.Inventory.Count(twigs));
        Assert.Equal((ItemsStatus.Done, 5), (kiln.Items.Calls[^1].Status, kiln.Items.Calls[^1].Done));
        Assert.Empty(kiln.Lines("tk-fakes", "event given:"));  // nobody is told
    }

    // 4 (MEDIUM): releasing an owner drops its requests not yet concluded, AsGameHit ones too (native t_release_owner
    // erases the owner's g_requests).
    [Fact]
    public void Finding04_AnAsGameHitOfAModThatUnloadedNeverApplies()
    {
        var (kiln, _, _) = Start();
        using var _k = kiln;
        var wolf = kiln.Entities.Create(new WorldPosition(3, 0, 0));
        kiln.Combat.SetHealth(wolf, 100, 100);
        kiln.As(kiln.Mod("tk-fakes"), () => _ = Combat.Damage(wolf, 10, options: new DamageOptions { AsGameHit = true }));
        kiln.Unload(kiln.Mod("tk-fakes"), checkCollected: false);
        kiln.Step(3);
        Assert.Equal(100, kiln.Combat.Health(wolf)!.Current);
        Assert.Equal(FakeHitState.Dropped, kiln.Combat.Hits[^1].State);
    }

    // 5 (MEDIUM): a death is what the game's death check finds at the next step (native g_dying, then actor_dead).
    [Fact]
    public void Finding05_ASetToZeroHealedBeforeTheStepIsNoDeath()
    {
        var (kiln, _, _) = Start();
        using var _k = kiln;
        var wolf = kiln.Entities.Create(new WorldPosition(3, 0, 0));
        kiln.Combat.SetHealth(wolf, 100, 100);
        kiln.Run(() =>
        {
            Combat.SetHealth(wolf, 0);
            Assert.True(Combat.GetHealth(wolf)!.Value.Dead);  // as the death check will leave it
            Combat.Heal(wolf, 100);
            Assert.False(Combat.GetHealth(wolf)!.Value.Dead);
        });
        kiln.Step(2);
        Assert.Empty(kiln.Lines("tk-fakes", "died:"));
        kiln.Run(() => Combat.SetHealth(wolf, 0));
        kiln.Step();
        Assert.Equal([$"died: {wolf.Value} killer 0 origin Other"], kiln.Lines("tk-fakes", "died:"));
    }

    // 6 (MEDIUM): a player's health is a component of that player's entity: the next player in the slot has none.
    [Fact]
    public void Finding06_ThePlayerWhoTakesAFreedSlotHasNoHealthOfTheLastOne()
    {
        var (kiln, ada, _) = Start();
        using var _k = kiln;
        kiln.Combat.SetHealth(ada.Entity, 50, 100);
        var slot = ada.Entity;
        ada.Leave();
        kiln.Step();
        var cy = kiln.Players.Join("Cy", 76561198000001003);
        kiln.Step();
        Assert.Equal(slot, cy.Entity);
        Assert.Null(kiln.Combat.Health(cy.Entity));
        Assert.Null(kiln.Run(() => Combat.GetHealth(cy.Entity)));
    }

    // 7 (MEDIUM): with chat.receive (or kiln.chat) off, players' lines reach no subscriber: the game's chat shows them.
    [Theory]
    [InlineData("chat.receive")]
    [InlineData("kiln.chat")]
    public void Finding07_SwitchedOffNoLineReachesTheHandlers(string row)
    {
        var (kiln, ada, bo) = Start();
        using var _k = kiln;
        kiln.Capabilities.Override(row, false);
        ada.Say("hello");
        kiln.Step();
        Assert.Empty(kiln.Lines("tk-fakes", "chat received:"));
        Assert.Contains("Ada: hello", bo.Chat.Select(l => l.Text));
        Assert.False(Assert.Single(kiln.Chat.Typed).Consumed);
    }

    // 8 (MEDIUM): give and take need the service's program (native submit: NONE or BROKEN is unavailable).
    [Fact]
    public void Finding08_GiveAndTakeNeedTheProgram()
    {
        var (kiln, ada, _) = Start();
        using var _k = kiln;
        var twigs = kiln.Items.AddItem("Material_T1_Twigs", 50);
        kiln.Capabilities.Override("items.program", false);
        var refused = kiln.Run(() => Items.Give(ada.Entity, twigs, 1));
        Assert.True(refused.IsCompleted);  // refused at once, not queued
        Assert.Equal(ItemsStatus.Unavailable, refused.Result.Status);
        Assert.Equal(0, kiln.Run(() => Items.Status())!.Program);  // KILN_ITEMS_PROGRAM_NONE
        kiln.Capabilities.ClearOverride("items.program");
        var accepted = kiln.Run(() => Items.Give(ada.Entity, twigs, 1));
        kiln.Capabilities.Override("items.program", false);  // switched off before it ran
        kiln.Step();
        Assert.Equal(ItemsStatus.Unavailable, accepted.Result.Status);
        Assert.Equal(0, ada.Inventory.Count(twigs));
    }

    // 9 (MEDIUM): find_inventories filters by kind only when a kind bit is set (native matches(): flags & 7).
    [Fact]
    public void Finding09_FindingInventoriesWithoutAKindFindsEveryKind()
    {
        var (kiln, ada, _) = Start();
        using var _k = kiln;
        var twigs = kiln.Items.AddItem("Material_T1_Twigs", 50);
        var chest = kiln.Entities.Create(new WorldPosition(2, 0, 0));
        kiln.Items.AddContainer(chest).Put(twigs, 3);
        var empty = kiln.Entities.Create(new WorldPosition(4, 0, 0));
        kiln.Items.AddContainer(empty);
        ada.Inventory.Put(twigs, 1);
        Assert.Equal([ada.Entity, chest], kiln.Run(() => Items.FindInventories(InventoryKinds.NonEmpty)));
        Assert.Contains(empty, kiln.Run(() => Items.FindInventories((InventoryKinds)0)));
        Assert.Contains(ada.Entity, kiln.Run(() => Items.FindInventories((InventoryKinds)0)));
        Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Items.FindInventories((InventoryKinds)16)));  // native: invalid argument
    }

    // 10 (MEDIUM): a new harness's clock is a new world's: Kiln.Time's day/night watch starts fresh.
    [Fact]
    public void Finding10_AFreshHarnessDoesNotCarryTheLastOnesNight()
    {
        using (var kiln = new KilnHarness())
        {
            kiln.Load<TkFakesMod>();
            kiln.Step();
            kiln.Time.Set(23);
            kiln.Step(26);  // Kiln.Time's poll saw the night
        }
        using var next = new KilnHarness();
        next.Load<TkFakesMod>();
        next.Step(30);  // 06:00 and later: day from the start
        Assert.Empty(next.Lines("tk-fakes", "day started"));
    }

    // 11 (LOW): a healing hit at the maximum stays at the maximum (native: 64-bit sums, clamped).
    [Fact]
    public void Finding11_AHealingHitAtTheMaximumDoesNotWrap()
    {
        var (kiln, _, _) = Start(fakesMod: false);
        using var _k = kiln;
        var e = kiln.Entities.Create();
        kiln.Combat.SetHealth(e, int.MaxValue, int.MaxValue);
        kiln.Combat.Hit(e, -1);
        kiln.Step();
        Assert.Equal(int.MaxValue, kiln.Combat.Health(e)!.Current);
    }

    // 12 (LOW): the program's state as kiln.items reports it: pending before the world, running once it ran.
    [Fact]
    public void Finding12_TheItemsProgramRunsOnceTheWorldStepped()
    {
        using var kiln = new KilnHarness();
        Assert.Equal(1, kiln.Run(() => Items.Status())!.Program);  // KILN_ITEMS_PROGRAM_PENDING
        kiln.Step(2);
        Assert.Equal(3, kiln.Run(() => Items.Status())!.Program);  // KILN_ITEMS_PROGRAM_RUNNING
    }

    // 13 (LOW): world_key needs room for its terminator and writes it (native t_world_key).
    [Fact]
    public void Finding13_TheWorldKeyIsTerminated()
    {
        var (kiln, _, _) = Start(fakesMod: false);
        using var _k = kiln;
        var mem = (nint)NativeMemory.Alloc(64 + 8);
        byte[] buffer;
        nuint len;
        int tight, roomy;
        try
        {
            new Span<byte>((void*)mem, 64).Fill(0xFF);
            var lenAt = mem + 64;
            tight = kiln.Run(() => Table<SpawnAbi.Table>(SpawnAbi.Service)->WorldKey((byte*)mem, 36, (nuint*)lenAt));
            roomy = kiln.Run(() => Table<SpawnAbi.Table>(SpawnAbi.Service)->WorldKey((byte*)mem, 37, (nuint*)lenAt));
            len = *(nuint*)lenAt;
            buffer = new ReadOnlySpan<byte>((void*)mem, 64).ToArray();
        }
        finally
        {
            NativeMemory.Free((void*)mem);
        }
        Assert.Equal(FakeTables.BufferTooSmall, tight);
        Assert.Equal(FakeTables.Ok, roomy);
        Assert.Equal((nuint)36, len);
        Assert.Equal(0, buffer[36]);
        Assert.Equal(kiln.World.Id.ToString("D"), System.Text.Encoding.UTF8.GetString(buffer, 0, 36));
    }
}
