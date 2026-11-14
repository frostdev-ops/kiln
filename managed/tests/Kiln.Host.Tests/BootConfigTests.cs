// SPDX-License-Identifier: MIT
// A boot mod's setting in OnBoot, through Kiln.Host's own boot author (RunBootAuthor): the mod reads its config in OnBoot and stages a
// definition with the value, and its runtime registration of the same config sees the same value. The mod is this test assembly,
// loaded by the author into its own collectible context, so the author's AltarBootSettings is another type than the one the test's
// "runtime" registers, as on a server (the author's assembly is unloaded after OnBoot).
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class AltarBootSettings
{
    [Comment("Flame altars per flame level.")]
    [Range(1, 100)]
    public int Altars { get; set; } = 3;
}

/// <summary>The boot mod: "the number is a setting".</summary>
public sealed class AltarBootMod : Kiln.Mod
{
    public override void OnBoot(BootContext boot)
    {
        var altars = Config.Register<AltarBootSettings>().Value.Altars;
        var staged = BootDefinitions.For(boot).Define("altars", new StatusBlock { Name = "Altars", Seconds = altars });
        if (!staged.Ok) throw new InvalidOperationException(staged.Reason);
    }
}

[Collection("boot generation")]
public sealed class BootConfigTests : ModsFixture
{
    private const string Manifest = """
        { "id": "more-altars", "name": "More Altars", "version": "1.0.0", "apiVersion": "0.2", "side": "server", "boot": true,
          "entryAssembly": "Kiln.Host.Tests.dll", "entryType": "Kiln.Host.Tests.AltarBootMod" }
        """;

    [Fact]
    public void A_boot_mod_reads_its_setting_in_OnBoot_stages_with_it_and_its_runtime_sees_the_same_value()
    {
        var root = KilnPaths.Root;
        KilnPaths.Root = Root;
        KilnPaths.Config = null!;
        try
        {
            Config.Reset();
            var dir = WriteManifest("more-altars", Manifest);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Kiln.Host.Tests.dll"), Path.Combine(dir, "Kiln.Host.Tests.dll"));
            var file = Path.Combine(Root, "config", "more-altars.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{ \"altars\": 7 }\n");

            var host = new FakeHost();
            using var mods = NewManager(host);
            var boot = new BootContext("more-altars", 1, BootRoles.DedicatedServer, new UnavailableGameData());
            Assert.True(mods.RunBootAuthor(Kiln.Host.Manifest.Read(dir), dir, 77, boot, out var why), why);
            var staged = Assert.Single(boot.Staged);
            Assert.Equal("altars", staged.Key);
            Assert.Equal(7, (int?)JsonNode.Parse(staged.Json)?["seconds"]);  // staged with the file's value, not the default 3
            Assert.Equal("{ \"altars\": 7 }\n", File.ReadAllText(file));      // OnBoot wrote nothing

            // the runtime instance (its OnLoad, on the tick thread) registers the same config with its own type
            var runtime = HostAccess.CreateContext(new ModInfo("more-altars", "More Altars", "1.0.0", dir), 78, Path.Combine(DataDir, "more-altars"));
            HostAccess.Current = runtime;
            ModConfig<AltarBootSettings> cfg;
            try
            {
                cfg = Config.Register<AltarBootSettings>();
            }
            finally
            {
                HostAccess.Current = null;
            }
            Assert.Equal(7, cfg.Value.Altars);
            Assert.Contains("\"altars\": 7", File.ReadAllText(file));  // written now, in its canonical form
            runtime.Retire();
        }
        finally
        {
            Config.Reset();
            KilnPaths.Root = root;
            KilnPaths.Config = null!;
        }
    }
}
