// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>kdh1 and a mod's identity against the shared fixtures (schema/fixtures/dirhash: the Python reference wrote
/// expected.json, kiln_manage's Rust port reads the same file), and what the identity is for: a cooked mod is the same
/// mod on a server and in a player's game.</summary>
public sealed class DirHashTests
{
    private static string Fixtures([CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", "..", "schema", "fixtures", "dirhash"));

    private static JsonElement Expected() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures(), "expected.json"))).RootElement;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Expected().GetProperty("cases").EnumerateObject()) data.Add(c.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Kdh1_and_identity_match_the_fixtures(string name)
    {
        var dir = Path.Combine(Fixtures(), "cases", name);
        var e = Expected();
        Assert.Equal(e.GetProperty("cases").GetProperty(name).GetString(), DirHash.Compute(dir));
        Assert.Equal(e.GetProperty("identity").GetProperty(name).GetString(), DirHash.ModIdentity(dir));
    }

    [Fact]
    public void The_fixtures_hold_the_cases_the_identity_rule_needs()
    {
        var e = Expected();
        string Of(string map, string name) => e.GetProperty(map).GetProperty(name).GetString()!;
        Assert.True(e.GetProperty("cases").EnumerateObject().Count() >= 14);
        // a mod with cooked files is the same mod without them
        Assert.Equal(Of("cases", "mod"), Of("identity", "mod-cooked"));
        Assert.NotEqual(Of("cases", "mod-cooked"), Of("identity", "mod-cooked"));
        // a folder that is not a Kiln mod, and the names elsewhere, still count
        foreach (var n in (string[])["cooked-not-a-mod", "cooked-manifest-folder", "mod-cooked-case", "mod-cooked-file"])
            Assert.Equal(Of("cases", n), Of("identity", n));
        Assert.NotEqual(Of("cases", "mod-cooked-names"), Of("identity", "mod-cooked-names"));
    }

    private sealed class Temp : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "kiln-dirhash-" + Guid.NewGuid());

        public string Write(string rel, string text)
        {
            var p = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            return p;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    /// <summary>The mod as it ships, in <paramref name="folder"/>.</summary>
    private static void Shipped(Temp t, string folder, string manifest = "kiln.mod.json")
    {
        t.Write($"{folder}/{manifest}", """{ "id": "blades", "name": "Blades", "version": "1.2.0" }""");
        t.Write($"{folder}/Blades.dll", "assembly");
        t.Write($"{folder}/assets/cook.json", """{ "format": 1, "out": "cooked", "steps": [] }""");
        t.Write($"{folder}/assets/assets.json", """{ "format": 1, "entries": [ { "kind": "blob", "file": "cooked/blade.blob" } ] }""");
        t.Write($"{folder}/assets/src/blade.gltf", "{}");
    }

    /// <summary>What a server's cook leaves: the clips, no model, a report that names the server.</summary>
    private static void CookedOnAServer(Temp t, string folder)
    {
        t.Write($"{folder}/assets/cooked/clip_slash.bin", "clip");
        t.Write($"{folder}/assets/cooked/cook-report.json", """{ "game": { "kfc": "Z:\\srv\\enshrouded_server.kfc", "side": "server" } }""");
    }

    /// <summary>What a player's cook leaves: the clips, the model, a report that names the player's game.</summary>
    private static void CookedOnAClient(Temp t, string folder)
    {
        t.Write($"{folder}/assets/cooked/clip_slash.bin", "clip");
        t.Write($"{folder}/assets/cooked/blade.bin", "model");
        t.Write($"{folder}/assets/cooked/blade.blob", "render data");
        t.Write($"{folder}/assets/cooked/cook-report.json", """{ "game": { "kfc": "C:\\Games\\Enshrouded\\enshrouded.kfc" } }""");
    }

    [Fact]
    public void A_mod_cooked_on_the_server_and_in_the_players_game_is_not_missing()
    {
        using var t = new Temp();
        Shipped(t, "server/blades");
        CookedOnAServer(t, "server/blades");
        Shipped(t, "client/blades");
        CookedOnAClient(t, "client/blades");
        Shipped(t, "plain/blades");
        var (server, client, plain) = (Path.Combine(t.Root, "server/blades"), Path.Combine(t.Root, "client/blades"), Path.Combine(t.Root, "plain/blades"));
        Assert.NotEqual(DirHash.Compute(server), DirHash.Compute(client));  // what dev.26 advertised and compared
        Assert.Equal(DirHash.Compute(plain), DirHash.ModIdentity(server));
        Assert.Equal(DirHash.ModIdentity(server), DirHash.ModIdentity(client));

        NetRequiredMod[] required = [new("blades", "1.2.0", DirHash.ModIdentity(server))];
        Assert.Empty(LastServer.Missing(required, [new LastServer.Local("kiln", "blades", client)]));
        Assert.Empty(LastServer.Missing(required, [new LastServer.Local("kiln", "blades", plain)]));
        Assert.Equal(["blades"], LastServer.Missing(required, []));

        // a shipped file that differs is still another mod
        File.WriteAllText(Path.Combine(client, "Blades.dll"), "another build");
        Assert.Equal(["blades"], LastServer.Missing(required, [new LastServer.Local("kiln", "blades", client)]));
    }

    [Fact]
    public void A_cooked_change_does_not_change_the_signature_of_a_mod_but_a_shipped_file_does()
    {
        using var t = new Temp();
        Shipped(t, "blades");
        var dir = Path.Combine(t.Root, "blades");
        var before = DirHash.Signature(dir, mod: true);
        CookedOnAClient(t, "blades");
        Assert.Equal(before, DirHash.Signature(dir, mod: true));
        Assert.NotEqual(before, DirHash.Signature(dir));
        t.Write("blades/extra.txt", "x");
        Assert.NotEqual(before, DirHash.Signature(dir, mod: true));
    }

    [Fact]
    public void Another_kinds_folder_is_all_its_own_files()
    {
        using var t = new Temp();
        // a native mod (native.json, no kiln.mod.json) with a folder named assets/cooked: every file counts
        Shipped(t, "server/tool", "native.json");
        CookedOnAServer(t, "server/tool");
        Shipped(t, "client/tool", "native.json");
        CookedOnAClient(t, "client/tool");
        var (server, client) = (Path.Combine(t.Root, "server/tool"), Path.Combine(t.Root, "client/tool"));
        Assert.Equal(DirHash.Compute(server), DirHash.ModIdentity(server));
        NetRequiredMod[] required = [new("tool", "1.2.0", DirHash.Compute(server), "native")];
        Assert.Equal(["tool"], LastServer.Missing(required, [new LastServer.Local("native", "tool", client)]));
        Assert.Empty(LastServer.Missing(required, [new LastServer.Local("native", "tool", server)]));
    }

    [Fact]
    public void A_link_named_assets_cooked_is_refused_and_links_inside_the_folder_are_not_looked_at()
    {
        if (OperatingSystem.IsWindows()) return;  // making a link needs a privilege there
        using var t = new Temp();
        Shipped(t, "blades");
        var dir = Path.Combine(t.Root, "blades");
        var plain = DirHash.ModIdentity(dir);
        CookedOnAClient(t, "blades");
        File.CreateSymbolicLink(Path.Combine(dir, "assets/cooked/link.bin"), "blade.bin");
        Assert.Throws<DirHashException>(() => DirHash.Compute(dir));
        Assert.Equal(plain, DirHash.ModIdentity(dir));
        Directory.Delete(Path.Combine(dir, "assets/cooked"), true);
        Directory.CreateSymbolicLink(Path.Combine(dir, "assets/cooked"), t.Root);
        Assert.Throws<DirHashException>(() => DirHash.ModIdentity(dir));
    }
}
