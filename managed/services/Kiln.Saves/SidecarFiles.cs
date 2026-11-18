// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Kiln.SaveWriter;

namespace Kiln;

/// <summary>
/// What Kiln.Saves' writer jobs do to the sidecar folder (since 0.2.0-dev.29.3): a staging's new blobs and pending
/// manifest, a confirmation (and the pending manifest's delete), pruning. One job at a time, on whatever thread
/// <see cref="SaveWriter"/> runs it on; it keeps what one world session needs to know between jobs (the generation it
/// confirmed last, the newest confirmation forced to disk, the stagings waiting for their confirmation).
/// </summary>
internal sealed class SidecarFiles(Func<bool> stagingWaiting)
{
    /// <summary>Generations whose staging is kept for their confirmation (the newest; usually one).</summary>
    private const int KeepStaged = 4;

    private int _epoch = -1;
    private readonly Dictionary<uint, StageJob> _byGen = [];
    private (uint Revision, uint Unix, string Bundles)? _confirmed;  // the newest generation this session confirmed
    private (uint Revision, uint Unix)? _forced;                     // the newest confirmation forced to disk
    private bool _pruneDue;                                          // a pruning stopped for a staging: again next time
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal);  // sidecar folders made in this session

    /// <summary>What a pruning read, per version of the file (its size and last write time): the world files' and backups'
    /// headers, the kept manifests' blob names. The next pruning reads only files that changed (inbox 50, a hard disk:
    /// "165 read in 18392.0 ms" at the first pruning, 13324.9 ms again at the 11th, the save folder on the game's disk).
    /// Process-wide (the paths name their world); a file that could not be read is not kept here.</summary>
    private readonly Dictionary<string, (long Size, DateTime Written, WorldStore.Header Header)> _headers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Size, DateTime Written, string[] Blobs)> _blobLists = new(StringComparer.Ordinal);

    /// <summary>Generations whose newest staging is not on disk because every try to write it failed (review 2: a backup
    /// of such a generation would carry no mod data, or an older staging's). Process-wide, by folder; under its own lock
    /// (read by a backup while the writer is idle).</summary>
    private readonly HashSet<(string Root, uint Gen)> _failed = [];

    public bool Failed(string root, uint gen)
    {
        lock (_failed) return _failed.Contains((root, gen));
    }

    private void MarkFailed(string root, uint gen, bool failed)
    {
        lock (_failed)
            if (failed) _failed.Add((root, gen));
            else _failed.Remove((root, gen));
    }

    public void Run(Job job)
    {
        if (job.Epoch != _epoch)
        {
            _epoch = job.Epoch;  // another world session: nothing of the last one counts here
            _byGen.Clear();
            _confirmed = null;
            _forced = null;
            _pruneDue = false;
            _folders.Clear();
        }
        if (job is StageJob s) WriteStaging(s);
        else if (job is CommitJob c) WriteConfirmation(c);
    }

    private static double Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    private void WriteStaging(StageJob job)
    {
        var queued = Since(job.Queued);
        var t0 = Stopwatch.GetTimestamp();
        var disk = new DiskTime();
        _byGen.TryGetValue(job.Gen, out var before);
        bool unchanged = false, same = false;
        int fresh = 0;
        long bytes = 0;
        string? retried = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                (fresh, bytes) = WriteBlobsAndManifest(job, before, ref disk, out unchanged, out same);
                break;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < WriteAttempts)
            {
                // A file held for a moment (an antivirus scan on Windows): the world write waits for this staging, so try
                // again before giving up (review 1). At most about a second, inside the barrier's shortest wait.
                retried = e.Message;
                Thread.Sleep(RetryMs * attempt);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Remember(job);
                MarkFailed(job.Root, job.Gen, true);
                SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: writing mod data generation {job.Gen} failed {attempt} times: {e.Message} (the world " +
                                                 "write goes ahead without it; its confirmation writes it again; a crash before then loads an older generation)");
                return;
            }
        }
        Remember(job);
        MarkFailed(job.Root, job.Gen, false);
        job.QueuedMs = queued;
        job.WorkMs = Since(t0);
        job.DiskText = disk.Text(job.WorkMs);
        job.Head = $"Kiln.Saves: staged mod data generation {job.Gen} ({Saves.Text(job.Reason)}{(job.Late ? ", late: after the world write" : "")}" +
                   $"{(job.Snapshot ? ", snapshot staging" : "")}): {job.Manifest!["bundles"]!.AsObject().Count} bundles, {fresh} new blobs ({bytes} bytes)" +
                   $"{(job.Replaced ? ", replacing the save request's staging before it was written" : "")}" +
                   $"{(unchanged ? ", manifest unchanged: nothing written" : "")}" +
                   $"{(same ? $", bundles as in generation {job.Gen - 1}: manifest not forced to disk" : "")}" +
                   $"{(retried != null ? $", written after a failed attempt ({retried})" : "")}";
    }

    /// <summary>How often a staging is written before the world write goes ahead without it, and the first pause between
    /// two tries (then twice that): 3 tries with 0.25 s and 0.5 s between them, plus the tries' own file time.</summary>
    internal static int WriteAttempts = 3, RetryMs = 250;

    /// <summary>The blobs that are not on disk, then the pending manifest (unless the same manifest is already in its file).</summary>
    private (int Fresh, long Bytes) WriteBlobsAndManifest(StageJob job, StageJob? before, ref DiskTime disk, out bool unchanged, out bool same)
    {
        var entries = new SortedDictionary<string, (string Sha, int Size)>(StringComparer.Ordinal);
        int fresh = 0;
        long bytes = 0;
        if (_folders.Add(job.Root))
        {
            disk.CreateFolder(Path.Combine(job.Root, "manifests"));
            disk.CreateFolder(Path.Combine(job.Root, "blobs"));
        }
        foreach (var (scope, data) in job.Bundles)
        {
            var t = Stopwatch.GetTimestamp();
            var sha = Convert.ToHexStringLower(SHA256.HashData(data));
            disk.CpuMs += Since(t);
            var path = WorldStore.BlobPath(job.Root, sha);
            if (!disk.Exists(path))
            {
                disk.CreateFolder(Path.GetDirectoryName(path)!);
                WorldStore.WriteAtomic(path, data, ref disk);
                fresh++;
                bytes += data.Length;
            }
            entries[scope] = (sha, data.Length);
        }
        var t1 = Stopwatch.GetTimestamp();
        var man = new JsonObject
        {
            ["format"] = 1,
            ["world"] = job.WorldId,
            ["revision"] = job.Gen,
            ["state"] = "pending",
            ["reason"] = Saves.Text(job.Reason),
            ["late"] = job.Late,
            ["createdUnixMs"] = job.CreatedUnixMs,
            ["kiln"] = job.Kiln,
        };
        var list = new JsonObject();
        foreach (var (scope, (sha, size)) in entries) list[scope] = new JsonObject { ["sha256"] = sha, ["size"] = size };
        man["bundles"] = list;
        job.Manifest = man;
        job.Content = WorldStore.ManifestContent(man);
        job.Key = WorldStore.BundlesKey(list);
        var text = Encoding.UTF8.GetBytes(man.ToJsonString());
        disk.CpuMs += Since(t1);
        var pending = WorldStore.PendingPath(job.Root, job.Gen);
        // The same manifest is already in that file (the save request's staging, one simulation step before the snapshot
        // staging, found nothing changed): writing it again would only change its creation time, which nothing reads.
        // Blobs are content-addressed (only a blob that is not on disk is written). So a crash at any point finds the same
        // files as after a rewrite: that manifest was renamed into place before this staging, as before.
        unchanged = before is { Written: true } b && b.Content == job.Content && disk.Exists(pending);
        // The save request's staging of a generation with the bundles of the previous one (confirmed by this process, still
        // on disk) and no new blob: not forced to disk (WorldStore's remarks). The snapshot staging, a late staging and every
        // staging with a change force their write as before.
        same = !unchanged && !job.Snapshot && !job.Late && fresh == 0 && _confirmed is { } c && c.Revision == job.Gen - 1 &&
               c.Bundles == job.Key && disk.Exists(WorldStore.CommittedPath(job.Root, c.Revision, c.Unix));
        if (!unchanged) WorldStore.WriteAtomic(pending, text, ref disk, force: !same);
        job.Written = true;
        return (fresh, bytes);
    }

    /// <summary>Keeps the generation's staging for its confirmation (its manifest; its bundles only while not written).</summary>
    private void Remember(StageJob job)
    {
        if (job.Written) job.Bundles = [];
        _byGen[job.Gen] = job;
        while (_byGen.Count > KeepStaged) _byGen.Remove(_byGen.Keys.Min());
    }

    private void WriteConfirmation(CommitJob job)
    {
        var queued = Since(job.Queued);
        var t0 = Stopwatch.GetTimestamp();
        var disk = new DiskTime();
        var unix = job.Unix;
        if (unix == 0 && job.WorldFile != null && disk.ReadHeader(job.WorldFile) is { } h && h.Revision == job.Rev) unix = h.Unix;
        var pending = WorldStore.PendingPath(job.Root, job.Rev);
        var same = false;
        var retried = false;
        try
        {
            JsonObject man;
            if (_byGen.Remove(job.Rev, out var staged))
            {
                if (!staged.Written)
                {
                    // Its staging could not be written before the world write: now, forced, before the confirmation.
                    staged.Late = true;
                    try
                    {
                        WriteBlobsAndManifest(staged, null, ref disk, out _, out _);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        MarkFailed(job.Root, job.Rev, true);  // still not on disk: no backup may claim this generation
                        throw;
                    }
                    MarkFailed(job.Root, job.Rev, false);
                    retried = true;
                }
                man = (JsonObject)staged.Manifest!.DeepClone();
            }
            else
            {
                // Not staged in this session (kept beyond the newest few): its pending manifest, as written.
                if (!disk.Exists(pending))
                {
                    SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: generation {job.Rev} has no pending manifest; not confirmed");
                    return;
                }
                man = JsonNode.Parse(disk.ReadAll(pending))!.AsObject();
            }
            var t1 = Stopwatch.GetTimestamp();
            man["state"] = "committed";
            man["worldUnix"] = unix;
            man["committedUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // The bundles of the generation confirmed before it (still on disk), so no new blob: not forced to disk, as
            // its pending manifest was not (WorldStore's remarks). The first confirmation of a session and every change force.
            var bundles = WorldStore.BundlesKey(man["bundles"] as JsonObject);
            var text = Encoding.UTF8.GetBytes(man.ToJsonString());
            disk.CpuMs += Since(t1);
            same = !retried && _forced != null && _confirmed is { } c && c.Revision == job.Rev - 1 && c.Bundles == bundles &&
                   disk.Exists(WorldStore.CommittedPath(job.Root, c.Revision, c.Unix));
            WorldStore.WriteAtomic(WorldStore.CommittedPath(job.Root, job.Rev, unix), text, ref disk, force: !same);
            disk.Delete(pending);
            _confirmed = (job.Rev, unix, bundles);
            if (!same) _forced = (job.Rev, unix);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            _confirmed = null;
            SavesRuntime.Log(LogLevel.Error, $"Kiln.Saves: confirming generation {job.Rev} failed: {e.Message} (it stays staged; loading still finds it)");
            return;
        }
        var work = Since(t0);
        var pruning = "";
        double pm = 0;
        if (job.Prune || _pruneDue)
        {
            var p = Stopwatch.GetTimestamp();
            var pd = new DiskTime();
            var stopped = !Prune(job, ref pd);
            _pruneDue = stopped;
            pm = Since(p);
            pruning = $"; pruning {pm:F1} ms{(stopped ? " (stopped for a staging; it runs again at the next confirmation)" : "")}: {pd.Text(pm)}";
        }
        var (tick, total) = job.TickPart(queued, work + pm);
        SavesRuntime.Log(WorldStore.TimingLevel(total), $"Kiln.Saves: confirmed mod data generation {job.Rev} ({Saves.Text(job.Reason)})" +
                                                         $"{(retried ? ", its staging written only now" : "")}" +
                                                         $"{(same ? $", bundles as in generation {job.Rev - 1}: not forced to disk" : "")} in {total:F1} ms " +
                                                         $"({tick} {work:F1} ms: " +
                                                         $"{disk.Text(work)}{pruning})");
    }

    /// <summary>Drops manifests no world file needs any more, then blobs no kept manifest names. Returns false when it
    /// stopped early because a staging is waiting (the world write waits for that one).</summary>
    private bool Prune(CommitJob job, ref DiskTime disk)
    {
        var root = job.Root;
        try
        {
            var dir = Path.Combine(root, "manifests");
            var files = disk.List(() => WorldStore.ListManifestFiles(dir));
            var all = files.Select(m => m.Name).ToList();
            var stamps = files.ToDictionary(m => m.Name.Path, m => (m.Size, m.Written), StringComparer.Ordinal);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in all.Where(m => !m.Pending).OrderByDescending(m => m.Revision).Take(WorldStore.KeepGenerations)) keep.Add(m.Path);
            if (_forced is { } fb) keep.Add(WorldStore.CommittedPath(root, fb.Revision, fb.Unix));  // what the unforced generations after it fall back to
            var wanted = new HashSet<(uint, uint)>();
            if (job.SaveDir is { } sd && Directory.Exists(sd))
            {
                if (_headers.Count > 4096) _headers.Clear();  // never more than a few hundred save files: a bound, not a policy
                if (_blobLists.Count > 4096) _blobLists.Clear();
                // the listing brings each file's size and write time (no extra query per file)
                var saveFiles = disk.List(() => new DirectoryInfo(sd).EnumerateFiles("world*.ksd").Concat(
                        Directory.Exists(Path.Combine(sd, "backup")) ? new DirectoryInfo(Path.Combine(sd, "backup")).EnumerateFiles() : [])
                    .Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc)).ToList());
                foreach (var (f, size, written) in saveFiles)
                {
                    if (stagingWaiting()) return false;
                    WorldStore.Header? h;
                    if (_headers.TryGetValue(f, out var known) && known.Size == size && known.Written == written) h = known.Header;
                    else if ((h = disk.ReadHeader(f)) is { } read) _headers[f] = (size, written, read);
                    else _headers.Remove(f);
                    if (h is { } header && header.Guid.ToString() == job.WorldId) wanted.Add((header.Revision, header.Unix));
                }
            }
            foreach (var m in all.Where(m => !m.Pending && wanted.Contains((m.Revision, m.Unix)))) keep.Add(m.Path);
            var newest = all.Where(m => !m.Pending).Select(m => m.Revision).DefaultIfEmpty(0u).Max();
            foreach (var m in all.Where(m => m.Pending && m.Revision > newest)) keep.Add(m.Path);
            int dropped = 0, blobsDropped = 0;
            foreach (var m in all.Where(m => !keep.Contains(m.Path)))
            {
                if (stagingWaiting()) return false;
                disk.Delete(m.Path);
                dropped++;
            }
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in keep)
            {
                if (stagingWaiting()) return false;
                var stamped = stamps.TryGetValue(p, out var stamp);
                if (stamped && _blobLists.TryGetValue(p, out var known) && known.Size == stamp.Size && known.Written == stamp.Written)
                {
                    referenced.UnionWith(known.Blobs);
                    continue;
                }
                try
                {
                    var named = new List<string>();
                    if (JsonNode.Parse(disk.ReadAll(p))?["bundles"] is JsonObject list)
                        foreach (var (_, e) in list)
                            if (e?["sha256"]?.GetValue<string>() is { } sha) named.Add(sha);
                    referenced.UnionWith(named);
                    if (stamped) _blobLists[p] = (stamp.Size, stamp.Written, named.ToArray());
                }
                catch (Exception e) when (e is IOException or JsonException)
                {
                    _blobLists.Remove(p);
                    return true;  // unsure what is referenced: keep every blob
                }
            }
            var cutoff = DateTime.UtcNow - WorldStore.OrphanAge;
            var blobs = disk.List(() => Directory.EnumerateFiles(Path.Combine(root, "blobs"), "*", SearchOption.AllDirectories)
                .Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList());
            foreach (var (f, written) in blobs)
            {
                var name = Path.GetFileName(f);
                var tmp = name.Contains(".tmp-", StringComparison.Ordinal);
                var sha = tmp ? "" : Path.GetFileNameWithoutExtension(name);
                if ((tmp || !referenced.Contains(sha)) && written < cutoff)
                {
                    if (stagingWaiting()) return false;
                    disk.Delete(f);
                    blobsDropped++;
                }
            }
            var tmps = disk.List(() => Directory.EnumerateFiles(dir, "*.tmp-*").Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList());
            foreach (var (f, written) in tmps)
            {
                if (written >= cutoff) continue;
                if (stagingWaiting()) return false;  // review 1: this loop too
                disk.Delete(f);
            }
            if (dropped + blobsDropped > 0)
                SavesRuntime.Log(LogLevel.Debug, $"Kiln.Saves: pruned {dropped} manifests and {blobsDropped} blobs; {keep.Count} manifests kept");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: pruning the sidecar failed: {e.Message}");
            return true;
        }
    }
}
