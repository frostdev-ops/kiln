// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>A running Impact program instance: the game's impact id, its program, the entity it runs on and that
/// entity's ImpactHierarchy root (the caster of a player ability; None when it has none).</summary>
/// <param name="ImpactId">The game's id of the instance.</param>
/// <param name="ProgramId">The id of the instance's program (FNV-1a of its GUID's bytes).</param>
/// <param name="Self">The entity the program runs on.</param>
/// <param name="Root">The entity's ImpactHierarchy root: the caster of a player ability; none when it has none.</param>
public readonly record struct ImpactRef(uint ImpactId, uint ProgramId, EntityId Self, EntityId Root)
{
    /// <summary>The instance id, the program id, the entity and the root, for logs.</summary>
    public override string ToString() => $"impact {ImpactId} (program 0x{ProgramId:x8}, self {Self}, root {Root})";
}

/// <summary>A program variable. Variables with a <see cref="ConfigId"/> receive the binding's config value when the
/// instance starts (template, sequence event, item, buff, skill).</summary>
/// <param name="Index">Its index (handle 0xFFFF0000 | index).</param>
/// <param name="Name">Debug name, e.g. "NoiseRange::value".</param>
/// <param name="NameHash">The hash of the variable's name.</param>
/// <param name="ConfigId">The config id that binds the variable to a config value; 0 when it is not bound.</param>
/// <param name="Type">The engine's type hash (<see cref="ImpactVariable.ValueType"/> sorts the common ones).</param>
/// <param name="Size">The variable's size in bytes.</param>
/// <param name="Offset">Where the variable sits in the instance's variable data, in bytes.</param>
public sealed record ImpactVariable(int Index, uint NameHash, uint ConfigId, uint Type, int Size, int Offset, string Name)
{
    /// <summary>The name without the "::value" suffix and node prefixes ("NoiseRange", "Damage1").</summary>
    public string Alias
    {
        get
        {
            var n = Name;
            var c = n.IndexOf("::", StringComparison.Ordinal);
            if (c > 0) n = n[..c];
            var dot = n.LastIndexOf('.');
            return dot >= 0 && n.StartsWith("impact_nodes.", StringComparison.Ordinal) ? n[(dot + 1)..] : n;
        }
    }

    /// <summary>What the variable holds, from its type and size.</summary>
    public ImpactValueType ValueType => ImpactTypes.Of(Type, Size);
    /// <summary>The index, name, value type and size, and the config id when there is one, for logs.</summary>
    public override string ToString() => $"#{Index} {Name} ({ValueType}, {Size} B{(ConfigId != 0 ? $", config 0x{ConfigId:x8}" : "")})";
}

/// <summary>What a variable holds, from its engine type.</summary>
public enum ImpactValueType
{
    /// <summary>Anything else: raw bytes.</summary>
    Bytes = 0,
    /// <summary>An unsigned 32-bit integer.</summary>
    UInt32,
    /// <summary>A signed 32-bit integer.</summary>
    Int32,
    /// <summary>A 32-bit float.</summary>
    Float,
    /// <summary>A boolean (1 byte).</summary>
    Bool,
    /// <summary>Engine time: int64 nanoseconds (<see cref="TimeSpan"/> in the API).</summary>
    Time,
    /// <summary>An entity id (u32).</summary>
    Entity,
    /// <summary>12 bytes: x, y, z floats.</summary>
    Float3,
    /// <summary>16 bytes: a GUID reference (template, buff, item ...).</summary>
    Guid,
    /// <summary>52 bytes: damage per type (<see cref="DamageSetup"/>).</summary>
    Damage,
}

internal static class ImpactTypes
{
    public const uint UInt32 = 0x32940bec, Int32 = 0x9859f31e, Float = 0xe89f7410, Bool = 0xc894953d, Time = 0xdfe4e404,
        DamageSetup = 0x4af58ef7;

    public static ImpactValueType Of(uint type, int size) => type switch
    {
        UInt32 => ImpactValueType.UInt32,
        Int32 => ImpactValueType.Int32,
        Float => ImpactValueType.Float,
        Bool => ImpactValueType.Bool,
        Time => ImpactValueType.Time,
        DamageSetup when size == 52 => ImpactValueType.Damage,
        _ => size switch
        {
            12 => ImpactValueType.Float3,
            16 => ImpactValueType.Guid,
            52 => ImpactValueType.Damage,
            _ => ImpactValueType.Bytes,
        },
    };
}

