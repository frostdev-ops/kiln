// SPDX-License-Identifier: MIT
// Kiln.Settings: the server's game settings (enshrouded_server.json "gameSettings"), read and changed while it runs.
// Managed face of native/core/abi/services/kiln_settings.h. Usage: docs/kiln/services/settings.md.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Kiln;

/// <summary>How a setting's value is stored.</summary>
public enum SettingKind
{
    /// <summary>On/off: <see cref="bool"/>.</summary>
    Bool = 1,
    /// <summary>A factor: <see cref="double"/> (a float32 in the game).</summary>
    Float = 2,
    /// <summary>A whole number: <see cref="long"/>.</summary>
    Int = 3,
    /// <summary>One of <see cref="SettingInfo.Options"/>: its name as a <see cref="string"/>.</summary>
    Enum = 4,
    /// <summary>A duration in nanoseconds (<see cref="long"/>), as enshrouded_server.json stores it.</summary>
    Duration = 5,
}

/// <summary>What Kiln knows about when a change takes effect.</summary>
[Flags]
public enum SettingFlags
{
    None = 0,
    /// <summary>The server uses a new value without a restart.</summary>
    Live = 0x01,
    /// <summary>Players' games read it too; they follow once the resent settings arrive.</summary>
    PlayersRead = 0x02,
    /// <summary>Applies to what happens next (a spawn, a death, a loot roll), not to the past.</summary>
    NextEvent = 0x04,
    /// <summary><see cref="SettingInfo.Min"/>/<see cref="SettingInfo.Max"/> are the server readme's documented range.</summary>
    DocumentedRange = 0x08,
    /// <summary>A field of this game build that Kiln does not know: writable only with <c>anyValue</c>.</summary>
    Unknown = 0x10,
    /// <summary>The value differs from the world's own value.</summary>
    Changed = 0x20,
    /// <summary>The live effect was observed in a Kiln lab.</summary>
    Verified = 0x40,
}

/// <summary>One game setting with its current value. Values are JSON-friendly: <see cref="bool"/>, <see cref="double"/>,
/// <see cref="long"/> (whole numbers and nanosecond durations) or <see cref="string"/> (option names).</summary>
public sealed record SettingInfo(
    int Index,
    string Name,
    string Group,
    SettingKind Kind,
    SettingFlags Flags,
    string Effect,
    object Value,
    object Initial,
    object? Default,
    double Min,
    double Max,
    IReadOnlyList<string> Options,
    int Offset)
{
    public bool Live => (Flags & SettingFlags.Live) != 0;
    public bool PlayersRead => (Flags & SettingFlags.PlayersRead) != 0;
    public bool Changed => (Flags & SettingFlags.Changed) != 0;
}

/// <summary>The result of <see cref="Settings.Set"/>: the value before and after (after = what the game holds now).</summary>
public sealed record SettingChange(string Name, object Before, object After, bool Changed);

/// <summary>State of kiln.settings and of the game's day/night clock.</summary>
public sealed record SettingsStatus(
    bool Ready,
    bool ResendsToPlayers,
    bool VerifiedSingleton,
    int Count,
    long Revision,
    long Sets,
    long ResendsRequested,
    long ResendsSent,
    string? Preset,
    TimeSpan? DayDuration,
    TimeSpan? NightDuration,
    TimeSpan? TimeOfDay,
    uint DaytimeChanges,
    string ConfigPath);

/// <summary>
/// The server's game settings (the <c>gameSettings</c> block of <c>enshrouded_server.json</c>: player, enemy and boss
/// factors, durability, starving, day/night length, tombstone mode, ...), read and changed live. A change applies to the
/// running world at once and is sent to every connected player; <see cref="Save"/> writes the live values into
/// <c>enshrouded_server.json</c> so they survive a restart.
/// </summary>
/// <remarks>Everything except <see cref="Available"/> and <see cref="Status"/> runs on the tick thread. The settings are
/// found at the end of the server's first world step: <see cref="Ready"/> is false during a mod's first OnLoad.
/// <para>Server side only. Capabilities (<see cref="Capabilities"/>): <c>kiln.settings</c> (the service is here) and
/// <c>settings.ready</c> (the live settings were found: pending until the end of the server's first world step). A
/// member that needs the service throws <see cref="KilnUnavailableException"/> naming <c>kiln.settings</c> when it is
/// off (a <see cref="NotSupportedException"/>, as before). A call before the settings are ready still throws
/// <see cref="InvalidOperationException"/>, as it always did: check <see cref="Ready"/> or
/// <c>Capabilities.Is("settings.ready")</c> first.</para></remarks>
[ServerOnly]
public static unsafe partial class Settings
{
    private const string Name = "kiln.settings";

