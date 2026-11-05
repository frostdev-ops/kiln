// SPDX-License-Identifier: MIT
// Block -> what the services take. Which game resource each block starts from is chosen here, never by the author:
//   SpellBlock    ItemInfo: the look's spell item; ActorSequenceResource: the CastAnimation's, cloned only with ReleaseAt
//   ProjectileBlock  TemplateResource: ProjectileLook.Projectile (the game's projectile program stays unless the spell has OnHit or OnCast: on impact it spawns the next template)
//   the impact    TemplateResource: ProjectileLook.Impact, keeping its VFX and sound, running a generated program
//   StatusBlock   BuffType: StatusLook.Starter (icon, body effect), running a generated program
//   item blocks   Items/ItemCompiler.cs
//   EffectBlock   no resource of its own: statements in the program of whatever triggers it (EffectEmitter)
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A block while it compiles: resolves references, names what it makes.</summary>
internal sealed class BlockContext(ModBlocks mod, string block)
{
    public ModBlocks Mod { get; } = mod;
    public string Block { get; } = block;

    public EffectBlock Effect(EffectRef r, string path)
    {
        if (r.Name == null) return r.Inline!;
        var handle = Lookup(r.Name, path);
        return (handle as BlockHandle<EffectBlock>)?.Block
               ?? throw new BlockError(Block, path, $"\"{r.Name}\" is a {handle.GetType().GenericTypeArguments[0].Name}, not an effect");
    }

    public (Guid Guid, double Seconds) Status(StatusRef r, string path)
    {
        if (r.Name == null)
        {
            var name = $"{Block.Split(' ')[^1]}.{path}".Replace('[', '.').Replace("]", "");
            // an effect compiles once where it is defined and once where it is used
            if (Mod.Handles.TryGetValue(name, out var known) && ReferenceEquals((known as BlockHandle<StatusBlock>)?.Block, r.Inline)) return (known.Guid, r.Inline!.Seconds);
            var h = Compiler.Define(Mod, name, r.Inline!);
            return (h.Guid, r.Inline!.Seconds);
        }
        var handle = Lookup(r.Name, path) as BlockHandle<StatusBlock>
                     ?? throw new BlockError(Block, path, $"\"{r.Name}\" is not a status");
        return (handle.Guid, handle.Block.Seconds);
    }

    /// <summary>A program's variable for a status: a block's, a game status's by its GUID, or by its <c>GameBuffs</c> id, which the running game
    /// turns into a GUID when content is ready. <paramref name="seconds"/>: its time, when Kiln knows it.</summary>
    public int StatusVar(ProgramBuilder b, string name, StatusRef r, string path, out double? seconds)
    {
        seconds = null;
        if (r.Name is { } n)
        {
            if (Guid.TryParse(n, out var game)) return b.VGuid(name, ImpactType.Buff, game);
            if (n.StartsWith("0x", StringComparison.Ordinal) || n.StartsWith("buff_", StringComparison.Ordinal))
                return b.VLate(name, ImpactType.Buff, "status", n, Block, path);
        }
        var (guid, own) = Status(r, path);
        seconds = own;
        return b.VGuid(name, ImpactType.Buff, guid);
    }

    /// <summary>Lays a program out. One that waits for the running game is finished by BlockRuntime when content is ready.</summary>
    public (Guid Guid, byte[] Blob) Build(ProgramBuilder b)
    {
        var built = b.Build(Mod.Id);
        if (b.IsLate) Mod.LateBuilders[built.Guid] = b;
        return built;
    }

    /// <summary>The block asks at least this of players' games: the mod's manifest must say so.</summary>
    public void Need(ClientsLevel level, string? path, string why)
    {
        Mod.ManifestClients ??= BlocksBridge.ManifestClients(Mod.Id);
        var have = Mod.ManifestClients switch { "none" => ClientsLevel.None, "mod" => ClientsLevel.Mod, _ => ClientsLevel.Kiln };
        if (have >= level) return;
        var want = level.ToString().ToLowerInvariant();
        throw new BlockError(Block, path, $"mod {Mod.Id} says \"clients\": \"{Mod.ManifestClients}\", and {why}. Set <KilnClients>{want}</KilnClients> in the project (\"clients\": \"{want}\" in kiln.mod.json)");
    }

