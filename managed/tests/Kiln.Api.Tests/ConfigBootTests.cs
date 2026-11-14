// SPDX-License-Identifier: MIT
// Config in Mod.OnBoot (08's boot phase): a boot mod reads its setting while its boot-author frame is open, on the boot thread, before
// the first tick. The file and its defaults are read as in OnLoad, nothing is written, and the runtime's registration takes over that
// read (the author's T and the runtime's T are different types: the author's assembly is unloaded after OnBoot).
using Xunit;

namespace Kiln.Api.Tests;

public sealed class AltarSettings
{
    [Comment("Flame altars per flame level.")]
    [Range(1, 100)]
    public int Altars { get; set; } = 3;

    public string Label { get; set; } = "altars";
}

// The same schema as another type: what the mod's runtime instance registers (its own load context's AltarSettings).
public sealed class AltarSettingsRuntime
{
    [Comment("Flame altars per flame level.")]
    [Range(1, 100)]
    public int Altars { get; set; } = 3;

    public string Label { get; set; } = "altars";
}

public sealed class ConfigBootTests : KilnTest
{
    private string FilePath(string id) => Path.Combine(Root, "config", id + ".json");

    // As Kiln.Host's RunBootAuthor: the author's context is current, the frame open, the thread is not the tick thread.
    private T InBoot<T>(ModContext author, Func<T> body)
    {
        var frame = new BootContext(author.Info.Id, 1, BootRoles.DedicatedServer, new UnavailableGameData());
        BootFrame.Current = frame;
        Backend.TickThread = false;
        try
        {
            return As(author, body);
        }
        finally
        {
            frame.Open = false;
            BootFrame.Current = null;
            Backend.TickThread = true;
        }
    }

    private void InBoot(ModContext author, Action body) => InBoot<int>(author, () =>
    {
        body();
        return 0;
    });

    [Fact]
    public void OnBoot_reads_the_setting_from_the_file_without_writing_anything()
    {
        const string text = "{ \"altars\": 7 }\n";
        File.WriteAllText(FilePath("altars"), text);
        var cfg = InBoot(NewMod("altars"), () => Config.Register<AltarSettings>());
        Assert.Equal(7, cfg.Value.Altars);
        Assert.Equal("altars", cfg.Value.Label);              // the default of an entry the file does not have
        Assert.Equal(text, File.ReadAllText(FilePath("altars")));  // not rewritten in its canonical form
        Assert.False(Directory.Exists(Path.Combine(Root, "config", ".schema")));  // no schema snapshot yet
        Assert.DoesNotContain(Config.All, f => f.Id == "altars");   // the registry gets it from the runtime
    }

    [Fact]
    public void OnBoot_without_a_file_has_the_defaults_and_the_runtime_writes_the_file()
    {
        var cfg = InBoot(NewMod("altars"), () => Config.Register<AltarSettings>());
        Assert.Equal(3, cfg.Value.Altars);
        Assert.False(File.Exists(FilePath("altars")));
        var runtime = As(NewMod("altars"), () => Config.Register<AltarSettingsRuntime>());
        Assert.Equal(3, runtime.Value.Altars);
        Assert.Contains("\"altars\": 3", File.ReadAllText(FilePath("altars")));
        Assert.Contains(Config.All, f => f.Id == "altars");
    }

    [Fact]
    public void The_runtime_takes_over_what_OnBoot_read_and_does_not_read_the_unchanged_file_again()
    {
        File.WriteAllText(FilePath("altars"), "{ \"altars\": 7 }\n");
        var when = File.GetLastWriteTimeUtc(FilePath("altars"));
        var boot = InBoot(NewMod("altars"), () => Config.Register<AltarSettings>());
        Assert.Equal(7, boot.Value.Altars);
        // the same length and time: to the file's stamp nothing changed, so a value read now would come from another read
        File.WriteAllText(FilePath("altars"), "{ \"altars\": 9 }\n");
        File.SetLastWriteTimeUtc(FilePath("altars"), when);
        var runtime = As(NewMod("altars"), () => Config.Register<AltarSettingsRuntime>());
        Assert.Equal(7, runtime.Value.Altars);  // OnBoot's read: both instances of the mod see the value it staged with
        Assert.Contains("\"altars\": 7", File.ReadAllText(FilePath("altars")));  // now written, in its canonical form
        Assert.True(Directory.Exists(Path.Combine(Root, "config", ".schema")));
    }

    [Fact]
    public void A_file_changed_after_OnBoot_is_read_again_by_the_runtime_and_said()
    {
        File.WriteAllText(FilePath("altars"), "{ \"altars\": 7 }\n");
        InBoot(NewMod("altars"), () => Config.Register<AltarSettings>());
        File.WriteAllText(FilePath("altars"), "{ \"altars\": 12, \"label\": \"more\" }\n");
        var runtime = As(NewMod("altars"), () => Config.Register<AltarSettingsRuntime>());
        Assert.Equal(12, runtime.Value.Altars);
        Assert.True(Backend.Logged("changed after the mod's OnBoot read it"), string.Join("\n", Backend.Lines));
    }

    [Fact]
    public void Writes_and_change_handlers_are_refused_in_OnBoot_with_what_to_do_instead()
    {
        File.WriteAllText(FilePath("altars"), "{ \"altars\": 7 }\n");
        InBoot(NewMod("altars"), () =>
        {
            var cfg = Config.Register<AltarSettings>();
            var set = Assert.Throws<InvalidOperationException>(() => cfg.Set("altars", 8));
            Assert.Equal("ConfigFile.Set is not available in OnBoot (it runs before the game's first tick); call it in OnLoad", set.Message);
            Assert.Contains("ConfigFile.Save is not available in OnBoot", Assert.Throws<InvalidOperationException>(cfg.Save).Message);
            Assert.Contains("ConfigFile.Reload is not available in OnBoot", Assert.Throws<InvalidOperationException>(cfg.Reload).Message);
            Assert.Contains("ConfigFile.Changed is not available in OnBoot",
                Assert.Throws<InvalidOperationException>(() => cfg.Changed += _ => { }).Message);
            Assert.Equal(7, cfg.Value.Altars);
        });
        Assert.Equal("{ \"altars\": 7 }\n", File.ReadAllText(FilePath("altars")));
    }

    [Fact]
    public void One_OnBoot_registers_a_config_once_and_a_later_boot_frame_may_again()
    {
        var author = NewMod("altars");
        InBoot(author, () =>
        {
            Config.Register<AltarSettings>();
            Assert.Contains("already registered", Assert.Throws<InvalidOperationException>(() => Config.Register<AltarSettings>()).Message);
        });
        var again = InBoot(author, () => Config.Register<AltarSettings>());  // another frame (another process start in the host)
        Assert.Equal(3, again.Value.Altars);
    }

    [Fact]
    public void After_the_frame_closed_Register_is_the_tick_thread_member_it_always_was()
    {
        Backend.TickThread = false;
        try
        {
            var e = Assert.Throws<InvalidOperationException>(() => As(NewMod("late"), () => Config.Register<AltarSettings>()));
            Assert.Contains("tick thread", e.Message);
        }
        finally
        {
            Backend.TickThread = true;
        }
    }
}
