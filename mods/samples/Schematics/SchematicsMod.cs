// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Text.Json;
using Kiln;

namespace Schematics;

/// <summary>
/// kiln.build sample: registers the <c>/build</c> admin commands (selection, capture, paste with rotation, shapes,
/// props, undo), writes a built-in outpost schematic (building-block voxels and three props, made in code) to
/// <c>kiln/data/kiln.build/schematics/kiln-outpost.kschem</c>, and places the outposts listed in
/// <c>kiln/data/schematics/schematics.json</c> once per world with <see cref="Build.PlaceOnce(string, Schematic, WorldPosition, PasteOptions?)"/>.
/// Everything goes through the game's own voxel writer and save, so vanilla clients see it.
/// </summary>
public sealed class SchematicsMod : Mod
{
    public sealed class OutpostConfig
    {
        public string Key { get; set; } = "";
        public string Schematic { get; set; } = "kiln-outpost";
        public double[] At { get; set; } = [];
        public int Rotation { get; set; }
    }

    public sealed class Config
    {
        public List<OutpostConfig> Outposts { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private CommandRegistration? _commands;

    public override void OnLoad()
    {
        if (!Build.Available)
        {
            Log.Warn("schematics: kiln.build is unavailable on this build");
            return;
        }
        if (Chat.Available) _commands = BuildCommands.Register();
        _ = Start();
    }

    public override void OnUnload() => _commands?.Dispose();

    private async Task Start()
    {
        try
        {
            while (!(Spawn.Available && Spawn.WorldRunning)) await Context.Scheduler.NextTick();
            var outpost = Outpost();
            var path = BuildCommands.SchematicPath("kiln-outpost");
            if (!File.Exists(path) || Schematic.Load(path).Hash != outpost.Hash)
            {
                outpost.Save(path);
                Log.Info($"schematics: wrote {path} ({outpost.SizeX}x{outpost.SizeY}x{outpost.SizeZ} cells, {outpost.Props.Count} props)");
            }
            var cfgPath = Path.Combine(Context.DataDirectory, "schematics.json");
            Directory.CreateDirectory(Context.DataDirectory);
            if (!File.Exists(cfgPath)) File.WriteAllText(cfgPath, JsonSerializer.Serialize(new Config(), Json));
            var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(cfgPath), Json) ?? new Config();
            foreach (var o in cfg.Outposts)
            {
                if (string.IsNullOrWhiteSpace(o.Key) || o.At.Length != 3)
                {
                    Log.Warn($"schematics: outpost '{o.Key}' needs a key and at [x, y, z]");
                    continue;
                }
                var s = o.Schematic == "kiln-outpost" ? outpost : global::Kiln.Schematic.Load(BuildCommands.SchematicPath(o.Schematic));
                var r = await Build.PlaceOnce(o.Key, s, new WorldPosition(o.At[0], o.At[1], o.At[2]),
                    new PasteOptions { Rotation = o.Rotation / 90, Journal = false, Label = $"outpost {o.Key}" });
                Log.Info(r.AlreadyPlaced
                    ? $"schematics: outpost '{o.Key}' is already in this world (placed {r.Record?.PlacedAt:u})"
                    : r.Placed
                        ? $"schematics: placed outpost '{o.Key}' ({r.Paste?.Cells.CellsChanged} cells, {r.Paste?.PropsPlaced} props) at {r.Paste?.Box}"
                        : $"schematics: outpost '{o.Key}' not placed: {r.Error}");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "schematics: start failed");
        }
    }

    /// <summary>A 6.5 x 5 x 6.5 m outpost: a Rough Stone floor, Rough Wood walls with a door and windows, a stepped straw
    /// roof, a chest and two torches. Anchor: the floor's centre cell (paste it at a ground point).</summary>
    public static Schematic Outpost()
    {
        const int sx = 13, sy = 10, sz = 13;
        const byte stone = 155, wood = 143, straw = 169;  // Rough Stone / Rough Wood / Straw Roof Block (block items' voxel materials)
        var cells = new Voxel[sx * sy * sz];
        int I(int x, int y, int z) => (z * sy + y) * sx + x;
        for (var z = 0; z < sz; z++)
            for (var x = 0; x < sx; x++)
            {
                cells[I(x, 0, z)] = Voxel.Of(stone);
                var edge = x == 0 || z == 0 || x == sx - 1 || z == sz - 1;
                for (var y = 1; y <= 6; y++)
                {
                    var door = z == 0 && x is >= 5 and <= 7 && y <= 4;
                    var window = (x == 0 || x == sx - 1) && z is >= 5 and <= 7 && y is 3 or 4;
                    if (edge && !door && !window) cells[I(x, y, z)] = Voxel.Of(wood);
                }
                cells[I(x, 7, z)] = Voxel.Of(straw);
                if (x is >= 2 and <= 10 && z is >= 2 and <= 10) cells[I(x, 8, z)] = Voxel.Of(straw);
                if (x is >= 4 and <= 8 && z is >= 4 and <= 8) cells[I(x, 9, z)] = Voxel.Of(straw);
            }
        // the game leaves a surface's material on the air cells next to it: do the same inside (floor, walls, ceiling)
        var marked = (Voxel[])cells.Clone();
        for (var z = 0; z < sz; z++)
            for (var y = 0; y < sy; y++)
                for (var x = 0; x < sx; x++)
                {
                    if (cells[I(x, y, z)].Fill != 0) continue;
                    foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                    {
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < 0 || ny < 0 || nz < 0 || nx >= sx || ny >= sy || nz >= sz) continue;
                        var n = cells[I(nx, ny, nz)];
                        if (n.Fill == 0) continue;
                        marked[I(x, y, z)] = Voxel.Of(n.Material, 0);
                        break;
                    }
                }
        var props = new List<SchematicProp>();
        void Prop(string template, float x, float y, float z, float yawDeg)
        {
            var t = Spawn.Available ? Spawn.Lookup(template) : null;
            if (t is null) return;
            props.Add(new SchematicProp(t.Id, t.Name, new Vector3(x, y, z),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, yawDeg * MathF.PI / 180), 0));
        }
        Prop("Prop_Decoration_T1_Storage_24", 1.5f, 0.5f, 5.0f, 90);
        Prop("Prop_Torch01_Wall_On_Craftable", 3.25f, 2.5f, 6.0f, 0);
        Prop("Prop_Torch01_Wall_On_Craftable", 3.25f, 2.5f, 0.5f, 180);
        return new Schematic(sx, sy, sz, marked, props, new CellPos(6, 0, 6))
        {
            Name = "kiln-outpost", Author = "Kiln (Schematics sample)", Description = "a small stone-and-wood outpost",
        };
    }
}
