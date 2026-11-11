// SPDX-License-Identifier: MIT
// Creative Mode (Nexus mod 146, a Flame mod) and Kiln's own creative mode in one game: the third-party mod hooks the same
// game systems (crafting and building payments, consumables, the map's fog, time of day, movement), so Kiln's creative
// features go idle while it runs. kiln-flame (a Kiln plugin, docs/kiln/engines/flame.md) publishes kiln.flame with
// running("creative"); without that plugin there is no service and Kiln's creative mode works as usual.
//
// A Creative Mode that cannot do its job does not count: from kiln-flame 1.1.0, kiln.flame symbols("creative") says
// what the folder the mod reads its own files from (<game>\mods\creative) holds for the running game build. With neither
// a symbol file for it (sigs\<build key>.json) nor recipes to resolve it from (symbols\), the mod switches its game
// features off by its own rule (the player's start of 2026-11-11, run from the Library: "No recipes at ...\symbols to
// resolve this build from: game features stay disabled"), and Kiln's creative mode stays available and says why. With
// recipes only, the mod may still resolve the build and hook the same functions: Kiln's stays idle, and every refusal
// says why and what to do.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class CreativeFlame
{
    public const string Service = "kiln.flame";
    public const uint Version = 1;
    public const string ModId = "creative";

    // kiln_flame.h KILN_FLAME_SYMBOLS_*
    public const uint SymbolsUnknown = 0, SymbolsFound = 1, SymbolsRecipes = 2, SymbolsNone = 3;

    private const string IdleNote = "Creative Mode (the Flame mod \"creative\") runs in this game, so Kiln's creative mode stays idle: " +
                                    "use Creative Mode's own menu";

    /// <summary>What a refused creative request and <c>/creative status</c> say while Creative Mode runs (no final
    /// period). With only recipes for this game build, also why its menu may not open and what to do then.</summary>
    public static string Note => Volatile.Read(ref _symbols) == SymbolsRecipes
        ? IdleNote + $". It has no symbol file for this game build ({_sigs}) and resolves the build from its recipes if it can; " +
          "if its menu does not open, it switched itself off until it is updated: switch it off in the Kiln profile to use Kiln's /creative"
        : IdleNote;

    /// <summary>Why Kiln's creative mode is available although Creative Mode was started (null otherwise): it has no
    /// symbols for this game build and switched its game features off.</summary>
    public static string? SwitchedOff => Volatile.Read(ref _state) == 2
        ? $"Creative Mode (the Flame mod \"creative\") was started, but it has no symbol file for this game build ({_sigs}) " +
          "and no recipes, so it switched its game features off: Kiln's creative mode is available. To use Creative Mode " +
          "instead, activate the profile again in the Kiln launcher (it places the mod in the game's mods folder, where it " +
          "reads its files) or update Creative Mode"
        : null;

    // mirror of native/plugins/flame/include/kiln_flame.h (kiln.flame v1, the four functions of kiln-flame 1.0.0)
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint> Count;
        public delegate* unmanaged[Cdecl]<uint, void*, int> Get;
        public delegate* unmanaged[Cdecl]<NativeStr, byte> Running;  // C bool
        public delegate* unmanaged[Cdecl]<byte> SteppedAside;
    }

    // ... and symbols(), from kiln-flame 1.1.0 (its header's size says whether it is there)
    [StructLayout(LayoutKind.Sequential)]
    private struct TableWithSymbols
    {
        public Table Base;
        public delegate* unmanaged[Cdecl]<NativeStr, NativeStr*, uint> Symbols;
    }

    // 0 not asked or not running, 1 running (stays: a mod that entered keeps its hooks), 2 started without symbols for
    // this build (stays: kiln-flame looked before the mod started)
    private static int _state;
    private static uint _symbols;
    private static string _sigs = "";

    /// <summary>Tests: replaces the service lookup (running).</summary>
    internal static Func<bool>? Probe;

    /// <summary>Tests: replaces symbols("creative") (state, sigs file).</summary>
    internal static Func<(uint State, string File)>? SymbolsProbe;

    /// <summary>Creative Mode runs (or may have entered) in this process and may be doing its job: Kiln's creative mode
    /// stays idle. False for one kiln-flame says has no symbols for this game build (<see cref="SwitchedOff"/>). Logged
    /// once.</summary>
    public static bool Running
    {
        get
        {
            var state = Volatile.Read(ref _state);
            if (state != 0) return state == 1;
            if (!Ask()) return false;
            var (symbols, file) = AskSymbols();
            _sigs = file;
            Volatile.Write(ref _symbols, symbols);
            if (symbols == SymbolsNone)
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                    RulesRuntime.Log(LogLevel.Info, $"creative: {SwitchedOff}.");
                return Volatile.Read(ref _state) == 1;
            }
            if (Interlocked.Exchange(ref _state, 1) != 1)
                RulesRuntime.Log(LogLevel.Info, $"creative: {Note}. Off while it runs: /creative on, the creative menu, taking items, " +
                                                "the time of day and the cycle, and kiln.rules' creative hooks.");
            return true;
        }
    }

    /// <summary>Tests: forget the sticky answer.</summary>
    internal static void Reset()
    {
        Volatile.Write(ref _state, 0);
        Volatile.Write(ref _symbols, SymbolsUnknown);
        _sigs = "";
    }

    private static bool Ask()
    {
        if (Probe is { } p) return p();
        try
        {
            var t = Services.Get<Table>(Service, Version);
            if (t == null || t->Running == null) return false;
            var id = "creative"u8;
            fixed (byte* s = id) return t->Running(new NativeStr { Ptr = s, Len = (nuint)id.Length }) != 0;
        }
        catch (Exception)
        {
            return false;  // no host (unit tests) or no such service
        }
    }

    private static (uint State, string File) AskSymbols()
    {
        if (SymbolsProbe is { } p) return p();
        try
        {
            var t = Services.Get<Table>(Service, Version);
            if (t == null || t->Header.Size < (uint)sizeof(TableWithSymbols)) return (SymbolsUnknown, "");
            var full = (TableWithSymbols*)t;
            if (full->Symbols == null) return (SymbolsUnknown, "");
            var id = "creative"u8;
            NativeStr file = default;
            uint state;
            fixed (byte* s = id) state = full->Symbols(new NativeStr { Ptr = s, Len = (nuint)id.Length }, &file);
            var path = file.Ptr == null || file.Len == 0 ? "" : System.Text.Encoding.UTF8.GetString(file.Ptr, checked((int)file.Len));
            return (state, path);
        }
        catch (Exception)
        {
            return (SymbolsUnknown, "");
        }
    }
}
