// SPDX-License-Identifier: MIT
// The overlay engine: one patch, batch by batch, with the ledger's rules (docs/kiln/services/terrain.md, "The ledger").
// It reaches the world only through ITerrainBackend. Tick thread.
namespace Kiln;

internal sealed class TerrainEngine(ITerrainBackend backend, TerrainSession session, Action<TerrainChangedEvent>? raise)
{
    private static readonly TimeSpan WorldWait = TimeSpan.FromMinutes(5);

    private enum BlockPlan { Write, Done, PlayerEdit, GivenUp }

    public async Task<TerrainResult> Apply(TerrainPlan plan, TerrainApplyOptions options, int edge)
    {
        var patch = plan.Patch;
        TerrainResult Result(TerrainState state, string? reason, long cells = 0, int batches = 0, IReadOnlyList<TerrainConflict>? conflicts = null) =>
            new(state is TerrainState.Completed or TerrainState.AlreadyApplied, reason, state, patch.Id, patch.Version, cells, batches, conflicts ?? []);

        if (!await WaitForWorld(options.Cancel)) return Result(TerrainState.Refused, "the world did not start running");
        CheckGrid(plan);
        var world = backend.WorldId ?? "";
        var kept = patch.Persistence == TerrainPersistence.Kept;
        if (kept && !session.Begin(world, backend.Mod, patch.Id))
            return Result(TerrainState.Refused, $"plan '{patch.Id}' is being applied already");
        try
        {
            return await Run(plan, options, edge, world, kept);
        }
        finally
        {
            if (kept) session.End(world, backend.Mod, patch.Id);
        }
    }

