// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class LocaleTests
{
    [Fact]
    public void LocaleFilesInTwoLanguagesAndTheLanguageSwitch()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.LoadFolder(TestMods.Folder("TkLocale"));
        kiln.Step();
        Assert.True(mod.IsLoaded, mod.Error);
        Assert.Equal("1.2.0", mod.Info.Version);
        Assert.True(File.Exists(Path.Combine(mod.Folder, "locales", "de.json")));
        Assert.Equal("en-us", kiln.Language);
        Assert.Equal(["de", "en"], mod.Context.Locale.Languages);

        kiln.Run(() => Mods.Send("tk-locale", "greet"));
        kiln.Step();
        kiln.Language = "de-DE";
        Assert.Equal("de-de", Game.Language);
        kiln.Run(() => Mods.Send("tk-locale", "greet"));
        kiln.Step();
        Assert.Equal(["text: Hello Ada / English only / no.such.key", "text: Hallo Ada / English only / no.such.key"],
            kiln.Lines("tk-locale", "text:"));
        Assert.True(kiln.HasLog("text 'no.such.key' of mod tk-locale", "tk-locale"));
    }

    [Fact]
    public void TheLanguageIsEnglishAgainInTheNextHarness()
    {
        using (var kiln = new KilnHarness()) kiln.Language = "fr";
        using (var kiln = new KilnHarness()) Assert.Equal("en-us", kiln.Language);
    }
}
