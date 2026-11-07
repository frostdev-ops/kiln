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