    private async Task<TerrainResult> Run(TerrainPlan plan, TerrainApplyOptions options, int edge, string world, bool kept)
    {
        var patch = plan.Patch;
        TerrainResult Result(TerrainState state, string? reason, long cells = 0, int batches = 0, IReadOnlyList<TerrainConflict>? conflicts = null) =>
            new(state is TerrainState.Completed or TerrainState.AlreadyApplied, reason, state, patch.Id, patch.Version, cells, batches, conflicts ?? []);
        TerrainBook? book = null;
        TerrainRecord? rec = null;
        if (kept)
        {
            if (!TryBook(out book, out var error)) return Result(TerrainState.Refused, error);
            session.Learn(world, backend.Mod, book!);
            book!.Plans.TryGetValue(patch.Id, out rec);
            if (rec is not null)
            {
                if (patch.Version < rec.Version)
                    return Result(TerrainState.Refused, $"this world has plan '{patch.Id}' at version {rec.Version}; version {patch.Version} is older and a downgrade is never applied");
                if (patch.Version == rec.Version && rec.Content != plan.ContentHash)
                    return Result(TerrainState.Refused, $"plan '{patch.Id}' version {patch.Version} was applied with other content: content changed without a new version");
                if (patch.Version == rec.Version && rec.Complete)
                {
                    var left = GivenUpOf(rec);
                    return left.Count == 0
                        ? Result(TerrainState.AlreadyApplied, null)
                        : Result(TerrainState.Conflict, $"{left.Count} block(s) were left alone when this version was applied", conflicts: left);
                }
            }
            else if (book.Plans.Count >= TerrainPlan.MaxPlans)
            {
                return Result(TerrainState.Refused, $"this mod already has {TerrainPlan.MaxPlans} kept plans in this world; Terrain.Forget one first");
            }
            // another mod's plan on a block of ours: nothing is written
            var foreign = new List<TerrainConflict>();
            foreach (var b in plan.Blocks())
                if (session.OwnerOf(world, backend.Mod, TerrainPlan.BlockKey(b)) is { } owner)
                    foreign.Add(new TerrainConflict(TerrainConflictKind.OtherPlan, TerrainPlan.WholeBlock(b), owner));
            if (foreign.Count > 0)
                return Result(TerrainState.Refused, $"{foreign.Count} block(s) belong to {foreign[0].Owner}", conflicts: foreign);
            // no record, but the ground may hold this plan already: its ledger was lost (TerrainLoss, "When the ledger is gone")
            if (rec is null && await TerrainLoss.Check(backend, plan, book, options.LoadTimeout) is { } lost) return Result(lost.State, lost.Reason);

            if (rec is null)
            {
                rec = new TerrainRecord();
                book.Plans[patch.Id] = rec;
            }
            else if (patch.Version > rec.Version)
            {
                // an upgrade: what the earlier version owns stays known until each block was visited. An earlier upgrade
                // that was interrupted leaves both of its maps as "earlier": its newer entries win.
                var earlier = rec.EarlierBlocks ?? new Dictionary<string, TerrainBlock>();
                var earlierRegion = rec.EarlierRegion ?? rec.Region;
                if (rec.EarlierBlocks is not null && !SameBox(rec.Region, earlierRegion))
                {
                    // two regions cannot both be "the earlier one": blocks of the interrupted version are checked
                    // against its region, the rest are given up (never written again) rather than guessed at
                    earlier = earlier.Where(p => !TerrainPlan.Intersect(KeyBox(p.Key), rec.Box).IsEmpty).ToDictionary(p => p.Key, p => p.Value);
                    foreach (var b in earlier.Values) b.GivenUp = true;
                    earlierRegion = rec.Region;
                }
                foreach (var (k, b) in rec.Blocks) earlier[k] = b;
                rec.EarlierBlocks = earlier;
                rec.EarlierRegion = earlierRegion;
                rec.Blocks = new();
            }
            rec.Version = patch.Version;
            rec.Region = TerrainRecord.FromBox(plan.Region);
            rec.Content = plan.ContentHash;
            rec.Complete = false;
            rec.KilnVersion = backend.KilnVersion;
            backend.Save(book);
            session.Claim(world, backend.Mod, patch.Id, plan.Blocks().Select(TerrainPlan.BlockKey));
        }

        if (!kept)
        {
            var foreign = plan.Blocks().Select(b => (Box: b, Owner: session.OwnerOf(world, backend.Mod, TerrainPlan.BlockKey(b))))
                .Where(b => b.Owner is not null).Select(b => new TerrainConflict(TerrainConflictKind.OtherPlan, TerrainPlan.WholeBlock(b.Box), b.Owner)).ToList();
            if (foreign.Count > 0) return Result(TerrainState.Refused, $"blocks belong to {foreign[0].Owner}", conflicts: foreign);
        }
        var batches = plan.Batches(edge);
        var conflicts = new List<TerrainConflict>();
        long changed = 0;
        var written = 0;
        string? stop = null;
        for (var i = 0; i < batches.Count && stop is null; i++)
        {
            if (options.Cancel.IsCancellationRequested) { stop = "canceled"; break; }
            if (!SameWorld(world)) { stop = "the world stopped or changed"; break; }
            var box = batches[i];
            bool loaded;
            try { loaded = await backend.Load(box, options.LoadTimeout); }
            catch (BuildException e) { stop = e.Message; break; }
            if (!SameWorld(world)) { stop = "the world stopped or changed"; break; }
            if (!loaded || !backend.TryRead(box, out var now))
            {
                stop = $"the voxel tiles under {box} did not load";
                break;
            }
            var (stopped, cells, wrote) = await Batch(plan, box, now, rec, options, conflicts, world);
            if (stopped is not null) { stop = stopped; }
            if (wrote)
            {
                written++;
                changed += cells;
                raise?.Invoke(new TerrainChangedEvent(backend.Mod, patch.Id, patch.Version, box, cells, i + 1, batches.Count, i == batches.Count - 1));
                options.Progress?.Report(new TerrainProgress(i + 1, batches.Count, changed));
            }
            if (stop is null && i < batches.Count - 1) await backend.NextTick();
        }

        if (stop is null && !SameWorld(world)) stop = "the world stopped or changed";
        if (stop is not null)
            return Result(TerrainState.Partial, stop, changed, written, conflicts);
        if (kept)
        {
            // blocks the earlier version owned and this one does not address: no longer the plan's
            rec!.EarlierBlocks = null;
            rec.EarlierRegion = null;
            rec.Complete = true;
            rec.AppliedAt = DateTimeOffset.UtcNow;
            backend.Save(book!);
            session.Learn(world, backend.Mod, book!);
        }
        return conflicts.Count == 0
            ? Result(TerrainState.Completed, null, changed, written)
            : Result(TerrainState.Conflict, $"{conflicts.Count} block(s) were left alone", changed, written, conflicts);
    }

