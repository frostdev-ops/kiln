// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>A running Impact program instance: the game's impact id, its program, the entity it runs on and that
/// entity's ImpactHierarchy root (the caster of a player ability; None when it has none).</summary>
public readonly record struct ImpactRef(uint ImpactId, uint ProgramId, EntityId Self, EntityId Root)
{
    public override string ToString() => $"impact {ImpactId} (program 0x{ProgramId:x8}, self {Self}, root {Root})";
}

/// <summary>A program variable. Variables with a <see cref="ConfigId"/> receive the binding's config value when the
/// instance starts (template, sequence event, item, buff, skill).</summary>
/// <param name="Index">Its index (handle 0xFFFF0000 | index).</param>
/// <param name="Name">Debug name, e.g. "NoiseRange::value".</param>
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

    public ImpactValueType ValueType => ImpactTypes.Of(Type, Size);
    public override string ToString() => $"#{Index} {Name} ({ValueType}, {Size} B{(ConfigId != 0 ? $", config 0x{ConfigId:x8}" : "")})";
}

/// <summary>What a variable holds, from its engine type.</summary>
public enum ImpactValueType
{
    Bytes = 0,
    UInt32,
    Int32,
    Float,
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
[StructLayout(LayoutKind.Sequential)]
public record struct DamageSetup(float Cut, float Pierce, float Blunt, float Fire, float Ice, float Fog, float Lightning,
    float Healing, float Poison, float Wood, float Stone, float Metal, float Explosion)
{
    public static DamageSetup FireOnly(float f) => new() { Fire = f };
    public static DamageSetup IceOnly(float f) => new() { Ice = f };

    public DamageSetup Scaled(float k) => new(Cut * k, Pierce * k, Blunt * k, Fire * k, Ice * k, Fog * k, Lightning * k,
        Healing * k, Poison * k, Wood * k, Stone * k, Metal * k, Explosion * k);

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

    public IReadOnlyList<ImpactVariable> Variables => _vars ??= Abilities.ReadVars(ImpactId, isProgram: false);

    /// <summary>A variable by alias or debug name ("NoiseRange", "NoiseRange::value"), config id text ("0xa19409b2")
    /// or a known config alias (<see cref="ImpactConfigIds"/>). Null if the program has none.</summary>
    public ImpactVariable? Find(string key) => Abilities.FindVar(Variables, key);

    public ImpactVariable? Find(uint configId) => Variables.FirstOrDefault(v => v.ConfigId == configId && configId != 0);

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

    [RequiresCapability(AbilitiesCaps.Service)]
    public bool Write(ImpactVariable v, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != v.Size) throw new ArgumentException($"{v.Name} is {v.Size} bytes, got {bytes.Length}");
        var t = AbilitiesNative.Require("ImpactInstance.Write");
        fixed (byte* p = bytes) return t->InstanceWrite(ImpactId, (uint)v.Index, p, (uint)bytes.Length) == AbilitiesNative.Ok;
    }

    public float? GetFloat(string key) => Find(key) is { Size: 4 } v && Read(v) is { } b ? BitConverter.ToSingle(b) : null;
    public int? GetInt(string key) => Find(key) is { Size: 4 } v && Read(v) is { } b ? BitConverter.ToInt32(b) : null;
    public bool? GetBool(string key) => Find(key) is { Size: 1 } v && Read(v) is { } b ? b[0] != 0 : null;
    public TimeSpan? GetTime(string key) =>
        Find(key) is { Size: 8 } v && Read(v) is { } b ? TimeSpan.FromTicks(BitConverter.ToInt64(b) / 100) : null;
    public DamageSetup? GetDamage(string key) => Find(key) is { Size: 52 } v && Read(v) is { } b ? DamageSetup.From(b) : null;
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

    public bool SetFloat(string key, float value) => Set(key, value);
    public bool SetInt(string key, int value) => Set(key, value);

    public override string ToString() => $"impact {ImpactId}";
}

/// <summary>Config ids of the shared vocabulary (docs/re/abilities.md §1) and the C++-read cost ids, for keys that
/// are not program variable names.</summary>
public static class ImpactConfigIds
{
    public const uint Damage = 0x72864524, BuffType = 0x5adb2644, Duration = 0x130a0ef2, AttackCategory = 0x242c6bbe,
        DamageScaleAttribute = 0x5333fdf6, Material = 0xbf09083f, SpawnTemplate = 0xf6a785cd, SpawnTemplate2 = 0x29aa2976,
        NoiseRange = 0xa19409b2, AttackMod = 0x854ea8fa, HitReactionType = 0x81dae9e7, PushBackStrength = 0xe5254945,
        WaitTime = 0xe21b4b72, PostWaitTime = 0xaa1f9d8c, IgnoreConsecutiveHits = 0xd6bba431,
        ManaCost = 0xaf236ece, StaminaCost = 0x727d7f78, SpecialAbilityCost = 0x2de5f1f5, FuelAmount = 0xc8492400,
        FuelCharges = 0xe99aaff8;

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

/// <summary>Config values for <see cref="Abilities.RunImpact"/> and <see cref="Abilities.RunProgram"/>: key = a
/// variable alias or name of the program ("NoiseRange", "Damage1", "SpawnTemplate_Pre"), a config id ("0xa19409b2") or
/// a known config alias (<see cref="ImpactConfigIds"/>); value = float, int, bool, TimeSpan, Guid, DamageSetup or
/// byte[], converted to the variable's type. Applied to the spawned entity's instance after the game copied its own
/// configs, before its first run.</summary>
public sealed class ImpactConfigs : Dictionary<string, object>
{
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