/// <summary>Damage per type, the engine's DamageSetup variable (52 bytes). The game's damage formula multiplies the
/// weapon/spell base by these factors (docs/re/abilities.md §2).</summary>
/// <param name="Cut">The factor for cut damage.</param>
/// <param name="Pierce">The factor for pierce damage.</param>
/// <param name="Blunt">The factor for blunt damage.</param>
/// <param name="Fire">The factor for fire damage.</param>
/// <param name="Ice">The factor for ice damage.</param>
/// <param name="Fog">The factor for fog damage.</param>
/// <param name="Lightning">The factor for lightning damage.</param>
/// <param name="Healing">The factor for healing.</param>
/// <param name="Poison">The factor for poison damage.</param>
/// <param name="Wood">The factor for wood damage.</param>
/// <param name="Stone">The factor for stone damage.</param>
/// <param name="Metal">The factor for metal damage.</param>
/// <param name="Explosion">The factor for explosion damage.</param>
[StructLayout(LayoutKind.Sequential)]
public record struct DamageSetup(float Cut, float Pierce, float Blunt, float Fire, float Ice, float Fog, float Lightning,
    float Healing, float Poison, float Wood, float Stone, float Metal, float Explosion)
{
    /// <summary>A setup with fire set to <paramref name="f"/> and every other factor 0.</summary>
    public static DamageSetup FireOnly(float f) => new() { Fire = f };
    /// <summary>A setup with ice set to <paramref name="f"/> and every other factor 0.</summary>
    public static DamageSetup IceOnly(float f) => new() { Ice = f };

    /// <summary>Every factor multiplied by <paramref name="k"/>.</summary>
    public DamageSetup Scaled(float k) => new(Cut * k, Pierce * k, Blunt * k, Fire * k, Ice * k, Fog * k, Lightning * k,
        Healing * k, Poison * k, Wood * k, Stone * k, Metal * k, Explosion * k);

    /// <summary>The sum of all factors.</summary>
    public float Total => Cut + Pierce + Blunt + Fire + Ice + Fog + Lightning + Healing + Poison + Wood + Stone + Metal + Explosion;

    internal static unsafe DamageSetup From(ReadOnlySpan<byte> b) => b.Length >= 52 ? MemoryMarshal.Read<DamageSetup>(b) : default;

    internal byte[] ToBytes()
    {
        var b = new byte[52];
        MemoryMarshal.Write(b, in this);
        return b;
    }
}

/// <summary>A live instance (tick thread: Pre/Post, handlers, OnLoad). Reads and writes its variables: a write is what
/// the program sees from then on (the config values were copied in when it started).</summary>
public sealed unsafe class ImpactInstance
{
    private IReadOnlyList<ImpactVariable>? _vars;

    internal ImpactInstance(uint impactId) => ImpactId = impactId;

    /// <summary>The game's id of the instance.</summary>
    public uint ImpactId { get; }

    /// <summary>Whether the instance still exists.</summary>
    public bool Alive
    {
        get
        {
            var t = AbilitiesNative.T;
            if (t == null) return false;
            uint self, prog, flags;
            return t->InstanceInfo(ImpactId, &self, &prog, &flags) == AbilitiesNative.Ok;
        }
    }

    /// <summary>Instance flags (Initialized 1, Executed 2, Halt 8, Error 0x10, Destroy 0x40), or null when gone.</summary>
    public uint? Flags
    {
        get
        {
            var t = AbilitiesNative.T;
            if (t == null) return null;
            uint self, prog, flags;
            return t->InstanceInfo(ImpactId, &self, &prog, &flags) == AbilitiesNative.Ok ? flags : null;
        }
    }

    /// <summary>The instance's variables (read once, then kept).</summary>
    public IReadOnlyList<ImpactVariable> Variables => _vars ??= Abilities.ReadVars(ImpactId, isProgram: false);

    /// <summary>A variable by alias or debug name ("NoiseRange", "NoiseRange::value"), config id text ("0xa19409b2")
    /// or a known config alias (<see cref="ImpactConfigIds"/>). Null if the program has none.</summary>
    public ImpactVariable? Find(string key) => Abilities.FindVar(Variables, key);

    /// <summary>The variable bound to this config id; null when there is none (and for 0).</summary>
    public ImpactVariable? Find(uint configId) => Variables.FirstOrDefault(v => v.ConfigId == configId && configId != 0);