    /// <summary>One batch: decide each block, write what may be written, record it. Returns a reason when the patch must
    /// stop, the cells changed, and whether anything was written.</summary>
    private async Task<(string? Stop, long Cells, bool Wrote)> Batch(TerrainPlan plan, CellBox box, Voxel[] now, TerrainRecord? rec,
        TerrainApplyOptions options, List<TerrainConflict> conflicts, string world)
    {
        var region = plan.Region;
        var n = now.Length;
        var cells = (Voxel[])now.Clone();
        var mask = new bool[n];
        var blocks = new List<(CellBox Part, string Key, string Before)>();
        foreach (var part in TerrainPlan.Cut(box, 8))
        {
            if (!plan.AnyAddressed(part)) continue;
            var key = TerrainPlan.BlockKey(part);
            var before = TerrainPlan.Fingerprint(box, now, part);
            var what = rec is null ? BlockPlan.Write : Decide(rec, key, part, before);
            switch (what)
            {
                case BlockPlan.Done:
                    continue;
                case BlockPlan.GivenUp:
                case BlockPlan.PlayerEdit:
                    var why = what == BlockPlan.GivenUp ? WhyOf(rec!, key) : TerrainConflictKind.PlayerEdit;
                    conflicts.Add(new TerrainConflict(why, TerrainPlan.WholeBlock(part)));
                    rec!.Blocks[key] = new TerrainBlock { Before = before, After = before, GivenUp = true, Why = (int)why };
                    rec.EarlierBlocks?.Remove(key);
                    continue;
            }
            for (var z = part.Z; z < part.Z + part.SizeZ; z++)
            for (var y = part.Y; y < part.Y + part.SizeY; y++)
            for (var x = part.X; x < part.X + part.SizeX; x++)
            {
                var ri = region.Index(x - region.X, y - region.Y, z - region.Z);
                if (!plan.Addressed[ri]) continue;
                var bi = box.Index(x - box.X, y - box.Y, z - box.Z);
                var want = plan.Target[ri];
                if (plan.OnlyAdd && now[bi].Fill >= want.Fill) continue;
                cells[bi] = want;
                mask[bi] = true;
            }
            blocks.Add((part, key, before));
        }
        if (blocks.Count == 0) return (null, 0, false);

        long changed = 0;
        var wrote = false;
        var label = $"terrain {plan.Patch.Id} v{plan.Patch.Version}";
        var transient = plan.Patch.Persistence == TerrainPersistence.Transient;
        // Publish expected fingerprints BEFORE calling Build: its events may save, edit cells or end the world.
        // Never adopt arbitrary readback as our "after", and never rewrite a receipt that no longer matches it.
        void Record(IEnumerable<(CellBox Part, string Key, string Before)> parts)
        {
            if (rec is null) return;
            foreach (var (part, key, before) in parts)
                rec.Blocks[key] = new TerrainBlock { Before = before, After = TerrainPlan.Fingerprint(box, cells, part) };
        }
        void Committed(IEnumerable<(CellBox Part, string Key, string Before)> parts)
        {
            if (rec is null) return;
            foreach (var (_, key, _) in parts) rec.EarlierBlocks?.Remove(key);
        }
        // An explicit refusal committed nothing: retain the older ownership until a later retry.
        var previous = rec is null ? null : new Dictionary<string, TerrainBlock>(rec.Blocks);
        void Undo(IEnumerable<(CellBox Part, string Key, string Before)> parts)
        {
            if (rec is null) return;
            foreach (var (_, key, _) in parts)
                if (previous!.TryGetValue(key, out var prior)) rec.Blocks[key] = prior;
                else rec.Blocks.Remove(key);
        }
        if (mask.AsSpan().Contains(true))
        {
            Record(blocks);
            BuildResult r;
            try { r = await backend.Write(box, cells, mask, options.InBases, transient, label, options.LoadTimeout); }
            catch (BuildException e) { return (e.Message, changed, wrote); }
            if (!SameWorld(world)) return ("the world stopped or changed", r.CellsChanged, !r.Refused);
            if (r.Refusal == BuildRefusal.Base)
            {
                Undo(blocks);
                // the batch touches a base: block by block, preserving receipts for each successful write
                foreach (var block in blocks)
                {
                    var (part, key, before) = block;
                    var (pc, pm) = Sub(box, cells, mask, part);
                    if (!pm.AsSpan().Contains(true)) { Record([block]); Committed([block]); continue; }
                    if (!SameWorld(world)) return ("the world stopped or changed", changed, wrote);
                    Record([block]);
                    BuildResult pr;
                    try { pr = await backend.Write(part, pc, pm, false, transient, label, options.LoadTimeout); }
                    catch (BuildException e) { return (e.Message, changed, wrote); }
                    if (pr.Refusal == BuildRefusal.Base)
                    {
                        conflicts.Add(new TerrainConflict(TerrainConflictKind.BaseZone, TerrainPlan.WholeBlock(part)));
                        if (rec is not null) rec.Blocks[key] = new TerrainBlock { Before = before, After = before, GivenUp = true, Why = (int)TerrainConflictKind.BaseZone };
                        Committed([block]);
                    }
                    else if (pr.Refused)
                    {
                        Undo([block]);
                        return ($"a write of {part} was refused: {pr.Reason}", changed, wrote);
                    }
                    else
                    {
                        Committed([block]);
                        changed += pr.CellsChanged;
                        wrote = true;
                    }
                }
            }
            else if (r.Refused)
            {
                Undo(blocks);
                return ($"a write of {box} was refused: {r.Reason}", 0, false);
            }
            else
            {
                Committed(blocks);
                changed = r.CellsChanged;
                wrote = true;
            }
        }
        else { Record(blocks); Committed(blocks); }
        if (!SameWorld(world)) return ("the world stopped or changed", changed, wrote);
        if (rec is not null && !backend.TryRead(box, out _))
            return ($"the voxel tiles under {box} went away after the write", changed, wrote);
        return (null, changed, wrote);
    }

