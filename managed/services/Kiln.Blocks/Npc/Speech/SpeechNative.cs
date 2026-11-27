// SPDX-License-Identifier: MIT
// The native kiln.npcspeech table (native/core/abi/services/kiln_npcspeech.h). Field order and types must match the C header.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class SpeechNative
{
    public const string Name = "kiln.npcspeech";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, Unavailable = 3;
    /// <summary><see cref="Text"/>: the world's bound of distinct texts is reached (NpcSpeechEngine.MaxTexts).</summary>
    public const int TooManyTexts = -1;
    public const string BubbleCapability = "npcspeech.bubble";
    public const string VoiceCapability = "npcspeech.voice";
    /// <summary>Since 0.2.0-dev.32: the dialogue menu's farewell, its own line, and Kiln topics on talk pages.</summary>
    public const string FarewellCapability = "npcspeech.farewell", MenuLineCapability = "npcspeech.menu_line", TopicsCapability = "npcspeech.topics";

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Resolved, Installed, Overrides, Says, Texts;
        public ulong Bubbles, Changed, Muted;
    }

    /// <summary>KilnSpeechEvent: an open of a host-picked line (Type 0) or a pick of a Kiln topic (Type 1).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Npc;
        public byte Type, Kind;
        public ushort Reserved;
        public int Line;
        public uint Value;
    }

    /// <summary>KilnNpcDialogStatus (since 0.2.0-dev.32).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DialogStatus
    {
        public uint Size, Resolved, Installed, Lines, Topics, Queued;
        public ulong Opens, Farewells, MenuLines, TopicRows, Picks, Dropped;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public uint HeaderSize, HeaderVersion;
        public delegate* unmanaged<uint, uint, uint*, uint, int> Set;
        public delegate* unmanaged<uint, uint, uint, int> Say;
        public delegate* unmanaged<uint, byte*, nuint, int> Text;
        public delegate* unmanaged<int> ClearAll;
        public delegate* unmanaged<Status*, int> GetStatus;
        // ---- since 0.2.0-dev.32, appended (a 31.16-31.21 table ends here, at 48) ----
        public delegate* unmanaged<uint, uint, uint, uint*, uint, uint, uint, int> SetLines;
        public delegate* unmanaged<uint, uint, int, int> Next;
        public delegate* unmanaged<Event*, uint, uint*, int> Events;
        public delegate* unmanaged<DialogStatus*, int> DialogStatus;
        public delegate* unmanaged<uint, uint, int> MenuSay;
    }

    /// <summary>The table ends at the 31.21 members (48 bytes) in a fake or an older kiln-npcspeech.dll: no dev.32
    /// members then, and none that a table left null.</summary>
    private const int V1Size = 48;
    public const int EventOpen = 0, EventTopic = 1;
    public const byte PickChange = 0, PickRandom = 1, PickOrder = 2, PickSeeded = 3, PickHost = 4;

    private static bool Has(Table* t, int end) => t != null && t->HeaderSize >= end;

    private static Table* _t;
    private static bool _looked;

    /// <summary>Test seam: a table to use instead of the process's (null: look it up).</summary>
    internal static Func<nint>? TableHook;

    internal static void LookAgain()
    {
        if (_t == null) _looked = false;
    }

    /// <summary>The table, or null where kiln.npcspeech is not loaded (a dedicated server, an unknown client build).</summary>
    public static Table* T
    {
        get
        {
            if (TableHook is { } hook) return (Table*)hook();
            if (_t != null || _looked) return _t;
            try { _t = (Table*)BlocksBridge.ServiceTable(Name, Version); }
            catch (Exception) { _t = null; }
            _looked = _t != null || Lifecycle.IsServerRunning;
            return _t;
        }
    }

    public static bool Loaded => T != null;

    /// <summary>Without kiln.npcspeech, its sigdb features are lowered, and where sigdb never declared them (a dedicated
    /// server: the bubble is client data) they get a row that says why (Kiln.Npcs' members name them).</summary>
    internal static void ReportRows()
    {
        if (ServiceCaps.Probe(Name)) return;  // the service is here: sigdb's rows say what works
        ServiceCaps.LowerFeatures(Name, BubbleCapability, VoiceCapability, FarewellCapability, MenuLineCapability, TopicsCapability);
        var server = !Client.IsClient;
        foreach (var row in new[] { BubbleCapability, VoiceCapability, FarewellCapability, MenuLineCapability, TopicsCapability })
            if (Capabilities.Get(row).Reason == CapabilityReason.Unknown)
                ServiceCaps.Report(row, CapabilityState.Unavailable, server ? CapabilityReason.WrongSide : CapabilityReason.ServiceMissing,
                    server ? "the NPC speech bubble and dialogue menu are the game client's: a server sends Kiln.Npcs' decisions to its Kiln clients, which show them"
                           : "kiln.npcspeech is not here: this game shows no Kiln speech");
    }

    public static Status? Read()
    {
        var t = T;
        if (t == null || t->GetStatus == null) return null;
        var s = new Status { Size = (uint)sizeof(Status) };
        return t->GetStatus(&s) == Ok ? s : null;
    }

    /// <summary>Registers the text of a tag once per world (kiln.content keeps it).</summary>
    private static readonly HashSet<uint> Registered = new();

    public static int Text(uint tag, string json)
    {
        var t = T;
        if (t == null) return Unavailable;
        lock (Registered)
        {
            if (Registered.Contains(tag)) return Ok;
            if (Registered.Count >= NpcSpeechEngine.MaxTexts) return TooManyTexts;
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            int rc;
            fixed (byte* p = bytes) rc = t->Text(tag, p, (nuint)bytes.Length);
            if (rc == Ok) Registered.Add(tag);
            return rc;
        }
    }

    public static int Set(uint npc, SpeechMode mode, uint[] tags)
    {
        var t = T;
        if (t == null) return Unavailable;
        fixed (uint* p = tags) return t->Set(npc, (uint)mode, tags.Length == 0 ? null : p, (uint)tags.Length);
    }

    /// <summary>The dev.32 member set_lines is here (else the caller uses set() for greetings and nothing else).</summary>
    public static bool HasLines
    {
        get
        {
            var t = T;
            return Has(t, V1Size + 8) && t->SetLines != null;
        }
    }

    public static int SetLines(uint npc, NpcLineKind kind, SpeechMode mode, uint[] tags, uint pick, uint seed)
    {
        var t = T;
        if (!Has(t, V1Size + 8) || t->SetLines == null) return Unavailable;
        fixed (uint* p = tags) return t->SetLines(npc, (uint)kind, (uint)mode, tags.Length == 0 ? null : p, (uint)tags.Length, pick, seed);
    }

    public static int Next(uint npc, NpcLineKind kind, int line)
    {
        var t = T;
        return Has(t, V1Size + 16) && t->Next != null ? t->Next(npc, (uint)kind, line) : Unavailable;
    }

    /// <summary>What the bubbles and the dialogue menu reported since the last call (opens of host-picked lines, topic
    /// picks); empty where the table has no such member.</summary>
    public static Event[] DrainEvents()
    {
        var t = T;
        if (!Has(t, V1Size + 24) || t->Events == null) return [];
        var all = new List<Event>();
        var buf = stackalloc Event[64];
        for (var round = 0; round < 16; round++)
        {
            uint n = 0;
            if (t->Events(buf, 64, &n) != Ok || n == 0) break;
            for (var i = 0; i < n && i < 64; i++) all.Add(buf[i]);
            if (n < 64) break;
        }
        return all.ToArray();
    }

    public static int MenuSay(uint npc, uint tag)
    {
        var t = T;
        return Has(t, V1Size + 40) && t->MenuSay != null ? t->MenuSay(npc, tag) : Unavailable;
    }

    public static DialogStatus? ReadDialog()
    {
        var t = T;
        if (!Has(t, V1Size + 32) || t->DialogStatus == null) return null;
        var s = new DialogStatus { Size = (uint)sizeof(DialogStatus) };
        return t->DialogStatus(&s) == Ok ? s : null;
    }

    public static int Say(uint npc, uint tag, uint ms)
    {
        var t = T;
        return t == null ? Unavailable : t->Say(npc, tag, ms);
    }

    public static void ClearAll()
    {
        var t = T;
        if (t != null) t->ClearAll();
    }

    internal static void ForgetTexts()
    {
        lock (Registered) Registered.Clear();
    }
}
