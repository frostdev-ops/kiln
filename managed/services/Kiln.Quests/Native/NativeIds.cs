// SPDX-License-Identifier: MIT
// The deterministic ids of a mod's native objects: the rule of native/services/quests/questpack.cpp (derive_guid), so a
// mod can name them without the manifest (and a test can name them when the build did not run).
using System.Security.Cryptography;
using System.Text;

namespace Kiln;

/// <summary>Ids of a mod's native quests, dialogue lines and knowledge objects: FNV-1a-32 of a UUIDv5 of
/// <c>&lt;mod&gt;:&lt;kind&gt;:&lt;name&gt;</c>. They never change for a given mod id and name, so saves keep them.</summary>
public static class NativeIds
{
    private static readonly Guid Namespace = new("6f6d2a44-53c1-5c52-9d51-0a3c6e1f2b77");

    /// <summary>The derived GUID (game byte order = <see cref="Guid.ToByteArray()"/>).</summary>
    public static Guid Derive(string mod, string kind, string name)
    {
        Span<byte> ns = stackalloc byte[16];
        Namespace.TryWriteBytes(ns, bigEndian: true, out _);
        var text = Encoding.UTF8.GetBytes($"{mod}:{kind}:{name}");
        var all = new byte[16 + text.Length];
        ns.CopyTo(all);
        text.CopyTo(all, 16);
        var h = SHA1.HashData(all);
        h[6] = (byte)((h[6] & 0x0f) | 0x50);
        h[8] = (byte)((h[8] & 0x3f) | 0x80);
        return new Guid(h.AsSpan(0, 16), bigEndian: true);
    }

    /// <summary>FNV-1a-32 over a GUID's bytes: the game's id of an item, recipe, NPC or loca tag with that GUID.</summary>
    public static uint Fnv(Guid g)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);
        var h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x01000193u;
        return h;
    }

    /// <summary>A knowledge object the mod declared in <c>knowledge</c> (or the generated ones: <c>q.&lt;quest&gt;.active</c>, …).</summary>
    public static uint Knowledge(string mod, string name) => Fnv(Derive(mod, "know", name));

    /// <summary>A native dialogue line: <c>&lt;dialogue&gt;.&lt;node&gt;.&lt;option&gt;</c> (its id is its "heard" knowledge).</summary>
    public static uint Line(string mod, string dialogue, string node, int option) => Knowledge(mod, $"line.{dialogue}.{node}.{option}");
}
