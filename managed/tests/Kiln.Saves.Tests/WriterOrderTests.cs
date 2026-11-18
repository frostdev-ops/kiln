// SPDX-License-Identifier: MIT
// Kiln.Saves' background writer (0.2.0-dev.29.3, issue #39) keeps every guarantee the tick thread's own writes gave:
// a world file never reaches the disk before its generation's pending manifest (kiln.saves' stage barrier), the pending
// manifest comes before its confirmation, a crash at any point of the writer leaves what a crash left before, and a
// reload, a world close or the shutdown save waits for what is still being written. A "crash" here is the writer frozen
// on one file operation while the test copies the folder, which is what kill -9 at that moment leaves on disk.
using System.Diagnostics;
using System.Reflection;
using Xunit;
using static Kiln.InstanceHarness;

namespace Kiln;

[Collection("instance data")]
public sealed class WriterOrderTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly InstanceHarness _h = new();
    private readonly Action<string>? _hook = WorldStore.IoHook;
    private readonly List<string> _copies = [];

    private readonly List<Freeze> _freezes = [];

    public void Dispose()
    {
        WorldStore.IoHook = _hook;
        foreach (var f in _freezes) f.Release();  // a test that failed while the writer was frozen lets it go on
        _h.Dispose();
        foreach (var c in _copies)
            try
            {
                Directory.Delete(c, recursive: true);
            }
            catch (IOException)
            {
            }
    }

    private string Pending(uint revision) => Path.Combine(_h.Root, "manifests", $"{revision:D10}.pending.json");
    private string Confirmed(uint revision, uint unix) => Path.Combine(_h.Root, "manifests", $"{revision:D10}-{unix}.json");

    private static void FiveBundles(int value)
    {
        for (var i = 0; i < 5; i++)
            Store.GetBundleRaw($"world/mod-{i}", $"mod-{i}").Keys["state"] = new WorldStore.Slot { Json = $"{{\"v\":{value + i}}}" };
    }

    private static void Set(int mod, int value) => Store.GetBundleRaw($"world/mod-{mod}", $"mod-{mod}").Keys["state"].Json = $"{{\"v\":{value}}}";

    private static int Value(WorldStore store, int mod) =>
        int.Parse(store.FindBundle($"world/mod-{mod}")!.Keys["state"].Json!.Split(':')[1].TrimEnd('}'));

    private static void Dispatch(uint kind, ulong seq, uint revision, ulong saveSeq = 0, uint unix = 0, SaveReason reason = SaveReason.Autosave) =>
        typeof(SavesRuntime).GetMethod("Dispatch", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
            [new SavesNative.Event { Kind = kind, Seq = seq, Revision = revision, SaveSeq = saveSeq, WorldUnix = unix, Reason = (uint)reason }]);

    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    /// <summary>A whole save through the events, the world write waiting as kiln.saves' barrier makes it wait.</summary>
    private static void SaveThroughEvents(uint gen, ulong seq, SaveReason reason = SaveReason.Autosave)
    {
        Dispatch(SavesNative.EvSaving, seq, gen, reason: reason);
        Dispatch(SavesNative.EvSnapshot, seq + 1, gen, saveSeq: seq, reason: reason);
        FakeSavesTable.WaitLikeTheWorldWrite(Long);
        Dispatch(SavesNative.EvWritten, seq + 2, gen, saveSeq: seq, unix: 1000 + gen, reason: reason);
    }

    /// <summary>The writer stops before its <paramref name="nth"/> file operation named <paramref name="op"/> (on the writer's
    /// thread) until <see cref="Freeze.Release"/>.</summary>
    private sealed class Freeze
    {
        private readonly ManualResetEventSlim _reached = new(), _gate = new();
        private int _seen;

        public Freeze(string op, int nth = 1, int tick = -1, TimeSpan? gate = null)
        {
            var hold = gate ?? Long;
            var tickThread = tick < 0 ? Environment.CurrentManagedThreadId : tick;
            WorldStore.IoHook = o =>
            {
                if (o != op || Environment.CurrentManagedThreadId == tickThread || Interlocked.Increment(ref _seen) != nth) return;
                _reached.Set();
                _gate.Wait(hold);  // never for good: a failed test must not leave the writer stuck for the next ones
            };
        }

        public void Reached() => Assert.True(_reached.Wait(Long), "the writer never got there");

        public void Release() => _gate.Set();
    }

    private Freeze FreezeAt(string op, int nth = 1, TimeSpan? gate = null)
    {
        var f = new Freeze(op, nth, gate: gate);
        _freezes.Add(f);
        return f;
    }

    /// <summary>What kill -9 now leaves: the sidecar folder as it is on disk this moment.</summary>
    private string CopyAsACrashLeavesIt()
    {
        var copy = Path.Combine(AppContext.BaseDirectory, "instance-tests", "crash-" + Guid.NewGuid().ToString("N"));
        foreach (var f in Directory.EnumerateFiles(_h.Root, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(copy, Path.GetRelativePath(_h.Root, f));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(f, to);
        }
        Directory.CreateDirectory(Path.Combine(copy, "manifests"));
        Directory.CreateDirectory(Path.Combine(copy, "blobs"));
        _copies.Add(copy);
        return copy;
    }

    /// <summary>The next start, on that folder, with the world file of <paramref name="revision"/> on disk.</summary>
    private static WorldStore StartOn(string folder, uint revision)
    {
        var store = new WorldStore();
        typeof(WorldStore).GetProperty("Root")!.SetValue(store, folder);
        typeof(WorldStore).GetProperty("WorldId")!.SetValue(store, "instances-unit");
        typeof(WorldStore).GetField("_opened", Hidden)!.SetValue(store, true);
        typeof(WorldStore).GetMethod("Load", Hidden)!.Invoke(store, [revision, 1000 + revision]);
        return store;
    }

    [Fact]
    public void TheTickThreadReturnsBeforeTheFilesAndTheWorldWriteWaitsForThem()
    {
        using var native = new FakeSavesTable();
        FiveBundles(1);
        SaveThroughEvents(1, 100);
        Written();
        Set(0, 10);
        var freeze = FreezeAt("rename");                    // the new blob's rename: nothing of generation 2 in place yet
        Dispatch(SavesNative.EvSaving, 200, 2);
        Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
        freeze.Reached();
        Assert.True(FakeSavesTable.Issued > FakeSavesTable.Done);   // kiln.saves holds the world write: the writer is behind
        Assert.False(File.Exists(Pending(2)));
        var start = Stopwatch.StartNew();
        var write = Task.Run(() => FakeSavesTable.WaitLikeTheWorldWrite(Long));
        Assert.False(write.Wait(300));                         // the world write does not go while the manifest is not there
        freeze.Release();
        write.Wait(Long);
        Assert.True(File.Exists(Pending(2)));                  // when it may go, the pending manifest is on disk
        Assert.Equal(FakeSavesTable.Issued, FakeSavesTable.Done);
        Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
        Assert.Equal(DataMatch.Exact, _h.Restart(2, new FakeInstanceWorld()));
        Assert.Equal(10, Value(Store, 0));
    }

    [Fact]
    public void ACrashAtAnyPointOfTheWriterLeavesWhatACrashLeftBefore()
    {
        using var native = new FakeSavesTable();
        FiveBundles(1);
        SaveThroughEvents(1, 100);
        Written();
        Set(0, 10);

        // 1. During the staging (its blob is in place, its manifest is not): the world write has not happened (the
        //    barrier), so the world file on disk is revision 1, whose generation is confirmed.
        var freeze = FreezeAt("create", nth: 2);
        Dispatch(SavesNative.EvSaving, 200, 2);
        Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
        freeze.Reached();
        var crash = CopyAsACrashLeavesIt();
        freeze.Release();
        var after = StartOn(crash, 1);
        Assert.Equal(DataMatch.Exact, after.Match);
        Assert.Equal(1, Value(after, 0));

        // 2. After the world write, before the confirmation is in place: generation 2, staged, with its own data.
        FakeSavesTable.WaitLikeTheWorldWrite(Long);
        freeze = FreezeAt("rename");
        Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
        freeze.Reached();
        crash = CopyAsACrashLeavesIt();
        freeze.Release();                                      // (the next start's load waits for the writer)
        after = StartOn(crash, 2);
        Assert.Equal(DataMatch.Staged, after.Match);
        Assert.Equal(10, Value(after, 0));

        // 3. The confirmation in place, the pending manifest not deleted yet: generation 2, exact.
        Written();
        Set(1, 20);
        freeze = FreezeAt("delete");
        SaveThroughEvents(3, 300);
        freeze.Reached();
        crash = CopyAsACrashLeavesIt();
        freeze.Release();
        after = StartOn(crash, 3);
        Assert.Equal(DataMatch.Exact, after.Match);
        Assert.Equal(20, Value(after, 1));
        Written();
        Assert.False(File.Exists(Pending(3)));
        Assert.True(File.Exists(Confirmed(3, 1003)));
        Assert.Empty(Directory.EnumerateFiles(_h.Root, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void TheSnapshotStagingReplacesTheRequestsStagingWhileItWaitsAndTheWorldWriteWaitsForTheNewest()
    {
        using var native = new FakeSavesTable();
        var log = new Capture();
        var backend = Backend.Current;
        try
        {
            Backend.Current = log;
            FiveBundles(1);
            SaveThroughEvents(1, 100);
            Written();
            var freeze = FreezeAt("delete");                // the writer is busy with generation 2's confirmation (a save that
            SaveThroughEvents(2, 200);                         // changed nothing), frozen on deleting its pending manifest
            freeze.Reached();
            var writes = Interlocked.Read(ref WorldStore.FilesWritten);
            Set(2, 30);
            Dispatch(SavesNative.EvSaving, 300, 3);            // queued behind the frozen confirmation
            Set(3, 40);                                        // the last simulation step changed more
            Dispatch(SavesNative.EvSnapshot, 301, 3, saveSeq: 300);   // replaces the save request's staging, never written
            var issued = FakeSavesTable.Issued;
            freeze.Release();
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            Assert.Equal(issued, FakeSavesTable.Done);
            Assert.Equal(3L, Interlocked.Read(ref WorldStore.FilesWritten) - writes);  // two new blobs and one manifest, once
            var line = log.Single("Kiln.Saves: staged mod data generation 3 (autosave, snapshot staging)");
            Assert.Contains("replacing the save request's staging before it was written", line.Text);
            Assert.DoesNotContain(log.Lines, l => l.Text.StartsWith("Kiln.Saves: staged mod data generation 3 (autosave):", StringComparison.Ordinal));
            Dispatch(SavesNative.EvWritten, 302, 3, saveSeq: 300, unix: 1003);
            Assert.Equal(DataMatch.Exact, _h.Restart(3, new FakeInstanceWorld()));
            Assert.Equal(30, Value(Store, 2));
            Assert.Equal(40, Value(Store, 3));
        }
        finally
        {
            Written();
            Backend.Current = backend;
        }
    }

    [Fact]
    public void PruningStopsForAStagingSoTheWorldWriteDoesNotWaitForItAndRunsAgainAtTheNextConfirmation()
    {
        using var native = new FakeSavesTable();
        var log = new Capture();
        var backend = Backend.Current;
        try
        {
            Backend.Current = log;
            FiveBundles(1);
            SaveThroughEvents(1, 100);
            Written();
            typeof(WorldStore).GetField("_commits", Hidden)!.SetValue(Store, 10);   // the next confirmation prunes
            Dispatch(SavesNative.EvSaving, 200, 2);
            Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            var freeze = FreezeAt("list");                  // pruning's first folder listing
            Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
            freeze.Reached();
            WorldStore.IoHook = op =>
            {
                if (op is "list" or "read") Thread.Sleep(400);  // the rest of pruning is slow
            };
            Set(0, 50);
            Dispatch(SavesNative.EvSaving, 300, 3);           // a staging comes while pruning runs
            freeze.Release();
            var waited = FakeSavesTable.WaitLikeTheWorldWrite(Long);
            // the rest of pruning would take 4 x 400 ms (two manifests read, two folders listed); it stops at its next file
            Assert.True(waited < TimeSpan.FromMilliseconds(1000), $"the world write waited {waited.TotalMilliseconds:F0} ms behind pruning");
            Written();
            WorldStore.IoHook = _hook;
            Assert.Contains("(stopped for a staging; it runs again at the next confirmation)", log.Single("Kiln.Saves: confirmed mod data generation 2 ").Text);
            Dispatch(SavesNative.EvSnapshot, 301, 3, saveSeq: 300);
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            Dispatch(SavesNative.EvWritten, 302, 3, saveSeq: 300, unix: 1003);
            Written();
            var third = log.Single("Kiln.Saves: confirmed mod data generation 3 ").Text;
            Assert.Contains("; pruning ", third);                // the 12th confirmation prunes, as the 11th's stopped
            Assert.DoesNotContain("stopped", third);
        }
        finally
        {
            WorldStore.IoHook = _hook;
            Written();
            Backend.Current = backend;
        }
    }

    [Fact]
    public void AReloadWhileTheWriterRunsWaitsForItAndFindsTheConfirmation()
    {
        using var native = new FakeSavesTable();
        FiveBundles(1);
        SaveThroughEvents(1, 100);
        Written();
        Set(4, 60);
        WorldStore.IoHook = op => Thread.Sleep(op == "create" ? 300 : 0);
        SaveThroughEvents(2, 200);                             // its confirmation is being written
        Assert.False(Store.Writer.Idle);
        Assert.Equal(DataMatch.Exact, _h.Restart(2, new FakeInstanceWorld()));   // the load waited for it
        Assert.Equal(60, Value(Store, 4));
        Assert.False(File.Exists(Pending(2)));
    }

    [Fact]
    public void TheShutdownSavesConfirmationAndAWorldCloseWaitForTheWriter()
    {
        using var native = new FakeSavesTable();
        FiveBundles(1);
        SaveThroughEvents(1, 100);
        Written();
        Set(1, 70);
        WorldStore.IoHook = op => Thread.Sleep(op == "create" ? 200 : 0);
        SaveThroughEvents(2, 200, SaveReason.Shutdown);       // the process ends soon after: the tick thread waits for it
        Assert.True(Store.Writer.Idle);
        Assert.True(File.Exists(Confirmed(2, 1002)));
        Set(2, 80);
        SaveThroughEvents(3, 300);                             // an autosave's confirmation, then the world closes
        Assert.False(Store.Writer.Idle);
        Store.CloseWorld();
        Assert.True(Store.Writer.Idle);
        Assert.True(File.Exists(Confirmed(3, 1003)));
        WorldStore.IoHook = _hook;
        var after = StartOn(_h.Root, 3);
        Assert.Equal(DataMatch.Exact, after.Match);
        Assert.Equal(80, Value(after, 2));
    }

    [Theory]
    [InlineData(false, true)]   // a build whose world write is not hooked: nothing would hold the world write
    [InlineData(true, false)]   // a kiln.saves older than 0.2.0-dev.29.3: no stage_progress in its table
    public void WithoutTheStageBarrierTheTickThreadWritesItsFilesItselfAsBefore(bool barrier, bool appended)
    {
        using var native = new FakeSavesTable(barrier, appended);
        FiveBundles(1);
        WorldStore.IoHook = op => Thread.Sleep(op == "rename" ? 100 : 0);
        Dispatch(SavesNative.EvSaving, 100, 1);
        Assert.True(File.Exists(Pending(1)));                  // on disk before the game's snapshot, as before
        Assert.Equal(0L, FakeSavesTable.ProgressCalls);
        Dispatch(SavesNative.EvSnapshot, 101, 1, saveSeq: 100);
        Dispatch(SavesNative.EvWritten, 102, 1, saveSeq: 100, unix: 1001);
        WorldStore.IoHook = _hook;
        Assert.Equal(DataMatch.Exact, _h.Restart(1, new FakeInstanceWorld()));
    }

    [Fact]
    public void TheWriterGivesItsThreadBackAfterItsFilesAndTheNextSaveStartsItAgain()
    {
        // The seam an executor of Kiln's own can take over: start (the first job), enqueue, drain, stop.
        using var native = new FakeSavesTable();
        FiveBundles(1);
        WorldStore.IoHook = op => Thread.Sleep(op == "create" ? 100 : 0);
        SaveThroughEvents(1, 100);
        Assert.True(Store.Writer.Started);
        Assert.True(Store.Writer.Stop("test"));               // its confirmation first, then the thread ends
        Assert.False(Store.Writer.Started);
        Assert.True(File.Exists(Confirmed(1, 1001)));
        Set(0, 90);
        SaveThroughEvents(2, 200);
        Assert.True(Store.Writer.Started);
        WorldStore.IoHook = _hook;
        Assert.Equal(DataMatch.Exact, _h.Restart(2, new FakeInstanceWorld()));
        Assert.Equal(90, Value(Store, 0));
    }

    // ---- review round 1 ----

    private string Temp(string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "instance-tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _copies.Add(dir);
        return dir;
    }

    [Fact]
    public void AResumedPruningStillKeepsTheGenerationsOfTheGamesBackups()
    {
        // Review 1, finding 1: a pruning that stopped for a staging ran again at the next confirmation without the save
        // folder, so it no longer saw the game's backups and dropped the generation one of them needs.
        using var native = new FakeSavesTable();
        var world = Guid.NewGuid();
        InstanceHarness.OpenFolder(_h.Root, world.ToString());
        var saves = Temp("savegame");
        FakeSavesTable.Paths[SavesNative.PathSaveDir] = saves;
        FakeSavesTable.WriteWorldHeader(Path.Combine(saves, "backup", "world0.ksd.backup-9"), world, 1, 1001);   // the game's oldest backup
        FiveBundles(1);
        for (uint gen = 1; gen <= 40; gen++)
        {
            Set(0, (int)gen);                                  // every save changes: every confirmation is forced
            SaveThroughEvents(gen, 100 * gen);
            Written();
        }
        Assert.True(File.Exists(Confirmed(1, 1001)));        // the 1st, 11th, 21st and 31st prunings kept it for the backup
        Set(0, 41);
        Dispatch(SavesNative.EvSaving, 4100, 41);
        Dispatch(SavesNative.EvSnapshot, 4101, 41, saveSeq: 4100);
        FakeSavesTable.WaitLikeTheWorldWrite(Long);
        var freeze = FreezeAt("list");                         // the 41st confirmation's pruning
        Dispatch(SavesNative.EvWritten, 4102, 41, saveSeq: 4100, unix: 1041);
        freeze.Reached();
        Set(0, 42);
        Dispatch(SavesNative.EvSaving, 4200, 42);              // a staging comes: the pruning stops
        freeze.Release();
        Dispatch(SavesNative.EvSnapshot, 4201, 42, saveSeq: 4200);
        FakeSavesTable.WaitLikeTheWorldWrite(Long);
        Dispatch(SavesNative.EvWritten, 4202, 42, saveSeq: 4200, unix: 1042);   // its pruning runs again here
        Written();
        Assert.False(File.Exists(Confirmed(2, 1002)));       // it did prune (beyond the newest 30, no backup needs it)
        Assert.True(File.Exists(Confirmed(1, 1001)), "the generation of the game's backup was pruned");
    }

    [Fact]
    public void APruningReadsOnlyTheSaveFilesAndManifestsThatChangedSinceTheLastOne()
    {
        // Inbox 50 (a hard-disk player, 0.2.0-dev.29.14): every pruning read the header of every world file and backup in
        // the save folder and every kept manifest, "165 read in 18392.0 ms" at the first and 13324.9 ms again at the 11th,
        // on the game's own disk while it streamed the world. A file that did not change since the last pruning is not
        // read again; one that changed is, and its generation is kept as before.
        using var native = new FakeSavesTable();
        var world = Guid.NewGuid();
        InstanceHarness.OpenFolder(_h.Root, world.ToString());
        var saves = Temp("savegame");
        FakeSavesTable.Paths[SavesNative.PathSaveDir] = saves;
        const int others = 24;                                 // other worlds' backups: read once, never needed here
        for (var i = 0; i < others; i++)
            FakeSavesTable.WriteWorldHeader(Path.Combine(saves, "backup", $"world{i + 1}.ksd.backup-1"), Guid.NewGuid(), 7, (uint)(500 + i));
        FakeSavesTable.WriteWorldHeader(Path.Combine(saves, "backup", "world0.ksd.backup-9"), world, 1, 1001);   // this world's oldest
        FiveBundles(1);
        for (uint gen = 1; gen <= 10; gen++)                   // the 1st confirmation pruned: every header read
        {
            Set(0, (int)gen);
            SaveThroughEvents(gen, 100 * gen);
            Written();
        }
        var reads = 0;
        WorldStore.IoHook = op =>
        {
            if (op == "read") Interlocked.Increment(ref reads);
        };
        Set(0, 11);
        SaveThroughEvents(11, 1100);                           // the 11th prunes again
        Written();
        WorldStore.IoHook = _hook;
        Assert.True(reads < others, $"the 11th confirmation's pruning read {reads} files; the {others + 1} unchanged headers again?");
        Assert.True(File.Exists(Confirmed(1, 1001)), "the generation of the game's backup was pruned");
        // A backup that changes is read again: world1's slot now holds a backup of this world's generation 2.
        FakeSavesTable.WriteWorldHeader(Path.Combine(saves, "backup", "world1.ksd.backup-1"), world, 2, 1002);
        File.SetLastWriteTimeUtc(Path.Combine(saves, "backup", "world1.ksd.backup-1"), DateTime.UtcNow.AddMinutes(1));
        for (uint gen = 12; gen <= 41; gen++)                  // prunings at the 21st, 31st and 41st: 2 is beyond the newest 30
        {
            Set(0, (int)gen);
            SaveThroughEvents(gen, 100 * gen);
            Written();
        }
        Assert.False(File.Exists(Confirmed(3, 1003)));       // it did prune (beyond the newest 30, no backup needs it)
        Assert.True(File.Exists(Confirmed(2, 1002)), "the generation of the changed backup was pruned: its header was not read again");
        Assert.True(File.Exists(Confirmed(1, 1001)), "the generation of the game's oldest backup was pruned");
    }

    [Fact]
    public void AReloadThatOutwaitsTheWriterNeverTakesAnOlderGeneration()
    {
        // Review 1, finding 2: a load whose wait for the writer ran out read the folder while the confirmation was being
        // written: it listed pending R, the writer then put confirmed R in place and deleted pending R, and the load fell
        // back to R-1. Now (review 2) a load whose wait runs out reads nothing: deferred, then read at a pump.
        using var native = new FakeSavesTable();
        var limit = SaveWriter.DrainLimit;
        try
        {
            SaveWriter.DrainLimit = TimeSpan.FromMilliseconds(100);
            FiveBundles(1);
            SaveThroughEvents(1, 100);
            Written();
            Set(0, 10);
            Dispatch(SavesNative.EvSaving, 200, 2);
            Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            var tick = Environment.CurrentManagedThreadId;
            var reached = new ManualResetEventSlim();
            var gate = new ManualResetEventSlim();
            var raced = 0;
            WorldStore.IoHook = op =>
            {
                if (Environment.CurrentManagedThreadId != tick)
                {
                    if (op == "rename" && !reached.IsSet)
                    {
                        reached.Set();                         // the confirmation, before its rename
                        gate.Wait(TimeSpan.FromSeconds(2));
                    }
                    return;
                }
                if (op != "read" || Interlocked.Exchange(ref raced, 1) != 0) return;
                gate.Set();                                    // the load reads: the writer finishes right now
                var sw = Stopwatch.StartNew();
                while (File.Exists(Pending(2)) && sw.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(5);
            };
            Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
            Assert.True(reached.Wait(Long));
            var match = _h.Restart(2, new FakeInstanceWorld());   // the world file of revision 2 is on disk
            Assert.Equal(DataMatch.None, match);                   // not read: never generation 1 for world file 2
            Assert.False(SavesRuntime.WorldDataReady);
            Assert.True(Store.Writer.Drain("test", Long));        // the stalled confirmation finishes
            Pump();
            WorldStore.IoHook = _hook;
            Assert.Equal(DataMatch.Exact, Store.Match);
            Assert.Equal(10, Value(Store, 0));
        }
        finally
        {
            WorldStore.IoHook = _hook;
            SaveWriter.DrainLimit = limit;
        }
    }

    [Fact]
    public void ABackupThatWaitsForTheWriterCarriesItsModData()
    {
        // Review 1, finding 3: a backup whose wait for the writer ran out found no confirmation and reported success with no
        // mod data at all. A writer that finishes within the limit: the backup waits for it and carries the generation (one
        // that does not finish fails the backup: ABackupWhileTheWriterHangsFailsInsteadOfHoldingTheTick).
        using var native = new FakeSavesTable();
        var world = Guid.NewGuid();
        InstanceHarness.OpenFolder(_h.Root, world.ToString());
        var saves = Temp("savegame");
        var file = Path.Combine(saves, "world0.ksd");
        FakeSavesTable.Paths[SavesNative.PathSaveDir] = saves;
        FakeSavesTable.Paths[SavesNative.PathWorldFile] = file;
        FakeSavesTable.Paths[SavesNative.PathBackups] = Temp("backups");
        var limit = SaveWriter.DrainLimit;
        try
        {
            SaveWriter.DrainLimit = TimeSpan.FromSeconds(5);
            FiveBundles(1);
            FakeSavesTable.WriteWorldHeader(file, world, 1, 1001);
            SaveThroughEvents(1, 100);
            Written();
            Set(0, 10);
            Dispatch(SavesNative.EvSaving, 200, 2);
            Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            FakeSavesTable.WriteWorldHeader(file, world, 2, 1002);   // the game's world write
            var freeze = FreezeAt("rename", gate: TimeSpan.FromSeconds(1));   // the confirmation stalls 1 s
            Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
            freeze.Reached();
            var backup = SavesBackups.Create("slow-disk", 2, 1002, SaveReason.Backup);   // right after the write, as Saves.Backup does
            WorldStore.IoHook = _hook;
            Assert.Equal(5, backup.DataBundles);
            var info = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(backup.Directory, "backup.json")))!;
            Assert.NotNull(info["dataManifest"]);
        }
        finally
        {
            WorldStore.IoHook = _hook;
            SaveWriter.DrainLimit = limit;
        }
    }

    [Fact]
    public void PruningsLastCleanupStopsForAStagingToo()
    {
        // Review 1, finding 5: the last loop of pruning (old temp files of manifests) did not look for a waiting staging.
        using var native = new FakeSavesTable();
        FiveBundles(1);
        SaveThroughEvents(1, 100);
        Written();
        var manifests = Path.Combine(_h.Root, "manifests");
        for (var i = 0; i < 20; i++)
        {
            var tmp = Path.Combine(manifests, $"0000000001-1001.json.tmp-1-{i}");   // left by a crash an hour ago
            File.WriteAllText(tmp, "{}");
            File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddHours(-1));
        }
        typeof(WorldStore).GetField("_commits", Hidden)!.SetValue(Store, 10);   // the next confirmation prunes
        Dispatch(SavesNative.EvSaving, 200, 2);
        Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
        FakeSavesTable.WaitLikeTheWorldWrite(Long);
        var freeze = FreezeAt("delete", nth: 2);               // the confirmation deletes its pending manifest, then pruning a temp
        Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
        freeze.Reached();
        WorldStore.IoHook = op =>
        {
            if (op == "delete") Thread.Sleep(300);             // the rest of the cleanup is slow
        };
        Set(0, 30);
        Dispatch(SavesNative.EvSaving, 300, 3);
        freeze.Release();
        var waited = FakeSavesTable.WaitLikeTheWorldWrite(Long);
        Written();
        WorldStore.IoHook = _hook;
        // the other 19 temp files would take 5.7 s; the cleanup stops at the next one
        Assert.True(waited < TimeSpan.FromMilliseconds(1500), $"the world write waited {waited.TotalMilliseconds:F0} ms behind pruning's cleanup");
    }

    [Fact]
    public void AStagingWhoseWriteFailsOnceIsWrittenAgainBeforeTheWorldWrite()
    {
        // Review 1, the failed staging: a write that fails reported "done" at once, so the world write went ahead without
        // its manifest. A failure that passes (a file held for a moment, as an antivirus scan does) is now retried first.
        using var native = new FakeSavesTable();
        FiveBundles(1);
        SaveThroughEvents(1, 100);
        Written();
        var tick = Environment.CurrentManagedThreadId;
        var failed = 0;
        WorldStore.IoHook = op =>
        {
            if (op == "rename" && Environment.CurrentManagedThreadId != tick && Interlocked.Exchange(ref failed, 1) == 0)
                throw new IOException("The process cannot access the file because it is being used by another process.");
        };
        Dispatch(SavesNative.EvSaving, 200, 2);                // its only staging (as for a shutdown save: no snapshot staging)
        FakeSavesTable.WaitLikeTheWorldWrite(Long);
        Assert.Equal(1, failed);
        Assert.True(File.Exists(Pending(2)), "the world write went ahead without generation 2's pending manifest");
        WorldStore.IoHook = _hook;
        Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
        Assert.Equal(DataMatch.Exact, _h.Restart(2, new FakeInstanceWorld()));
    }

    // ---- review round 2 ----

    private static void Pump() =>
        typeof(SavesRuntime).GetMethod("Pump", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);

    [Fact]
    public void AReloadWhileTheWriterHangsIsDeferredNotHeldAndReadOnceTheWriterIsDone()
    {
        // Review 2, finding 1: a load waited for the writer without a limit, so a disk that never answers held the tick
        // thread for good. Now the load is deferred: no data, nothing saved over it, read at a pump once the writer is done.
        using var native = new FakeSavesTable();
        var limit = SaveWriter.DrainLimit;
        try
        {
            SaveWriter.DrainLimit = TimeSpan.FromMilliseconds(200);
            FiveBundles(1);
            SaveThroughEvents(1, 100);
            Written();
            Set(0, 10);
            Dispatch(SavesNative.EvSaving, 200, 2);
            Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            var freeze = FreezeAt("rename", gate: TimeSpan.FromSeconds(6));   // the confirmation's forced write does not return
            Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
            freeze.Reached();
            var sw = Stopwatch.StartNew();
            var match = _h.Restart(2, new FakeInstanceWorld(), tick: false);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"the load held the tick thread {sw.Elapsed.TotalMilliseconds:F0} ms");
            Assert.Equal(DataMatch.None, match);
            Assert.False(SavesRuntime.WorldDataReady);              // unavailable, as a hosted world's data before its load
            Dispatch(SavesNative.EvSaving, 300, 3);            // a save meanwhile writes nothing over the unread data
            freeze.Release();
            Written();
            Assert.False(File.Exists(Pending(3)));
            Pump();                                            // the writer is done: the next pump reads it
            Assert.True(SavesRuntime.WorldDataReady);
            Assert.Equal(DataMatch.Exact, Store.Match);
            Assert.Equal(10, Value(Store, 0));
        }
        finally
        {
            SaveWriter.DrainLimit = limit;
        }
    }

    private (Guid World, string File) BackupSetup()
    {
        var world = Guid.NewGuid();
        InstanceHarness.OpenFolder(_h.Root, world.ToString());
        var saves = Temp("savegame");
        var file = Path.Combine(saves, "world0.ksd");
        FakeSavesTable.Paths[SavesNative.PathSaveDir] = saves;
        FakeSavesTable.Paths[SavesNative.PathWorldFile] = file;
        FakeSavesTable.Paths[SavesNative.PathBackups] = Temp("backups");
        return (world, file);
    }

    [Fact]
    public void ABackupWhileTheWriterHangsFailsInsteadOfHoldingTheTick()
    {
        // Review 2, finding 1, for backups: the backup fails after the writer's limit instead of waiting for good.
        using var native = new FakeSavesTable();
        var (world, file) = BackupSetup();
        var limit = SaveWriter.DrainLimit;
        try
        {
            SaveWriter.DrainLimit = TimeSpan.FromMilliseconds(200);
            FiveBundles(1);
            FakeSavesTable.WriteWorldHeader(file, world, 1, 1001);
            SaveThroughEvents(1, 100);
            Written();
            Set(0, 10);
            Dispatch(SavesNative.EvSaving, 200, 2);
            Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            FakeSavesTable.WriteWorldHeader(file, world, 2, 1002);
            var freeze = FreezeAt("rename", gate: TimeSpan.FromSeconds(6));
            Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
            freeze.Reached();
            var sw = Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(() => SavesBackups.Create("hung-disk", 2, 1002, SaveReason.Backup));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"the backup held the tick thread {sw.Elapsed.TotalMilliseconds:F0} ms");
            Assert.Empty(Directory.EnumerateFileSystemEntries(FakeSavesTable.Paths[SavesNative.PathBackups]));
        }
        finally
        {
            SaveWriter.DrainLimit = limit;
        }
    }

    [Fact]
    public void ABackupOfAGenerationThatCouldNotBeWrittenFails()
    {
        // Review 2, finding 2: the manifests could not be written at all (tries, then the confirmation's try); the writer is
        // idle, and the backup used to publish a world file with no mod data as a success.
        using var native = new FakeSavesTable();
        var (world, file) = BackupSetup();
        var retry = SidecarFiles.RetryMs;
        try
        {
            SidecarFiles.RetryMs = 1;
            FiveBundles(1);
            FakeSavesTable.WriteWorldHeader(file, world, 1, 1001);
            SaveThroughEvents(1, 100);
            Written();
            var tick = Environment.CurrentManagedThreadId;
            WorldStore.IoHook = op =>
            {
                if (op == "create" && Environment.CurrentManagedThreadId != tick) throw new IOException("Access to the path is denied.");
            };
            Dispatch(SavesNative.EvSaving, 200, 2);
            Dispatch(SavesNative.EvSnapshot, 201, 2, saveSeq: 200);
            FakeSavesTable.WaitLikeTheWorldWrite(Long);
            FakeSavesTable.WriteWorldHeader(file, world, 2, 1002);
            Dispatch(SavesNative.EvWritten, 202, 2, saveSeq: 200, unix: 1002);
            Written();
            WorldStore.IoHook = _hook;
            Assert.False(File.Exists(Pending(2)));
            Assert.Throws<InvalidOperationException>(() => SavesBackups.Create("no-data", 2, 1002, SaveReason.Backup));
            Assert.Empty(Directory.EnumerateFileSystemEntries(FakeSavesTable.Paths[SavesNative.PathBackups]));
        }
        finally
        {
            WorldStore.IoHook = _hook;
            SidecarFiles.RetryMs = retry;
        }
    }

    [Fact]
    public void AConfirmationOfAGenerationNotStagedInThisSessionStagesItLate()
    {
        // The save began before Kiln.Saves was started: its confirmation stages it first, as before.
        using var native = new FakeSavesTable();
        FiveBundles(2);
        Dispatch(SavesNative.EvWritten, 100, 7, saveSeq: 99, unix: 1007);
        Written();
        Assert.True(File.Exists(Confirmed(7, 1007)));
        Assert.Equal(DataMatch.Exact, _h.Restart(7, new FakeInstanceWorld()));
        Assert.Equal(6, Value(Store, 4));
    }

    private sealed class Capture : IKilnBackend
    {
        public readonly List<(LogLevel Level, string Text)> Lines = [];
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public nint Service(string name, uint minVersion) => 0;
        public void Log(ulong owner, LogLevel level, string text)
        {
            lock (Lines) Lines.Add((level, text));
        }

        public (LogLevel Level, string Text) Single(string start)
        {
            lock (Lines) return Assert.Single(Lines, l => l.Text.StartsWith(start, StringComparison.Ordinal));
        }
    }
}