    /// <summary>The variable's bytes now; null when the instance is gone or the read was refused.</summary>
    [RequiresCapability(AbilitiesCaps.Service)]
    public byte[]? Read(ImpactVariable v)
    {
        var t = AbilitiesNative.Require("ImpactInstance.Read");
        var buf = new byte[v.Size];
        uint len;
        int rc;
        fixed (byte* p = buf) rc = t->InstanceRead(ImpactId, (uint)v.Index, p, (uint)buf.Length, &len);
        return rc == AbilitiesNative.Ok ? buf : null;
    }

    /// <summary>Writes a variable. False when the instance is gone or refused the write. A write that changed the value
    /// posts <see cref="ImpactVariableChangedEvent"/>.</summary>
    [RequiresCapability(AbilitiesCaps.Service)]
    public bool Write(ImpactVariable v, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != v.Size) throw new ArgumentException($"{v.Name} is {v.Size} bytes, got {bytes.Length}");
        var t = AbilitiesNative.Require("ImpactInstance.Write");
        // the bytes before, read only while someone listens for the change
        var watched = Events.HasSubscribers<ImpactVariableChangedEvent>();
        var before = watched ? Read(v) : null;
        int rc;
        fixed (byte* p = bytes) rc = t->InstanceWrite(ImpactId, (uint)v.Index, p, (uint)bytes.Length);
        if (rc != AbilitiesNative.Ok) return false;
        if (before is not null && !bytes.SequenceEqual(before)) ImpactChanges.Changed(t, ImpactId, v);
        return true;
    }

    /// <summary>A 4-byte variable (<paramref name="key"/> as for <see cref="Find(string)"/>) read as a float; null when
    /// there is none.</summary>
    public float? GetFloat(string key) => Find(key) is { Size: 4 } v && Read(v) is { } b ? BitConverter.ToSingle(b) : null;
    /// <summary>A 4-byte variable read as an int; null when there is none.</summary>
    public int? GetInt(string key) => Find(key) is { Size: 4 } v && Read(v) is { } b ? BitConverter.ToInt32(b) : null;
    /// <summary>A 1-byte variable read as a bool; null when there is none.</summary>
    public bool? GetBool(string key) => Find(key) is { Size: 1 } v && Read(v) is { } b ? b[0] != 0 : null;
    /// <summary>An 8-byte variable read as engine time (int64 nanoseconds); null when there is none.</summary>
    public TimeSpan? GetTime(string key) =>
        Find(key) is { Size: 8 } v && Read(v) is { } b ? TimeSpan.FromTicks(BitConverter.ToInt64(b) / 100) : null;
    /// <summary>A 52-byte variable read as a <see cref="DamageSetup"/>; null when there is none.</summary>
    public DamageSetup? GetDamage(string key) => Find(key) is { Size: 52 } v && Read(v) is { } b ? DamageSetup.From(b) : null;
    /// <summary>A 16-byte variable read as a GUID; null when there is none.</summary>
    public Guid? GetGuid(string key) => Find(key) is { Size: 16 } v && Read(v) is { } b ? new Guid(b) : null;

    /// <summary>Sets a variable from a value (float, int, bool, TimeSpan, Guid, DamageSetup, byte[]), converted to the
    /// variable's type. False when the program has no such variable or the value does not fit.</summary>
    public bool Set(string key, object value)
    {
        var v = Find(key);
        if (v is null) return false;
        var bytes = ImpactConfigs.Encode(value, v);
        return bytes is not null && Write(v, bytes);
    }

    /// <summary><see cref="Set(string, object)"/> with a float.</summary>
    public bool SetFloat(string key, float value) => Set(key, value);
    /// <summary><see cref="Set(string, object)"/> with an int.</summary>
    public bool SetInt(string key, int value) => Set(key, value);

    /// <summary>The instance id, for logs.</summary>
    public override string ToString() => $"impact {ImpactId}";
}