    // ---- native layout (kiln_settings.h) ----
    [StructLayout(LayoutKind.Sequential)]
    private struct ValueN
    {
        public uint Kind;
        public uint Reserved;
        public long Integer;
        public double Number;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InfoN
    {
        public uint Size;
        public uint Index;
        public NativeStr Name;
        public NativeStr Group;
        public NativeStr Effect;
        public uint Kind;
        public uint Flags;
        public uint Offset;
        public uint OptionCount;
        public double Min, Max;
        public ValueN Value, Initial, Default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StatusN
    {
        public uint Size;
        public byte Ready, Resend, Daytime, Verified;
        public uint Count;
        public ulong Revision, Sets, ResendsRequested, ResendsSent;
        public int Preset;
        public uint Reserved;
        public long Day, Night, TimeOfDay;
        public uint DaytimeChanges;
        public uint Reserved2;
        public NativeStr ConfigPath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<StatusN*, int> Status;
        public delegate* unmanaged[Cdecl]<uint> Count;
        public delegate* unmanaged[Cdecl]<uint, InfoN*, int> Info;
        public delegate* unmanaged[Cdecl]<NativeStr, uint*, int> Find;
        public delegate* unmanaged[Cdecl]<uint, uint, NativeStr*, long*, int> Option;
        public delegate* unmanaged[Cdecl]<uint, ValueN*, int> Get;
        public delegate* unmanaged[Cdecl]<ulong, uint, ValueN*, uint, ValueN*, int> Set;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, int> Reset;
        public delegate* unmanaged[Cdecl]<int> Resend;
        public delegate* unmanaged[Cdecl]<uint, byte*, nuint, nuint*, int> Save;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> LastError;
    }

    private const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4;
    private const uint SetNoResend = 0x1, SetAnyValue = 0x2;

    private static Table* _table;
    private static Table* T => _table != null ? _table : (_table = Services.Get<Table>(Name, 1));

    // Static per-setting data (names, kinds, options) never changes once the native catalog is published.
    private static Meta[]? _meta;

    private sealed record Meta(string Name, SettingKind Kind, string[] Options, long[] OptionValues);

    /// <summary>True when the native service is loaded and usable on this game build (capability <c>kiln.settings</c>).</summary>
    [AnyThread]
    public static bool Available => T != null;

    /// <summary>True once the live settings were found (from the end of the server's first world step; capability
    /// <c>settings.ready</c>).</summary>
    [AnyThread]
    public static bool Ready => T != null && Native().Ready != 0;

    /// <summary>State of kiln.settings and of the day/night clock. Any thread.</summary>
    [AnyThread]
    public static SettingsStatus Status
    {
        get
        {
            var s = Native();
            string[] presets = ["Default", "Relaxed", "Hard", "Survival", "Custom"];
            var dt = s.Daytime != 0;
            return new SettingsStatus(s.Ready != 0, s.Resend != 0, s.Verified != 0, (int)s.Count, (long)s.Revision,
                (long)s.Sets, (long)s.ResendsRequested, (long)s.ResendsSent,
                s.Preset is >= 0 and < 5 ? presets[s.Preset] : null,
                dt ? Ns(s.Day) : null, dt ? Ns(s.Night) : null, dt ? Ns(s.TimeOfDay) : null, s.DaytimeChanges,
                s.ConfigPath.ToString());
        }
    }

    /// <summary>+1 for every change kiln.settings saw (its own, or the game's admin menu). Cheap; any thread.</summary>
    [AnyThread]
    public static long Revision => T != null ? (long)Native().Revision : 0;

    /// <summary>Every setting with its current value, in the game's order.</summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static IReadOnlyList<SettingInfo> All
    {
        get
        {
            Require("Settings.All");
            var meta = Metas();
            var list = new SettingInfo[meta.Length];
            for (var i = 0; i < meta.Length; i++) list[i] = InfoAt(i);
            return list;
        }
    }

    /// <summary>name → current value, JSON-friendly (what an admin "settings_get" returns).</summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static IReadOnlyDictionary<string, object> Values
    {
        get
        {
            Require("Settings.Values");
            var meta = Metas();
            var d = new Dictionary<string, object>(meta.Length, StringComparer.Ordinal);
            for (var i = 0; i < meta.Length; i++)
            {
                ValueN v;
                Check(T->Get((uint)i, &v), meta[i].Name);
                d[meta[i].Name] = Box(meta[i], v);
            }
            return d;
        }
    }

    /// <summary>Everything about one setting. <exception cref="KeyNotFoundException">unknown name</exception></summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static SettingInfo Info(string name)
    {
        Require("Settings.Info");
        return InfoAt(IndexOf(name));
    }

    /// <summary>Whether the game has a setting of that name (enshrouded_server.json key, any case).</summary>
    public static bool Has(string name) => T != null && Ready && TryIndex(name, out _);

    /// <summary>The current value: bool, double, long (whole numbers, nanosecond durations) or string (option name).</summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static object Get(string name)
    {
        Require("Settings.Get");
        var i = IndexOf(name);
        ValueN v;
        Check(T->Get((uint)i, &v), name);
        return Box(Metas()[i], v);
    }

    /// <summary>
    /// Changes a setting now and sends the settings to every player (unless <paramref name="resend"/> is false).
    /// <paramref name="value"/>: bool, any number, a <see cref="TimeSpan"/> or a nanosecond count for durations, an
    /// option name or value for choices, or text ("1.5", "on", "Rare", "45m"); a <see cref="JsonElement"/> works too.
    /// Values outside the documented range are refused unless <paramref name="anyValue"/>.
    /// </summary>
    /// <exception cref="ArgumentException">the value is refused (the message says why and gives the range)</exception>
    /// <exception cref="KeyNotFoundException">no such setting</exception>
    /// <exception cref="InvalidOperationException">not ready yet, or not on the tick thread</exception>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static SettingChange Set(string name, object value, bool resend = true, bool anyValue = false)
    {
        if (!TrySet(name, value, out var change, out var error, resend, anyValue))
            throw new ArgumentException(error, nameof(value));
        return change!;
    }

    /// <summary><see cref="Set"/> without the argument exception: false and the reason when refused.</summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static bool TrySet(string name, object value, out SettingChange? change, out string error, bool resend = true,
        bool anyValue = false)
    {
        Require("Settings.Set");
        change = null;
        var i = IndexOf(name);
        var meta = Metas()[i];
        if (!Convert(meta, value, out var v, out error)) return false;
        ValueN before;
        var flags = (resend ? 0 : SetNoResend) | (anyValue ? SetAnyValue : 0);
        var rc = T->Set(Services.CallerOwner, (uint)i, &v, flags, &before);
        if (rc == InvalidArgument)
        {
            error = LastError() is { Length: > 0 } e ? e : $"{meta.Name}: value refused";
            return false;
        }
        Check(rc, meta.Name);
        ValueN after;
        Check(T->Get((uint)i, &after), meta.Name);
        var b = Box(meta, before);
        var a = Box(meta, after);
        change = new SettingChange(meta.Name, b, a, !Equals(b, a));
        error = "";
        if (change.Changed) SettingsEvents.AfterKilnChange(meta.Name, b, a);
        return true;
    }

