// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using Kiln;

namespace TypegenProbe;

/// <summary>
/// Walks the game's reflection type registry at run time (through kiln.hooks: content/TypeByHash + Memory reads) and
/// writes the layout of a set of ECS component types to <c>kiln/data/typegen-probe/types.json</c>, in the shape
/// tools/typegen consumes. The type hash is FNV-1a-32 of the qualified name ("keen::ecs::" + component). Record
/// layout (docs/re/settings.md, content.md): +0x20/+0x28 qualified name, +0x40 size, +0x48 field count, +0x4C kind,
/// +0x50 hash, +0x58 field records (stride 0x30: name ptr, name len, type ptr +0x10, offset u64 +0x18).
/// </summary>
public sealed unsafe class TypegenProbeMod : Mod
{
    // Components to dump (extend freely). Known ones from the RE docs are first so the output is easy to verify.
    private static readonly string[] Components =
    [
        "NetworkHealth", "Health", "Mana", "Stamina", "CurrentTransform", "LastTransform", "Velocity",
        "TeleportCount", "NpcCommon",
    ];

    public override void OnLoad()
    {
        try
        {
            Run();
        }
        catch (Exception e)
        {
            Log.Error(e, "typegen-probe failed");
        }
    }

    private void Run()
    {
        if (!Hooks.Available || !Symbols.TryGet("content/TypeByHash", out var typeByHashAddr))
        {
            Log.Warn("typegen-probe: kiln.hooks or content/TypeByHash unavailable; cannot walk reflection");
            return;
        }
        var typeByHash = (delegate* unmanaged[Cdecl]<uint, nuint>)typeByHashAddr;

        var types = new List<object>();
        foreach (var name in Components)
        {
            try
            {
                var t = DumpType(typeByHash, name);
                if (t != null) types.Add(t);
            }
            catch (Exception e)
            {
                Log.Warn($"typegen-probe: {name} skipped ({e.Message})");
            }
        }

        var outDir = Context.DataDirectory;
        Directory.CreateDirectory(outDir);
        var doc = new
        {
            comment = "Reflection dump of ECS component types, walked live from content/TypeByHash via kiln.hooks.",
            side = "server",
            types,
        };
        var path = Path.Combine(outDir, "types.json");
        File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        Log.Info($"typegen-probe: wrote {types.Count} type(s) to {path}");

        VerifyGenerated();
    }

    // Cross-check generated Kiln.Game structs against the raw component bytes on live entities.
    private void VerifyGenerated()
    {
        if (!Kiln.GameTypes.Components.Available) return;
        VerifyStruct<Kiln.GameTypes.NetworkHealth>("NetworkHealth");
        VerifyStruct<Kiln.GameTypes.CurrentTransform>("CurrentTransform");
        VerifyStruct<Kiln.GameTypes.TeleportCount>("TeleportCount");
    }

    private void VerifyStruct<T>(string name) where T : unmanaged
    {
        var ents = World.Find(name);
        if (ents.Count == 0) { Log.Info($"typegen verify: no {name} entities in this world"); return; }
        var e = ents[0];
        var raw = World.ReadBytes(e, name);
        if (raw is null || !Kiln.GameTypes.Components.TryGet<T>(e, out var value)) return;
        var typed = new byte[sizeof(T)];
        fixed (byte* p = typed) *(T*)p = value;
        var ok = raw.Length >= typed.Length && typed.AsSpan().SequenceEqual(raw.AsSpan(0, typed.Length));
        Log.Info($"typegen verify: {(ok ? "PASS" : "FAIL")} {name} of {e} ({ents.Count} entities): " +
                 $"{sizeof(T)} typed bytes {(ok ? "match" : "DIFFER from")} the raw component");
    }

    private static bool Ok(nuint p) => p > 0x10000;

    private object? DumpType(delegate* unmanaged[Cdecl]<uint, nuint> typeByHash, string name)
    {
        var hash = Fnv1a("keen::ecs::" + name);
        var rec = typeByHash(hash);
        if (!Ok(rec))
        {
            Log.Info($"typegen-probe: {name} (0x{hash:X8}) not in the registry; skipped");
            return null;
        }
        var qname = ReadStr(Memory.Read<nuint>(rec + 0x20), Memory.Read<nuint>(rec + 0x28));
        var size = Memory.Read<uint>(rec + 0x40);
        var fieldCount = Memory.Read<uint>(rec + 0x48);
        var kind = Memory.Read<uint>(rec + 0x4C) & 0xFF;
        var recHash = Memory.Read<uint>(rec + 0x50);

        // +0x58 holds a pointer to the field-record array (stride 0x30: name ptr, name len, type ptr +0x10, offset
        // u64 +0x18).
        var fieldsPtr = Memory.Read<nuint>(rec + 0x58);
        var fields = new List<object>();
        if (fieldCount is > 0 and < 512 && Ok(fieldsPtr))
        {
            for (uint i = 0; i < fieldCount; i++)
            {
                var f = fieldsPtr + (nuint)(i * 0x30);
                var fname = ReadStr(Memory.Read<nuint>(f), Memory.Read<nuint>(f + 0x08));
                var ftypeRec = Memory.Read<nuint>(f + 0x10);
                var foffset = (uint)Memory.Read<ulong>(f + 0x18);
                string ftypeName = "";
                uint ftypeSize = 0, ftypeKind = 0;
                if (Ok(ftypeRec))
                {
                    ftypeName = ReadStr(Memory.Read<nuint>(ftypeRec + 0x20), Memory.Read<nuint>(ftypeRec + 0x28));
                    ftypeSize = Memory.Read<uint>(ftypeRec + 0x40);
                    ftypeKind = Memory.Read<uint>(ftypeRec + 0x4C) & 0xFF;
                }
                fields.Add(new { name = fname, offset = foffset, type = ftypeName, size = ftypeSize, kind = ftypeKind });
            }
        }
        Log.Info($"typegen-probe: {qname} hash=0x{recHash:X8} size={size} fields={fieldCount}");
        return new { name, qualifiedName = qname, hash = $"0x{recHash:X8}", size, kind, fields };
    }

    private static string ReadStr(nuint ptr, nuint len)
    {
        if (ptr == 0 || len == 0 || len > 256) return "";
        var bytes = Memory.ReadBytes(ptr, (int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static uint Fnv1a(string s)
    {
        uint h = 0x811c9dc5u;
        foreach (var b in Encoding.UTF8.GetBytes(s)) h = (h ^ b) * 0x01000193u;
        return h;
    }
}
