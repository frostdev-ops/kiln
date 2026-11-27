// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using N = Kiln.SpeechNative;

namespace Kiln.TestKit;

/// <summary>One harness's NPC speech bubbles as a Kiln client would show them (KilnHarness.NpcSpeech). Guarded by its
/// own lock: Kiln.Npcs calls the table from any thread.</summary>
internal sealed class NpcSpeechFakeState
{
    internal readonly object Lock = new();
    internal readonly Dictionary<uint, (SpeechMode Mode, uint[] Tags)> Overrides = new();
    internal readonly Dictionary<uint, (uint Tag, long Until)> Says = new();
    internal readonly Dictionary<uint, string> Texts = new();
    // since 0.2.0-dev.32: the farewell and menu line overrides, and each override's pick (kind, seed)
    internal readonly Dictionary<(uint Npc, uint Kind), uint[]> Lines = new();
    internal readonly Dictionary<(uint Npc, uint Kind), (uint Pick, uint Seed)> Picks = new();
    internal int Calls;
}

/// <summary>The kit's fake of kiln.npcspeech (native/core/abi/services/kiln_npcspeech.h): set, say, text and clear_all
/// recorded per NPC, so a test reads what a Kiln client's bubble would show. Since 0.2.0-dev.31.16 (npc-speech).</summary>
internal sealed unsafe class NpcSpeechFake : IKitFake
{
    private static N.Table* _table;

    public string Service => N.Name;

    public void Register()
    {
        if (_table == null)
        {
            _table = (N.Table*)NativeMemory.AllocZeroed((nuint)sizeof(N.Table));
            _table->HeaderSize = (uint)sizeof(N.Table);
            _table->HeaderVersion = N.Version;
            _table->Set = &Set;
            _table->Say = &Say;
            _table->Text = &Text;
            _table->ClearAll = &ClearAll;
            _table->GetStatus = &Status;
            _table->SetLines = &SetLines;
            _table->Next = &Next;
            _table->Events = &Events;
            _table->DialogStatus = &DialogStatus;
        }
        KitBackend.Instance.RegisterTable(Service, (nint)_table);
        FakeTables.Features([N.BubbleCapability, N.VoiceCapability]);
    }

    public void Start(KilnHarness harness) { }
    public void BeforeStep(KilnHarness harness, ulong step) { }
    public void End(KilnHarness harness) { }

    public void AddCoverage(List<CoverageRow> rows) => rows.Add(new("NPC speech (since 0.2.0-dev.31.16)", CoverageKind.RecordingFake,
        [Service, N.BubbleCapability, N.VoiceCapability],
        "Kiln.Npcs on the kit's kiln.npcspeech: Npcs.Say, Npcs.Speech(...).Suppress / Replace / Clear with the owners' priorities, " +
        "and what a Kiln client's bubble of each NPC would be told (KilnHarness.NpcSpeech: ModeOf, LinesOf, Saying in a language, " +
        "since 0.2.0-dev.32 PickOf). " +
        "Not modelled: the game's bubble itself (which NPC is the player's target, the 5 m range, its random pick among the lines, " +
        "the voice-overs Kiln mutes) and the network: the kit is the host, nothing is sent."));

    internal static NpcSpeechFakeState? StateOf()
    {
        if (FakeTables.Active(N.Name) is not { } h || FakeTables.Off(N.BubbleCapability)) return null;
        return KitFakes.State<NpcSpeechFakeState>(h);
    }

    [UnmanagedCallersOnly]
    private static int Set(uint npc, uint mode, uint* tags, uint n)
    {
        try
        {
            if (StateOf() is not { } s) return FakeTables.Unavailable;
            if (npc == 0 || mode > 2 || (mode == 2 && (tags == null || n == 0 || n > 32))) return FakeTables.InvalidArgument;
            lock (s.Lock)
            {
                s.Calls++;
                if (mode == 0) s.Overrides.Remove(npc);
                else s.Overrides[npc] = ((SpeechMode)mode, mode == 2 ? new ReadOnlySpan<uint>(tags, (int)n).ToArray() : []);
            }
            return 0;
        }
        catch { return FakeTables.Internal; }
    }

