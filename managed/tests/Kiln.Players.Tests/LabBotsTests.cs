// SPDX-License-Identifier: MIT
#pragma warning disable KILNX001  // LabBots is lab-only API
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace Kiln.PlayerTests;

/// <summary>Kiln.LabBots without a game: the wrapper over kiln.labbot's table (kiln_labbot.h) on a fake table: the
/// request it sends, the info it reads back, and how each answer becomes a result or an exception. The bots themselves
/// run in a lab (mods/tests/labbot-test, tools/labbot).</summary>
[Collection("labbot-table")]
public sealed unsafe class LabBotsTests : IDisposable
{
    // ---- the fake table ----
    private static int _rc;
    private static string _name = "";
    private static uint _group, _groupIndex, _flags, _leaveId, _leaveFlags;
    private static ulong _owner;
    private static readonly LabBotNative.Table* Table = MakeTable();

    private static LabBotNative.Table* MakeTable()
    {
        var t = (LabBotNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(LabBotNative.Table));
        ((uint*)t)[0] = (uint)sizeof(LabBotNative.Table);  // KilnServiceHeader {size, version}
        ((uint*)t)[1] = 1;
        t->StatusFn = &FakeStatus;
        t->Join = &FakeJoin;
        t->Leave = &FakeLeave;
        t->InfoFn = &FakeInfo;
        t->List = &FakeList;
        return t;
    }