    private BlockHandle Lookup(string name, string path)
    {
        var colon = name.IndexOf(':');
        if (colon > 0)
            return Blocks.Find(name[(colon + 1)..], name[..colon])
                   ?? throw new BlockError(Block, path, $"no block \"{name[(colon + 1)..]}\" in mod {name[..colon]} (is it loaded before this one?)");
        if (Mod.Handles.TryGetValue(name, out var h)) return h;
        if (Mod.Pending.TryGetValue(name, out var pending))
        {
            if (!Mod.Compiling.Add(name)) throw new BlockError(Block, path, $"\"{name}\" refers back to itself");
            try { return pending(); }
            finally { Mod.Compiling.Remove(name); }
        }
        throw new BlockError(Block, path, $"no block \"{name}\" in this mod; define it first, or say \"other-mod:{name}\"; there are: {string.Join(", ", Mod.Handles.Keys.Order())}");
    }
}

internal static partial class Compiler
{
    // The game's damage power: int(int(seed * creep * level + seed) * dmgMod * speedMod), keen::BalancingTable +0 genericWeaponSeed,
    // +0x28 powerCreepWeapon (0x91e9d0). Checked against the running game when content is ready (BlockRuntime).
    internal static uint Seed = 10;
    internal static float Creep = 0.15f;

    public static int BasePower(int level) => (int)((long)(Seed * Creep * level + Seed));

    /// <summary>The dmgMod that makes a level's power (with speedMod 1) this number, as the tooltip shows it.</summary>
    public static double DmgModFor(int power, int level) => (power + 0.5) / BasePower(level);

    private static string Label<T>(string name) => $"{typeof(T).Name.Replace("Block", "").ToLowerInvariant()} {name}";

