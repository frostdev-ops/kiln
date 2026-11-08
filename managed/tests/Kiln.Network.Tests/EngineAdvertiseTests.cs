// SPDX-License-Identifier: MIT
using Xunit;
namespace Kiln.Network.Tests;

public sealed class EngineAdvertiseTests
{
    [Theory]
    [InlineData(".bad")]
    [InlineData("Bad/../id")]
    public void Invalid_manifest_and_folder_ids_do_not_enter_required(string id)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-id-advert-" + Guid.NewGuid(), "BadFolder");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "mod.json"), System.Text.Json.JsonSerializer.Serialize(new { id, version = "1.0.0" }));
            Assert.Null(NetAdvertise.EngineIdentity(dir).Id);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Theory]
    [InlineData("eml")]
    [InlineData("flame")]
    public void Server_only_data_mods_are_not_advertised_to_clients(string kind)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-advert-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "mod.json"), "{\"id\":\"x\",\"side\":\"server\",\"clients\":\"mod\"}");
            File.WriteAllText(Path.Combine(dir, "src/mod.lua"), "return {}");
            File.WriteAllText(Path.Combine(dir, "flame.json"), "{}");
            Assert.False(NetAdvertise.EngineKindOf(kind, dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Enshwick_mods_players_need_are_advertised_with_their_pack_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "kiln-enshwick-advert-" + Guid.NewGuid());
        try
        {
            var pack = Path.Combine(root, "lanterns");
            Directory.CreateDirectory(pack);
            File.WriteAllText(Path.Combine(pack, "pack.toml"),
                "id = \"you.lanterns\" # the id\nversion = '1.0.0'\nname = \"Lanterns\"\ndescription = \"\"\"\nid = \"not.this\"\n\"\"\"\n[requires]\nversion = \"9\"\n");
            Assert.True(NetAdvertise.EngineKindOf("enshwick", pack));
            Assert.Equal(("you.lanterns", "1.0.0", "Lanterns"), NetAdvertise.EngineIdentity(pack));

            var server = Path.Combine(root, "greeter");
            Directory.CreateDirectory(server);
            File.WriteAllText(Path.Combine(server, "pack.toml"), "id = \"you.greeter\"\nside = \"server\"\n");
            File.WriteAllText(Path.Combine(server, "main.luau"), "");
            Assert.False(NetAdvertise.EngineKindOf("enshwick", server));

            var dll = Path.Combine(root, "lockers");
            Directory.CreateDirectory(dll);
            File.WriteAllBytes(Path.Combine(dll, "lockers.dll"), System.Text.Encoding.ASCII.GetBytes("MZ\0enshwick_mod_query\0"));
            Assert.True(NetAdvertise.EngineKindOf("enshwick", dll));
            Assert.Equal("lockers", NetAdvertise.EngineIdentity(dll).Id);

            // the loader's own folder and other engines' folders in the shared mods/ are not Enshwick mods
            var loader = Path.Combine(root, "enshwick");
            Directory.CreateDirectory(loader);
            File.WriteAllBytes(Path.Combine(loader, "enshwick.dll"), System.Text.Encoding.ASCII.GetBytes("enshwick_mod_abi"));
            Assert.Null(NetAdvertise.EngineKindOf("enshwick", loader));
            File.WriteAllText(Path.Combine(dll, "flame.json"), "{}");
            Assert.Null(NetAdvertise.EngineKindOf("enshwick", dll));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("# comment = \"\"\"\nid = 'you.real'\nside = 'server'\n")]
    [InlineData("description = \"\"\"\nnot a close: \\\"\"\"\nside = 'server'\n\"\"\"\nid = 'you.real'\n")]
    public void Comments_and_escaped_multiline_delimiters_cannot_spoof_pack_metadata(string toml)
    {
        WithPack(toml, dir =>
        {
            Assert.Equal("you.real", NetAdvertise.EngineIdentity(dir, "enshwick").Id);
            Assert.Equal(!toml.StartsWith('#'), NetAdvertise.EngineKindOf("enshwick", dir));
        });
    }

    [Theory]
    [InlineData("id = 'one'\nid = 'two'\n")]
    [InlineData("id = 'one'\n\"id\" = 'two'\n")]
    [InlineData("id = '.invalid'\n")]
    [InlineData("id = 'enshwick'\n")]
    [InlineData("id = 'con.config'\n")]
    [InlineData("id = 'you.pack.'\n")]
    [InlineData("id = 'one'\nversion = '1'\n")]
    [InlineData("id = 'one'\nversion = '1.0.0-01'\n")]
    [InlineData("id = 'one'\nversion = '2147483648.0.0'\n")]
    [InlineData("id = 'one'\nversion = 'v1.0.0'\n")]
    [InlineData("id = 'one'\nversion = ' 1.0.0'\n")]
    [InlineData("id = 'one'\nside = false\n")]
    [InlineData("id = 'one'\nside = \"ser\\nver\"\n")]
    [InlineData("id = 'one'\n# bad\u0001\n")]
    [InlineData("id = 'one'\nname = \"bad\\uD800\"\n")]
    [InlineData("id = 'one'\nname = \"bad\\U00110000\"\n")]
    [InlineData("id = 'one'\nname = \"bad\\q\"\n")]
    [InlineData("id = 'one'\nname = \"open\n")]
    [InlineData("id = 'one'\nauthors = [\"]\",\n\"id='spoof'\"]\n")]
    [InlineData("id = 'one'\nvalue = {\nside = 'server'\n}\n")]
    [InlineData("id = 'one'\n[broken\n")]
    public void Invalid_or_unsupported_pack_metadata_is_never_advertised(string toml)
    {
        WithPack(toml, dir =>
        {
            Assert.Null(NetAdvertise.EngineKindOf("enshwick", dir));
            Assert.Null(NetAdvertise.EngineIdentity(dir, "enshwick").Id);
            Assert.Empty(NetAdvertise.TomlStrings(Path.Combine(dir, "pack.toml")));
        });
    }

    [Fact]
    public void Pack_metadata_decodes_multiline_strings_and_unicode_scalars()
    {
        WithPack("id = \"\"\"you.\\\n  real\"\"\"\nname = \"Lantern \\U0001F525\"\nside = '''server'''\n", dir =>
        {
            Assert.Equal(("you.real", "0.0.0", "Lantern 🔥"), NetAdvertise.EngineIdentity(dir, "enshwick"));
            Assert.False(NetAdvertise.EngineKindOf("enshwick", dir));
        });
    }

    [Theory]
    [InlineData("2147483647.0.0")]
    [InlineData("1.2.3-rc.1+build.01")]
    public void Pack_versions_preserve_valid_canonical_semver(string version)
    {
        WithPack("id = 'you.real'\nversion = '" + version + "'\n", dir =>
            Assert.Equal(version, NetAdvertise.EngineIdentity(dir, "enshwick").Version));
    }

    [Fact]
    public void Enshwick_pack_identity_and_side_ignore_adjacent_mod_json()
    {
        WithPack("id = 'you.real'\nversion = '2.0.0'\nside = 'both'\n", dir =>
        {
            File.WriteAllText(Path.Combine(dir, "mod.json"), "{\"id\":\"you.spoof\",\"version\":\"9\",\"side\":\"server\",\"clients\":\"none\"}");
            Assert.True(NetAdvertise.EngineKindOf("enshwick", dir));
            Assert.Equal("you.real", NetAdvertise.EngineIdentity(dir, "enshwick").Id);
            Assert.Equal("2.0.0", NetAdvertise.EngineIdentity(dir, "enshwick").Version);
        });
    }

    [Fact]
    public void Enshwick_dll_requirement_ignores_unrelated_mod_json()
    {
        WithPack("", dir =>
        {
            File.Delete(Path.Combine(dir, "pack.toml"));
            File.WriteAllBytes(Path.Combine(dir, "mod.dll"), System.Text.Encoding.ASCII.GetBytes("MZ\0enshwick_mod_query\0"));
            File.WriteAllText(Path.Combine(dir, "mod.json"), "{\"id\":\"you.spoof\",\"side\":\"server\",\"clients\":\"none\"}");
            Assert.True(NetAdvertise.EngineKindOf("enshwick", dir));
            Assert.Equal("folder", NetAdvertise.EngineIdentity(dir, "enshwick").Id);
        });
    }

    [Fact]
    public void Oversized_invalid_utf8_and_symlinked_packs_are_not_advertised()
    {
        WithPack("id = 'you.real'\n", dir =>
        {
            var file = Path.Combine(dir, "pack.toml");
            File.WriteAllText(file, "id = 'you.real'\n#" + new string('x', 1024 * 1024));
            Assert.Null(NetAdvertise.EngineKindOf("enshwick", dir));
            File.WriteAllBytes(file, [0xff, 0xfe, 0x61, 0]);
            Assert.Null(NetAdvertise.EngineKindOf("enshwick", dir));
            File.WriteAllText(file, "id = 'you.real'\nname = '" + new string('é', 65) + "'\n");
            Assert.Null(NetAdvertise.EngineKindOf("enshwick", dir));
            if (!OperatingSystem.IsWindows())
            {
                var target = Path.Combine(dir, "real.toml");
                File.Move(file, target);
                File.WriteAllText(target, "id = 'you.real'\n");
                File.CreateSymbolicLink(file, target);
                Assert.Null(NetAdvertise.EngineKindOf("enshwick", dir));
            }
        });
    }

    private static void WithPack(string toml, Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "kiln-pack-advert-" + Guid.NewGuid());
        var dir = Path.Combine(root, "folder");
        Directory.CreateDirectory(dir);
        try { File.WriteAllText(Path.Combine(dir, "pack.toml"), toml); test(dir); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Shroudforge_advertisement_uses_the_configured_mods_folder()
    {
        var game = Path.Combine(Path.GetTempPath(), "kiln-sf-advert-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(game, "shroudforge/config"));
        try
        {
            File.WriteAllText(Path.Combine(game, "shroudforge/config/modloader-config.json"), "{\"paths\":{\"mods\":\"custom\\\\packages\"}}");
            Assert.Equal(Path.Combine(game, "custom", "packages"), NetAdvertise.ShroudForgeRoot(game));
        }
        finally { Directory.Delete(game, true); }
    }
}