    /// <summary>Back to the world's own value (what the server started with).</summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static SettingChange Reset(string name, bool resend = true)
    {
        Require("Settings.Reset");
        var i = IndexOf(name);
        var meta = Metas()[i];
        ValueN before, after;
        Check(T->Get((uint)i, &before), meta.Name);
        Check(T->Reset(Services.CallerOwner, (uint)i, resend ? 0 : SetNoResend), meta.Name);
        Check(T->Get((uint)i, &after), meta.Name);
        var b = Box(meta, before);
        var a = Box(meta, after);
        if (!Equals(b, a)) SettingsEvents.AfterKilnChange(meta.Name, b, a);
        return new SettingChange(meta.Name, b, a, !Equals(b, a));
    }

    /// <summary>Every setting back to the world's own value. Returns how many changed.</summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static int ResetAll(bool resend = true)
    {
        Require("Settings.ResetAll");
        var before = Values;
        Check(T->Reset(Services.CallerOwner, uint.MaxValue, resend ? 0 : SetNoResend), "reset");
        var after = Values;
        foreach (var kv in before)
            if (!Equals(kv.Value, after[kv.Key])) SettingsEvents.AfterKilnChange(kv.Key, kv.Value, after[kv.Key]);
        return before.Count(kv => !Equals(kv.Value, after[kv.Key]));
    }

    /// <summary>Sends the settings to every player now (after several <c>Set(..., resend: false)</c>).</summary>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static void Resend()
    {
        Require("Settings.Resend");
        Check(T->Resend(), "resend");
    }

