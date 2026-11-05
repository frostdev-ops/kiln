// SPDX-License-Identifier: MIT
// kiln.build lab test. Prints "build-test: PASS ..." / "FAIL ..." / "INFO ..." lines, and "build-test: READY <what>"
// when the driver should stop (SIGINT) or kill (-9) the server. Phases over several boots (kiln/data/build-test/
// state.json):
//   1  fresh: grid, read, exact write + read back, shapes, props, base and zone refusals, journal undo, a schematic
//      round trip (capture -> .kschem -> paste turned 90 degrees elsewhere -> read equal -> undo -> read the original),
//      a paste kept for later, PlaceOnce (and again: already placed), then a save.                READY stop
//   2  after SIGINT: every edit and prop is back; PlaceOnce still says placed. Then edit E, a save, then edit D (after
//      the save).                                                                              READY kill
//   3  after kill -9: E (saved) is back, D (after the last save) is gone. With kiln/data/build-test/client present:
//      the client demo for a joined (vanilla) player (teleport to the test area, live edits; "CLIENT step" lines),
//      READY client-done, and the cleanup waits for the next boot (phase 30). Without it: the cleanup at once.
//  30  cleanup: every box gets its original cells back, Kiln's props are removed, the save regions released,
//      PlaceOnce forgotten; a save.                                                            READY stop
//   4  after SIGINT: the test area reads exactly as before phase 1.                            done
// The test area is a flat plateau 790 m from the lab world's only base (3006..3042, 902.6, 1530..1570).
// Delete kiln/data/build-test/ to start over.
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using Kiln;

namespace BuildTest;

public sealed class State
{
    public int Phase { get; set; } = 1;
    public Dictionary<string, string> Hashes { get; set; } = new();
    public Dictionary<string, int[]> Boxes { get; set; } = new();
    public List<PropRecord> Props { get; set; } = new();
    public double Ground { get; set; }
    public int Pass { get; set; }
    public int Fail { get; set; }
}

public sealed class PropRecord
{
    public string Name { get; set; } = "";
    public Guid Template { get; set; }
    public double[] At { get; set; } = [];
    public string Box { get; set; } = "";
}

public sealed class BuildTestMod : Mod
{
    private State _s = new();
    private string StatePath => Path.Combine(Context.DataDirectory, "state.json");
    private string OrigDir => Path.Combine(Context.DataDirectory, "orig");

    // the test area
    private const double Ax = 3006, Bx = 3042, Cx = 3024, Az = 1548, Cz = 1535, Dx = 3012, Ex = 3036, Dz = 1562;
    private const double Fx = 3024, Fz = 1560, Gx = 3016, Gz = 1541;
    private const string Chest = "Prop_Decoration_T1_Storage_24";
    private static readonly string[] AllBoxes = ["A", "B", "C", "D", "E", "F", "G"];

