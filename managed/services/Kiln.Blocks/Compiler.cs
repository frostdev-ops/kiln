// SPDX-License-Identifier: MIT
// Block -> what the services take. Which game resource each block starts from is chosen here, never by the author:
//   SpellBlock    ItemInfo: the look's spell item; ActorSequenceResource: the CastAnimation's, cloned only with ReleaseAt
//   ProjectileBlock  TemplateResource: ProjectileLook.Projectile (the game's projectile program stays unless the spell has OnHit or OnCast: on impact it spawns the next template)
//   the impact    TemplateResource: ProjectileLook.Impact, keeping its VFX and sound, running a generated program
//   StatusBlock   BuffType: the look's starter (icon, body effect; without one: StatusLook.Blessing for a buff, StatusLook.Timer for a debuff), running a generated program
//   item blocks   Items/ItemCompiler.cs
//   EffectBlock   no resource of its own: statements in the program of whatever triggers it (EffectEmitter)
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>A block while it compiles: resolves references, names what it makes.</summary>
internal sealed class BlockContext(ModBlocks mod, string block)
{
    public ModBlocks Mod { get; } = mod;
    public string Block { get; } = block;
    /// <summary>What this block lowers to, for what a field adds on the way: a VFX's carrier template.</summary>
    public Lowered? Low { get; set; }

    public EffectBlock Effect(EffectRef r, string path)
    {
        if (r.Name == null) return r.Inline!;
        var handle = Lookup(r.Name, path);
        return (handle as BlockHandle<EffectBlock>)?.Block
               ?? throw new BlockError(Block, path, $"\"{r.Name}\" is a {handle.BlockType.Name}, not an effect");
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
        // a product of a kind's plan names another by "consumer-mod:kind-...": staged with it, not published yet
        if (colon > 0 && Mod.StagesFor != null && name.AsSpan(0, colon).SequenceEqual(Mod.Id))
        {
            var local = name[(colon + 1)..];
            if (Mod.Handles.TryGetValue(local, out var staged)) return staged;
            // a product of the plan that is not staged yet: staged now, whatever order the plan lists them in
            if (Mod.Pending.TryGetValue(local, out var product))
            {
                if (!Mod.Compiling.Add(local)) throw new BlockError(Block, path, $"\"{name}\" refers back to itself");
                try { return product(); }
                finally { Mod.Compiling.Remove(local); }
            }
        }
        if (colon > 0)
            return Blocks.Find(name[(colon + 1)..], name[..colon])
                   ?? throw new BlockError(Block, path, $"no block \"{name[(colon + 1)..]}\" in mod {name[..colon]} (is it loaded before this one?)");
        if (Mod.TryHandle(name, out var h)) return h;
        var authored = Mod.Authored;
        if (authored.Pending.TryGetValue(name, out var pending))
        {
            if (!authored.Compiling.Add(name)) throw new BlockError(Block, path, $"\"{name}\" refers back to itself");
            try { return pending(); }
            finally { authored.Compiling.Remove(name); }
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
        // A kind's plan is being staged on this thread: whoever asked (an inline status, a data file's block named for the
        // first time, a closure over the mod's own ledger), the block joins the plan and nothing is committed.
        if (KindTransaction.Current is { } staging) return staging.Define(mod, name, block);
        if (KindApply.NameReserved(mod, name)) throw new BlockError(name, null, "the name belongs to a block kind's pending or applied plan");
        var (handle, low, label) = Lower(mod, name, block);
        low.Commit();
        mod.Handles[name] = handle;
        mod.Lowered[name] = low.Json;
        mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Info, $"blocks: {mod.Id}: {label} -> {low.Summary()}");
        return (BlockHandle<T>)handle;
    }