    /// <summary>What to do with a block the record may know. <paramref name="now"/> is the fingerprint of the block's cells
    /// inside the new region.</summary>
    private BlockPlan Decide(TerrainRecord rec, string key, CellBox part, string now)
    {
        if (rec.Blocks.TryGetValue(key, out var mine))
        {
            // this version already met the block (a resumed apply)
            if (mine.GivenUp) return BlockPlan.GivenUp;
            if (now == mine.After) return BlockPlan.Done;
            // Equal to "before" is ambiguous: a player may have restored precisely the original ground.
            return BlockPlan.PlayerEdit;
        }
        if (rec.EarlierBlocks is not null && rec.EarlierBlocks.TryGetValue(key, out var old))
        {
            if (old.GivenUp) return BlockPlan.GivenUp;
            // the earlier version's fingerprint is over its own region's part of the block
            var oldPart = TerrainPlan.Intersect(TerrainPlan.WholeBlock(part), TerrainRecord.ToBox(rec.EarlierRegion ?? []));
            if (oldPart.IsEmpty || !backend.TryRead(oldPart, out var cells)) return BlockPlan.PlayerEdit;
            return TerrainPlan.Fingerprint(oldPart, cells, oldPart) == old.After ? BlockPlan.Write : BlockPlan.PlayerEdit;
        }
        return BlockPlan.Write;
    }