    public static BlockHandle<T> Define<T>(ModBlocks mod, string name, T block) where T : class, IBlock
    {
        var label = Label<T>(name);
        if (!ValidName(name)) throw new BlockError(label, null, "a block name takes letters, digits, '-', '_' and '.'");
        var ctx = new BlockContext(mod, label);
        // a new item or template is drawn by the player's game; an effect alone is nothing a client sees
        if (block is not EffectBlock) ctx.Need(ClientsLevel.Kiln, null, "vanilla clients cannot show new items, spells or statuses: Kiln clients then get the definitions from the server");
        var low = new Lowered(mod);
        BlockHandle handle = block switch
        {
            EffectBlock e => DefineEffect(ctx, low, name, e),
            StatusBlock s => DefineStatus(ctx, low, name, s),
            SpellBlock s => DefineSpell(ctx, low, name, s),
            _ => DefineItemBlock(ctx, low, name, block) ?? throw new BlockError(label, null, $"{typeof(T).Name} is not a block Kiln can compile yet"),
        };
        low.Commit();
        mod.Handles[name] = handle;
        mod.Lowered[name] = low.Json;
        mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Info, $"blocks: {mod.Id}: {label} -> {low.Summary()}");
        return (BlockHandle<T>)handle;
    }

    private static bool ValidName(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>VFX fields are there so that a block's shape does not change when they start to work.</summary>
    internal static BlockError NoVfxYet(BlockContext ctx, string path) =>
        new(ctx.Block, path, "blocks cannot show a VFX yet; leave this out for now");

    // ---- effects ------------------------------------------------------------------------------------------------

    private static BlockHandle DefineEffect(BlockContext ctx, Lowered low, string name, EffectBlock e)
    {
        // An effect has no resource of its own. Compiling it once as "on a target" catches what is wrong at the place it was written.
        var check = new ProgramBuilder("check");
        check.BeginCode();
        var em = new EffectEmitter(check, ctx);
        em.OnTarget([e], check.V("target", ImpactType.Entity), "effect");
        low.Json["effect"] = name;
        return new BlockHandle<EffectBlock>(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "effect", name), name, e);
    }

    // ---- statuses -----------------------------------------------------------------------------------------------

    private static BlockHandle DefineStatus(BlockContext ctx, Lowered low, string name, StatusBlock s)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new BlockError(ctx.Block, "name", "a status needs a name players can read");
        if (s.Seconds <= 0) throw new BlockError(ctx.Block, "seconds", $"{s.Seconds} must be more than 0");
        if (s.Vfx != null) throw NoVfxYet(ctx, "vfx");
        var key = s.Was ?? name;
        var guid = BlockIds.Derive(ctx.Mod.Id, "buff", key);
        var (program, blob, listing) = EffectEmitter.StatusProgram(ctx, $"{key}.status", s);
        low.Program(program, $"{key}.status", blob, listing);
        var game = $"{ctx.Mod.Id}.{name}";
        if (s.Icon is { } icon)
        {
            if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new BlockError(ctx.Block, "icon", "a status cannot have a PNG icon of its own yet: name a game status, whose icon it takes");
            if (!icon.StartsWith("0x", StringComparison.Ordinal) && !icon.StartsWith("buff_", StringComparison.Ordinal))
                throw new BlockError(ctx.Block, "icon", $"\"{icon}\" is not a game status: a GameBuffs constant");
            low.AfterCommit(() => BlockRuntime.AddIcon(ctx.Mod, game, icon, ctx.Block));
        }
        low.Clone("buffs", guid, game, s.Look.Starter, new JsonObject
        {
            ["defaultLifeTime"] = s.Seconds,
            ["slot"] = s.Kind == StatusKind.Buff ? "Buff" : "Debuff",
            ["applyType"] = s.Refreshes ? "ResetTime" : "Replace",
            ["impact"] = program.ToString(),
        }, s.Name, s.Description);
        return new BlockHandle<StatusBlock>(ctx.Mod.Id, name, guid, game, s);
    }

    // ---- spells -------------------------------------------------------------------------------------------------

    private static BlockHandle DefineSpell(BlockContext ctx, Lowered low, string name, SpellBlock s)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new BlockError(ctx.Block, "name", "a spell needs a name players can read");
        if (s.Cast.ManaCost < 0) throw new BlockError(ctx.Block, "cast.manaCost", $"{s.Cast.ManaCost} is less than 0");
        if (s.Level is < 1 or > 55) throw new BlockError(ctx.Block, "level", $"{s.Level} is outside the game's item levels 1..55");
        if (s.MaxCharges is < 1 or > 65535) throw new BlockError(ctx.Block, "maxCharges", $"{s.MaxCharges} is outside 1..65535");
        if (s.Cast.Vfx != null) throw NoVfxYet(ctx, "cast.vfx");
        var key = s.Was ?? name;
        var mod = ctx.Mod.Id;

        var delivery = LowerDelivery(ctx, low, key, name, s.Delivery, "delivery", s.OnCast);
        var em = delivery.Emitter;

        // the item: the look's own spell item, with the rows this spell has
        var starter = delivery.HandItem;
        var itemGuid = BlockIds.Derive(mod, "item", key);
        var game = $"{mod}.{name}";
        var main = em.PrimaryDamage;
        var fields = new Dictionary<string, object>
        {
            ["maxStackSize"] = s.MaxCharges,
            ["itemLevelRange.minLevel"] = s.Level,
            ["itemLevelRange.maxLevel"] = s.Level,
            ["equipment.placedEntityLocalSpawnVelocity.z"] = (double)delivery.Speed,
        };
        AddDamageFields(fields, main, s.Level);
        var refs = new Dictionary<string, string>
        {
            ["equipment.placedEntity"] = delivery.PlacedEntity.ToString(),
            ["equipment.visualEntity"] = "item:" + delivery.HandItem,
        };
        low.Item(new ItemDefinition
        {
            Guid = itemGuid, Name = game, BaseOn = starter, DisplayName = s.Name, Description = s.Description,
            Fields = fields, Refs = refs,
        });

        // what the game reads from configs, and the tooltip rows (uiValues show what these configs say; they are tooltip-only)
        var rules = new JsonObject { ["impactValues.ManaCost"] = s.Cast.ManaCost, ["impactValues.0x579b2657"] = s.Cast.Animation.ChargeSeconds };
        var sequence = s.Cast.Animation.Sequence;
        if (s.Cast.ReleaseAt is { } at)
        {
            if (at <= 0) throw new BlockError(ctx.Block, "cast.releaseAt", $"{at} must be more than 0");
            var seqGuid = BlockIds.Derive(mod, "sequence", key);
            var seqName = $"{mod}.{name}.cast";
            var times = new JsonObject();
            foreach (var ev in new[] { "VfxNotifierEvent#0", "SfxNotifierEvent#0", "SpawnEntityEvent#0", "PayUsageCost#0", "PayUsageCost#1" })
                times[$"events[{ev}].time"] = at;
            low.Clone("sequences", seqGuid, seqName, sequence, times);
            rules["sequences[0].sequence"] = seqName;
        }
        else rules["sequences[0].sequence"] = sequence;
        rules["uiValues"] = TooltipRows(s.Cast, em);
        low.ItemRules(game, rules);
        if (s.Recipe != null) DefineRecipe(ctx, low, key, game, s.Recipe);
        return new BlockHandle<SpellBlock>(mod, name, itemGuid, game, s);
    }

    // The rows a spell's tooltip shows, each only if the spell has it. Loca ids are the game's: Mana Cost, Charge Time, Debuff Duration, Radius.
    private const string RowMana = "0xbe5b2600", RowChargeTime = "0x5c76837c", RowDebuffDuration = "0xa63d29bf", RowRadius = "0xc9d82dea";

    internal static JsonObject Row(string locaId, string format, int value) => new() { ["locaId"] = locaId, ["valueFormat"] = format, ["value"] = value };

    internal static JsonArray TooltipRows(CastBlock cast, EffectEmitter em)
    {
        var rows = new JsonArray
        {
            Row(RowMana, "Normal", cast.ManaCost),
            Row(RowChargeTime, "Duration", (int)Math.Round(cast.Animation.ChargeSeconds * 1000)),
        };
        if (em.FirstStatusSeconds is { } seconds) rows.Add(Row(RowDebuffDuration, "Duration", (int)Math.Round(seconds * 1000)));
        if (em.FirstRadius is { } radius) rows.Add(Row(RowRadius, "Float", (int)Math.Round(radius * 100)));
        return rows;
    }

    private static void AddDamageFields(Dictionary<string, object> fields, DamageBlock? d, int level)
    {
        var total = d?.Total ?? 0;
        fields["damageSetup.speedMod"] = 1.0;
        fields["damageSetup.dmgMod"] = DmgModFor((int)Math.Round(total), level);
        double Share(float x) => total > 0 ? x / total : 0;
        fields["damageSetup.distribution.physicalCutDamage"] = Share(d?.Cut ?? 0);
        fields["damageSetup.distribution.physicalPierceDamage"] = Share(d?.Pierce ?? 0);
        fields["damageSetup.distribution.physicalBluntDamage"] = Share(d?.Blunt ?? 0);
        fields["damageSetup.distribution.magicalFireDamage"] = Share(d?.Fire ?? 0);
        fields["damageSetup.distribution.magicalIceDamage"] = Share(d?.Ice ?? 0);
        fields["damageSetup.distribution.magicalFogDamage"] = Share(d?.Shroud ?? 0);
        fields["damageSetup.distribution.magicalLightningDamage"] = Share(d?.Lightning ?? 0);
        fields["damageSetup.distribution.poisonDamage"] = Share(d?.Poison ?? 0);
        fields["damageSetup.distribution.woodDamage"] = d?.Wood ?? 0;
        fields["damageSetup.distribution.stoneDamage"] = d?.Stone ?? 0;
        fields["damageSetup.distribution.metalDamage"] = d?.Metal ?? 0;
        fields["damageSetup.distribution.explosionDamage"] = d?.Explosion ?? 0;
    }
}

