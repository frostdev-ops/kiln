// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kiln.Admin;

/// <summary>World editing ops over Kiln.Build (docs/kiln/services/build.md "Admin ops"): status, bases, cell reads,
/// shapes, props, schematics (capture/paste), undo/history, kept regions, PlaceOnce records. Edits are journaled with
/// Kiln.Admin as their owner ("admin" in build_undo/build_history); writes inside player bases need flags ["InBases"].
/// Registered only when Kiln.Build is present.</summary>
internal static partial class Ops
{
    private const int MaxReadCells = 65536;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SchematicName();

    internal static bool RegisterBuildSafe(Bridge bridge, Logger log)
    {
        try
        {
            return RegisterBuild(bridge, log);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException or MissingMethodException)
        {
            log.Info($"build ops off: Kiln.Build is not available ({e.GetType().Name})");
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RegisterBuild(Bridge bridge, Logger log)
    {
        if (!Build.Available) return false;
        bridge.Register("build_status", _ =>
        {
            var s = Build.Status;
            var g = Build.Grid;
            return new JsonObject
            {
                ["read"] = s?.Read, ["write"] = s?.Write, ["props"] = s?.Props, ["world"] = s?.World, ["persist"] = s?.Persist,
                ["reads"] = s?.Reads, ["writes"] = s?.Writes, ["cellsRead"] = s?.CellsRead, ["cellsWritten"] = s?.CellsWritten,
                ["blocksWritten"] = s?.BlocksWritten, ["propsPlaced"] = s?.PropsPlaced, ["propsRemoved"] = s?.PropsRemoved,
                ["refused"] = s?.Refused, ["keptChunks"] = s?.KeptChunks, ["savesSeen"] = s?.SavesSeen, ["detail"] = s?.Detail,
                ["grid"] = g is null ? null : new JsonObject
                {
                    ["cellSize"] = g.CellSize, ["origin"] = Pos(g.Origin), ["cellsX"] = g.CellsX, ["cellsZ"] = g.CellsZ,
                    ["tileCells"] = g.TileCells, ["level"] = g.Level,
                },
            };
        });
        bridge.Register("build_bases", _ => new JsonArray([.. Build.Bases.Select(b => (JsonNode)new JsonObject
        {
            ["altar"] = b.Altar.Value, ["center"] = Pos(b.Center), ["half"] = Vec(b.Half), ["abandoned"] = b.Abandoned,
        })]));
        bridge.Register("build_regions", _ => new JsonObject { ["keptChunks"] = Build.Status?.KeptChunks ?? 0 });
        // {box: {x, y, z, sx, sy, sz}} (cells) or the box fields at the top level
        bridge.Register("build_release", req => BuildGuard(() => new JsonObject { ["released"] = Build.Release(Box(req["box"] as JsonObject ?? req)) }));
        // {x, y, z, sx, sy, sz} in cells (Build.CellOf: world metres / grid cellSize) -> u16 cells (low byte material, high
        // byte fill), little-endian, base64; x fastest, then y, then z: index = (z * sy + y) * sx + x
        bridge.RegisterAsync("build_read", async req =>
        {
            var box = Box(req);
            if ((long)box.SizeX * box.SizeY * box.SizeZ > MaxReadCells) throw new AdminError($"at most {MaxReadCells} cells per read");
            var cells = await BuildGuardAsync(() => Build.ReadAsync(box));
            var bytes = new byte[cells.Length * 2];
            var solid = 0;
            for (var i = 0; i < cells.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 2, 2), cells[i].Raw);
                if (cells[i].Raw != 0) solid++;
            }
            return new JsonObject
            {
                ["box"] = BoxJson(box), ["corner"] = Pos(Build.CornerOf(box.Min)), ["cells"] = cells.Length, ["solid"] = solid,
                ["data"] = Convert.ToBase64String(bytes),
            };
        });
        // {edit: dig|add|flatten|smooth|paint|replace ("op" names the request itself), shape: box|sphere|cylinder, x, y, z, hx, hy?, hz?, material?, from?, to?,
        //  level?, strength?, passes?, flags?: ["InBases", "RespectZones", "DryRun", ...]}
        bridge.RegisterAsync("build_shape", async req =>
        {
            var at = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var hx = Number(req, "hx");
            var hy = req["hy"] is null ? hx : Number(req, "hy");
            var hz = req["hz"] is null ? hx : Number(req, "hz");
            if (hx <= 0 || hy <= 0 || hz <= 0 || hx > 64 || hy > 64 || hz > 64) throw new AdminError("half sizes: 0 to 64 m");
            var shape = (Str(req, "shape") ?? "box").ToLowerInvariant() switch
            {
                "box" => BuildShape.Box(at, hx, hy, hz),
                "sphere" => BuildShape.Sphere(at, hx),
                "cylinder" => BuildShape.Cylinder(at, hx, hy),
                var s => throw new AdminError($"shape '{s}': box, sphere or cylinder"),
            };
            var options = new BuildOptions { Flags = BuildFlagSet(req["flags"]), Label = "admin " + (Str(req, "edit") ?? "") };
            byte Mat(string key) => req[key] is null ? throw new AdminError($"{key} required (a voxel material 0-255)") : (byte)Math.Clamp(Number(req, key), 0, 255);
            var r = await BuildGuardAsync(() => (Str(req, "edit") ?? "").ToLowerInvariant() switch
            {
                "dig" => Build.Dig(shape, options),
                "add" => Build.Add(shape, Mat("material"), options),
                "flatten" => Build.Flatten(shape, Number(req, "level"), req["material"] is null ? (byte)0 : Mat("material"), options),
                "smooth" => Build.Smooth(shape, req["strength"] is null ? 0.5f : (float)Number(req, "strength"), req["passes"] is null ? 2 : (int)Number(req, "passes"), options),
                "paint" => Build.Paint(shape, Mat("material"), options),
                "replace" => Build.Replace(shape, Mat("from"), Mat("to"), options),
                var o => throw new AdminError($"edit '{o}': dig, add, flatten, smooth, paint or replace"),
            });
            log.Info($"dashboard build {Str(req, "edit")} at {at}: {r.CellsChanged} cell(s){(r.Refused ? $", refused: {r.Refusal}" : "")}");
            return ResultJson(r);
        });
        // {min: {x,y,z}, max: {x,y,z}, filter?: placed|saved|all|static}
        bridge.Register("build_props", req =>
        {
            var filter = (Str(req, "filter") ?? "placed").ToLowerInvariant() switch
            {
                "placed" => PropFilter.Placed,
                "saved" => PropFilter.Saved,
                "all" => PropFilter.All,
                "static" => PropFilter.Static,
                var f => throw new AdminError($"filter '{f}': placed, saved, all or static"),
            };
            var list = Build.Props(PosOf(req, "min"), PosOf(req, "max"), filter);
            return new JsonArray([.. list.Take(1000).Select(p => (JsonNode)PropJson(p))]);
        });
        // {template (name or GUID), x, y, z, yaw? (degrees), item?}
        // {template, x, y, z, yaw?, item?, flags?: ["InBases", ...]} (inside a player base only with InBases, like build_shape)
        bridge.Register("build_prop_place", req => BuildGuard(() =>
        {
            var template = Str(req, "template") ?? throw new AdminError("template required");
            var at = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var yaw = req["yaw"] is null ? 0 : Number(req, "yaw");
            var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(yaw * Math.PI / 180));
            var item = req["item"] is null ? 0u : (uint)Number(req, "item");
            var e = Build.PlaceProp(template, at, rot, item, new BuildOptions { Flags = BuildFlagSet(req["flags"]), Label = "admin prop" });
            if (e.IsNone) throw new AdminError("the prop was not placed");
            log.Info($"dashboard placed prop {template} at {at}: entity {e}");
            return Build.Prop(e) is { } p ? PropJson(p) : new JsonObject { ["entity"] = e.Value };
        }));
        bridge.Register("build_prop_remove", req => BuildGuard(() => new JsonObject { ["removed"] = Build.RemoveProp(new EntityId(Entity(req)), new BuildOptions { Label = "admin remove prop" }) }));
        bridge.Register("build_schematics", _ =>
        {
            var dir = BuildCommands.SchematicDirectory;
            if (!Directory.Exists(dir)) return new JsonArray();
            var list = new JsonArray();
            foreach (var f in Directory.GetFiles(dir, "*.kschem").OrderBy(f => f, StringComparer.Ordinal).Take(200))
            {
                var o = new JsonObject { ["name"] = Path.GetFileNameWithoutExtension(f), ["bytes"] = new FileInfo(f).Length };
                try
                {
                    var s = Schematic.Load(f);
                    o["size"] = new JsonArray(s.SizeX, s.SizeY, s.SizeZ);
                    o["props"] = s.Props.Count;
                    o["hash"] = s.Hash;
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or FormatException)
                {
                    o["error"] = e.Message;
                }
                list.Add(o);
            }
            return list;
        });
        // {name, min: {x,y,z}, max: {x,y,z}} -> kiln/data/kiln.build/schematics/<name>.kschem
        bridge.RegisterAsync("build_capture", async req =>
        {
            var name = SchematicFile(req);
            var box = Build.BoxBetween(PosOf(req, "min"), PosOf(req, "max"));
            if ((long)box.SizeX * box.SizeY * box.SizeZ > 1 << 22) throw new AdminError("the box is larger than a schematic can hold (4M cells)");
            var s = await BuildGuardAsync(() => Schematic.CaptureAsync(box, new CaptureOptions { Name = name, Author = "Kiln.Admin" }));
            Directory.CreateDirectory(BuildCommands.SchematicDirectory);
            var path = Path.Combine(BuildCommands.SchematicDirectory, name + ".kschem");
            s.Save(path);
            log.Info($"dashboard captured schematic {name}: {s.SizeX}x{s.SizeY}x{s.SizeZ}, {s.Props.Count} prop(s)");
            return new JsonObject
            {
                ["name"] = name, ["size"] = new JsonArray(s.SizeX, s.SizeY, s.SizeZ), ["props"] = s.Props.Count, ["hash"] = s.Hash,
                ["bytes"] = new FileInfo(path).Length,
            };
        });
        // {name, x, y, z, rotation? (0/90/180/270), mode?: replace|merge, flags?}
        bridge.RegisterAsync("build_paste", async req =>
        {
            var name = SchematicFile(req);
            var path = Path.Combine(BuildCommands.SchematicDirectory, name + ".kschem");
            if (!File.Exists(path)) throw new AdminError($"no schematic '{name}'");
            var rotation = req["rotation"] is null ? 0 : (int)Number(req, "rotation");
            if (rotation % 90 != 0) throw new AdminError("rotation: 0, 90, 180 or 270");
            var mode = (Str(req, "mode") ?? "replace").ToLowerInvariant() switch
            {
                "replace" => PasteMode.Replace,
                "merge" => PasteMode.Merge,
                var m => throw new AdminError($"mode '{m}': replace or merge"),
            };
            var at = new WorldPosition(Number(req, "x"), Number(req, "y"), Number(req, "z"));
            var r = await BuildGuardAsync(() => Schematic.Load(path).Paste(at, new PasteOptions
            {
                Rotation = rotation, Mode = mode, Flags = BuildFlagSet(req["flags"]), Label = "admin paste " + name,
            }));
            log.Info($"dashboard pasted {name} at {at}: {r.Cells.CellsChanged} cell(s), {r.PropsPlaced} prop(s)");
            return new JsonObject
            {
                ["box"] = BoxJson(r.Box), ["cells"] = ResultJson(r.Cells), ["propsPlaced"] = r.PropsPlaced, ["propsFailed"] = r.PropsFailed,
                ["propsCleared"] = r.PropsCleared, ["journalId"] = r.JournalId, ["ok"] = r.Ok,
            };
        });
        // {owner?: "admin" (Kiln.Admin's edits, default) | "player:<entity>" | a mod id}
        bridge.RegisterAsync("build_undo", async req =>
        {
            var owner = JournalOwnerOf(req);
            var u = await BuildGuardAsync(() => Build.Undo(owner));
            if (u is null) throw new AdminError($"nothing of {owner} to undo");
            log.Info($"dashboard undid {u.Label}: {u.CellsRestored} cell(s) back");
            return new JsonObject
            {
                ["id"] = u.Id, ["label"] = u.Label, ["cellsRestored"] = u.CellsRestored, ["cellsKept"] = u.CellsKept,
                ["propsRemoved"] = u.PropsRemoved, ["propsRestored"] = u.PropsRestored, ["complete"] = u.Complete,
            };
        });
        bridge.Register("build_history", req =>
        {
            var owner = req["owner"] is null ? null : JournalOwnerOf(req);
            return new JsonArray([.. Build.History(owner).Select(h => (JsonNode)new JsonObject
            {
                ["id"] = h.Id, ["owner"] = h.Owner, ["label"] = h.Label, ["kind"] = h.Kind.ToString(), ["at"] = h.At.ToString("O"),
                ["cells"] = h.Cells, ["propsPlaced"] = h.PropsPlaced, ["propsRemoved"] = h.PropsRemoved,
            })]);
        });
        // PlaceOnce records are kept per mod; Kiln.Admin can list and forget its own (mod "kiln-admin", the default).
        bridge.Register("build_placeonce_list", req =>
        {
            OwnPlaceOnce(req);
            return new JsonArray([.. Build.PlacedOnce.Select(r => (JsonNode)new JsonObject
            {
                ["key"] = r.Key, ["placedAt"] = r.PlacedAt.ToString("O"), ["at"] = new JsonArray([.. r.At.Select(v => (JsonNode)v)]),
                ["rotation"] = r.Rotation, ["schematic"] = r.Schematic, ["hash"] = r.Hash, ["cells"] = r.Cells, ["props"] = r.Props,
            })]);
        });
        bridge.Register("build_placeonce_forget", req =>
        {
            OwnPlaceOnce(req);
            var key = Str(req, "key") ?? throw new AdminError("key required");
            return new JsonObject { ["forgotten"] = Build.ForgetPlaced(key) };
        });
        return true;
    }

    private static void OwnPlaceOnce(JsonObject req)
    {
        if (Str(req, "mod") is { Length: > 0 } m && m != SelfId)
            throw new AdminError($"PlaceOnce records belong to each mod; Kiln.Admin can reach only its own ('{SelfId}'), not '{m}'");
    }

    private static string JournalOwnerOf(JsonObject req) => Str(req, "owner") switch
    {
        null or "" or "admin" => SelfId,
        var o => o,
    };

    private static string SchematicFile(JsonObject req)
    {
        var name = Str(req, "name") ?? throw new AdminError("name required");
        return SchematicName().IsMatch(name) ? name : throw new AdminError("name: 1-64 of A-Z a-z 0-9 . _ -");
    }

    private static CellBox Box(JsonObject req)
    {
        int I(string k) => (int)Math.Floor(Number(req, k));
        var box = new CellBox(I("x"), I("y"), I("z"), I("sx"), I("sy"), I("sz"));
        if (box.SizeX <= 0 || box.SizeY <= 0 || box.SizeZ <= 0) throw new AdminError("sx, sy and sz must be positive");
        return box;
    }

    private static JsonObject BoxJson(CellBox b) => new() { ["x"] = b.X, ["y"] = b.Y, ["z"] = b.Z, ["sx"] = b.SizeX, ["sy"] = b.SizeY, ["sz"] = b.SizeZ };

    private static WorldPosition PosOf(JsonObject req, string key) =>
        req[key] is JsonObject o ? new WorldPosition(Number(o, "x"), Number(o, "y"), Number(o, "z")) : throw new AdminError($"{key}: {{x, y, z}}");

    private static JsonObject Vec(Vector3 v) => new() { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z };

    private static JsonObject ResultJson(BuildResult r) => new()
    {
        ["cellsChanged"] = r.CellsChanged, ["blocksWritten"] = r.BlocksWritten, ["verified"] = r.Verified, ["kept"] = r.Kept,
        ["box"] = BoxJson(r.Box), ["ms"] = Math.Round(r.Milliseconds, 2), ["refusal"] = r.Refusal.ToString(), ["zone"] = r.Zone == 0 ? null : r.Zone,
    };

    private static JsonObject PropJson(PlacedProp p) => new()
    {
        ["entity"] = p.Entity.Value, ["template"] = p.Template.ToString(), ["templateName"] = p.TemplateName, ["pos"] = Pos(p.Position),
        ["item"] = p.ItemId == 0 ? null : p.ItemId, ["kind"] = p.Kind.ToString(), ["flags"] = p.Flags.ToString(),
    };

    private static BuildFlags BuildFlagSet(JsonNode? n)
    {
        BuildFlags f = 0;
        var names = n switch
        {
            null => [],
            JsonArray a => a.Select(x => x?.GetValue<string>() ?? "").ToArray(),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split([',', ' ', '|'], StringSplitOptions.RemoveEmptyEntries),
            _ => throw new AdminError("flags: a list of InBases, RespectZones, AllowAltars, DryRun, Transient"),
        };
        foreach (var name in names)
            f |= Enum.TryParse<BuildFlags>(name, true, out var x) && x != BuildFlags.Unloaded ? x : throw new AdminError($"unknown flag '{name}'");
        return f;
    }

    private static JsonNode? BuildGuard(Func<JsonNode?> f)
    {
        try
        {
            return f();
        }
        catch (Exception e) when (e is BuildException or ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            throw new AdminError(e.Message);
        }
    }

    private static async Task<T> BuildGuardAsync<T>(Func<Task<T>> f)
    {
        try
        {
            return await f();
        }
        catch (Exception e) when (e is BuildException or ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException or TimeoutException or InvalidDataException)
        {
            throw new AdminError(e.Message);
        }
    }
}
