// SPDX-License-Identifier: MIT
using TkSaves;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class WorldTests
{
    private const ulong AdaSteam = 76561198000000201;

    /// <summary>A server folder outside the harness's own temp folder, so harnesses can share it; removed at the end.</summary>
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kiln-testkit-world-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static KilnHarness On(Folder f, Guid? world = null) => new(new KilnHarnessOptions { ServerDirectory = f.Path, WorldId = world });

    private static string Get(KilnHarness kiln, string key) => string.Join("", kiln.RunCommand($"tk-saves-get {key}").Output);

    [Fact]
    public void EveryHarnessHasAWorldFromTheStart()
    {
        using var kiln = new KilnHarness();
        var id = kiln.World.Id;
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(0u, kiln.World.Revision);
        Assert.Equal(Path.Combine(kiln.DataDirectory, "worlds", id.ToString()), kiln.World.Directory);
        kiln.Load<TkSavesMod>();
        kiln.Step();
        Assert.Equal([$"load: world {id} loaded None box 0 keys box"], kiln.Lines("tk-saves", "load:"));
        Assert.False(kiln.World.Loaded);
        kiln.Step();  // the world loads at the first step after the server started
        Assert.True(kiln.World.Loaded);
        Assert.Equal([$"world loaded: {id} revision 0 new True data None"], kiln.Lines("tk-saves", "world loaded:"));
        Assert.Equal(id.ToString(), kiln.Run(() => Saves.WorldId));
        Assert.Null(kiln.Run(() => Saves.WorldFile));  // the kit writes no world file
    }

    [Fact]
    public void ASaveRunsOverTheNextStepsAndASecondHarnessReadsIt()
    {
        using var folder = new Folder();
        Guid id;
        using (var kiln = On(folder))
        {
            id = kiln.World.Id;
            kiln.Load<TkSavesMod>();
            kiln.Step(2);
            Assert.True(kiln.RunCommand("tk-saves-set box 41").Ok);
            var at = kiln.CurrentStep;
            Assert.Equal(1u, kiln.World.Save());
            Assert.Equal(at + 2, kiln.CurrentStep);  // accepted at the next step, written at the one after
            var saving = Assert.Single(kiln.LogsOf("tk-saves"), l => l.Text.StartsWith("saving:", StringComparison.Ordinal));
            Assert.Equal($"saving: Request revision 1 world {id}", saving.Text);
            Assert.Equal(at + 1, saving.Step);
            var saved = Assert.Single(kiln.LogsOf("tk-saves"), l => l.Text.StartsWith("saved:", StringComparison.Ordinal));
            Assert.Equal("saved: Request ok True revision 1", saved.Text);
            Assert.Equal(at + 2, saved.Step);
            Assert.Single(Directory.GetFiles(Path.Combine(kiln.World.Directory, "manifests"), "0000000001-*.json"));
            kiln.Kill();  // no shutdown save
        }
        using (var kiln = On(folder))
        {
            Assert.Equal(id, kiln.World.Id);  // the world the folder ran last
            Assert.Equal(1u, kiln.World.Revision);
            kiln.Load<TkSavesMod>();
            kiln.Step();
            Assert.Equal([$"load: world {id} loaded Exact box 41 keys box"], kiln.Lines("tk-saves", "load:"));
            Assert.Equal("41", Get(kiln, "box"));
        }
    }

    [Fact]
    public void WhatChangedAfterTheLastSaveIsGoneAfterAKillAndKeptByAStop()
    {
        using var folder = new Folder();
        using (var kiln = On(folder))
        {
            kiln.Load<TkSavesMod>();
            kiln.Step(2);
            kiln.RunCommand("tk-saves-set a 1");
            kiln.World.Save();
            kiln.RunCommand("tk-saves-set a 2");
            kiln.RunCommand("tk-saves-set b 5");
            kiln.Step(10);
            kiln.Kill();
            Assert.Equal(HarnessState.Stopped, kiln.State);
            Assert.Empty(kiln.Lines("tk-saves", "saving: Shutdown"));
        }
        using (var kiln = On(folder))
        {
            kiln.Load<TkSavesMod>();
            kiln.Step();
            Assert.Equal("1", Get(kiln, "a"));
            Assert.Equal("none", Get(kiln, "b"));
            kiln.RunCommand("tk-saves-set b 6");
            kiln.Step();
            // a stop makes the game's shutdown save: mods hear of it at ServerStopping, before they unload
        }
        using (var kiln = On(folder))
        {
            Assert.Equal(2u, kiln.World.Revision);
            kiln.Load<TkSavesMod>();
            kiln.Step();
            Assert.Equal("6", Get(kiln, "b"));
            Assert.Equal("1", Get(kiln, "a"));
        }
    }

    [Fact]
    public void TheShutdownSaveIsAnnouncedToModsInEveryHarness()
    {
        using var folder = new Folder();
        for (var i = 0; i < 2; i++)
        {
            using var kiln = On(folder);
            kiln.Load<TkSavesMod>();
            kiln.Step(2);
            kiln.Stop();
            var saving = kiln.Lines("tk-saves", "saving:");
            Assert.Equal([$"saving: Shutdown revision {i + 1} world {kiln.World.Id}"], saving);
            Assert.Equal((uint)(i + 1), kiln.World.Revision);
        }
    }

    [Fact]
    public void AModsOwnRequestCompletesWhenTheWorldIsWritten()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkSavesMod>();
        kiln.Step(2);
        kiln.RunCommand("tk-saves-save");
        kiln.Step();
        Assert.Empty(kiln.Lines("tk-saves", "request done:"));
        Assert.Single(kiln.Lines("tk-saves", "saving: Request revision 1"));
        kiln.Step();
        Assert.Equal(["request done: ok True revision 1 reason Request tick thread True"], kiln.Lines("tk-saves", "request done:"));
        Assert.Equal(1u, kiln.World.Revision);
    }

    [Fact]
    public void TheGameSavesWhenAPlayerLeavesAndOnItsAutosaveTimer()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkSavesMod>();
        kiln.Step(2);
        var ada = kiln.Players.Join("Ada", AdaSteam);
        kiln.Step();
        ada.Leave();
        kiln.Step(3);
        Assert.Equal(["saving: PlayerLeft revision 1"], kiln.Lines("tk-saves", "saving:").Select(l => l[..29]).ToList());
        Assert.Equal(1u, kiln.World.Revision);
        kiln.Step(30_000 / 40);  // 30 s of virtual time since that save
        Assert.Equal(["saving: Autosave revision 2"], kiln.Lines("tk-saves", "saving: Autosave").Select(l => l[..27]).ToList());
    }

    [Fact]
    public void TwoModsCannotSeeEachOthersKeysAndAReloadKeepsTheData()
    {
        using var kiln = new KilnHarness();
        var a = kiln.Load<TkSavesMod>();
        kiln.Load<TkSavesMod>(new ModManifest("tk-saves-b"));
        kiln.Step(2);
        kiln.RunCommand("tk-saves-set box 3");
        Assert.Equal("3", Get(kiln, "box"));
        Assert.Equal(["0"], kiln.RunCommand("tk-saves-b-get box").Output);  // its own "box", created at its load
        kiln.RunCommand("tk-saves-b-set mine 8");
        Assert.Equal("none", Get(kiln, "mine"));
        Assert.Equal(["8"], kiln.RunCommand("tk-saves-b-get mine").Output);
        kiln.Reload(a);
        Assert.Equal("3", Get(kiln, "box"));
        Assert.Contains("load: world", kiln.Lines("tk-saves", "load:").Last());
        Assert.EndsWith("box 3 keys box", kiln.Lines("tk-saves", "load:").Last());
    }

    [Fact]
    public void PlayerDataAndEntityDataOnAPlayerRoundTripThroughASave()
    {
        using var folder = new Folder();
        using (var kiln = On(folder))
        {
            kiln.Load<TkSavesMod>();
            kiln.Step(2);
            var ada = kiln.Players.Join("Ada", AdaSteam);
            kiln.Step();
            Assert.Equal(["False"], kiln.RunCommand("tk-saves-entity-available").Output);  // needs kiln.spawn
            Assert.True(kiln.RunCommand($"tk-saves-eset {ada.Entity.Value} 9").Ok);  // a player entity: PlayerData by Steam id
            Assert.Equal(["9"], kiln.RunCommand($"tk-saves-pget {AdaSteam}").Output);
            kiln.RunCommand($"tk-saves-pset {AdaSteam + 1} 4");  // a player who is not online
            var other = kiln.RunCommand("tk-saves-eset 2000 1");  // not a player: needs kiln.spawn's entity lookups
            Assert.False(other.Ok);
            Assert.Contains(kiln.LogsOf("tk-saves"), l => l.Text.Contains("EntityData needs kiln.spawn", StringComparison.Ordinal));
            kiln.World.Save();
            kiln.Kill();
        }
        using (var kiln = On(folder))
        {
            kiln.Load<TkSavesMod>(manifest: null);
            kiln.Step();
            Assert.Equal(["9"], kiln.RunCommand($"tk-saves-pget {AdaSteam}").Output);
            Assert.Equal(["4"], kiln.RunCommand($"tk-saves-pget {AdaSteam + 1}").Output);
            var ada = kiln.Players.Join("Ada", AdaSteam);  // another slot is fine: the data follows the Steam id
            kiln.Players.Join("Bob");
            kiln.Step();
            Assert.Equal(["9"], kiln.RunCommand($"tk-saves-eget {ada.Entity.Value}").Output);
            Assert.Equal(["none"], kiln.RunCommand("tk-saves-eget 2").Output);
        }
    }

    [Fact]
    public void PerWorldConfigLandsInTheWorldsFolderAndDiffersBetweenWorlds()
    {
        using var folder = new Folder();
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        string firstFile;
        using (var kiln = On(folder, one))
        {
            kiln.Load<TkSavesMod>();
            kiln.Step();
            var line = Assert.Single(kiln.Lines("tk-saves", "config:"));
            firstFile = line[(line.IndexOf(" at ", StringComparison.Ordinal) + 4)..];
            Assert.StartsWith(Path.Combine(kiln.World.Directory, "config") + Path.DirectorySeparatorChar, firstFile);
            Assert.Contains(one.ToString(), firstFile);
            Assert.True(File.Exists(firstFile));
            File.WriteAllText(firstFile, """{ "Label": "first world" }""");
        }
        using (var kiln = On(folder, two))
        {
            Assert.Equal(two, kiln.World.Id);
            kiln.Load<TkSavesMod>();
            kiln.Step();
            var line = Assert.Single(kiln.Lines("tk-saves", "config:"));
            Assert.StartsWith("config: default at ", line);
            Assert.Contains(two.ToString(), line);
        }
        using (var kiln = On(folder, one))
        {
            kiln.Load<TkSavesMod>();
            kiln.Step();
            Assert.Equal([$"config: first world at {firstFile}"], kiln.Lines("tk-saves", "config:"));
        }
    }

    [Fact]
    public void SwitchedOffTheSaveRequestIsRefused()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkSavesMod>();
        kiln.Step(2);
        kiln.Capabilities.Override("kiln.saves", false);
        kiln.RunCommand("tk-saves-save");
        kiln.Step();
        Assert.Equal(["request done: ok False revision 0 reason Request tick thread True"], kiln.Lines("tk-saves", "request done:"));
        var status = kiln.Run(() => Saves.Status)!;  // switched off, the service says nothing works
        Assert.Equal("none", status.State);
        Assert.False(status.CanRequest);
        kiln.Capabilities.ClearOverride("kiln.saves");
        Assert.Equal(1u, kiln.World.Save());
    }

    [Fact]
    public void ABackupIsUnavailable()
    {
        using var kiln = new KilnHarness();
        kiln.Load<TkSavesMod>();
        kiln.Step(2);
        var e = Assert.Throws<InvalidOperationException>(() => kiln.Run(() => Saves.Backup("before-test")).GetAwaiter().GetResult());
        Assert.Contains("rc 3", e.Message);
    }

    [Fact]
    public void TheResetOfKilnSavesFindsEveryMemberItNeeds()
    {
        // fails when Kiln.Saves renames or drops one of them (T2-needs N1): the kit must not silently keep a world open
        var m = SavesReset.Resolve(typeof(WorldData).Assembly);
        Assert.Equal(SavesReset.RuntimeFields.Length, m.Flags.Length);
        Assert.Equal("CloseWorld", m.CloseWorld.Name);
        Assert.Equal(typeof(bool), m.Closed.FieldType);
    }
}