    public async Task<TerrainPlanReport> Inspect(TerrainPlan plan, TerrainApplyOptions options, int edge)
    {
        var patch = plan.Patch;
        var batches = plan.Batches(edge);
        var blockList = plan.Blocks().ToList();
        TerrainPlanReport Report(bool ok, string? reason, TerrainState would, IReadOnlyList<TerrainConflict>? conflicts = null) =>
            new(ok, reason, plan.Region, plan.AddressedCells, blockList.Count, batches.Count, (long)blockList.Count * TerrainPlan.BytesPerBlock,
                would, conflicts ?? []);

        if (!backend.WorldRunning) return Report(false, "the world is not running", TerrainState.Refused);
        CheckGrid(plan);
        var world = backend.WorldId ?? "";
        TerrainRecord? rec = null;
        if (patch.Persistence == TerrainPersistence.Kept)
        {
            if (!TryBook(out var book, out var error)) return Report(false, error, TerrainState.Refused);
            session.Learn(world, backend.Mod, book!);
            book!.Plans.TryGetValue(patch.Id, out rec);
            if (rec is not null)
            {
                if (patch.Version < rec.Version) return Report(true, null, TerrainState.Refused);
                if (patch.Version == rec.Version && rec.Content != plan.ContentHash) return Report(true, null, TerrainState.Refused);
                if (patch.Version == rec.Version && rec.Complete)
                {
                    var left = GivenUpOf(rec);
                    return Report(true, null, left.Count == 0 ? TerrainState.AlreadyApplied : TerrainState.Conflict, left);
                }
            }
            else if (book.Plans.Count >= TerrainPlan.MaxPlans)
            {
                return Report(true, null, TerrainState.Refused);
            }
            var foreign = new List<TerrainConflict>();
            foreach (var b in blockList)
                if (session.OwnerOf(world, backend.Mod, TerrainPlan.BlockKey(b)) is { } owner)
                    foreign.Add(new TerrainConflict(TerrainConflictKind.OtherPlan, TerrainPlan.WholeBlock(b), owner));
            if (foreign.Count > 0) return Report(true, null, TerrainState.Refused, foreign);
            if (rec is null && await TerrainLoss.Check(backend, plan, book, options.LoadTimeout) is { } lost) return Report(lost.State == TerrainState.Refused, lost.Reason, lost.State);
        }
        if (session.Running(world, backend.Mod, patch.Id)) return Report(true, "the plan is being applied already", TerrainState.Refused);
        if (patch.Persistence == TerrainPersistence.Transient)
        {
            var foreign = blockList.Select(b => (Box: b, Owner: session.OwnerOf(world, backend.Mod, TerrainPlan.BlockKey(b))))
                .Where(b => b.Owner is not null).Select(b => new TerrainConflict(TerrainConflictKind.OtherPlan, TerrainPlan.WholeBlock(b.Box), b.Owner)).ToList();
            if (foreign.Count > 0) return Report(true, $"blocks belong to {foreign[0].Owner}", TerrainState.Refused, foreign);
        }
        bool loaded;
        try { loaded = await backend.Load(plan.Region, options.LoadTimeout); }
        catch (BuildException e) { return Report(false, e.Message, TerrainState.Partial); }
        if (!SameWorld(world)) return Report(false, "the world stopped or changed", TerrainState.Partial);
        if (!loaded || !backend.TryRead(plan.Region, out var now))
            return Report(false, $"the voxel tiles under {plan.Region} did not load", TerrainState.Partial);

        var conflicts = new List<TerrainConflict>();
        // what an upgrade or a resumed apply would meet; a view of the record as the apply would see it
        TerrainRecord? view = rec is null ? null : ViewFor(rec, patch.Version);
        foreach (var part in blockList)
        {
            var whole = TerrainPlan.WholeBlock(part);
            var key = TerrainPlan.BlockKey(part);
            var what = view is null ? BlockPlan.Write : Decide(view, key, part, TerrainPlan.Fingerprint(plan.Region, now, part));
            if (what is BlockPlan.PlayerEdit or BlockPlan.GivenUp)
            {
                conflicts.Add(new TerrainConflict(what == BlockPlan.GivenUp ? WhyOf(view!, key) : TerrainConflictKind.PlayerEdit, whole));
                continue;
            }
            if (what == BlockPlan.Done) continue;
            if (options.InBases) continue;
            // a dry-run write of the block: kiln.build says whether a base is in the way
            var (pc, pm) = Sub(plan.Region, plan.Target, plan.Addressed, part);
            BuildResult probe;
            try { probe = await backend.Write(part, pc, pm, false, true, "terrain inspect", options.LoadTimeout, dryRun: true); }
            catch (BuildException e) { return Report(false, e.Message, TerrainState.Partial); }
            if (!SameWorld(world)) return Report(false, "the world stopped or changed", TerrainState.Partial);
            if (probe.Refusal == BuildRefusal.Base) conflicts.Add(new TerrainConflict(TerrainConflictKind.BaseZone, whole));
            else if (probe.Refused) return Report(false, probe.Reason, TerrainState.Partial);
        }
        return Report(true, null, conflicts.Count == 0 ? TerrainState.Completed : TerrainState.Conflict, conflicts);
    }

    /// <summary>The record as an apply of <paramref name="version"/> would start from, without changing the stored one.</summary>
    private static TerrainRecord ViewFor(TerrainRecord rec, int version)
    {
        if (version == rec.Version) return rec;
        var earlier = new Dictionary<string, TerrainBlock>(rec.EarlierBlocks ?? []);
        var region = rec.EarlierRegion ?? rec.Region;
        if (rec.EarlierBlocks is not null && !SameBox(rec.Region, region))
        {
            earlier = earlier.Where(p => !TerrainPlan.Intersect(KeyBox(p.Key), rec.Box).IsEmpty).ToDictionary(p => p.Key, p => p.Value);
            foreach (var k in earlier.Keys.ToList()) earlier[k] = new TerrainBlock { GivenUp = true };
            region = rec.Region;
        }
        foreach (var (k, b) in rec.Blocks) earlier[k] = b;
        return new TerrainRecord { Version = version, Region = rec.Region, EarlierBlocks = earlier, EarlierRegion = region };
    }

