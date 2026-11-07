// SPDX-License-Identifier: MIT
// Props and stations -> an item that places a template of its own, and recipes. What it derives:
//  - the starter: a station's kind; a prop's look, or else the surface it sits on.
//  - a workshop: the id is FNV-1a of a GUID from the mod id and the block's name, as the game pairs its own. Crafting.workshopId is the one the server's gate compares a recipe with;
//    CraftingInteraction.workshopId is the one a client opens the menu of (it copies it to ClientCraftingInteraction, and the HUD reads that). So a station can run its own recipes and
//    show an existing station's menu, with no hook. kiln.content lists its recipes in that menu.
//  - comfort: a ComfortProvider of its own when the starter has none.
using System.Numerics;
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    private const string RowComfort = "0xf2b8d8e0";      // the tooltip row of the game's comfort props
    private const double NeverOut = 1e9;                  // above any rain: Extinguish.extinguishAboveRainAmount
    private const int MaxSlots = 64;

    /// <summary>The prop and station blocks. Null: not one of them.</summary>
    private static BlockHandle? DefineWorldBlock<T>(BlockContext ctx, Lowered low, string name, T block) where T : class, IBlock => DefineFurnitureBlock(ctx, low, name, block) ?? DefineTerrainBlock(ctx, low, name, block) ?? block switch
    {
        StationBlock s => DefineStation(ctx, low, name, s),
        PropBlock p => DefineProp(ctx, low, name, p),
        PlacementBlock p => Part(ctx, low, name, p, (x, b) => CheckPlacement(x, b, "")),
        ComfortBlock c => Part(ctx, low, name, c, (x, b) => CheckComfort(x, b, "")),
        ProcessingBlock p => Part(ctx, low, name, p, (x, b) => CheckProcessing(x, b, "")),
        MenuBlock m => Part(ctx, low, name, m, null),
        _ => null,
    };

    // ---- what is placed ----------------------------------------------------------------------------------------

    /// <summary>A template being put together, for the item a <see cref="Draft"/> holds.</summary>
    private sealed class Placed(Draft item, string template, Guid guid, string starter)
    {
        public readonly Draft Item = item;
        public readonly string Template = template, Starter = starter;
        public readonly Guid Guid = guid;
        public readonly JsonObject Fields = new();
        public readonly List<string> Add = [];
        /// <summary>The template's own texts, by slot: language -> slot -> text. Its loca fields name them by a tag derived from its GUID.</summary>
        public readonly JsonObject Texts = new();
        /// <summary>A glTF of the mod's own that it shows, in the mod's folder: its box is the default collision.</summary>
        public string? ModelFile;
    }

    /// <summary>What every placed thing says, on its item and its template.</summary>
    private static void PlacedParts(Placed p, PropBlock b, bool starterIsLook)
    {
        if (b.Collider != null && b.GetType() != typeof(PropBlock))
            throw new BlockError(p.Item.Ctx.Block, "collider", "a station or a piece of furniture keeps the collision of the game's it starts from: a box of yours is for a prop with a model of yours");
        PlacedVisual(p, b.Visual, starterIsLook);
        ApplyLoot(p.Item, b.Loot);
        if (b.Placement != null) ApplyPlacement(p.Item, p.Item.Ctx.Part(b.Placement, "placement"));
        if (b.Comfort != null)
        {
            var c = p.Item.Ctx.Part(b.Comfort, "comfort");
            CheckComfort(p.Item.Ctx, c, "comfort.");
            p.Add.Add("ComfortProvider");
            p.Fields["ComfortProvider.comfort"] = c.Amount;
            p.Fields["ComfortProvider.category"] = c.Category.ToString();
            p.Fields["ComfortProvider.range"] = c.Range;
            p.Fields["ComfortProvider.freezingResistance"] = c.Warmth;
            p.Item.Rows = new JsonArray { Row(RowComfort, "Normal", c.Amount) };
        }
        if (b.OnInteract != null) ApplyInteract(p, p.Item.Ctx.Part(b.OnInteract, "onInteract"), "onInteract.");
        // a look's VFX on a placed thing is carried by what stands there, as a light is
        var lookVfx = b.Visual != null ? p.Item.Ctx.Part(b.Visual, "visual").Vfx : null;
        if (lookVfx != null && b.Light != null) throw new BlockError(p.Item.Ctx.Block, "visual.vfx", "it has a light already: one VFX on what stands there, say it in light or in visual.vfx");
        if ((b.Light ?? lookVfx) is { } light)
        {
            // a station has these, a prop mostly doesn't. Added ones start zeroed, so the rotation is said.
            p.Add.Add("VfxComponent");
            p.Add.Add("VfxComponentResource");
            p.Fields["VfxComponent.attachmentOffset.orientationOffset.w"] = 1;
            foreach (var (k, v) in VfxData.Component(p.Item.Ctx, VfxData.Lower(p.Item.Ctx, light, b.Light != null ? "light" : "visual.vfx"), p.Template)) p.Fields[k] = v?.DeepClone();
        }
    }

    private static void CommitPlaced(BlockContext ctx, Lowered low, Placed p, PropBlock b, string name, string key)
    {
        low.Clone("templates", p.Guid, p.Template, p.Starter, p.Fields, addComponents: p.Add.Count > 0 ? p.Add : null, texts: p.Texts.Count > 0 ? p.Texts : null);
        p.Item.Refs["equipment.placedEntity"] = p.Guid.ToString();
        p.Item.Commit(b.Name, b.Description);
        if (b.TweakPlaced.Count > 0) low.Tweak(ctx.Block, "templates", p.Template, b.TweakPlaced);
        if (b.Recipe != null) DefineRecipe(ctx, low, key, p.Item.Game, b.Recipe);
    }

    // The model of a placed thing is on three things: what stands there, the ghost under the cursor, and the picture of the item.
    private static void PlacedVisual(Placed p, PartRef<VisualBlock>? part, bool starterIsLook)
    {
        if (part == null) return;
        var d = p.Item;
        var ctx = d.Ctx;
        var v = ctx.Part(part, "visual");
        string? from = null;
        if (v.Like is { } like)
        {
            var n = ctx.ItemName(like, "visual.like");
            d.Icon = "item:" + n;
            if (!starterIsLook) from = n;
        }
        if (v.Model is { } model)
        {
            if (IsGltf(model))
            {
                // a model of the mod's own: kiln cook makes it from the file (tools/modelcook finds it in blocks/*.json), under the GUID that follows from its path
                if (model.Contains("..") || Path.IsPathRooted(model)) throw new BlockError(ctx.Block, "visual.model", $"\"{model}\": a file in the mod's folder, with a relative path");
                ctx.Need(ClientsLevel.Mod, "visual.model", "a model of the mod's own has to be on every player's game: nothing can send it (an item's look, \"item:<name>\", asks no more than kiln)");
                var dir = BlocksBridge.ModDirectory(ctx.Mod.Id);
                if (dir.Length > 0)
                {
                    var full = Path.Combine(dir, model);
                    if (!File.Exists(full))
                        throw new BlockError(ctx.Block, "visual.model", $"\"{model}\" is not in the mod's folder. The build copies a mod's folders to where it runs: models/ is one of them (KilnModFolders)");
                    p.ModelFile = full;
                    if (BlocksBridge.IsClient && !File.Exists(Path.Combine(dir, "assets", "cooked", WornStem(model) + ".bin")))
                        lock (ctx.Mod.Warnings) ctx.Mod.Warnings.Add($"block {ctx.Block}, visual.model: {model} is not cooked, so it shows the look it starts from. Run kiln cook: a model in blocks/*.json is found by itself, one in code needs a model step in assets/cook.json");
                }
                var guid = WornModelGuid(ctx.Mod.Id, model).ToString();
                p.Fields["ModelResource.model"] = guid;
                d.Refs["iconModel"] = guid;
                d.Refs["equipment.cursorModel"] = guid;
                d.Low.Json["model"] = new JsonObject { ["file"] = model, ["guid"] = guid };
            }
            else if (model.StartsWith("item:", StringComparison.Ordinal)) from = model[5..];
            else if (Guid.TryParse(model, out _))
            {
                ctx.Need(ClientsLevel.Mod, "visual.model", "a model of the mod's own has to be on every player's game: nothing can send it (an item's look, \"item:<name>\", asks no more than kiln)");
                p.Fields["ModelResource.model"] = model;
                d.Refs["iconModel"] = model;
                d.Refs["equipment.cursorModel"] = model;
            }
            else throw new BlockError(ctx.Block, "visual.model", $"\"{model}\" is neither \"item:<name>\" nor the GUID of a model your assets/assets.json serves");
        }
        if (from != null)
        {
            // what that item shows, wherever it came from: on a client too, as these are rules
            var shows = $"item:{from}.iconModel";
            d.Refs["iconModel"] = shows;
            d.Refs["equipment.cursorModel"] = shows;
            ctx.Low!.TemplateRules(p.Template, new JsonObject { ["ModelResource.model"] = shows });
        }
        if (v.Icon is { } icon)
        {
            d.Icon = icon;
            if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ctx.Need(ClientsLevel.Mod, "visual.icon", "a PNG icon has to be on every player's game: nothing can send it (an item's icon, \"item:<name>\", asks no more than kiln)");
        }
        if (v.Dropped is { } dropped) d.Refs["iconModel"] = "item:" + ctx.ItemName(dropped, "visual.dropped");
        if (v.Tint is { } t)
        {
            if (!IsColour(t)) throw new BlockError(ctx.Block, "visual.tint", $"\"{t}\" is not a colour: #rrggbb");
            d.Fields["itemColorCombinationSetup.color0"] = (double)(0xff000000u | Convert.ToUInt32(t[1..], 16));
            d.Fields["itemColorCombinationSetup.isSet"] = true;
        }
    }

    // A prop with a model of its own starts from a plain game prop with one collider: a box (floor, wall) or a capsule (ceiling). The box becomes the model's,
    // on what is placed and on the item's placement check alike. Each collider names a physics shape by a hash; the clone's has its own, so the game can't
    // take it for the starter's shape.
    private static void ApplyCollider(Placed p, BoxCollider? c, PlacementBlock? placement, string key)
    {
        var ctx = p.Item.Ctx;
        Vector3 centre, half;
        if (c != null)
        {
            if (c.Size.X <= 0 || c.Size.Y <= 0 || c.Size.Z <= 0 || float.IsNaN(c.Size.X + c.Size.Y + c.Size.Z))
                throw new BlockError(ctx.Block, "collider.size", $"[{c.Size.X}, {c.Size.Y}, {c.Size.Z}]: a size in metres is more than 0 each way");
            if (Math.Max(c.Size.X, Math.Max(c.Size.Y, c.Size.Z)) > MaxColliderSize)
                throw new BlockError(ctx.Block, "collider.size", $"[{c.Size.X}, {c.Size.Y}, {c.Size.Z}]: at most {MaxColliderSize} m each way");
            half = c.Size / 2;
            centre = c.Center ?? new Vector3(0, half.Y, 0);
        }
        else if (p.ModelFile != null && GltfBounds.Of(p.ModelFile) is var (min, max))
        {
            half = Vector3.Max((max - min) / 2, new Vector3(MinHalf));
            centre = (min + max) / 2;
        }
        else return;
        var capsule = placement?.On is { } on && !on.HasFlag(Surface.Floor) && !on.HasFlag(Surface.Wall);
        var hash = (double)BlockIds.Fnv(BlockIds.Derive(ctx.Mod.Id, "collider", key));
        void Put(Action<string, double> set, string at, string jolt)
        {
            set($"{at}.offset.x", centre.X);
            set($"{at}.offset.y", centre.Y);
            set($"{at}.offset.z", centre.Z);
            if (capsule)
            {
                var r = Math.Max(half.X, half.Z);
                set($"{at}.shape.radius", r);
                set($"{at}.shape.length", Math.Max(0, 2 * half.Y - 2 * r));
            }
            else
            {
                set($"{at}.shape.halfSize.x", half.X);
                set($"{at}.shape.halfSize.y", half.Y);
                set($"{at}.shape.halfSize.z", half.Z);
            }
            set(jolt, hash);
        }
        Put((k, v) => p.Fields[k] = v, "ColliderResourceComponent.colliders[0].dataArray[0]", "ColliderResourceComponent.colliders[0].joltObjects[0].hash");
        Put((k, v) => p.Item.DataRules[k] = v, "equipment.placementColliders[0].dataArray[0]", "equipment.placementColliders[0].joltObjects[0].hash");
        if (placement?.Size == null)
        {
            foreach (var (axis, lo, hi) in new[] { ("x", centre.X - half.X, centre.X + half.X), ("y", centre.Y - half.Y, centre.Y + half.Y), ("z", centre.Z - half.Z, centre.Z + half.Z) })
            {
                p.Item.Fields[$"equipment.placementAABBmin.{axis}"] = (double)lo;
                p.Item.Fields[$"equipment.placementAABBmax.{axis}"] = (double)hi;
            }
        }
        p.Item.Low.Json["collider"] = new JsonObject
        {
            ["shape"] = capsule ? "capsule" : "box", ["center"] = new JsonArray(centre.X, centre.Y, centre.Z), ["half"] = new JsonArray(half.X, half.Y, half.Z),
            ["from"] = c != null ? "block" : "model",
        };
    }

    private const float MaxColliderSize = 32, MinHalf = 0.02f;

    private static void CheckPlacement(BlockContext ctx, PlacementBlock p, string path)
    {
        if (p.On is { } on && (on == 0 || (on & ~(Surface.Floor | Surface.Wall | Surface.Ceiling)) != 0)) throw new BlockError(ctx.Block, $"{path}on", $"{(int)on}: say floor, wall, ceiling, or several of them");
        if (p.Size is { } s && (s.X <= 0 || s.Y <= 0 || s.Z <= 0)) throw new BlockError(ctx.Block, $"{path}size", $"[{s.X}, {s.Y}, {s.Z}]: a size in metres is more than 0 each way");
    }

    private static void CheckComfort(BlockContext ctx, ComfortBlock c, string path)
    {
        if (c.Amount < 0) throw new BlockError(ctx.Block, $"{path}amount", $"{c.Amount} is less than 0");
        if (c.Range <= 0) throw new BlockError(ctx.Block, $"{path}range", $"{c.Range} must be more than 0");
        if (c.Warmth < 0) throw new BlockError(ctx.Block, $"{path}warmth", $"{c.Warmth} is less than 0");
    }

    private static void CheckProcessing(BlockContext ctx, ProcessingBlock p, string path)
    {
        if (p.Slots is < 1 or > MaxSlots) throw new BlockError(ctx.Block, $"{path}slots", $"{p.Slots} is outside 1..{MaxSlots}");
    }

    private static void ApplyPlacement(Draft d, PlacementBlock p)
    {
        CheckPlacement(d.Ctx, p, "placement.");
        const string Attach = "equipment.cursorConfig.attachAngleConfig";
        if (p.On is { } on)
        {
            string Angle(Surface s) => on.HasFlag(s) ? "Narrow" : "Disabled";
            d.Fields[$"{Attach}.floorAngle"] = Angle(Surface.Floor);
            d.Fields[$"{Attach}.wallAngle"] = Angle(Surface.Wall);
            d.Fields[$"{Attach}.ceilingAngle"] = Angle(Surface.Ceiling);
            var order = new[] { Surface.Floor, Surface.Wall, Surface.Ceiling }.Where(s => on.HasFlag(s)).ToList();
            d.Fields[$"{Attach}.defaultAttachDirection"] = order[0].ToString();
            d.Fields[$"{Attach}.hasSecondaryAttachDirection"] = order.Count > 1;
            if (order.Count > 1) d.Fields[$"{Attach}.secondaryAttachDirection"] = order[1].ToString();
        }
        if (p.InBase is { } inBase) d.Fields["equipment.buildZoneRequired"] = inBase;
        if (p.Size is { } s)
        {
            d.Fields["equipment.placementAABBmin.x"] = (double)(-s.X / 2);
            d.Fields["equipment.placementAABBmin.y"] = 0.0;
            d.Fields["equipment.placementAABBmin.z"] = (double)(-s.Z / 2);
            d.Fields["equipment.placementAABBmax.x"] = (double)(s.X / 2);
            d.Fields["equipment.placementAABBmax.y"] = (double)s.Y;
            d.Fields["equipment.placementAABBmax.z"] = (double)(s.Z / 2);
        }
        if (p.Snap is { } snap) d.Fields["equipment.cursorConfig.translationConfig.defaultSnapping"] = snap;
        if (p.Water is { } water)
        {
            d.Fields["equipment.cursorConfig.allowWaterPlacement"] = water;
            d.Fields["equipment.waterUsability.outsideBuildzone.allowInsideWater"] = water;
            d.Fields["equipment.waterUsability.insideBuildzone.allowInsideWater"] = water;
        }
        if (p.Fog is { } fog) d.Fields["equipment.allowPlacementBelowFog"] = fog;
        if (p.Consumed is { } consumed) d.Fields["equipment.placementConsume"] = consumed;
    }

    // ---- props --------------------------------------------------------------------------------------------------

    // With only a cooked model, the rest comes from a plain prop of the game that sits on that surface: its collision, the way it attaches, and what breaks it.
    private static string PropStarter(Surface? on) =>
        on is { } s && !s.HasFlag(Surface.Floor)
            ? (s.HasFlag(Surface.Wall) ? "Prop_Decoration_T4_Banner_Wall" : "Prop_Decoration_T4_Ceiling_Chain")
            : "Prop_Decoration_T2_Vase_Clay_01";

    private static BlockHandle DefineProp(BlockContext ctx, Lowered low, string name, PropBlock b)
    {
        RequireName(ctx, b.Name, "a prop");
        var look = b.Visual != null ? ctx.Part(b.Visual, "visual") : null;
        if (look?.Like == null && look?.Model == null)
            throw new BlockError(ctx.Block, "visual", "a prop needs a look: like (a game item that places something), or model");
        var placement = b.Placement != null ? ctx.Part(b.Placement, "placement") : null;

        var key = b.Was ?? name;
        var mod = ctx.Mod.Id;
        var starter = look.Like is { } like ? ctx.ItemName(like, "visual.like") : PropStarter(placement?.On);
        var d = new Draft(ctx, low, key, name, starter);
        var placed = look.Like is { } self ? ctx.Find(self.Value, "visual.like") ?? Blocks.AllMods.SelectMany(m => m.Handles.Values).FirstOrDefault(h => h.GameName == starter) : null;
        var p = new Placed(d, $"{mod}.{name}.prop", BlockIds.Derive(mod, "template", $"{key}.prop"), placed switch
        {
            // another block's template by name, as its item may not be in the registry yet; a game item's is found when the items are there
            StationHandle => $"{placed.Mod}.{placed.Name}.station",
            BlockHandle<PropBlock> => $"{placed.Mod}.{placed.Name}.prop",
            _ => "item:" + starter,
        });
        PlacedParts(p, b, starterIsLook: look.Like != null);
        if (look.Like != null && b.Collider != null)
            throw new BlockError(ctx.Block, "collider", "a game item's look brings its own collision: a box of yours goes with a model of yours (visual.model)");
        if (look.Like == null) ApplyCollider(p, b.Collider, placement, key);
        CommitPlaced(ctx, low, p, b, name, key);
        return new BlockHandle<PropBlock>(mod, name, d.Guid, d.Game, b);
    }

    // ---- stations -----------------------------------------------------------------------------------------------

    private static StationKind KindOf(StationBlock s, ProcessingBlock? processing) => s.Kind ?? (processing != null ? StationKind.DryingRack : StationKind.Workbench);

    private static BlockHandle DefineStation(BlockContext ctx, Lowered low, string name, StationBlock s)
    {
        RequireName(ctx, s.Name, "a station");
        var mod = ctx.Mod.Id;
        var key = s.Was ?? name;
        var processing = s.Processing != null ? ctx.Part(s.Processing, "processing") : null;
        var kind = KindOf(s, processing);
        if (processing != null)
        {
            CheckProcessing(ctx, processing, "processing.");
            if (!kind.Timed)
                throw new BlockError(ctx.Block, "processing", $"a {kind.Key} doesn't work on its own: a player crafts at it at once. Give it a kind that does ({string.Join(", ", StationKind.All.Where(k => k.Timed).Select(k => k.Key))}), or leave processing out");
        }
        var timed = kind.Timed;
        var shown = s.Menu != null ? ctx.Part(s.Menu, "menu").Like ?? kind : kind;
        // Where its recipes go: the id its template carries, what kiln.content takes for a recipe at it, and the menu that lists them when it isn't that workshop's own.
        var own = s.OwnWorkshop ?? (timed && (processing != null || s.Recipes is { Count: > 0 }));
        var workshopGuid = BlockIds.Derive(mod, "workshop", key);
        var workshop = own ? BlockIds.Fnv(workshopGuid) : Convert.ToUInt32(kind.Workshop, 16);
        var station = own ? $"workshop:{workshopGuid}" : kind.Workshop;
        var menu = own || shown.Workshop != kind.Workshop ? shown.Workshop : null;

        var d = new Draft(ctx, low, key, name, kind.Item);
        // What is placed: a template of its own. Crafting.workshopId is the workshop it runs, CraftingInteraction.workshopId the one its screens
        // show: a workshop of its own has a menu of its own in a player's game, laid out as the shown station's and listing only its recipes.
        var p = new Placed(d, $"{mod}.{name}.station", BlockIds.Derive(mod, "template", $"{key}.station"), kind.Template);
        if (own) p.Fields["Crafting.workshopId"] = workshop;
        if (own || shown.Workshop != kind.Workshop) p.Fields["CraftingInteraction.workshopId"] = own ? workshop : Convert.ToUInt32(shown.Workshop, 16);
        if (processing != null) ApplyProcessing(p, processing);
        PlacedParts(p, s, starterIsLook: false);
        CommitPlaced(ctx, low, p, s, name, key);

        var made = new HashSet<string>();
        for (var i = 0; i < (s.Recipes?.Count ?? 0); i++)
        {
            var r = s.Recipes![i];
            var path = $"recipes[{i}]";
            if (r.Output is not { } output) throw new BlockError(ctx.Block, $"{path}.output", "a station's recipe says what it makes");
            if (r.Station.Value != Stations.Hand) throw new BlockError(ctx.Block, $"{path}.station", "a recipe in a station's list is at that station: leave this out");
            if (!timed && r.Seconds != null) throw new BlockError(ctx.Block, $"{path}.seconds", $"a {kind.Key} doesn't work on its own, so its recipes take no time: give it a kind that does, or leave this out");
            var item = ctx.ItemName(output, $"{path}.output");
            // ids follow from what it makes: reordering the list changes nothing. A second recipe for the same thing is numbered.
            var entry = item.StartsWith(mod + ".", StringComparison.Ordinal) ? item[(mod.Length + 1)..] : item;
            for (var n = 2; !made.Add(entry); n++) entry = n == 2 ? $"{entry}.{n}" : $"{entry[..entry.LastIndexOf('.')]}.{n}";
            if (timed && r.Sheltered != null)
                throw new BlockError(ctx.Block, $"{path}.sheltered", $"a {kind.Key} works on its own, and the game's stations that do never check a roof: rain is what puts them out (GoesOutInRain). Leave this out");
            LowerRecipe(ctx, low, $"{key}.makes.{entry}", $"{mod}.{name}.makes.{entry}", item, r, path, (station, menu, own ? d.Game : null), timed && own, r.Sheltered);
        }
        low.Json["workshop"] = new JsonObject { ["id"] = $"0x{workshop:x8}", ["station"] = station, ["menu"] = menu, ["own"] = own };
        return new StationHandle(mod, name, d.Guid, d.Game, s, station, menu, own ? d.Game : null);
    }

    private static void ApplyProcessing(Placed p, ProcessingBlock b)
    {
        var ctx = p.Item.Ctx;
        if (b.Slots is { } slots)
        {
            p.Fields["InventorySetup.genericSlotCount"] = slots;
            p.Fields["InventorySetup.availableSlotCount"] = slots;
        }
        if (b.GoesOutInRain == false) p.Fields["Extinguish.extinguishAboveRainAmount"] = NeverOut;
        if (b.Running is not { } running) return;
        if (running.Vfx is { } vfx)
            foreach (var (k, v) in VfxData.Component(ctx, VfxData.Lower(ctx, vfx, "processing.running.vfx"), p.Template)) p.Fields[k] = v?.DeepClone();
        if (running.Sound is { } sound) p.Fields["FactoryStationClient.soundLoopRunning"] = RunningSound(ctx, sound).ToString();
    }

    private static Guid RunningSound(BlockContext ctx, SoundRef sound)
    {
        const string Path = "processing.running.sound";
        var s = sound.Value;
        if (Guid.TryParse(s, out var guid)) return guid;
        if (string.IsNullOrWhiteSpace(s)) throw new BlockError(ctx.Block, Path, "a sound needs a name: a cue of this mod, \"other-mod:name\", or a cue's GUID");
        ctx.Need(ClientsLevel.Mod, Path, "a cue of a mod has to be on every player's game: nothing can send it");
        // a sound block of this mod: its GUID is known without the cooked index, which a server doesn't have
        if (!s.Contains(':') && ctx.Find(s, Path) is BlockHandle<SoundBlock> block) return block.Guid;
        var colon = s.IndexOf(':');
        try
        {
            return (colon > 0 ? SoundId.Named(s[..colon], s[(colon + 1)..]) : SoundId.Named(ctx.Mod.Id, s)).Cue;
        }
        catch (InvalidOperationException e)
        {
            throw new BlockError(ctx.Block, Path, e.Message);
        }
    }

    // ---- recipes ------------------------------------------------------------------------------------------------

    /// <summary>A recipe that makes <paramref name="item"/>. <paramref name="at"/>: the station, when the block's list says it, not the recipe; with a workshop of its own, the item that
    /// places it, which names that workshop in a player's game. <paramref name="needsTime"/>: a workshop of its own has no recipes of the game to take a time from, and a cycle of no
    /// time would run every step. Burns are inputs: fuel is an input in the game's data too.</summary>
    private static void LowerRecipe(BlockContext ctx, Lowered low, string key, string name, string item, RecipeBlock r, string path, (string Station, string? Menu, string? Item)? at, bool needsTime, bool? sheltered)
    {
        if (r.Makes is < 1 or > 65535) throw new BlockError(ctx.Block, $"{path}.makes", $"{r.Makes} is outside 1..65535");
        if (r.Needs.Count == 0 && r.Burns.Count == 0) throw new BlockError(ctx.Block, $"{path}.needs", "a recipe that needs nothing is not listed by the game");
        if (r.Seconds is <= 0) throw new BlockError(ctx.Block, $"{path}.seconds", $"{r.Seconds} must be more than 0");
        if (r.Comfort is < 0) throw new BlockError(ctx.Block, $"{path}.comfort", $"{r.Comfort} is less than 0");
        var inputs = new List<RecipeIngredient>();
        void Add(ItemRef what, int count, string field)
        {
            var p = $"{path}.{field}[{what}]";
            if (count < 1) throw new BlockError(ctx.Block, p, $"{count} is less than 1");
            var game = ctx.ItemName(what, p);
            var i = inputs.FindIndex(x => x.Item == game);
            if (i >= 0) inputs[i] = new RecipeIngredient(game, inputs[i].Count + count);
            else inputs.Add(new RecipeIngredient(game, count));
        }
        foreach (var (what, count) in r.Needs) Add(what, count, "needs");
        foreach (var (what, count) in r.Burns) Add(what, count, "burns");
        var (station, menu, stationItem) = at ?? ctx.StationOf(r.Station, $"{path}.station");
        if (r.Seconds == null && (needsTime || (at == null && station.StartsWith("workshop:", StringComparison.Ordinal))))
            throw new BlockError(ctx.Block, $"{path}.seconds", "a station of its own has no recipes of the game to take a time from: say how many seconds");
        var fields = new Dictionary<string, object>();
        if (sheltered is { } roof) fields["requiresSheltered"] = roof;
        if (r.Comfort is { } comfort) fields["comfortLevel"] = comfort;
        low.Recipe(new RecipeDefinition
        {
            Guid = BlockIds.Derive(ctx.Mod.Id, "recipe", key), Name = name, Station = station, Menu = menu, StationItem = stationItem, Inputs = inputs,
            Outputs = [new RecipeIngredient(item, r.Makes)], Duration = r.Seconds is { } d ? TimeSpan.FromSeconds(d) : null, Fields = fields.Count > 0 ? fields : null,
        });
    }
}

/// <summary>A station's handle: where a recipe that names it goes.</summary>
/// <param name="item">With a workshop of its own: the item that places it, which names that workshop in a player's game.</param>
internal sealed class StationHandle(string mod, string name, Guid guid, string gameName, StationBlock block, string station, string? menu, string? item) : BlockHandle<StationBlock>(mod, name, guid, gameName, block)
{
    public string Station { get; } = station;
    public string? Menu { get; } = menu;
    public string? Item { get; } = item;
}