    /// <summary>
    /// Writes the live values into enshrouded_server.json with <c>"gameSettingsPreset": "Custom"</c> (the game ignores
    /// <c>gameSettings</c> otherwise), keeping every other key; the previous file stays as <c>.kiln-backup</c>. The game
    /// reads the file when it starts. Returns what was done ("saved 38 settings ...").
    /// </summary>
    /// <exception cref="IOException">the file could not be read, parsed or written</exception>
    [TickThreadOnly]
    [RequiresCapability(Caps.Service)]
    public static string Save()
    {
        Require("Settings.Save");
        var buf = stackalloc byte[1024];
        nuint len;
        var rc = T->Save(0, buf, 1024, &len);
        var msg = Encoding.UTF8.GetString(buf, (int)Math.Min(len, 1023));
        if (rc == Ok) return msg;
        if (rc == Unavailable || rc == WrongThread) Check(rc, "save");
        throw new IOException(msg.Length > 0 ? msg : LastError());
    }

    // ------------------------------------------------------------------------------------------ internals

    private static StatusN Native()
    {
        var s = new StatusN { Size = (uint)sizeof(StatusN) };
        if (T != null) T->Status(&s);
        return s;
    }

    private static void Require(string api)
    {
        if (T == null)
        {
            Rows.Ensure();
            throw ServiceCaps.Unavailable(Caps.Service, "kiln.settings is unavailable on this game build");
        }
        Services.RequireTickThread(api);
        if (Native().Ready == 0)
            throw new InvalidOperationException("kiln.settings: the live settings are not found yet (they are from the end of the server's first world step)");
    }

    private static void Check(int rc, string what)
    {
        switch (rc)
        {
            case Ok: return;
            case NotFound: throw new KeyNotFoundException($"no game setting '{what}'");
            case WrongThread: throw new InvalidOperationException("kiln.settings must be called on the tick thread");
            case Unavailable: throw new InvalidOperationException(LastError() is { Length: > 0 } e ? e : "kiln.settings is not ready");
            case InvalidArgument: throw new ArgumentException(LastError());
            default: throw new InvalidOperationException($"kiln.settings: {what}: {LastError()} (error {rc})");
        }
    }

    private static string LastError()
    {
        var buf = stackalloc byte[512];
        nuint len;
        T->LastError(buf, 512, &len);
        return Encoding.UTF8.GetString(buf, (int)Math.Min(len, 511));
    }

    private static Meta[] Metas()
    {
        if (_meta != null) return _meta;
        var n = (int)T->Count();
        var list = new Meta[n];
        for (var i = 0; i < n; i++)
        {
            var info = new InfoN { Size = (uint)sizeof(InfoN) };
            Check(T->Info((uint)i, &info), $"#{i}");
            var names = new string[info.OptionCount];
            var values = new long[info.OptionCount];
            for (var k = 0; k < names.Length; k++)
            {
                NativeStr s;
                long v;
                T->Option((uint)i, (uint)k, &s, &v);
                names[k] = s.ToString();
                values[k] = v;
            }
            list[i] = new Meta(info.Name.ToString(), (SettingKind)info.Kind, names, values);
        }
        return _meta = list;
    }

