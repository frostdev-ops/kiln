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
