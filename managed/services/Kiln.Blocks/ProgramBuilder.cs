// SPDX-License-Identifier: MIT
// Writes keen::ImpactProgram blobs at load time, with no cook step and no game files: the layout, opcodes and the stack rules of
// docs/kiln/modding/impact.md and tools/impact (program.py encode). kiln.abilities' verifier checks every blob before it is
// registered, and tools/impact can disassemble and lint them (Blocks.DumpPrograms).
using System.Security.Cryptography;
using System.Text;

namespace Kiln;

internal readonly record struct ImpactType(uint Hash, int Size)
{
    public static readonly ImpactType Float = new(0xe89f7410, 4), Bool = new(0xc894953d, 1), Sint32 = new(0x9859f31e, 4),
        Uint32 = new(0x32940bec, 4), Entity = new(0x737c8f86, 4), Attribute = new(0xb8172c5c, 16), MathOp = new(0x02484651, 1),
        Float3 = new(0x3d1f8582, 12), EventStream = new(0x8de534b1, 8), Buff = new(0x305916d9, 16), Time = new(0xdfe4e404, 8),
        Transform = new(0x9493a8c5, 56), Material = new(0x10e7bb7a, 4), Template = new(0x2a44d9ef, 16), Damage = new(0x4af58ef7, 52),
        FactionMask = new(0x1549512f, 2), WeaponCategory = new(0x87d03d12, 16), Reaction = new(0x3aa3bbdf, 2), Push = new(0x2247ca8b, 1),
        Direction = new(0x1b94ffe1, 1), AttackMask = new(0x5a36e6e3, 1), StreamType = new(0x3c78bf79, 1), Visceral = new(0x90d72cd0, 1),
        ItemId = new(0x5bfc0dbb, 4), CompareOp = new(0x70424866, 1), Slot = new(0xa1fc72d4, 1);
}

/// <summary>A value of a program only the running game knows (a game status's GUID, a template's by its name): it is filled in when content is ready.</summary>
internal sealed record LateRef(int Var, string Kind, string Value, string Block, string Path);

/// <summary>One program. Variables are 16-byte aligned; arguments are variable handles or unconnected (null).</summary>
internal sealed class ProgramBuilder(string key)
{
    private const byte IAdd = 1, ILeq = 7, Br = 8, BrT = 9, BrF = 10, IConst = 11, IConst0 = 12, Dup = 17, Ecall = 19, GLoad = 22, GStore = 24,
        LTime = 25, PopOp = 27, Rvm = 28, DSelf = 29, HaltOp = 30;
    private const uint FetchOptional = 0xa385dcb9;

    private static readonly Dictionary<string, (uint Hash, int Argc)> Natives = new()
    {
        ["getSelf"] = (0x01d09759, 1), ["getRoot"] = (0xbf5ad13b, 1), ["getTransform"] = (0xa18f5959, 3), ["getBuffTarget"] = (0xc24cb9bb, 1),
        ["isSameEntity"] = (0x530b5054, 2), ["isEnemy"] = (0x79b5a97b, 2), ["isFriend"] = (0xac31b17d, 2), ["applyDamage"] = (0xb04d0e7a, 25),
        ["changeAttributeValue"] = (0x044e4187, 6), ["applyBuff"] = (0x91f30b26, 5), ["randomBranchNode"] = (0x9dcb0a66, 1),
        ["onEvent"] = (0x45dbf538, 2), ["forEachHitEventInStream"] = (0x72fdd0f5, 25), ["forEachEntityWithFactionInRange"] = (0x19143416, 6),
        ["destroyFactionQuery"] = (0x93e40aa9, 6), ["hasAttackCategory"] = (0x2d3e3e5f, 3), ["spawnEntity"] = (0x61b11b03, 13),
        ["fetchOptionalAttribute"] = (FetchOptional, 3), ["removeBuff"] = (0xca687904, 3), ["kiln.Signal"] = (0x38b05baf, 2),
        ["applyAttributeValue"] = (0x798388df, 7), ["revertApplyAttributeValue"] = (0xe41652a7, 7), ["isValidEntity"] = (0xcf3810a6, 1),
        ["filterFriends"] = (0xd6bb2b78, 4), ["filterSameRoot"] = (0xf52a5619, 4), ["forEachCollisionInStream"] = (0xd0a532b0, 11),
        ["itemBasedMaterialReaction"] = (0xad415283, 8), ["equal"] = (0x0294c6df, 3), ["triggerNoise"] = (0xd471249f, 2),
        ["GetSlotTransform"] = (0x3e1c0b5d, 5),
    };

    internal sealed class Label { public int Pc = -1; public readonly List<int> Uses = new(); }
    private sealed record Var(string Name, ImpactType Type, int Offset, uint Config);