    public bool Forget(string id)
    {
        if (session.Running(backend.WorldId ?? "", backend.Mod, id)) return false;
        if (!TryBook(out var book, out _)) return false;
        if (!book!.Plans.Remove(id)) return false;
        backend.Save(book);
        session.Learn(backend.WorldId ?? "", backend.Mod, book);
        return true;
    }

    public IReadOnlyList<TerrainPlanInfo> Applied()
    {
        if (!TryBook(out var book, out _)) return [];
        session.Learn(backend.WorldId ?? "", backend.Mod, book!);
        return book.Plans.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new TerrainPlanInfo(p.Key, p.Value.Version, p.Value.Box,
            p.Value.Complete, p.Value.Blocks.Values.Count(b => !b.GivenUp), p.Value.Blocks.Values.Count(b => b.GivenUp), p.Value.AppliedAt)).ToList();
    }

    private bool SameWorld(string world) => backend.WorldRunning && (backend.WorldId ?? "") == world;

    private bool TryBook([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TerrainBook? book, out string? error)
    {
        try
        {
            book = backend.Book(); // opens this world's sidecar before its match is inspected
            if (backend.LoadedData == DataMatch.Older)
                throw new InvalidDataException("terrain ledger is older than this world save; writes are refused until matching world data is restored");
            if (book is null) throw new InvalidDataException("terrain ledger is null; stored data was kept");
            book.Validate(backend.GridCells);
            error = null;
            return true;
        }
        catch (InvalidDataException e)
        {
            book = null;
            error = e.Message;
            Kiln.Backend.Current.Log(0, LogLevel.Warn, $"terrain: {error}");
            return false;
        }
    }

    private async Task<bool> WaitForWorld(CancellationToken cancel)
    {
        var until = DateTime.UtcNow + WorldWait;
        while (!backend.WorldRunning)
        {
            if (cancel.IsCancellationRequested || DateTime.UtcNow > until) return false;
            await backend.NextTick();
        }
        return true;
    }

    private void CheckGrid(TerrainPlan plan)
    {
        if (backend.GridCells is not { } g) return;
        var r = plan.Region;
        if ((long)r.X + r.SizeX > g.X || (long)r.Z + r.SizeZ > g.Z)
            throw new ArgumentException($"plan '{plan.Patch.Id}': the region {r} is outside the voxel grid ({g.X} x {g.Z} cells)", "patch");
    }

    private static List<TerrainConflict> GivenUpOf(TerrainRecord rec)
    {
        var list = new List<TerrainConflict>();
        foreach (var (key, b) in rec.Blocks.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!b.GivenUp) continue;
            var p = key.Split(',');
            list.Add(new TerrainConflict(KindOf(b), new CellBox(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), 8, 8, 8)));
        }
        return list;
    }

    private static TerrainConflictKind KindOf(TerrainBlock b) =>
        b.Why == (int)TerrainConflictKind.BaseZone ? TerrainConflictKind.BaseZone : TerrainConflictKind.PlayerEdit;

    private static TerrainConflictKind WhyOf(TerrainRecord rec, string key) =>
        rec.Blocks.TryGetValue(key, out var b) || (rec.EarlierBlocks is not null && rec.EarlierBlocks.TryGetValue(key, out b))
            ? KindOf(b) : TerrainConflictKind.PlayerEdit;

    private static CellBox KeyBox(string key)
    {
        var p = key.Split(','); // the book was validated before use
        return new CellBox(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), 8, 8, 8);
    }

    private static bool SameBox(int[] a, int[] b) => a.AsSpan().SequenceEqual(b);

    /// <summary>The cells and mask of <paramref name="part"/> cut out of those of <paramref name="box"/>.</summary>
    private static (Voxel[] Cells, bool[] Mask) Sub(CellBox box, Voxel[] cells, bool[] mask, CellBox part)
    {
        var pc = new Voxel[part.Count];
        var pm = new bool[part.Count];
        for (var z = 0; z < part.SizeZ; z++)
        for (var y = 0; y < part.SizeY; y++)
        {
            var from = box.Index(part.X - box.X, part.Y - box.Y + y, part.Z - box.Z + z);
            var to = part.Index(0, y, z);
            cells.AsSpan(from, part.SizeX).CopyTo(pc.AsSpan(to));
            mask.AsSpan(from, part.SizeX).CopyTo(pm.AsSpan(to));
        }
        return (pc, pm);
    }
}