    private static bool TryIndex(string name, out int index)
    {
        index = -1;
        var meta = Metas();
        for (var i = 0; i < meta.Length; i++)
            if (string.Equals(meta[i].Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        return false;
    }

    private static int IndexOf(string name) =>
        TryIndex(name, out var i) ? i : throw new KeyNotFoundException($"no game setting '{name}'");

    private static SettingInfo InfoAt(int i)
    {
        var meta = Metas()[i];
        var n = new InfoN { Size = (uint)sizeof(InfoN) };
        Check(T->Info((uint)i, &n), meta.Name);
        return new SettingInfo(i, meta.Name, n.Group.ToString(), meta.Kind, (SettingFlags)n.Flags, n.Effect.ToString(),
            Box(meta, n.Value), Box(meta, n.Initial), n.Default.Kind == 0 ? null : Box(meta, n.Default), n.Min, n.Max,
            meta.Options, (int)n.Offset);
    }

    private static TimeSpan Ns(long ns) => TimeSpan.FromTicks(ns / 100);

    private static object Box(Meta m, ValueN v) => m.Kind switch
    {
        SettingKind.Bool => v.Integer != 0,
        SettingKind.Float => Math.Round(v.Number, 6), // float32 -> the decimal it came from (0.3f -> 0.3)
        SettingKind.Enum => OptionName(m, v.Integer) ?? (object)v.Integer,
        _ => v.Integer,
    };

    private static string? OptionName(Meta m, long value)
    {
        for (var k = 0; k < m.OptionValues.Length; k++)
            if (m.OptionValues[k] == value) return m.Options[k];
        return null;
    }

    private static ValueN Int(long v) => new() { Kind = 0, Integer = v, Number = v };
    private static ValueN Num(double v) => new() { Kind = (uint)SettingKind.Float, Number = v, Integer = (long)v };

    // Turns whatever a caller (or a JSON request) passes into a native value of the setting's kind.
    private static bool Convert(Meta m, object? value, out ValueN v, out string error)
    {
        v = default;
        error = "";
        switch (value)
        {
            case null:
                error = $"{m.Name}: no value";
                return false;
            case JsonElement j:
                return Convert(m, j.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => j.TryGetInt64(out var l) ? (object)l : j.GetDouble(),
                    JsonValueKind.String => j.GetString(),
                    _ => null,
                }, out v, out error);
            case bool b:
                if (m.Kind != SettingKind.Bool)
                {
                    error = $"{m.Name}: expects {Expect(m)}, not on/off";
                    return false;
                }
                v = Int(b ? 1 : 0);
                return true;
            case TimeSpan t:
                if (m.Kind != SettingKind.Duration)
                {
                    error = $"{m.Name}: expects {Expect(m)}, not a duration";
                    return false;
                }
                v = Int(t.Ticks * 100);
                return true;
            case string s:
                return Parse(m, s.Trim(), out v, out error);
            case float or double or decimal:
            {
                var d = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (m.Kind == SettingKind.Float) v = Num(d);
                else if (d == Math.Floor(d) && Math.Abs(d) < 9e15) v = Int((long)d);
                else
                {
                    error = $"{m.Name}: needs a whole number";
                    return false;
                }
                return true;
            }
            case sbyte or byte or short or ushort or int or uint or long or ulong:
            {
                var l = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
                v = m.Kind == SettingKind.Float ? Num(l) : Int(l);
                return true;
            }
            default:
                error = $"{m.Name}: cannot use a {value.GetType().Name} as {Expect(m)}";
                return false;
        }
    }

    private static bool Parse(Meta m, string s, out ValueN v, out string error)
    {
        v = default;
        error = "";
        var inv = CultureInfo.InvariantCulture;
        switch (m.Kind)
        {
            case SettingKind.Bool:
                switch (s.ToLowerInvariant())
                {
                    case "1" or "true" or "on" or "yes": v = Int(1); return true;
                    case "0" or "false" or "off" or "no": v = Int(0); return true;
                }
                break;
            case SettingKind.Enum:
                for (var k = 0; k < m.Options.Length; k++)
                    if (string.Equals(m.Options[k], s, StringComparison.OrdinalIgnoreCase))
                    {
                        v = Int(m.OptionValues[k]);
                        return true;
                    }
                if (long.TryParse(s, NumberStyles.Integer, inv, out var e))
                {
                    v = Int(e);
                    return true;
                }
                break;
            case SettingKind.Duration:
            {
                // "600000000000" (ns, as in enshrouded_server.json), or "45m" / "45 min" / "90s" / "1.5h"
                var t = s.ToLowerInvariant().Replace(" ", "");
                (string suffix, double ns)[] units = [("min", 60e9), ("m", 60e9), ("h", 3600e9), ("s", 1e9), ("ns", 1)];
                foreach (var (suffix, ns) in units)
                    if (t.EndsWith(suffix, StringComparison.Ordinal) &&
                        double.TryParse(t[..^suffix.Length], NumberStyles.Float, inv, out var x))
                    {
                        v = Int((long)Math.Round(x * ns));
                        return true;
                    }
                if (long.TryParse(t, NumberStyles.Integer, inv, out var raw))
                {
                    v = Int(raw);
                    return true;
                }
                break;
            }
            case SettingKind.Float:
                if (double.TryParse(s, NumberStyles.Float, inv, out var f))
                {
                    v = Num(f);
                    return true;
                }
                break;
            default:
                if (long.TryParse(s, NumberStyles.Integer, inv, out var i))
                {
                    v = Int(i);
                    return true;
                }
                break;
        }
        error = $"{m.Name}: '{s}' is not {Expect(m)}";
        return false;
    }

    private static string Expect(Meta m) => m.Kind switch
    {
        SettingKind.Bool => "on or off",
        SettingKind.Float => "a number",
        SettingKind.Int => "a whole number",
        SettingKind.Enum => "one of " + string.Join(", ", m.Options),
        SettingKind.Duration => "a duration (nanoseconds, or e.g. \"45m\")",
        _ => "a value",
    };
}