    [UnmanagedCallersOnly]
    private static int SetLines(uint npc, uint kind, uint mode, uint* tags, uint n, uint pick, uint seed)
    {
        try
        {
            if (StateOf() is not { } s) return FakeTables.Unavailable;
            if (npc == 0 || kind > 2 || mode > 2 || pick > 4 || (kind != 0 && mode == 1) || (mode == 2 && (tags == null || n == 0 || n > 32)))
                return FakeTables.InvalidArgument;
            var t = mode == 2 ? new ReadOnlySpan<uint>(tags, (int)n).ToArray() : [];
            lock (s.Lock)
            {
                s.Calls++;
                if (kind == 0)
                {
                    if (mode == 0) s.Overrides.Remove(npc);
                    else s.Overrides[npc] = ((SpeechMode)mode, t);
                }
                else if (mode == 0) s.Lines.Remove((npc, kind));
                else s.Lines[(npc, kind)] = t;
                if (mode == 0) s.Picks.Remove((npc, kind));
                else s.Picks[(npc, kind)] = (pick, seed);
            }
            return 0;
        }
        catch { return FakeTables.Internal; }
    }

    // The kit is the host and has no bubbles: nothing reports an open, and a host's pick has nowhere to go.
    [UnmanagedCallersOnly]
    private static int Next(uint npc, uint kind, int line) => 0;

    [UnmanagedCallersOnly]
    private static int Events(N.Event* o, uint max, uint* n)
    {
        if (n != null) *n = 0;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int DialogStatus(N.DialogStatus* o)
    {
        if (o == null || o->Size < (uint)sizeof(N.DialogStatus)) return FakeTables.InvalidArgument;
        o->Resolved = 7;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int Say(uint npc, uint tag, uint ms)
    {
        try
        {
            if (StateOf() is not { } s) return FakeTables.Unavailable;
            if (npc == 0 || ms > 60_000) return FakeTables.InvalidArgument;
            lock (s.Lock)
            {
                s.Calls++;
                if (tag == 0 || ms == 0) s.Says.Remove(npc);
                else s.Says[npc] = (tag, Environment.TickCount64 + ms);
            }
            return 0;
        }
        catch { return FakeTables.Internal; }
    }

    [UnmanagedCallersOnly]
    private static int Text(uint tag, byte* json, nuint len)
    {
        try
        {
            if (StateOf() is not { } s) return FakeTables.Unavailable;
            if (tag == 0 || json == null || len == 0) return FakeTables.InvalidArgument;
            lock (s.Lock) s.Texts[tag] = System.Text.Encoding.UTF8.GetString(json, (int)len);
            return 0;
        }
        catch { return FakeTables.Internal; }
    }

    [UnmanagedCallersOnly]
    private static int ClearAll()
    {
        try
        {
            if (StateOf() is not { } s) return FakeTables.Unavailable;
            lock (s.Lock)
            {
                s.Overrides.Clear();
                s.Says.Clear();
                s.Lines.Clear();
                s.Picks.Clear();
            }
            return 0;
        }
        catch { return FakeTables.Internal; }
    }

    [UnmanagedCallersOnly]
    private static int Status(N.Status* o)
    {
        try
        {
            if (o == null || o->Size < (uint)sizeof(N.Status)) return FakeTables.InvalidArgument;
            if (StateOf() is not { } s) return FakeTables.Unavailable;
            lock (s.Lock)
            {
                o->Resolved = 15;
                o->Installed = 3;
                o->Overrides = (uint)s.Overrides.Count;
                o->Says = (uint)s.Says.Count(x => x.Value.Until > Environment.TickCount64);
                o->Texts = (uint)s.Texts.Count;
            }
            return 0;
        }
        catch { return FakeTables.Internal; }
    }
}