/// <summary>Config ids of the shared vocabulary (docs/re/abilities.md §1) and the C++-read cost ids, for keys that
/// are not program variable names.</summary>
public static class ImpactConfigIds
{
    /// <summary>The config id of the shared <c>Damage</c> variable (a <see cref="DamageSetup"/>).</summary>
    public const uint Damage = 0x72864524;
    /// <summary>The config id of the shared <c>BuffType</c> variable (a buff GUID). The alias <c>DebuffType</c> names
    /// it too.</summary>
    public const uint BuffType = 0x5adb2644;
    /// <summary>The config id of the shared <c>Duration</c> variable. The alias <c>DebuffDuration</c> names it
    /// too.</summary>
    public const uint Duration = 0x130a0ef2;
    /// <summary>The config id of the shared <c>AttackCategory</c> variable.</summary>
    public const uint AttackCategory = 0x242c6bbe;
    /// <summary>The config id of the shared <c>DamageScaleAttribute</c> variable.</summary>
    public const uint DamageScaleAttribute = 0x5333fdf6;
    /// <summary>The config id of the shared <c>Material</c> variable.</summary>
    public const uint Material = 0xbf09083f;
    /// <summary>The config id of the shared <c>SpawnTemplate</c> variable (a template GUID).</summary>
    public const uint SpawnTemplate = 0xf6a785cd;
    /// <summary>The config id of the shared <c>SpawnTemplate2</c> variable (a template GUID).</summary>
    public const uint SpawnTemplate2 = 0x29aa2976;
    /// <summary>The config id of the shared <c>NoiseRange</c> variable.</summary>
    public const uint NoiseRange = 0xa19409b2;
    /// <summary>The config id of the shared <c>AttackMod</c> variable (the attack multiplier).</summary>
    public const uint AttackMod = 0x854ea8fa;
    /// <summary>The config id of the shared <c>HitReactionType</c> variable.</summary>
    public const uint HitReactionType = 0x81dae9e7;
    /// <summary>The config id of the shared <c>PushBackStrength</c> variable.</summary>
    public const uint PushBackStrength = 0xe5254945;
    /// <summary>The config id of the shared <c>WaitTime</c> variable.</summary>
    public const uint WaitTime = 0xe21b4b72;
    /// <summary>The config id of the shared <c>PostWaitTime</c> variable.</summary>
    public const uint PostWaitTime = 0xaa1f9d8c;
    /// <summary>The config id of the shared <c>IgnoreConsecutiveHits</c> variable.</summary>
    public const uint IgnoreConsecutiveHits = 0xd6bba431;
    /// <summary>The config id the game's own code reads as the mana cost when a PayUsageCost event pays.</summary>
    public const uint ManaCost = 0xaf236ece;
    /// <summary>The config id the game's own code reads as the stamina cost when a PayUsageCost event pays.</summary>
    public const uint StaminaCost = 0x727d7f78;
    /// <summary>The config id the game's own code reads as the special-ability cost when a PayUsageCost event
    /// pays.</summary>
    public const uint SpecialAbilityCost = 0x2de5f1f5;
    /// <summary>The config id the game's own code reads as the fuel amount when a PayUsageCost event pays.</summary>
    public const uint FuelAmount = 0xc8492400;
    /// <summary>The config id the game's own code reads as the fuel charges when a PayUsageCost event pays.</summary>
    public const uint FuelCharges = 0xe99aaff8;

    internal static readonly Dictionary<string, uint> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Damage"] = Damage, ["BuffType"] = BuffType, ["DebuffType"] = BuffType, ["Duration"] = Duration,
        ["DebuffDuration"] = Duration, ["AttackCategory"] = AttackCategory, ["DamageScaleAttribute"] = DamageScaleAttribute,
        ["Material"] = Material, ["SpawnTemplate"] = SpawnTemplate, ["SpawnTemplate2"] = SpawnTemplate2,
        ["NoiseRange"] = NoiseRange, ["AttackMod"] = AttackMod, ["HitReactionType"] = HitReactionType,
        ["PushBackStrength"] = PushBackStrength, ["WaitTime"] = WaitTime, ["PostWaitTime"] = PostWaitTime,
        ["IgnoreConsecutiveHits"] = IgnoreConsecutiveHits, ["ManaCost"] = ManaCost, ["StaminaCost"] = StaminaCost,
        ["SpecialAbilityCost"] = SpecialAbilityCost, ["FuelAmount"] = FuelAmount, ["FuelCharges"] = FuelCharges,
    };

    /// <summary>"0x…" text or a known alias -> config id; 0 when neither.</summary>
    public static uint Parse(string key)
    {
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(key.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var id))
            return id;
        return ByName.TryGetValue(key, out var v) ? v : 0;
    }
}