    private readonly List<Var> _vars = new();
    private readonly List<byte> _data = new();
    private readonly List<uint> _code = new(), _shutdown = new();
    private readonly HashSet<uint> _queries = new();
    private readonly Dictionary<string, int> _named = new();
    private readonly List<LateRef> _late = new();
    private List<uint> _cur = null!;
    private int _sites;

    /// <summary>The event streams the instance allocates (OnCollision 1, OnHit 2).</summary>
    public byte Streams { get; set; }

    /// <summary>A debug listing of what was emitted: one line per statement.</summary>
    public StringBuilder Listing { get; } = new();

    public void BeginCode() => _cur = _code;
    public void BeginShutdown() => _cur = _shutdown;

    /// <summary>A variable. Same name = the same variable (shared constants).</summary>
    public int V(string name, ImpactType type, ReadOnlySpan<byte> initial = default, uint config = 0)
    {
        if (_named.TryGetValue(name, out var have)) return have;
        var off = (_data.Count + 15) / 16 * 16;
        while (_data.Count < off + type.Size) _data.Add(0);
        for (var i = 0; i < initial.Length && i < type.Size; i++) _data[off + i] = initial[i];
        _vars.Add(new Var(name, type, off, config));
        _named[name] = _vars.Count - 1;
        return _vars.Count - 1;
    }

    /// <summary>Gives a variable that already exists its value, and a config to bind it to.</summary>
    public void SetVar(int v, ReadOnlySpan<byte> initial, uint config)
    {
        var x = _vars[v];
        for (var i = 0; i < initial.Length && i < x.Type.Size; i++) _data[x.Offset + i] = initial[i];
        _vars[v] = x with { Config = config };
    }

    public int VFloat(string name, float value, uint config = 0) => V(name, ImpactType.Float, BitConverter.GetBytes(value), config);
    public int VInt(string name, int value) => V(name, ImpactType.Sint32, BitConverter.GetBytes(value));
    public int VBool(string name, bool value) => V(name, ImpactType.Bool, new[] { (byte)(value ? 1 : 0) });
    public int VGuid(string name, ImpactType type, Guid value, uint config = 0) => V(name, type, value.ToByteArray(), config);
    public int VTime(string name, double seconds) => V(name, ImpactType.Time, BitConverter.GetBytes((long)Math.Round(seconds * 1e9)));
    public int VByte(string name, ImpactType type, byte value) => V(name, type, new[] { value });
    public int VU16(string name, ImpactType type, ushort value) => V(name, type, BitConverter.GetBytes(value));

    /// <summary>A GUID the running game knows. The variable's name carries the reference, so the program's GUID follows from what the author
    /// wrote and not from the game's data.</summary>
    public int VLate(string name, ImpactType type, string kind, string value, string block, string path)
    {
        var full = $"{name}:{value}";
        if (_named.TryGetValue(full, out var have)) return have;
        var v = V(full, type);
        _late.Add(new LateRef(v, kind, value, block, path));
        return v;
    }

    /// <summary>The program waits for values from the running game.</summary>
    public bool IsLate => _late.Count > 0;

    /// <summary>The final blob, with the values only the running game knows. Null: <paramref name="failed"/> has no value.</summary>
    public byte[]? Finish(Guid guid, Func<LateRef, Guid?> resolve, out LateRef? failed)
    {
        failed = null;
        foreach (var r in _late)
        {
            if (resolve(r) is not { } value)
            {
                failed = r;
                return null;
            }
            var off = _vars[r.Var].Offset;
            var bytes = value.ToByteArray();
            for (var i = 0; i < 16; i++) _data[off + i] = bytes[i];
        }
        return Layout(guid);
    }

    public Label NewLabel() => new();

    public void Mark(Label l)
    {
        l.Pc = _cur.Count;
        foreach (var at in l.Uses) _cur[at] = (uint)l.Pc;
    }

    private void Jump(byte op, Label l)
    {
        _cur.Add(op);
        if (l.Pc >= 0) _cur.Add((uint)l.Pc);
        else
        {
            l.Uses.Add(_cur.Count);
            _cur.Add(0);
        }
    }

    public void Goto(Label l) => Jump(Br, l);
    public void IfTrue(Label l) => Jump(BrT, l);
    public void IfFalse(Label l) => Jump(BrF, l);
    public void Pop() => _cur.Add(PopOp);
    public void Halt() => _cur.Add(HaltOp);
    public void DestroySelf() => _cur.Add(DSelf);
    public void Yield() => _cur.Add(Rvm);

    /// <summary>Pushes a uint32, sint32, float32, bool or Time variable.</summary>
    public void Load(int v)
    {
        _cur.Add(GLoad);
        _cur.Add(Handle(v));
    }