    /// <summary>What a block becomes, before anything reaches the game: <see cref="Define{T}"/> without the commit (tests read <see cref="Lowered.Json"/>).</summary>
    internal static (BlockHandle Handle, Lowered Low, string Label) Lower<T>(ModBlocks mod, string name, T block, bool uiGenerated = false) where T : class, IBlock
    {
        var label = Label<T>(name);
        if (!ValidName(name)) throw new BlockError(label, null, "a block name takes letters, digits, '-', '_' and '.'");
        // Check authored names before lowering: a later block (or an ability's Was alias) must not replace an
        // earlier UI-generated ability in the case-insensitive runtime registry.
        if (!uiGenerated)
        {
            if (name.StartsWith("ui-apply.", StringComparison.OrdinalIgnoreCase)) throw new BlockError(label, "name", "ui-apply.* names are reserved for UI actions' hidden abilities");
            if (block is AbilityBlock { Was: { } was } && was.StartsWith("ui-apply.", StringComparison.OrdinalIgnoreCase)) throw new BlockError(label, "was", "ui-apply.* names are reserved for UI actions' hidden abilities");
        }
        AffixNameFree(mod, name, block, label);
        var ctx = new BlockContext(mod, label);
        // a new item or template is drawn by the player's game; an effect alone is nothing a client sees; a VFX or material asks for more, and says so itself
        if (block is not EffectBlock and not VfxBlock and not MaterialBlock and not AbilityBlock and not TriggerBlock and not MaterialTweakBlock and not StrengthBlock and not SurfaceBlock and not BuildingMaterialBlock and not TerrainMaterialBlock and not ScreenEffectBlock and not LootTableBlock and not INpcBlock and not IQuestBlock and not IUiBlock and not AffixBlock and not AffixPoolBlock) ctx.Need(ClientsLevel.Kiln, null, "vanilla clients cannot show new items, spells or statuses: Kiln clients then get the definitions from the server");
        var low = new Lowered(mod);
        ctx.Low = low;
        BlockHandle handle = block switch
        {
            EffectBlock e => DefineEffect(ctx, low, name, e),
            StatusBlock s => DefineStatus(ctx, low, name, s),
            SpellBlock s => DefineSpell(ctx, low, name, s),
            VfxBlock v => DefineVfx(ctx, low, name, v),
            MaterialBlock m => DefineMaterial(ctx, low, name, m),
            MaterialTweakBlock t => DefineMaterialTweak(ctx, low, name, t),
            BuildingMaterialBlock m => DefineBuildingMaterial(ctx, low, name, m),
            StrengthBlock s => DefineStrength(ctx, low, name, s),
            SurfaceBlock s => DefineSurface(ctx, low, name, s),
            SoundBlock s => DefineSound(ctx, low, name, s),
            AffixBlock a => DefineAffix(ctx, low, name, a),
            AffixPoolBlock p => DefineAffixPool(ctx, low, name, p),
            _ => DefineItemBlock(ctx, low, name, block) ?? DefineAbilityBlock(ctx, low, name, block) ?? DefineScreenBlock(ctx, low, name, block) ?? DefineNpcBlock(ctx, low, name, block) ?? DefineQuestBlock(ctx, low, name, block) ?? DefineUiBlock(ctx, low, name, block) ?? throw new BlockError(label, null, $"{typeof(T).Name} is not a block Kiln can compile yet"),
        };
        if (block is ITweakable { Tweak.Count: > 0 } tweak) low.Tweak(label, block is StatusBlock ? "buffs" : block is PerkBlock ? "perks" : "items", handle.GameName, tweak.Tweak);
        return (handle, low, label);
    }