/// <summary>What one block lowers to: collected first, so that a block that is wrong registers nothing.</summary>
internal sealed class Lowered(ModBlocks mod)
{
    private readonly List<(Guid Guid, byte[] Blob)> _programs = new();
    private readonly List<(string Section, JsonObject Def)> _clones = new();
    private readonly List<ItemDefinition> _items = new();
    private readonly List<RecipeDefinition> _recipes = new();
    private readonly List<(string Section, string Entry, JsonObject Rules)> _rules = new();
    private readonly List<Action> _after = new();
    private readonly List<(string Section, string Entry, string Path, Guid Program)> _lateRules = new();

    public JsonObject Json { get; } = new();

    private JsonArray Arr(string key) => (Json[key] ??= new JsonArray()).AsArray();

    public void Program(Guid guid, string key, byte[] blob, string listing)
    {
        _programs.Add((guid, blob));
        mod.Programs[guid] = (key, blob, listing);
        Arr("programs").Add(new JsonObject { ["key"] = key, ["guid"] = guid.ToString(), ["size"] = blob.Length });
    }

    public void Clone(string section, Guid guid, string name, string baseName, JsonObject fields, string? displayName = null, string? description = null)
    {
        var def = new JsonObject { ["guid"] = guid.ToString(), ["name"] = name, ["base"] = baseName, ["fields"] = fields };
        if (displayName != null) def["displayName"] = displayName;
        if (description != null) def["description"] = description;
        Arr(section).Add(def.DeepClone());
        // a program that waits for the running game is not there yet for the clone's reference check: named when it is
        foreach (var (path, value) in fields.ToList())
            if (value is JsonValue v && v.TryGetValue<string>(out var text) && Guid.TryParse(text, out var program) && mod.LateBuilders.ContainsKey(program))
            {
                fields.Remove(path);
                _lateRules.Add((section, name, path, program));
            }
        _clones.Add((section, def));
    }