    private static void Put(byte* dst, int cap, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        var n = Math.Min(b.Length, cap - 1);
        for (var i = 0; i < n; i++) dst[i] = b[i];
        dst[n] = 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeStatus(LabBotNative.Status* s)
    {
        if (s->Size < 224) return 1;
        s->Available = _rc == 3 ? 0u : 1u;
        s->Bots = 2;
        s->Groups = 4;
        s->Publishes = 77;
        Put(s->Why, 200, _rc == 3 ? "lab servers only: needs kiln.json policy \"any\"" : "");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeJoin(ulong owner, LabBotNative.JoinRequest* rq, uint* id)
    {
        _owner = owner;
        var name = *(byte**)((byte*)rq + 8);  // KilnLabBotJoin.name {ptr, len}
        var len = *(nuint*)((byte*)rq + 16);
        _name = Encoding.UTF8.GetString(name, (int)len);
        _group = rq->Group;
        _groupIndex = rq->GroupIndex;
        _flags = rq->Flags;
        if (rq->Size != 56) return 1;
        *id = 5;
        return _rc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeLeave(uint id, uint flags)
    {
        _leaveId = id;
        _leaveFlags = flags;
        return _rc;
    }

    private static void Fill(LabBotNative.Info* i, uint id, uint phase, string name)
    {
        i->Id = id;
        i->Phase = phase;
        i->Slot = 15;
        i->Machine = 0x55 << 7 | 16;
        i->Player = 0x55 << 6 | 15;
        i->Entity = phase == 5 ? 16u : 0u;
        i->RecordState = 3;
        i->Permissions = 0x3f;
        i->Group = 0x1234;
        i->SavesAcked = 2;
        i->Account = 0x1122334455667788;
        for (var k = 0; k < 16; k++) i->CharacterId[k] = (byte)(0xa0 + k);
        i->JoinedStep = 100;
        i->InGameStep = phase == 5 ? 140u : 0u;
        Put(i->Name, 40, name);
        Put(i->Reason, 200, phase == 8 ? "the game found no spawn position (record state 6)" : "in game");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeInfo(uint id, LabBotNative.Info* i)
    {
        if (i->Size < 328) return 1;
        if (id != 5) return 2;
        Fill(i, 5, 5, "Bot Alder");
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeList(LabBotNative.Info* o, uint cap, uint* count)
    {
        *count = 10;  // more than the wrapper's buffer: it reads what fits
        for (uint k = 0; k < cap; k++)
        {
            if (o[k].Size < 328) return 1;
            Fill(&o[k], 10 - k, k == 0 ? 8u : 5u, $"bot {10 - k}");
        }
        return 0;
    }

    public LabBotsTests()
    {
        _rc = 0;
        LabBotNative.UseTable(Table);
    }

    public void Dispose() => LabBotNative.UseTable(null);

    [Fact]
    public void TheLayoutsAreTheHeaders()
    {
        Assert.Equal(56, sizeof(LabBotNative.JoinRequest));
        Assert.Equal(328, sizeof(LabBotNative.Info));
        Assert.Equal(224, sizeof(LabBotNative.Status));
        Assert.Equal(48, sizeof(LabBotNative.Table));
        Assert.Equal(40, (int)Marshal.OffsetOf<LabBotNative.JoinRequest>(nameof(LabBotNative.JoinRequest.Account)));
        Assert.Equal(48, (int)Marshal.OffsetOf<LabBotNative.Info>(nameof(LabBotNative.Info.Account)));
        Assert.Equal(72, (int)Marshal.OffsetOf<LabBotNative.Info>(nameof(LabBotNative.Info.JoinedStep)));
        Assert.Equal(16, (int)Marshal.OffsetOf<LabBotNative.Status>(nameof(LabBotNative.Status.Publishes)));
    }

    [Fact]
    public void JoinSendsTheNameAndGroupAndReturnsTheId()
    {
        Assert.Equal(5, LabBots.Join("Bot Alder", LabBotGroup.Index, 2));
        Assert.Equal("Bot Alder", _name);
        Assert.Equal(2u, _group);
        Assert.Equal(2u, _groupIndex);
        Assert.Equal(0u, _flags);
        Assert.Equal(0ul, _owner);  // no mod calling: the core's owner
        Assert.Equal(5, LabBots.Join("Bot Ä"));
        Assert.Equal("Bot Ä", _name);
        Assert.Equal(0u, _group);  // admin by default
    }

    [Fact]
    public void JoinRefusalsBecomeExceptions()
    {
        _rc = 1;
        Assert.Throws<ArgumentException>(() => LabBots.Join("x/y"));
        _rc = 2;
        Assert.Contains("no room", Assert.Throws<InvalidOperationException>(() => LabBots.Join("Bot Birch")).Message);
        _rc = 3;
        var e = Assert.Throws<KilnUnavailableException>(() => LabBots.Join("Bot Birch"));
        Assert.Equal(LabBots.Capability, e.Capability);
        Assert.Contains("policy", e.Message);
        Assert.Throws<ArgumentNullException>(() => LabBots.Join(null!));
    }

    [Fact]
    public void AnInvalidArgumentSaysWhichRuleTheJoinBroke()
    {
        _rc = 1;  // kiln.labbot answers KILN_ERR_INVALID_ARGUMENT for each of these
        Assert.Contains("letters, digits", Assert.Throws<ArgumentException>(() => LabBots.Join("x/y")).Message);
        Assert.Contains("either end", Assert.Throws<ArgumentException>(() => LabBots.Join(" Bot")).Message);
        Assert.Contains("1 to 32", Assert.Throws<ArgumentException>(() => LabBots.Join(new string('a', 33))).Message);
        // FakeList: "bot 9" is in game
        Assert.Contains("a live bot", Assert.Throws<ArgumentException>(() => LabBots.Join("bot 9")).Message);
        // a valid name no live bot has: its character logged in earlier in this server session (lab p1, p2, s6)
        var e = Assert.Throws<ArgumentException>(() => LabBots.Join("Bot Alder"));
        Assert.Contains("logged in earlier in this server session", e.Message);
        // and the way out that works: a restart (lab g1-g3: after a logout, a new name stayed in the spawn search too)
        Assert.Contains("restart the lab", e.Message);
        Assert.DoesNotContain("join under a new name", e.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => LabBots.Join("Bot Alder", (LabBotGroup)7));
    }

    [Fact]
    public void LeaveSendsTheDropFlag()
    {
        LabBots.Leave(5);
        Assert.Equal((5u, 0u), (_leaveId, _leaveFlags));
        LabBots.Leave(5, drop: true);
        Assert.Equal(1u, _leaveFlags);
        _rc = 2;
        Assert.Throws<ArgumentException>(() => LabBots.Leave(9));
    }

    [Fact]
    public void InfoReadsEveryField()
    {
        var b = LabBots.Info(5)!;
        Assert.Equal(5, b.Id);
        Assert.Equal(LabBotPhase.InGame, b.Phase);
        Assert.True(b.InGame);
        Assert.False(b.Ended);
        Assert.Equal("Bot Alder", b.Name);
        Assert.Equal(15, b.Slot);
        Assert.Equal((uint)(0x55 << 7 | 16), b.Machine);
        Assert.Equal((uint)(0x55 << 6 | 15), b.Connection);
        Assert.Equal(new EntityId(16), b.Entity);
        Assert.Equal(3, b.RecordState);
        Assert.Equal(0x3fu, b.Permissions);
        Assert.Equal(0x1234u, b.Group);
        Assert.Equal(2, b.SavesAcked);
        Assert.Equal(0x1122334455667788ul, b.Account);
        Assert.Equal(new Guid([0xa0, 0xa1, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xab, 0xac, 0xad, 0xae, 0xaf]), b.CharacterId);
        Assert.Equal((100ul, 140ul), (b.JoinedStep, b.InGameStep));
        Assert.Equal("in game", b.Reason);
        Assert.Null(LabBots.Info(6));
    }

    [Fact]
    public void AllReadsWhatFitsNewestFirst()
    {
        var all = LabBots.All();
        Assert.Equal(LabBots.Max * 2, all.Count);
        Assert.Equal(10, all[0].Id);
        Assert.Equal(LabBotPhase.Failed, all[0].Phase);
        Assert.True(all[0].Ended);
        Assert.Contains("spawn position", all[0].Reason);
        Assert.Equal("bot 9", all[1].Name);
    }

    [Fact]
    public void StatusSaysWhy()
    {
        var s = LabBots.Status();
        Assert.True(s.Available);
        Assert.Equal((2, 4, 77ul, ""), (s.Bots, s.Groups, s.Publishes, s.Why));
        _rc = 3;
        Assert.Contains("policy", LabBots.Status().Why);
    }

    [Fact]
    public void WithoutKilnLabbotEveryMemberThrowsKilnUnavailable()
    {
        LabBotNative.UseTable(null);
        Assert.Equal("kiln.labbot", Assert.Throws<KilnUnavailableException>(() => LabBots.Status()).Capability);
        Assert.Throws<KilnUnavailableException>(() => LabBots.Join("Bot Alder"));
        Assert.Throws<KilnUnavailableException>(() => LabBots.Info(1));
        Assert.Throws<KilnUnavailableException>(() => LabBots.All());
    }
}
