// SPDX-License-Identifier: MIT
// The /build commands (admin): selection, capture/paste of schematics, shape edits at the player, undo, props.
// They are Kiln's, not a mod's: a mod that wants them calls BuildCommands.Register() (the Schematics sample does). The
// first call adds /build (owner "kiln", permission node kiln.command.build), later calls share it, and it goes when the
// last mod that asked lets go of it or unloads. The handlers run as Kiln: edits made from the console are journaled
// under "kiln", a player's under that player. Schematics live in kiln/data/kiln.build/schematics/<name>.kschem.
using System.Globalization;
using System.Numerics;

namespace Kiln;

/// <summary>The <c>/build</c> command tree (admins only; permission node <c>kiln.command.build</c>).</summary>
public static class BuildCommands
{
    private sealed class Selection
    {
        public WorldPosition? A, B;
    }

    private static readonly Dictionary<string, Selection> Selections = new();

    /// <summary>Where <c>/build capture</c> and <c>/build paste</c> keep schematics.</summary>
    public static string SchematicDirectory => Path.Combine(KilnPaths.Root, "data", "kiln.build", "schematics");

    /// <summary>The path of a schematic by name (letters, digits, <c>- _ .</c>).</summary>
    public static string SchematicPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) || name.Contains(".."))
            throw new CommandException($"bad schematic name '{name}' (letters, digits, - _ .)");
        return Path.Combine(SchematicDirectory, name.EndsWith(".kschem", StringComparison.OrdinalIgnoreCase) ? name : name + ".kschem");
    }

    /// <summary>Asks for <c>/build</c> on behalf of the calling mod. The command is Kiln's and shared: it is added once,
    /// however many mods ask, stays while one of them holds its registration, and goes when the last one is disposed or
    /// its mod unloads.</summary>
    public static CommandRegistration Register() =>
        Commands.RegisterShared("build", Tree, CommandPermission.Admin, "Kiln world edits (kiln.build)");

    private static LiteralNode Tree()
    {
        var root = Commands.Literal("build").Describe("world edits: selection, schematics, shapes, props, undo")
            .Executes(StatusCmd)
            .Then(Commands.Literal("status").Describe("what kiln.build can do here").Executes(StatusCmd))
            .Then(Commands.Literal("pos1").Describe("selection corner 1 (your position, or x y z)").Executes(c => Mark(c, 1))
                .Then(Commands.Argument("at", Arg.Position()).Executes(c => Mark(c, 1))))
            .Then(Commands.Literal("pos2").Describe("selection corner 2 (your position, or x y z)").Executes(c => Mark(c, 2))
                .Then(Commands.Argument("at", Arg.Position()).Executes(c => Mark(c, 2))))
            .Then(Commands.Literal("sel").Describe("shows the selection").Executes(ShowSel))
            .Then(Commands.Literal("capture").Describe("saves the selection as a schematic")
                .Then(Commands.Argument("name", Arg.Word()).Executes(Capture)))
            .Then(Commands.Literal("paste").Describe("pastes a schematic at you (or x y z), turned 0/90/180/270")
                .Then(Commands.Argument("name", Arg.Word()).Executes(Paste)
                    .Then(Commands.Argument("rotation", Arg.Choice("0", "90", "180", "270")).Executes(Paste)
                        .Then(Commands.Argument("at", Arg.Position()).Executes(Paste)))))
            .Then(Commands.Literal("list").Describe("schematics on this server").Executes(List))
            .Then(Commands.Literal("undo").Describe("reverts your last edit").Executes(Undo))
            .Then(Commands.Literal("history").Describe("your recent edits").Executes(History))
            .Then(Commands.Literal("dig").Describe("removes a sphere/box/cylinder of terrain at you")
                .Then(Commands.Argument("shape", Arg.Choice("sphere", "box", "cylinder"))
                    .Then(Commands.Argument("size", Arg.Double(0.5, 32)).Executes(c => Shape(c, ShapeOp.Dig))
                        .Then(Commands.Argument("at", Arg.Position()).Executes(c => Shape(c, ShapeOp.Dig))))))
            .Then(Commands.Literal("add").Describe("fills a shape with a material (id or building item) at you")
                .Then(Commands.Argument("shape", Arg.Choice("sphere", "box", "cylinder"))
                    .Then(Commands.Argument("size", Arg.Double(0.5, 32))
                        .Then(Commands.Argument("material", Arg.Word()).Executes(c => Shape(c, ShapeOp.Add))
                            .Then(Commands.Argument("at", Arg.Position()).Executes(c => Shape(c, ShapeOp.Add)))))))
            .Then(Commands.Literal("flatten").Describe("levels a circle to your height (or a given level)")
                .Then(Commands.Argument("size", Arg.Double(0.5, 32)).Executes(c => Shape(c, ShapeOp.Flatten))
                    .Then(Commands.Argument("level", Arg.Double(-1000, 5000)).Executes(c => Shape(c, ShapeOp.Flatten)))))
            .Then(Commands.Literal("smooth").Describe("smooths terrain in a sphere at you")
                .Then(Commands.Argument("size", Arg.Double(0.5, 32)).Executes(c => Shape(c, ShapeOp.Smooth))))
            .Then(Commands.Literal("paint").Describe("repaints terrain in a sphere at you")
                .Then(Commands.Argument("size", Arg.Double(0.5, 32))
                    .Then(Commands.Argument("material", Arg.Word()).Executes(c => Shape(c, ShapeOp.Paint)))))
            .Then(Commands.Literal("prop").Describe("places a prop template at you or at x y z (yaw in degrees)")
                .Then(Commands.Argument("template", Arg.Word()).Executes(PlaceProp)
                    .Then(Commands.Argument("yaw", Arg.Double(-360, 360)).Executes(PlaceProp)
                        .Then(Commands.Argument("at", Arg.Position()).Executes(PlaceProp)))))
            .Then(Commands.Literal("bases").Describe("player bases (build zones)").Executes(Bases))
            .Then(Commands.Literal("release").Describe("stops keeping Kiln's terrain in the selection (it reverts at a restart)")
                .Executes(Release));
        return root;
    }

    private static string Key(CommandContext c) => c.Source.IsPlayer ? $"p{c.Source.Player.Value}" : c.Source.Name;

    private static BuildOptions Options(CommandContext c, string label) => new()
    {
        Actor = c.Source.IsPlayer ? c.Source.Player : EntityId.None,
        Label = label,
        // admins editing by hand may touch bases; they asked for it by name
        Flags = BuildFlags.InBases,
    };

    private static WorldPosition Here(CommandContext c)
    {
        if (c.TryGet<WorldPosition>("at", out var at)) return at;
        if (c.Source.IsPlayer && World.TryGetPosition(c.Source.Player, out var p)) return p;
        throw new CommandException("give a position (x y z): the console has none");
    }

    private static void Require(CommandContext c)
    {
        if (!Build.Available) throw new CommandException("kiln.build is not available on this server");
    }

    private static void StatusCmd(CommandContext c)
    {
        var s = Build.Status;
        if (s is null)
        {
            c.Reply("kiln.build is not available on this server");
            return;
        }
        c.Reply($"kiln.build: read {On(s.Read)}, write {On(s.Write)}, props {On(s.Props)}, kept by the save {On(s.Persist)}; " +
                $"{s.CellsWritten} cells written, {s.PropsPlaced} props placed, {s.KeptChunks} chunks kept" +
                (s.Detail.Length > 0 ? $" ({s.Detail})" : ""));
        static string On(bool b) => b ? "on" : "off";
    }

    private static void Mark(CommandContext c, int which)
    {
        Require(c);
        var p = Here(c);
        if (!Selections.TryGetValue(Key(c), out var sel)) Selections[Key(c)] = sel = new Selection();
        if (which == 1) sel.A = p;
        else sel.B = p;
        c.Reply($"pos{which} = {p.X:0.0} {p.Y:0.0} {p.Z:0.0}");
        if (sel.A is { } a && sel.B is { } b) c.Reply($"selection {Build.BoxBetween(a, b)}");
    }

    private static CellBox Sel(CommandContext c)
    {
        if (!Selections.TryGetValue(Key(c), out var s) || s.A is not { } a || s.B is not { } b)
            throw new CommandException("set /build pos1 and /build pos2 first");
        return Build.BoxBetween(a, b);
    }

    private static void ShowSel(CommandContext c)
    {
        Require(c);
        var box = Sel(c);
        c.Reply($"selection {box} ({box.Count} cells, {box.SizeX * Build.Grid!.CellSize:0.#} x {box.SizeY * Build.Grid.CellSize:0.#} x " +
                $"{box.SizeZ * Build.Grid.CellSize:0.#} m)");
    }

    // Kiln.Chat's handlers return void and run synchronously, so these start an async Task and let it go: each catches
    // what it can throw (nothing can take the server down the way an async void could) and replies to the source's chat.
    private static void Capture(CommandContext c) => _ = CaptureAsync(c);
    private static void Paste(CommandContext c) => _ = PasteAsync(c);
    private static void Undo(CommandContext c) => _ = UndoAsync(c);
    private static void Shape(CommandContext c, ShapeOp op) => _ = ShapeAsync(c, op);

    private static async Task CaptureAsync(CommandContext c)
    {
        try
        {
            var name = c.Get<string>("name");
            Require(c);
            var box = Sel(c);
            var path = SchematicPath(name);
            var s = await Schematic.CaptureAsync(box, new CaptureOptions { Name = name, Author = c.Source.Name });
            s.Save(path);
            c.Reply($"captured {name}: {s.SizeX}x{s.SizeY}x{s.SizeZ} cells ({s.SolidCount} solid), {s.Props.Count} props -> {Path.GetFileName(path)}");
        }
        catch (Exception e)
        {
            c.Reply($"capture failed: {e.Message}");
        }
    }

    private static async Task PasteAsync(CommandContext c)
    {
        try
        {
            var name = c.Get<string>("name");
            Require(c);
            var path = SchematicPath(name);
            if (!File.Exists(path)) throw new CommandException($"no schematic '{name}' (/build list)");
            var s = Schematic.Load(path);
            var rot = int.Parse(c.GetOrDefault("rotation", "0"), CultureInfo.InvariantCulture) / 90;
            var at = Here(c);
            var r = await s.Paste(at, new PasteOptions
            {
                Rotation = rot, Actor = c.Source.IsPlayer ? c.Source.Player : EntityId.None, Flags = BuildFlags.InBases,
                Label = $"paste {name}",
            });
            c.Reply(r.Cells.Refused
                ? $"paste refused: {r.Cells.Refusal}"
                : $"pasted {name} at {r.Box}: {r.Cells.CellsChanged} cells, {r.PropsPlaced} props{(r.PropsFailed > 0 ? $" ({r.PropsFailed} failed)" : "")}; /build undo reverts it");
        }
        catch (Exception e)
        {
            c.Reply($"paste failed: {e.Message}");
        }
    }

    private static void List(CommandContext c)
    {
        var dir = SchematicDirectory;
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.kschem").OrderBy(f => f).ToArray() : [];
        if (files.Length == 0)
        {
            c.Reply("no schematics yet (/build pos1, /build pos2, /build capture <name>)");
            return;
        }
        foreach (var f in files.Take(30))
        {
            try
            {
                var s = Schematic.Load(f);
                c.Reply($"{Path.GetFileNameWithoutExtension(f)}: {s.SizeX}x{s.SizeY}x{s.SizeZ}, {s.Props.Count} props");
            }
            catch (Exception e)
            {
                c.Reply($"{Path.GetFileNameWithoutExtension(f)}: unreadable ({e.Message})");
            }
        }
    }

    private static async Task UndoAsync(CommandContext c)
    {
        try
        {
            Require(c);
            var owner = Build.JournalOwner(Options(c, ""));
            var r = await Build.Undo(owner);
            c.Reply(r is null
                ? "nothing to undo"
                : $"undid {r.Label}: {r.CellsRestored} cells back, {r.PropsRemoved} props removed, {r.PropsRestored} put back" +
                  (r.CellsKept > 0 ? $"; {r.CellsKept} cells changed since and were left alone" : ""));
        }
        catch (Exception e)
        {
            c.Reply($"undo failed: {e.Message}");
        }
    }

    private static void History(CommandContext c)
    {
        var h = Build.History(Build.JournalOwner(Options(c, "")));
        if (h.Count == 0) c.Reply("no edits this session");
        foreach (var e in h.Take(10)) c.Reply($"#{e.Id} {e.Label}: {e.Cells} cells, {e.PropsPlaced} props placed, {e.PropsRemoved} removed");
    }

    private static byte Material(CommandContext c)
    {
        var s = c.Get<string>("material");
        if (byte.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0) return m;
        var item = Items.Lookup(s) ?? throw new CommandException($"'{s}' is neither a material id (1..255: terrain 1..127, building blocks 128..255) nor an item");
        var mat = Build.MaterialOf(item.Id.Value);
        return mat != 0 ? mat : throw new CommandException($"{item.Label} places no voxel material");
    }

    private static async Task ShapeAsync(CommandContext c, ShapeOp op)
    {
        try
        {
            Require(c);
            var at = Here(c);
            var size = c.Get<double>("size");
            var kind = c.GetOrDefault("shape", op == ShapeOp.Flatten ? "cylinder" : "sphere");
            BuildShape shape = kind switch
            {
                "box" => BuildShape.Box(at, size, size, size),
                "cylinder" => BuildShape.Cylinder(at, size, op == ShapeOp.Flatten ? Math.Max(4, size) : size),
                _ => BuildShape.Sphere(at, size),
            };
            var o = Options(c, $"/build {op.ToString().ToLowerInvariant()}");
            var r = op switch
            {
                ShapeOp.Dig => await Build.Dig(shape, o),
                ShapeOp.Add => await Build.Add(shape, Material(c), o),
                ShapeOp.Flatten => await Build.Flatten(shape, c.GetOrDefault("level", at.Y), 0, o),
                ShapeOp.Smooth => await Build.Smooth(shape, 0.7f, 2, o),
                ShapeOp.Paint => await Build.Paint(shape, Material(c), o),
                _ => throw new CommandException("unknown operation"),
            };
            c.Reply(r.Refused ? $"{op} refused: {r.Refusal}" : $"{op}: {r.CellsChanged} cells changed ({r.BlocksWritten} blocks, {r.Milliseconds:0.0} ms)");
        }
        catch (Exception e)
        {
            c.Reply($"{op} failed: {e.Message}");
        }
    }

    private static void PlaceProp(CommandContext c)
    {
        Require(c);
        var template = c.Get<string>("template");
        var yaw = c.GetOrDefault("yaw", 0.0);
        var at = Here(c);
        var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(yaw * Math.PI / 180));
        var e = Build.PlaceProp(template, at, q, 0, Options(c, $"/build prop {template}"));
        c.Reply(e.IsNone ? $"{template}: refused (base, zone or altar)" : $"placed {template} as {e}");
    }

    private static void Bases(CommandContext c)
    {
        Require(c);
        var b = Build.Bases;
        if (b.Count == 0) c.Reply("no player bases");
        foreach (var x in b)
            c.Reply($"base of altar {x.Altar}: centre {x.Center.X:0} {x.Center.Y:0} {x.Center.Z:0}, size {x.Half.X * 2:0} x {x.Half.Y * 2:0} x {x.Half.Z * 2:0} m{(x.Abandoned ? " (abandoned)" : "")}");
    }

    private static void Release(CommandContext c)
    {
        Require(c);
        var n = Build.Release(Sel(c));
        c.Reply($"released {n} chunk(s): their terrain reverts at the next restart unless a base covers it");
    }
}