    public override void OnLoad()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        if (File.Exists(StatePath)) _s = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State();
        _ = Run();
    }

    private void Save() => File.WriteAllText(StatePath, JsonSerializer.Serialize(_s, new JsonSerializerOptions { WriteIndented = true }));

    private void Pass(string m)
    {
        _s.Pass++;
        Context.Log.Info($"build-test: PASS {m}");
    }

    private void Fail(string m)
    {
        _s.Fail++;
        Context.Log.Warn($"build-test: FAIL {m}");
    }

    private void Check(bool ok, string m)
    {
        if (ok) Pass(m);
        else Fail(m);
    }

    private void Info(string m) => Context.Log.Info($"build-test: INFO {m}");

    private async Task Run()
    {
        try
        {
            await WaitWorld();
            switch (_s.Phase)
            {
                case 1: await Phase1(); break;
                case 2: await Phase2(); break;
                case 3: await Phase3(); break;
                case 30: await Cleanup(); break;
                case 4: await Phase4(); break;
                default: Info($"phase {_s.Phase}: nothing to do (delete {Context.DataDirectory} to start over)"); break;
            }
        }
        catch (Exception e)
        {
            Fail($"phase {_s.Phase} threw: {e}");
        }
        Save();
    }

    private async Task WaitWorld()
    {
        for (var i = 0; i < 24 * 300 && !(Spawn.Available && Spawn.WorldRunning && Build.Available && Build.Grid is not null); i++)
            await Context.Scheduler.NextTick();
        // a few seconds more: the save's entities stream in
        for (var i = 0; i < 24 * 5; i++) await Context.Scheduler.NextTick();
    }

    private async Task<double> Ground(double x, double z)
    {
        var box = Build.BoxBetween(new WorldPosition(x, 850, z), new WorldPosition(x, 960, z));
        var cells = await Build.ReadAsync(box);
        for (var y = box.SizeY - 1; y >= 0; y--)
        {
            var c = cells[box.Index(0, y, 0)];
            if (c.Fill > 0 && c.Material is > 0 and < 128) return Build.CornerOf(new CellPos(box.X, box.Y + y, box.Z)).Y + c.Fill / 255.0 * 0.5;
        }
        return double.NaN;
    }

    private static string Hash(Voxel[] cells) =>
        Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(cells.AsSpan()))).ToLowerInvariant()[..16];

    private CellBox BoxOf(string name) => new(_s.Boxes[name][0], _s.Boxes[name][1], _s.Boxes[name][2], _s.Boxes[name][3],
        _s.Boxes[name][4], _s.Boxes[name][5]);

    private void Remember(string name, CellBox b) => _s.Boxes[name] = [b.X, b.Y, b.Z, b.SizeX, b.SizeY, b.SizeZ];

    // ------------------------------------------------------------------------------------------------- phase 1 ----

    private async Task Phase1()
    {
        var st = Build.Status!;
        Check(st.Read && st.Write && st.Props, $"kiln.build is on (read {st.Read}, write {st.Write}, props {st.Props}, kept by the save {st.Persist})");
        var g = Build.Grid!;
        Check(Math.Abs(g.CellSize - 0.5) < 1e-9 && g.CellsX > 0, $"grid: cell {g.CellSize} m, origin {g.Origin}, {g.CellsX} x {g.CellsZ} cells ({g.SizeX:0} x {g.SizeZ:0} m), tiles of {g.TileCells} cells");
        var c0 = Build.CellOf(new WorldPosition(3006.2, 902.7, 1548.4));
        Check(c0 == new CellPos(6012, 1805, 3096) && Build.CornerOf(c0) == new WorldPosition(3006, 902.5, 1548),
            $"world <-> cell: (3006.2, 902.7, 1548.4) is cell {c0}, its corner {Build.CornerOf(c0)}");

        // ground of the area, all points must agree (flat plateau)
        var hs = new List<double>();
        foreach (var (x, z) in new[] { (Ax, Az), (Bx, Az), (Cx, Cz), (Dx, Dz), (Ex, Dz) }) hs.Add(await Ground(x, z));
        var h = hs.Average();
        _s.Ground = h;
        Check(hs.All(v => Math.Abs(v - h) < 0.6), $"read: ground of the test area {string.Join(", ", hs.Select(v => v.ToString("0.00")))}");

        // every box the test touches: original cells kept for the cleanup
        Directory.CreateDirectory(OrigDir);
        var structure = Build.BoxAround(new WorldPosition(Ax, h + 2.5, Az), 4.5, 3.25, 4.5);
        var boxes = new Dictionary<string, CellBox>
        {
            ["A"] = structure,
            ["B"] = structure.Offset(Build.CellOf(new WorldPosition(Bx, h, Az)) - Build.CellOf(new WorldPosition(Ax, h, Az))),
            ["C"] = structure.Offset(Build.CellOf(new WorldPosition(Cx, h, Cz)) - Build.CellOf(new WorldPosition(Ax, h, Az))),
            ["D"] = Build.BoxAround(new WorldPosition(Dx, h + 1.5, Dz), 1.5, 2.5, 1.5),
            ["E"] = Build.BoxAround(new WorldPosition(Ex, h + 1.5, Dz), 1.5, 2.5, 1.5),
            // client demo: F holds the outpost pasted live, G the live pillar / pit / beam
            ["F"] = Build.BoxAround(new WorldPosition(Fx, h + 2.25, Fz), 3.75, 3.0, 3.75),
            ["G"] = Build.BoxAround(new WorldPosition(Gx, h + 2.0, Gz), 2.0, 3.0, 2.0),
        };
        foreach (var (name, b) in boxes)
        {
            Remember(name, b);
            var orig = await Schematic.CaptureAsync(b, new CaptureOptions { Name = "orig-" + name, Props = PropFilter.Static });
            orig.Save(Path.Combine(OrigDir, name + ".kschem"));
            _s.Hashes["orig-" + name] = Hash(orig.Cells.ToArray());
            Check(orig.Props.Count == 0, $"box {name} {b} is empty ground: {orig.SolidCount} solid cells, {orig.Props.Count} props");
        }
        Save();

        // 1. exact cells: a 4x4x4 block of material 2, read back
        var probe = new CellBox(structure.X + 1, Build.CellOf(new WorldPosition(Ax, h + 6, Az)).Y, structure.Z + 1, 4, 4, 4);
        var cells = Enumerable.Repeat(Voxel.Of(2), (int)probe.Count).ToArray();
        var w = Build.Write(probe, cells);
        var back = Build.Read(probe);
        Check(w.Changed && w.Verified && back.SequenceEqual(cells), $"read back after write: {w.CellsChanged} cells, {w.BlocksWritten} blocks, verified {w.Verified}, kept {w.Kept}, {w.Milliseconds:0.00} ms");
        var u = await Build.Undo();
        Check(u is not null && u.CellsRestored == probe.Count && Build.Read(probe).All(v => v.IsAir || v.Material >= 128 || v.Fill == 0),
            $"undo of the write: {u?.CellsRestored} cells back");

        // 2. shapes: a hut (floor, walls, door), a pit, flatten, smooth, paint
        var floorY = h + 0.25;
        var hut = BuildShape.Box(new WorldPosition(Ax, h + 1.5, Az), 3, 1.75, 3);
        var add = await Build.Add(hut, 2);
        Check(add.Changed && add.Verified, $"add box (material 2): {add.CellsChanged} cells");
        var inner = await Build.Dig(BuildShape.Box(new WorldPosition(Ax, h + 1.75, Az), 2.25, 1.25, 2.25));
        Check(inner.Changed && inner.Verified, $"dig box (the hut's room): {inner.CellsChanged} cells");
        var door = await Build.Dig(BuildShape.Box(new WorldPosition(Ax + 2.75, h + 1.25, Az), 0.6, 1.0, 0.6));
        Check(door.Changed, $"dig box (door): {door.CellsChanged} cells");
        var roof = await Build.Paint(BuildShape.Box(new WorldPosition(Ax, h + 3.0, Az), 3, 0.3, 3), 66);
        Check(roof.Changed, $"paint the roof (material 66): {roof.CellsChanged} cells");
        var pit = await Build.Dig(BuildShape.Sphere(new WorldPosition(Ax - 3.4, h, Az - 3.4), 1.0));
        Check(pit.Changed && pit.Verified, $"dig sphere r=1: {pit.CellsChanged} cells");
        var bump = await Build.Add(BuildShape.Sphere(new WorldPosition(Ax + 3.4, h + 0.2, Az - 3.4), 0.9), 1);
        var flat = await Build.Flatten(BuildShape.Cylinder(new WorldPosition(Ax + 3.4, h, Az - 3.4), 1.0, 1.5), h);
        Check(bump.Changed && flat.Changed, $"add sphere then flatten it back to the ground: {bump.CellsChanged} / {flat.CellsChanged} cells");
        var smooth = await Build.Smooth(BuildShape.Sphere(new WorldPosition(Ax - 3.4, h, Az - 3.4), 1.6), 0.8f, 2);
        Check(smooth.Verified, $"smooth around the pit: {smooth.CellsChanged} cells");
        var inside = Build.ReadCell(Build.CellOf(new WorldPosition(Ax, h + 1.5, Az)));
        var wall = Build.ReadCell(Build.CellOf(new WorldPosition(Ax + 2.8, h + 2.5, Az + 1.5)));
        Check(inside.IsAir && wall.IsSolid && wall.Material == 2, $"the hut: room cell {inside}, wall cell {wall}");
        // building-block material (Rough Wood Block): the item's material id, cells of it
        var wood = Items.Lookup("Block_T1_Wood_Rough_CRAFTING");
        var woodMat = wood is null ? (byte)0 : Build.MaterialOf(wood.Id.Value);
        Check(woodMat == 143, $"Rough Wood Block places voxel material {woodMat}");
        var bc = Build.CenterOf(Build.CellOf(new WorldPosition(Ax, h + 3.75, Az)));  // on the cell grid: whole cells
        var beam = await Build.Add(BuildShape.Box(bc, 3.25, 0.25, 0.5), woodMat);
        Check(beam.Changed && Build.ReadCell(Build.CellOf(bc)) == Voxel.Of(woodMat),
            $"a roof beam of Rough Wood Block cells: {beam.CellsChanged} cells");

        // 3. props
        var chestTpl = Spawn.Lookup(Chest);
        Check(chestTpl is not null, $"template {Chest}: {chestTpl?.Id}");
        var chest = Build.PlaceProp(Chest, new WorldPosition(Ax - 1.5, floorY + 0.25, Az + 1.5), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2));
        var info = chest.IsNone ? null : Build.Prop(chest);
        Check(info is not null && info.TemplateName == Chest && info.ItemId != 0 && (info.Flags & PropFlags.Placed) != 0,
            $"place prop: {chest} {info?.TemplateName} item {info?.ItemId} at {info?.Position} flags {info?.Flags}");
        var torch = Build.PlaceProp("Prop_Torch01_Wall_On_Craftable", new WorldPosition(Ax + 2.2, h + 2.2, Az - 1.0));
        Check(!torch.IsNone, $"place a second prop (wall torch): {torch}");
        var inBox = Build.Props(structure);
        Check(inBox.Count == 2, $"props in the hut's box: {string.Join(", ", inBox.Select(p => p.TemplateName))}");

        // 4. bases: the lab world's base, and a refused write inside it
        var bases = Build.Bases;
        var home = bases.FirstOrDefault();
        Check(home is not null && home.Contains(new WorldPosition(3800, 835, 1430)), $"bases: {string.Join("; ", bases.Select(b => $"altar {b.Altar} centre {b.Center} half {b.Half}"))}");
        if (home is not null)
        {
            var spot = Build.BoxAround(new WorldPosition(home.Center.X + home.Half.X - 2, home.Center.Y + home.Half.Y - 2, home.Center.Z + home.Half.Z - 2), 0.5, 0.5, 0.5);
            await Build.Load(spot);
            var refused = Build.Write(spot, Enumerable.Repeat(Voxel.Of(2), (int)spot.Count).ToArray());
            Check(refused.Refusal == BuildRefusal.Base,
                $"a write inside the base without InBases is refused: {refused.Refusal} (altar {refused.Zone})");
            var dry = Build.Write(spot, Enumerable.Repeat(Voxel.Of(2), (int)spot.Count).ToArray(), new BuildOptions { Flags = BuildFlags.InBases | BuildFlags.DryRun });
            Check(dry.Refusal == BuildRefusal.DryRun, $"with InBases + DryRun it would change {dry.CellsChanged} cells and changes none");
        }

        // 5. zones: a Gameplay.Protect zone stops an edit made for a player (RespectZones), not Kiln's own
        using (var zone = Gameplay.Protect(new ProtectionZone
               {
                   Name = "build-test", Shape = ZoneShape.Sphere, Center = new WorldPosition(Ex, h, Dz - 6), Radius = 3,
                   Deny = GameplayActions.Build | GameplayActions.Terraform, AllowAdmins = false,
               }))
        {
            await Context.Scheduler.NextTick();
            await Context.Scheduler.NextTick();
            var zshape = BuildShape.Sphere(new WorldPosition(Ex, h, Dz - 6), 0.6);
            var forPlayer = await Build.Dig(zshape, new BuildOptions { Flags = BuildFlags.RespectZones, Actor = new EntityId(5), Journal = false });
            Check(forPlayer.Refusal == BuildRefusal.Zone, $"dig for player 5 in a Protect zone with RespectZones: {forPlayer.Refusal} (zone {forPlayer.Zone}, ours {zone.Id})");
            var own = await Build.Dig(zshape, new BuildOptions { Flags = BuildFlags.DryRun, Journal = false });
            Check(own.Refusal == BuildRefusal.DryRun && own.CellsChanged > 0, $"Kiln's own edit ignores the zone (dry run: {own.CellsChanged} cells)");
        }

        // 6. schematic round trip: capture A, .kschem, paste at B turned 90 degrees, read equal; undo; B as before
        // the paste point: the ground cell under the hut's centre (pasting "at the ground" keeps ground levels)
        var anchor = Build.CellOf(new WorldPosition(Ax, h, Az)) - structure.Min;
        var cap = Schematic.Capture(structure, new CaptureOptions { Name = "hut", Author = "build-test", Anchor = anchor });
        var file = Path.Combine(Context.DataDirectory, "hut.kschem");
        cap.Save(file);
        var loaded = Schematic.Load(file);
        Check(loaded.CellsEqual(cap) && loaded.Props.Count == 2 && loaded.Hash == cap.Hash,
            $"capture -> .kschem -> load: {cap.SizeX}x{cap.SizeY}x{cap.SizeZ} cells ({cap.SolidCount} solid), {cap.Props.Count} props, {new FileInfo(file).Length} bytes, hash {cap.Hash[..12]}");
        var rot = loaded.Rotated(1);
        Check(rot.Rotated(3).CellsEqual(loaded) && loaded.Rotated(4).CellsEqual(loaded), "rotation: four quarter turns give the original");
        var bBefore = Build.Read(BoxOf("B"));
        var at = new WorldPosition(Bx, h, Az);
        var paste = await loaded.Paste(at, new PasteOptions { Rotation = 1, Label = "test paste" });
        Check(paste.Box == loaded.TargetBox(at, 1) && paste.Box == BoxOf("B"), $"paste box {paste.Box} (B is {BoxOf("B")})");
        var bAfter = Build.Read(paste.Box);
        Check(paste.Ok && bAfter.AsSpan().SequenceEqual(rot.Cells), $"schematic round trip: capture -> paste elsewhere turned 90 degrees -> read equal ({paste.Cells.CellsChanged} cells, {paste.PropsPlaced} props, verified {paste.Cells.Verified})");
        var pasted = Build.Props(paste.Box);
        var corner = Build.CornerOf(paste.Box.Min);
        var propsOk = rot.Props.All(sp => pasted.Any(p => p.Template == sp.Template &&
            Vector3.Distance(new Vector3((float)(p.Position.X - corner.X), (float)(p.Position.Y - corner.Y), (float)(p.Position.Z - corner.Z)), sp.Offset) < 0.05f));
        Check(pasted.Count == 2 && propsOk, $"the pasted props stand where the turned schematic says ({string.Join(", ", pasted.Select(p => $"{p.TemplateName} {p.Position}"))})");
        var undo = await Build.Undo();
        Check(undo is not null && undo.PropsRemoved == 2 && Build.Read(BoxOf("B")).SequenceEqual(bBefore) && Build.Props(BoxOf("B")).Count == 0,
            $"undo of the paste: {undo?.CellsRestored} cells back, {undo?.PropsRemoved} props removed, B reads as before");

        // 7. kept for the restart checks: the paste at B (turned 180), PlaceOnce at C (and again)
        var keep = await loaded.Paste(at, new PasteOptions { Rotation = 2, Label = "kept paste", Journal = false });
        Check(keep.Ok, $"paste at B turned 180 degrees (kept): {keep.Cells.CellsChanged} cells, {keep.PropsPlaced} props, kept by the save {keep.Cells.Kept}");
        var once = await Build.PlaceOnce("build-test-outpost", loaded, new WorldPosition(Cx, h, Cz), new PasteOptions { Journal = false });
        Check(once.Placed && once.Record is not null, $"PlaceOnce placed it: {once.Record?.Cells} cells, {once.Record?.Props} props, box [{string.Join(",", once.Record?.Box ?? [])}]");
        var again = await Build.PlaceOnce("build-test-outpost", loaded, new WorldPosition(Cx, h, Cz));
        Check(!again.Placed && again.AlreadyPlaced, $"PlaceOnce again: already placed ({again.Record?.PlacedAt:O})");
        foreach (var name in new[] { "A", "B", "C" })
        {
            var cellsNow = Build.Read(BoxOf(name));
            _s.Hashes[name] = Hash(cellsNow);
            foreach (var p in Build.Props(BoxOf(name)))
                _s.Props.Add(new PropRecord { Name = p.TemplateName, Template = p.Template, At = [p.Position.X, p.Position.Y, p.Position.Z], Box = name });
        }
        Info($"expected after restart: {string.Join(", ", _s.Hashes.Where(k => k.Key.Length == 1).Select(k => $"{k.Key}={k.Value}"))}, {_s.Props.Count} props");
        var st2 = Build.Status!;
        Info($"status: {st2.CellsWritten} cells written, {st2.BlocksWritten} blocks, {st2.PropsPlaced} props placed, {st2.KeptChunks} chunks kept");
        var saved = await Saves.Request();
        Check(saved.Success, $"world saved: {saved}");
        _s.Phase = 2;
        Save();
        Context.Log.Info("build-test: READY stop");
    }

    // ------------------------------------------------------------------------------------------------- phase 2 ----

    private async Task<bool> SameAsRecorded(string name, string what)
    {
        var cells = await Build.ReadAsync(BoxOf(name));
        var ok = Hash(cells) == _s.Hashes[name];
        Check(ok, $"{what}: box {name} reads {Hash(cells)} (recorded {_s.Hashes[name]})");
        return ok;
    }

    private void PropsBack(string what)
    {
        var missing = _s.Props.Where(r => !Build.Props(new WorldPosition(r.At[0] - 0.1, r.At[1] - 0.1, r.At[2] - 0.1),
            new WorldPosition(r.At[0] + 0.1, r.At[1] + 0.1, r.At[2] + 0.1)).Any(p => p.Template == r.Template)).ToList();
        Check(missing.Count == 0, $"{what}: {_s.Props.Count - missing.Count} of {_s.Props.Count} props back{(missing.Count > 0 ? " (missing " + string.Join(", ", missing.Select(m => m.Name)) + ")" : "")}");
    }

    private async Task Phase2()
    {
        foreach (var n in new[] { "A", "B", "C" }) await SameAsRecorded(n, "survives SIGINT save + restart");
        PropsBack("props after SIGINT restart");
        var again = await Build.PlaceOnce("build-test-outpost", () => Task.FromResult(true));
        Check(again.AlreadyPlaced, $"PlaceOnce after the restart: already placed (record in WorldData: {again.Record?.PlacedAt:O})");
        var st = Build.Status!;
        Check(st.KeptChunks > 0 && st.SavesSeen > 0, $"save regions: {st.KeptChunks} chunk(s) kept, {st.SavesSeen} save(s) extended");

        // E before a save, D after it; then kill -9
        var h = _s.Ground;
        var e = await Build.Add(BuildShape.Box(new WorldPosition(Ex, h + 1.5, Dz), 0.75, 1.5, 0.75), 2, new BuildOptions { Journal = false });
        _s.Hashes["E"] = Hash(await Build.ReadAsync(BoxOf("E")));
        var saved = await Saves.Request();
        Check(e.Changed && saved.Success, $"edit E ({e.CellsChanged} cells), then a save: {saved}");
        var d = await Build.Add(BuildShape.Box(new WorldPosition(Dx, h + 1.5, Dz), 0.75, 1.5, 0.75), 2, new BuildOptions { Journal = false });
        _s.Hashes["D"] = Hash(await Build.ReadAsync(BoxOf("D")));
        Check(d.Changed, $"edit D after the save ({d.CellsChanged} cells): kill -9 now");
        _s.Phase = 3;
        Save();
        Context.Log.Info("build-test: READY kill");
    }

    // ------------------------------------------------------------------------------------------------- phase 3 ----

    private async Task Phase3()
    {
        await SameAsRecorded("E", "an edit saved before kill -9 survives it");
        var d = Hash(await Build.ReadAsync(BoxOf("D")));
        Check(d == _s.Hashes["orig-D"], $"an edit made after the last save is lost by kill -9 (D reads {d}, its original {_s.Hashes["orig-D"]}): the game writes the world only when it saves");
        foreach (var n in new[] { "A", "B", "C" }) await SameAsRecorded(n, "still there after kill -9");
        if (File.Exists(Path.Combine(Context.DataDirectory, "client")))
        {
            _s.Phase = 30;
            Save();
            await ClientDemo();
            Context.Log.Info("build-test: READY client-done");
            return;
        }
        await Cleanup();
    }

    private async Task Cleanup()
    {
        // cleanup: original cells back, props removed, regions released, PlaceOnce forgotten
        var removed = 0;
        foreach (var name in AllBoxes)
        {
            foreach (var p in Build.Props(BoxOf(name)))
                if (Build.RemoveProp(p.Entity, new BuildOptions { Journal = false })) removed++;
            var orig = Schematic.Load(Path.Combine(OrigDir, name + ".kschem"));
            var r = await Build.WriteAsync(BoxOf(name), orig.Cells.ToArray(), new BuildOptions { Journal = false, Flags = BuildFlags.Transient });
            var now = Build.Read(BoxOf(name));
            Check(Hash(now) == _s.Hashes["orig-" + name], $"restore box {name}: {r.CellsChanged} cells back to the original");
            Build.Release(BoxOf(name));
        }
        Check(removed >= _s.Props.Count, $"removed {removed} props (expected {_s.Props.Count} kept ones, plus any from the client demo)");
        Check(Build.ForgetPlaced("build-test-outpost") && !Build.WasPlaced("build-test-outpost"), "PlaceOnce record forgotten");
        Info($"save regions after release: {Build.Status!.KeptChunks} chunk(s)");
        var saved = await Saves.Request();
        Check(saved.Success, $"world saved: {saved}");
        _s.Phase = 4;
        Save();
        Context.Log.Info("build-test: READY stop");
    }

    // ------------------------------------------------------------------------------------------------ client ----

    private void Step(int n, string what) => Context.Log.Info($"build-test: CLIENT step {n}: {what}");

    private async Task Ticks(double seconds)
    {
        for (var i = 0; i < (int)(seconds * 24.5); i++) await Context.Scheduler.NextTick();
    }

    private async Task ClientDemo()
    {
        var h = _s.Ground;
        Step(0, "waiting for a player (vanilla client) to join");
        EntityId player = default;
        while (player.IsNone)
        {
            var ps = World.Find("PlayerInput");
            if (ps.Count > 0 && World.Position(ps[0]) is { } pp && pp.Y > 100) player = ps[0];
            else await Ticks(1);
        }
        await Ticks(8);  // let the client finish loading in
        await Next(1);
        Step(1, $"player {player} is at {World.Position(player)}; teleporting to the test area (3024, {h + 1:0.0}, 1528), facing the structures (north, +z)");
        var tp = await Teleport.To(player, new WorldPosition(3024, h + 1, 1528), new TeleportOptions { FindGround = true, YawDegrees = 0, Checkpoint = true });
        Step(1, $"teleport: {tp.Status} at {tp.Position} ({tp.Reason})");
        await Ticks(10);
        Step(2, "look: hut A (left, x 3006 z 1548: dirt walls, wood beam, door to the east, chest + torch inside), copy B turned 180 degrees (right, x 3042), outpost C from PlaceOnce (ahead, x 3024 z 1535). All saved and reloaded twice");
        await Next(3);
        var outpostPath = Path.Combine(KilnPathsRoot(), "data", "kiln.build", "schematics", "kiln-outpost.kschem");
        if (File.Exists(outpostPath))
        {
            var outpost = Schematic.Load(outpostPath);
            var r = await outpost.Paste(new WorldPosition(Fx, h, Fz), new PasteOptions { Label = "client outpost" });
            Step(3, $"LIVE: the stone-and-wood outpost appears behind C (x 3024 z 1560): {r.Cells.CellsChanged} cells, {r.PropsPlaced} props (chest, two torches)");
        }
        else
        {
            Step(3, "skipped: no kiln-outpost.kschem (deploy the Schematics sample)");
        }
        await Next(4);
        for (var i = 1; i <= 4; i++)
        {
            var r = await Build.Add(BuildShape.Box(new WorldPosition(Gx, h + i - 0.5, Gz), 0.5, 0.5, 0.5), 155, new BuildOptions { Label = "pillar" });
            Step(4, $"LIVE: stone pillar at x 3016 z 1541 grows to {i} m ({r.CellsChanged} cells)");
            await Ticks(4);
        }
        await Next(5);
        var pit = await Build.Dig(BuildShape.Sphere(new WorldPosition(Gx + 1.2, h, Gz - 1.2), 0.9));
        Step(5, $"LIVE: a pit is dug next to the pillar ({pit.CellsChanged} cells)");
        await Next(6);
        var woodMat = Items.Lookup("Block_T1_Wood_Rough_CRAFTING") is { } wood ? Build.MaterialOf(wood.Id.Value) : (byte)143;
        var beam = await Build.Add(BuildShape.Box(Build.CenterOf(Build.CellOf(new WorldPosition(Gx, h + 4.25, Gz))), 1.75, 0.25, 0.25), woodMat);
        Step(6, $"LIVE: a Rough Wood beam on top of the pillar ({beam.CellsChanged} cells, material {woodMat})");
        await Next(7);
        var chest = Build.PlaceProp(Chest, new WorldPosition(Gx - 1.2, h + 0.05, Gz - 1.2), Quaternion.Identity);
        Step(7, $"LIVE: a chest appears left of the pillar ({chest}); try opening it");
        await Next(8);
        var u = await Build.Undo();
        Step(8, $"LIVE: undo of the last edit (the chest disappears): {u?.Label}, {u?.PropsRemoved} prop(s) removed");
        await Next(9);
        var u2 = await Build.Undo();
        Step(9, $"LIVE: undo again (the beam disappears): {u2?.Label}, {u2?.CellsRestored} cells back");
        await Ticks(5);
        Step(10, "demo done; the server keeps running until it is stopped (cleanup on the next boot)");
    }

    // the driver advances the client demo by creating kiln/data/build-test/next (or after 10 minutes: wall clock, the
    // server steps at about 60/s, so a tick count would run out early and let the demo get ahead of the driver)
    private async Task Next(int step)
    {
        var f = Path.Combine(Context.DataDirectory, "next");
        Step(step, "waiting for the driver (touch kiln/data/build-test/next)");
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (DateTime.UtcNow < until && !File.Exists(f)) await Context.Scheduler.NextTick();
        try { File.Delete(f); } catch (IOException) { }
    }

    private static string KilnPathsRoot()
    {
        // <kiln>/mods/build-test/BuildTest.dll -> <kiln>
        var dir = Path.GetDirectoryName(typeof(BuildTestMod).Assembly.Location);
        return string.IsNullOrEmpty(dir) ? "kiln" : Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    // ------------------------------------------------------------------------------------------------- phase 4 ----

    private async Task Phase4()
    {
        foreach (var name in AllBoxes)
        {
            var now = Hash(await Build.ReadAsync(BoxOf(name)));
            Check(now == _s.Hashes["orig-" + name], $"after the cleanup and a restart box {name} reads its original ({now})");
            Check(Build.Props(BoxOf(name), PropFilter.Static).Count == 0, $"no props left in box {name}");
        }
        Context.Log.Info($"build-test: done, {_s.Pass} PASS, {_s.Fail} FAIL over all phases");
        _s.Phase = 5;
    }
}