/// <summary>Config values for <see cref="Abilities.RunImpact(string, WorldPosition, ImpactOptions)"/> and <see cref="Abilities.RunProgram"/>: key = a
/// variable alias or name of the program ("NoiseRange", "Damage1", "SpawnTemplate_Pre"), a config id ("0xa19409b2") or
/// a known config alias (<see cref="ImpactConfigIds"/>); value = float, int, bool, TimeSpan, Guid, DamageSetup or
/// byte[], converted to the variable's type. Applied to the spawned entity's instance after the game copied its own
/// configs, before its first run.</summary>
public sealed class ImpactConfigs : Dictionary<string, object>
{
    /// <summary>An empty set of config values; keys compare without regard to case.</summary>
    public ImpactConfigs() : base(StringComparer.OrdinalIgnoreCase) { }

    /// <summary>The bytes of <paramref name="value"/> for variable <paramref name="v"/> (null: does not fit).</summary>
    internal static byte[]? Encode(object value, ImpactVariable v)
    {
        switch (value)
        {
            case byte[] raw: return raw.Length == v.Size ? raw : null;
            case DamageSetup d: return v.Size == 52 ? d.ToBytes() : null;
            case Guid g: return v.Size == 16 ? g.ToByteArray() : null;
            case TimeSpan ts:
                return v.Size == 8 ? BitConverter.GetBytes(ts.Ticks * 100) :
                    v.Size == 4 && v.ValueType == ImpactValueType.Float ? BitConverter.GetBytes((float)ts.TotalSeconds) : null;
            case bool b: return v.Size == 1 ? [(byte)(b ? 1 : 0)] : v.Size == 4 ? BitConverter.GetBytes(b ? 1 : 0) : null;
            case EntityId e: return v.Size == 4 ? BitConverter.GetBytes(e.Value) : null;
        }
        double num;
        try
        {
            num = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
        return v.ValueType switch
        {
            ImpactValueType.Float => BitConverter.GetBytes((float)num),
            ImpactValueType.Int32 or ImpactValueType.Entity => BitConverter.GetBytes((int)Math.Round(num)),
            ImpactValueType.UInt32 => BitConverter.GetBytes((uint)Math.Max(0, Math.Round(num))),
            ImpactValueType.Bool => [(byte)(num != 0 ? 1 : 0)],
            ImpactValueType.Time => BitConverter.GetBytes((long)(num * 1e9)),  // seconds
            _ => v.Size switch
            {
                1 => [(byte)Math.Clamp((int)Math.Round(num), 0, 255)],  // enums (HitReaction, PushBack, ...)
                2 => BitConverter.GetBytes((ushort)Math.Clamp((int)Math.Round(num), 0, 65535)),
                4 => BitConverter.GetBytes((int)Math.Round(num)),
                _ => null,
            },
        };
    }
}

/// <summary>The change events of <see cref="ImpactInstance"/> writes (the contract: every write Kiln makes has one).</summary>
internal static unsafe class ImpactChanges
{
    // One key per variable of one instance changed by one mod: a handler that writes again waits for the next step.
    internal static readonly ChangeNotices<ImpactVariableChangedEvent, (uint, int, string)> Notices = new("kiln.abilities");

    /// <summary>Posts <see cref="ImpactVariableChangedEvent"/> for a write that changed <paramref name="v"/>. A waiting
    /// event goes out only while the instance is the one written (same program and self entity).</summary>
    internal static void Changed(AbilitiesNative.Table* t, uint impactId, ImpactVariable v)
    {
        if (!Events.HasSubscribers<ImpactVariableChangedEvent>()) return;
        uint s = 0, p = 0, flags = 0;
        if (t->InstanceInfo(impactId, &s, &p, &flags) != AbilitiesNative.Ok) return;
        var (self, program) = (s, p);
        var origin = ModContext.CurrentMod?.Info.Id ?? "";
        Notices.PostChecked((impactId, v.Index, origin),
            new ImpactVariableChangedEvent(impactId, program, new EntityId(self), v.Name, origin),
            () => Same(impactId, self, program));
    }

    private static bool Same(uint impactId, uint self, uint program)
    {
        var t = AbilitiesNative.T;
        uint s = 0, p = 0, f = 0;
        return t != null && t->InstanceInfo(impactId, &s, &p, &f) == AbilitiesNative.Ok && s == self && p == program;
    }
}