    /// <summary>index variable := 0</summary>
    public void Zero(int v)
    {
        _cur.Add(IConst0);
        _cur.Add(GStore);
        _cur.Add(Handle(v));
    }

    /// <summary>Waits a Time variable: the game's own idiom (the end time stays on the stack).</summary>
    public void Wait(int time)
    {
        _cur.Add(GLoad);
        _cur.Add(Handle(time));
        _cur.Add(LTime);
        _cur.Add(IAdd);
        var loop = NewLabel();
        var go = NewLabel();
        Mark(loop);
        _cur.Add(Dup);
        _cur.Add(LTime);
        _cur.Add(ILeq);
        IfTrue(go);
        Yield();
        Goto(loop);
        Mark(go);
        Pop();
    }

    private static uint Handle(int v) => 0xFFFF0000u | (uint)v;

    /// <summary>Calls a native; pushes its result byte (pop it, or branch on it). Arguments in args[] order; null = unconnected.</summary>
    public void Call(string native, string? site, params int?[] args)
    {
        var (hash, argc) = Natives[native];
        if (args.Length != argc) throw new InvalidOperationException($"{native}: {args.Length} arguments, needs {argc}");
        for (var i = args.Length - 1; i >= 0; i--)
        {
            if (args[i] is { } v)
            {
                _cur.Add(IConst);
                _cur.Add(Handle(v));
            }
            else _cur.Add(IConst0);
        }
        var siteId = BlockIds.Fnv(Encoding.UTF8.GetBytes($"{key}|{site ?? "s" + ++_sites}"));
        if (siteId == 0) siteId = 1;
        if (native is "forEachEntityWithFactionInRange" or "destroyFactionQuery") _queries.Add(siteId);
        _cur.Add(Ecall);
        _cur.Add(siteId);
        _cur.Add(hash);
        Listing.Append("  ").Append(native).Append('(').Append(string.Join(", ", args.Select(a => a is { } x ? _vars[x].Name : "_"))).AppendLine(")");
    }

    /// <summary>Lays the blob out, with a GUID from the mod, the program's key and its bytes: a changed program is a new program
    /// (kiln.abilities never replaces a registered one, because running instances read their code live), so hot reload works.</summary>
    public (Guid Guid, byte[] Blob) Build(string mod)
    {
        var blob = Layout(Guid.Empty);
        var guid = BlockIds.Derive(mod, "program", $"{key}#{Convert.ToHexString(SHA1.HashData(blob))[..12]}");
        return (guid, Layout(guid));
    }

    private byte[] Layout(Guid guid)
    {
        var b = new List<byte>(new byte[64]);
        void Reserve(int align)
        {
            while (b.Count % align != 0) b.Add(0);
        }
        void Set32(int at, uint v)
        {
            var x = BitConverter.GetBytes(v);
            for (var i = 0; i < 4; i++) b[at + i] = x[i];
        }
        void Arr(int field, int at, int n)
        {
            Set32(field, n == 0 ? 0 : (uint)(at - field));
            Set32(field + 4, (uint)n);
        }
        void Words(int field, IReadOnlyList<uint> w)
        {
            Reserve(4);
            var at = b.Count;
            foreach (var x in w) b.AddRange(BitConverter.GetBytes(x));
            Arr(field, at, w.Count);
        }
        var g = guid.ToByteArray();
        Set32(0, BlockIds.Fnv(g));
        for (var i = 0; i < 16; i++) b[4 + i] = g[i];
        b[0x14] = 0; b[0x15] = 1;    // stackSize 256, as vanilla
        b[0x16] = Streams;
        Words(0x18, _code);
        Words(0x20, _shutdown);
        Reserve(4);
        var layout = b.Count;
        b.AddRange(new byte[24 * _vars.Count]);
        Arr(0x28, layout, _vars.Count);
        for (var i = 0; i < _vars.Count; i++)
        {
            var v = _vars[i];
            var e = layout + 24 * i;
            var name = Encoding.UTF8.GetBytes(v.Name);
            Set32(e, BlockIds.Fnv(name));
            Set32(e + 4, v.Config);
            Set32(e + 8, v.Type.Hash);
            b[e + 12] = (byte)v.Type.Size; b[e + 13] = (byte)(v.Type.Size >> 8);
            b[e + 14] = (byte)v.Offset; b[e + 15] = (byte)(v.Offset >> 8);
            var at = b.Count;
            b.AddRange(name);
            Arr(e + 16, at, name.Length);
        }
        var data = b.Count;
        b.AddRange(_data);
        Arr(0x30, data, _data.Count);
        Words(0x38, _queries.OrderBy(x => x).ToList());
        return b.ToArray();
    }
}