    private static bool ValidName(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

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

    /// <summary>A debuff's <c>color</c>: white, as the game's own debuffs for enemies (Poisoned, Bleeding, the weapons' Burning) have it.</summary>
    internal const uint DebuffColor = 0xFFFFFFFF;

    private static BlockHandle DefineStatus(BlockContext ctx, Lowered low, string name, StatusBlock s)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new BlockError(ctx.Block, "name", "a status needs a name players can read");
        if (s.While == null && s.Seconds <= 0) throw new BlockError(ctx.Block, "seconds", $"{s.Seconds} must be more than 0");
        if (s.Seconds < 0) throw new BlockError(ctx.Block, "seconds", $"{s.Seconds} is less than 0");
        var key = s.Was ?? name;
        var guid = BlockIds.Derive(ctx.Mod.Id, "buff", key);
        var (program, blob, listing) = EffectEmitter.StatusProgram(ctx, $"{key}.status", s);
        low.Program(program, $"{key}.status", blob, listing);
        var game = $"{ctx.Mod.Id}.{name}";
        if (s.Icon is { } png && png.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            BlockIcons.Own(ctx, low, "buffs", game, Content.Buffs, StatusLook.Of(s).Starter, png, key, "icon");
        else if (s.Icon is { } icon)
        {
            if (!icon.StartsWith("0x", StringComparison.Ordinal) && !icon.StartsWith("buff_", StringComparison.Ordinal))
                throw new BlockError(ctx.Block, "icon", $"\"{icon}\" is not a game status: a GameBuffs constant");
            low.AfterCommit(() => BlockRuntime.AddIcon(ctx.Mod, game, icon, ctx.Block));
        }
        var fields = new JsonObject
        {
            ["defaultLifeTime"] = s.Seconds > 0 ? s.Seconds : PlayerTriggers.WhileSeconds,
            ["slot"] = s.Kind == StatusKind.Buff ? "Buff" : "Debuff",
            ["applyType"] = s.Refreshes ? "ResetTime" : "Replace",
            ["impact"] = program.ToString(),
            // a timed status shows its time in the buff bar; one held by a state has none to show
            ["hideDurationInUi"] = s.Seconds <= 0,
        };
        // No description: none, not the look's (the frost slow's "Reduced Movement Speed."). A loca tag no table has shows as nothing.
        if (string.IsNullOrEmpty(s.Description)) fields["description"] = BlockIds.Derive(ctx.Mod.Id, "text", $"{key}.description").ToString();
        // The health bar over a creature draws a status only when its colour is set, tinted by it (client 0xba64b0 skips a BuffType whose color
        // is 0). The game's own debuffs for players have none and its debuffs for enemies are white; the frost slow and the timer have none,
        // so without this a debuff was on the creature and not on its bar.
        if (s.Kind == StatusKind.Debuff) fields["color"] = DebuffColor;
        low.Clone("buffs", guid, game, StatusLook.Of(s).Starter, fields, s.Name, s.Description);
        StatusVfx(ctx, low, game, s);
        if (s.While is { } state) PlayerTriggers.DefineWhile(ctx, low, key, guid, state);
        return new BlockHandle<StatusBlock>(ctx.Mod.Id, name, guid, game, s);
    }

    // ---- spells -------------------------------------------------------------------------------------------------

    private static BlockHandle DefineSpell(BlockContext ctx, Lowered low, string name, SpellBlock s)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new BlockError(ctx.Block, "name", "a spell needs a name players can read");
        if (s.Cast.ManaCost < 0) throw new BlockError(ctx.Block, "cast.manaCost", $"{s.Cast.ManaCost} is less than 0");
        if (s.Cast.StaminaCost < 0) throw new BlockError(ctx.Block, "cast.staminaCost", $"{s.Cast.StaminaCost} is less than 0");
        if (s.Cast.Charges is < 0) throw new BlockError(ctx.Block, "cast.charges", $"{s.Cast.Charges} is less than 0");
        if (s.Level is < 1 or > 55) throw new BlockError(ctx.Block, "level", $"{s.Level} is outside the game's item levels 1..55");
        if (s.MaxCharges is < 1 or > 65535) throw new BlockError(ctx.Block, "maxCharges", $"{s.MaxCharges} is outside 1..65535");
        var key = s.Was ?? name;
        var mod = ctx.Mod.Id;

        var spellItem = $"{mod}.{name}";
        // The game takes one charge; the rest a cast takes on its way out.
        var onCast = s.Cast.Charges is > 1 ? [.. s.OnCast ?? [], new EffectBlock { On = EffectOn.Caster, TakesCharges = (spellItem, s.Cast.Charges.Value - 1) }] : s.OnCast;
        var delivery = LowerDelivery(ctx, low, key, name, s.Delivery, "delivery", onCast);
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
            // no description: none, not the starter's (the Fireball's): an empty one tells kiln.content to clear it
            Guid = itemGuid, Name = game, BaseOn = starter, DisplayName = s.Name, Description = s.Description ?? "",
            Fields = fields, Refs = refs,
        });

        // what the game reads from configs, and the tooltip rows (uiValues show what these configs say; they are tooltip-only)
        var animation = s.Cast.Animation ?? CastAnimation.FastBolt;
        var rules = new JsonObject { ["impactValues.ManaCost"] = s.Cast.ManaCost, ["impactValues.0x579b2657"] = animation.ChargeSeconds };
        if (s.Cast.StaminaCost > 0) rules["impactValues.StaminaCost"] = s.Cast.StaminaCost;
        var sequence = animation.Sequence;
        // cast moves (docs/kiln/modding/item-blocks.md, "Moves"): the state the cast plays, and hitAt, the release, which retimes the whole motion when content is ready
        if (animation.HitAt is { } hit && s.Cast.ReleaseAt is { } release && Math.Abs(hit - release) > 1e-9)
            throw new BlockError(ctx.Block, "cast.animation.hitAt", $"{hit} s, while cast.releaseAt says {release} s: both are the moment the spell leaves the staff. Give one of them, or the same number");
        var moved = animation.Animation != null || animation.HitAt != null;
        var states = moved ? CheckMove(ctx, animation.Animation, null, animation.HitAt, null, "cast.animation") : null;
        if (s.Cast.ReleaseAt != null || s.Cast.Vfx != null || s.Cast.Charges == 0 || moved)
        {
            var seqGuid = BlockIds.Derive(mod, "sequence", key);
            var seqName = $"{mod}.{name}.cast";
            var times = new JsonObject();
            if (s.Cast.ReleaseAt is { } at)
            {
                if (at <= 0) throw new BlockError(ctx.Block, "cast.releaseAt", $"{at} must be more than 0");
                if (animation.HitAt == null)
                    foreach (var ev in new[] { "VfxNotifierEvent#0", "SfxNotifierEvent#0", "SpawnEntityEvent#0", "PayUsageCost#0", "PayUsageCost#1" })
                        times[$"events[{ev}].time"] = at;
            }
            // The cast's VFX takes the place of the motion's own VFX event, at its time: its hand and its moment are the motion's.
            if (s.Cast.Vfx is { } vfx) Merge(times, VfxData.Notifier(ctx, VfxData.Lower(ctx, vfx, "cast.vfx", Slot.HandR), seqName));
            if (s.Cast.Charges == 0) times["events[PayUsageCost#1].consume"] = false;
            low.Clone("sequences", seqGuid, seqName, sequence, times);
            rules["sequences[0].sequence"] = seqName;
            if (moved) CastMoves(ctx, low, "cast.animation", seqName, animation.Key, sequence, states, animation.HitAt, e => e.Type == "SpawnEntityEvent");
        }
        else rules["sequences[0].sequence"] = sequence;
        rules["uiValues"] = TooltipRows(s.Cast, em);
        low.ItemRules(game, rules);
        if (s.Recipe != null) DefineRecipe(ctx, low, key, game, s.Recipe);
        return new BlockHandle<SpellBlock>(mod, name, itemGuid, game, s);
    }

    // The rows a spell's tooltip shows, each only if the spell has it. Loca ids are the game's: Mana Cost, Charge Time, Debuff Duration, Radius.
    private const string RowStamina = "0xaa06e3ec", RowMana = "0xbe5b2600", RowChargeTime = "0x5c76837c", RowDebuffDuration = "0xa63d29bf", RowRadius = "0xc9d82dea";

    internal static JsonObject Row(string locaId, string format, int value) => new() { ["locaId"] = locaId, ["valueFormat"] = format, ["value"] = value };

    internal static JsonArray TooltipRows(CastBlock cast, EffectEmitter em)
    {
        // The starter's rows first, in its order (mana, charge time), then what this spell adds. A row follows its config by its locaId, wherever it is.
        var rows = new JsonArray
        {
            Row(RowMana, "Normal", cast.ManaCost),
            Row(RowChargeTime, "Duration", (int)Math.Round((cast.Animation ?? CastAnimation.FastBolt).ChargeSeconds * 1000)),
        };
        if (cast.StaminaCost > 0) rows.Add(Row(RowStamina, "Normal", cast.StaminaCost));
        if (em.FirstStatusSeconds is { } seconds) rows.Add(Row(RowDebuffDuration, "Duration", (int)Math.Round(seconds * 1000)));
        if (em.FirstRadius is { } radius) rows.Add(Row(RowRadius, "Float", (int)Math.Round(radius * 100)));
        return rows;
    }

    /// <summary>Adds fields to fields, a helper's to a clone's.</summary>
    internal static void Merge(JsonObject into, JsonObject add)
    {
        foreach (var (k, v) in add) into[k] = v?.DeepClone();
    }

    // A status's VFX are the buff's own lists, which clients play from the buff they get from the server. Lists are rules, as a tooltip's rows are.
    private static void StatusVfx(BlockContext ctx, Lowered low, string game, StatusBlock s)
    {
        var rules = new JsonObject();
        if (s.Vfx is { } vfx) rules["whileApplied"] = new JsonArray(VfxData.BuffEntry(ctx, VfxData.Lower(ctx, vfx, "vfx"), game));
        else if (StatusLook.Of(s).Body is { } body) rules["whileApplied"] = new JsonArray(VfxData.BuffEntry(ctx, VfxData.Lower(ctx, body, "look"), game));
        var rows = new JsonArray();
        var onApply = s.OnApply ?? [];
        for (var i = 0; i < onApply.Count; i++)
            if (OnCreateVfx(ctx.Effect(onApply[i], $"onApply[{i}]")) is { } first)
                rows.Add(VfxData.BuffEntry(ctx, VfxData.Lower(ctx, first, $"onApply[{i}].vfx"), game, onCreate: true));
        if (rows.Count > 0) rules["onCreate"] = rows;
        if (rules.Count == 0) return;
        low.Json["buffRules"] = rules.DeepClone();
        low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "buffs", game, rules));
    }

    /// <summary>The VFX of an OnApply effect that the buff itself plays when it starts: one that happens each time, on who has the status. Any other runs in the program, with a carrier.</summary>
    internal static VfxRef? OnCreateVfx(EffectBlock e) => e.Vfx is { Parameters: null or { Count: 0 } } && e.Chance == null && e.On == EffectOn.Target ? e.Vfx : null;

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
    private readonly List<(string Section, string Entry, JsonObject Rules, VfxLate Late)> _vfxLates = new();
    private readonly HashSet<Guid> _carriers = new();

    public JsonObject Json { get; } = new();

    private JsonArray Arr(string key) => (Json[key] ??= new JsonArray()).AsArray();

    public void Program(Guid guid, string key, byte[] blob, string listing)
    {
        _programs.Add((guid, blob));
        mod.Programs[guid] = (key, blob, listing);
        Arr("programs").Add(new JsonObject { ["key"] = key, ["guid"] = guid.ToString(), ["size"] = blob.Length });
    }

    public void Clone(string section, Guid guid, string name, string baseName, JsonObject fields, string? displayName = null, string? description = null, IReadOnlyList<string>? addComponents = null,
        IReadOnlyList<string>? removeComponents = null, JsonObject? texts = null, IReadOnlyDictionary<string, int>? growArrays = null)
    {
        var def = new JsonObject { ["guid"] = guid.ToString(), ["name"] = name, ["base"] = baseName, ["fields"] = fields };
        if (growArrays is { Count: > 0 }) def["growArrays"] = new JsonObject(growArrays.Select(g => KeyValuePair.Create(g.Key, (JsonNode?)JsonValue.Create(g.Value))));
        if (texts is { Count: > 0 }) def["texts"] = texts.DeepClone();
        if (addComponents != null) def["addComponents"] = new JsonArray(addComponents.Select(c => (JsonNode)JsonValue.Create(c)).ToArray());
        if (removeComponents != null) def["removeComponents"] = new JsonArray(removeComponents.Select(c => (JsonNode)JsonValue.Create(c)).ToArray());
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

    /// <summary>A template a VFX rides on, shared by every block of the mod that plays it: it is listed for each, and defined once.</summary>
    public void Carrier(Guid guid, string name, string baseName, JsonObject fields)
    {
        if (!mod.Carriers.Contains(guid) && _carriers.Add(guid))
        {
            Clone("templates", guid, name, baseName, fields);
            AfterCommit(() => mod.Carriers.Add(guid));
            return;
        }
        Arr("templates").Add(new JsonObject { ["guid"] = guid.ToString(), ["name"] = name, ["base"] = baseName, ["fields"] = fields.DeepClone() });
    }

    /// <summary>A game VFX's name: <paramref name="fill"/> writes what it stands for into this entry's rules once the running game can answer.</summary>
    public void Late(VfxTarget v, string section, string entry, Action<JsonObject, VfxResolved> fill)
    {
        var rules = new JsonObject();
        _vfxLates.Add((section, entry, rules, new VfxLate(v, r => fill(rules, r))));
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
        var json = new JsonObject { ["guid"] = d.Guid.ToString(), ["name"] = d.Name, ["station"] = d.Station, ["menu"] = d.Menu, ["seconds"] = d.Duration?.TotalSeconds,
            ["fields"] = d.Fields is { Count: > 0 } f ? new JsonObject(f.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => KeyValuePair.Create(x.Key, (JsonNode?)JsonValue.Create(x.Value.ToString())))) : null,
            ["inputs"] = new JsonArray(d.Inputs.Select(x => (JsonNode)new JsonObject { ["item"] = x.Item, ["count"] = x.Count }).ToArray()),
            ["outputs"] = new JsonArray(d.Outputs.Select(x => (JsonNode)new JsonObject { ["item"] = x.Item, ["count"] = x.Count }).ToArray()) };
        if (d.Props is { Count: > 0 }) json["props"] = new JsonArray(d.Props.Select(x => (JsonNode)new JsonObject { ["id"] = $"0x{x.Id:x8}", ["index"] = x.Index }).ToArray());
        Arr("recipes").Add(json);
    }

    public void ItemRules(string item, JsonObject rules)
    {
        _rules.Add(("items", item, rules));
        Json["itemRules"] = rules.DeepClone();
    }

    /// <summary>What the author set below the block: applied with the block's other rules, after them.</summary>
    public void Tweak(string block, string section, string entry, FieldValues values)
    {
        try { values.Require(section, "tweak"); }
        catch (ArgumentException e) { throw new BlockError(block, "tweak", e.Message); }
        var rules = new JsonObject();
        foreach (var (path, value) in values.Json) rules[path] = value.DeepClone();
        _rules.Add((section, entry, rules));
        Json[section == "templates" ? "templateTweak" : "tweak"] = rules.DeepClone();
    }

    public void TemplateRules(string template, JsonObject rules)
    {
        _rules.Add(("templates", template, rules));
        Json["templateRules"] = rules.DeepClone();
    }

    public void AfterCommit(Action a) => _after.Add(a);

    /// <summary>Where a commit's registrations go: the services. Tests count them and make one fail (Kinds: a plan that is
    /// refused part way is revoked whole).</summary>
    internal static ILoweredSink Sink => _sinkOverride ?? Services;

    private static readonly ILoweredSink Services = new ServiceSink();
    /// <summary>Another sink for this thread's commits (tests, and a plan's commit that may be refused part way).</summary>
    [ThreadStatic] internal static ILoweredSink? _sinkOverride;

    internal interface ILoweredSink
    {
        void Program(ulong owner, Guid guid, byte[] blob);
        void Clone(ulong owner, string section, JsonObject def);
        void Item(ulong owner, ItemDefinition d);
        void Recipe(ulong owner, RecipeDefinition d);
    }

    private sealed class ServiceSink : ILoweredSink
    {
        public void Program(ulong owner, Guid guid, byte[] blob)
        {
            Assets.RegisterAs(owner, new AssetKey(guid, 0xd4922d88u), blob);
            if (Abilities.Available) Abilities.Programs.Refresh();
        }

        public void Clone(ulong owner, string section, JsonObject def)
        {
            var registry = section switch { "buffs" => Content.Buffs, "sequences" => Content.Sequences, "perks" => Content.Perks, _ => Content.Templates };
            BlocksBridge.DefineClone(registry, def, owner);
        }

        public void Item(ulong owner, ItemDefinition d) => BlocksBridge.Register(d, owner);
        public void Recipe(ulong owner, RecipeDefinition d) => BlocksBridge.Register(d, owner);
    }

    /// <summary>Set while a boot generation is lowered (BootLowering.cs, chapter 08): a commit hands its registrations to the sink (the IR
    /// envelope) and leaves the runtime parts (rules, late programs, after-commit actions) to the mod's runtime activation.</summary>
    [ThreadStatic] internal static bool _bootLowering;

    public void Commit()
    {
        CommitRegistrations();
        CommitAfter();
    }

    /// <summary>The first half of a commit: what the services register, and the ledger rows of the block's own mod. A kind's
    /// plan runs this half for every block of the plan before the second half of any: a refusal here leaves nothing running.</summary>
    internal void CommitRegistrations()
    {
        if (mod.StagesFor != null && !mod.CommitOpen) throw new InvalidOperationException("a block staged for a kind's plan was committed before the plan: nothing of a plan registers until all of it is checked");
        if (_bootLowering)
        {
            foreach (var (guid, blob) in _programs)
                if (!mod.LateBuilders.ContainsKey(guid)) Sink.Program(mod.Owner, guid, blob);
            foreach (var (section, def) in _clones) Sink.Clone(mod.Owner, section, def);
            foreach (var d in _items) Sink.Item(mod.Owner, d);
            foreach (var d in _recipes) Sink.Recipe(mod.Owner, d);
            return;
        }
        foreach (var (guid, blob) in _programs)
        {
            if (mod.LateBuilders.ContainsKey(guid)) continue;     // BlockRuntime finishes it when content is ready
            // kiln.assets serves it: kiln.abilities registers what it serves, and kiln.gamedata's reference checks find it
            Sink.Program(mod.Owner, guid, blob);
        }
        foreach (var (guid, _) in _programs)
            if (mod.LateBuilders.ContainsKey(guid)) BlockRuntime.AddLate(mod, guid);
        foreach (var rule in _lateRules) BlockRuntime.AddLateRule(mod, rule.Section, rule.Entry, rule.Path, rule.Program);
        foreach (var (section, def) in _clones) Sink.Clone(mod.Owner, section, def);
        foreach (var d in _items) Sink.Item(mod.Owner, d);
        foreach (var d in _recipes) Sink.Recipe(mod.Owner, d);
        foreach (var (section, entry, rules) in _rules) BlockRuntime.AddRules(mod, section, entry, rules);
    }

    /// <summary>The second half: what runs in Kiln because the block exists (triggers, icons, checks at content ready).</summary>
    internal void CommitAfter()
    {
        if (mod.StagesFor != null && !mod.CommitOpen) throw new InvalidOperationException("a block staged for a kind's plan was committed before the plan");
        if (_bootLowering) return;  // a boot generation lowers registrations only; the runtime parts wait for the mod's runtime activation
        foreach (var a in _after) a();
        foreach (var (section, entry, rules, late) in _vfxLates)
        {
            BlockRuntime.AddRules(mod, section, entry, rules);
            BlockRuntime.AddVfxLate(mod, late);
        }
    }

    /// <summary>How many bytes of programs this block made (a plan's bound).</summary>
    internal long GeneratedBytes => _programs.Sum(p => (long)p.Blob.Length) + Json.ToJsonString().Length;

    public string Summary()
    {
        var parts = new List<string>();
        void Count(string key, string what)
        {
            if (Json[key] is JsonArray a && a.Count > 0) parts.Add($"{a.Count} {what}");
        }
        Count("items", "item(s)"); Count("recipes", "recipe(s)"); Count("templates", "template(s)"); Count("buffs", "buff(s)"); Count("perks", "perk(s)"); Count("sequences", "sequence(s)"); Count("programs", "program(s)");
        if (Json["lootTable"] is JsonObject loot && loot["entries"] is JsonArray entries) parts.Add($"loot table of {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");
        if (Json["vfx"] is { } vfx) parts.Add($"VFX {vfx} ({Json["status"] ?? "not built here"})");
        if (Json["material"] is { } material) parts.Add($"material {material} ({Json["status"] ?? "not built here"})");
        if (Json["quest"] is JsonObject quest) parts.Add($"quest {quest["id"]}: {quest["steps"]!.AsArray().Count} step(s), {quest["rewards"]!.AsArray().Count} reward(s)");
        if (Json["dialogue"] is JsonObject dialogue) parts.Add($"dialogue {dialogue["id"]}: {dialogue["nodes"]!.AsArray().Count} node(s)");
        if (Json["window"] is JsonObject window) parts.Add($"{window["kind"]} with {(window["rows"] as JsonArray)?.Count ?? 0} row(s), drawn by players' games");
        if (Json["hudLine"] is JsonObject hud) parts.Add($"HUD line {hud["id"]}, drawn by players' games");
        if (Json["keyBinding"] is JsonObject key) parts.Add($"key {key["id"]} ({key["key"]}), bound on players' games");
        if (Json["affix"] is JsonObject affix) parts.Add($"affix {affix["id"]} ({affix["stat"]}, {affix["tiers"]} tier(s))");
        if (Json["affixPool"] is JsonObject affixPool) parts.Add($"affix pool {affixPool["id"]} of {(affixPool["affixes"] as JsonArray)?.Count ?? 0} affix(es)");
        return parts.Count == 0 ? "effect (compiled where it is used)" : string.Join(", ", parts);
    }
}

/// <summary>What a mod asks of players' games, lowest first: nothing, Kiln (definitions by the content sync), the mod itself (its own assets).</summary>
public enum ClientsLevel { None, Kiln, Mod }