    public void Item(ItemDefinition d)
    {
        _items.Add(d);
        var fields = new JsonObject();
        foreach (var (k, v) in d.Fields ?? new Dictionary<string, object>()) fields[k] = v switch { double x => JsonValue.Create(x), int i => JsonValue.Create(i), _ => JsonValue.Create(v.ToString()) };
        var refs = new JsonObject();
        foreach (var (k, v) in d.Refs ?? new Dictionary<string, string>()) refs[k] = v;
        Arr("items").Add(new JsonObject { ["guid"] = d.Guid.ToString(), ["name"] = d.Name, ["base"] = d.BaseOn, ["displayName"] = d.DisplayName, ["description"] = d.Description, ["fields"] = fields, ["refs"] = refs });
    }

    public void Recipe(RecipeDefinition d)
    {
        _recipes.Add(d);
        Arr("recipes").Add(new JsonObject { ["guid"] = d.Guid.ToString(), ["name"] = d.Name, ["station"] = d.Station,
            ["inputs"] = new JsonArray(d.Inputs.Select(x => (JsonNode)new JsonObject { ["item"] = x.Item, ["count"] = x.Count }).ToArray()),
            ["outputs"] = new JsonArray(d.Outputs.Select(x => (JsonNode)new JsonObject { ["item"] = x.Item, ["count"] = x.Count }).ToArray()) });
    }

    public void ItemRules(string item, JsonObject rules)
    {
        _rules.Add(("items", item, rules));
        Json["itemRules"] = rules.DeepClone();
    }

    public void TemplateRules(string template, JsonObject rules)
    {
        _rules.Add(("templates", template, rules));
        Json["templateRules"] = rules.DeepClone();
    }

    public void AfterCommit(Action a) => _after.Add(a);

    public void Commit()
    {
        foreach (var (guid, blob) in _programs)
        {
            if (mod.LateBuilders.ContainsKey(guid)) continue;     // BlockRuntime finishes it when content is ready
            // kiln.assets serves it: kiln.abilities registers what it serves, and kiln.gamedata's reference checks find it
            Assets.RegisterAs(mod.Owner, new AssetKey(guid, 0xd4922d88u), blob);
            if (Abilities.Available) Abilities.Programs.Refresh();
        }
        foreach (var (guid, _) in _programs)
            if (mod.LateBuilders.ContainsKey(guid)) BlockRuntime.AddLate(mod, guid);
        foreach (var rule in _lateRules) BlockRuntime.AddLateRule(mod, rule.Section, rule.Entry, rule.Path, rule.Program);
        foreach (var (section, def) in _clones)
        {
            var registry = section switch { "buffs" => Content.Buffs, "sequences" => Content.Sequences, _ => Content.Templates };
            BlocksBridge.DefineClone(registry, def, mod.Owner);
        }
        foreach (var d in _items) BlocksBridge.Register(d, mod.Owner);
        foreach (var d in _recipes) BlocksBridge.Register(d, mod.Owner);
        foreach (var (section, entry, rules) in _rules) BlockRuntime.AddRules(mod, section, entry, rules);
        foreach (var a in _after) a();
    }

    public string Summary()
    {
        var parts = new List<string>();
        void Count(string key, string what)
        {
            if (Json[key] is JsonArray a && a.Count > 0) parts.Add($"{a.Count} {what}");
        }
        Count("items", "item(s)"); Count("recipes", "recipe(s)"); Count("templates", "template(s)"); Count("buffs", "buff(s)"); Count("sequences", "sequence(s)"); Count("programs", "program(s)");
        return parts.Count == 0 ? "effect (compiled where it is used)" : string.Join(", ", parts);
    }
}

/// <summary>What a mod asks of players' games, lowest first: nothing, Kiln (definitions by the content sync), the mod itself (its own assets).</summary>
public enum ClientsLevel { None, Kiln, Mod }
