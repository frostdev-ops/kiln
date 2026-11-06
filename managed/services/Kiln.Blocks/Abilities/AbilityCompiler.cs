// SPDX-License-Identifier: MIT
// Perk, ability, talent and delivery blocks -> what the services take:
//   PerkBlock     keen::Perk: a clone of PerkStarter running a generated program, in an item's enhancement slot. The game starts it while the item is in an equipment slot, once that enhancement is unlocked.
//   AbilityBlock  an AbilityDefinition (kiln.abilities), and the template, program and sequence its delivery needs
//   TalentBlock   a node of the game's tree (kiln.skills: native slot or shadow table, its call) or of a TalentTreeBlock (Skills.Register); a generated program either way
//   a delivery    a clone of PlacedBase running a generated program, placed by a staff's or a wand's event or by a cast
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    /// <summary>What a placed delivery starts from: a transform, an impact and its hierarchy, DoNotSave, nothing to see.</summary>
    private const string PlacedBase = "Base_Impact_Zone";
    /// <summary>A real staff spell's homing projectile: it has the look's own gravity and the components a homing flight needs, which a clone cannot add.</summary>
    private const string HomingStarter = "Projectile_Player_Staff_Bone_Channel_Homing";
    /// <summary>A perk with one tooltip row. Its program is replaced, and nothing else of it runs.</summary>
    private const string PerkStarter = "Perk_OnDamage_Dealt_Debuff_Poisoned_T2";
    private const string RowPerkValue = "0xc828d898";
    /// <summary>A levelled passive of the game's tree: its config <see cref="EffectEmitter.CfgLevel"/> scales with the level. A node with none has it at no scale, and its program reads no level.</summary>
    private const string TalentLike = "0x2fdadb3d";

    private static BlockHandle? DefineAbilityBlock<T>(BlockContext ctx, Lowered low, string name, T block) where T : class, IBlock => block switch
    {
        PerkBlock p => DefinePerk(ctx, low, name, p),
        AbilityBlock a => DefineAbility(ctx, low, name, a),
        TalentBlock t => DefineTalent(ctx, low, name, t),
        TalentTreeBlock t => DefineTree(ctx, low, name, t),
        TriggerBlock t => PlayerTriggers.Define(ctx, low, name, t),
        _ => null,
    };

    // ---- deliveries ---------------------------------------------------------------------------------------------

    private static DeliveryLowered LowerPlaced(BlockContext ctx, Lowered low, string key, string name, PlacedDelivery d, string path, IReadOnlyList<EffectRef>? onCast, string onCastPath, bool primary)
    {
        CheckPlaced(ctx, d, path);
        var (program, blob, em, listing) = EffectEmitter.DeliveryProgram(ctx, $"{key}.delivery", d, path, onCast, onCastPath, primary);
        low.Program(program, $"{key}.delivery", blob, listing);
        var mod = ctx.Mod.Id;
        var guid = BlockIds.Derive(mod, "template", $"{key}.delivery");
        var game = $"{mod}.{name}.delivery";
        low.Clone("templates", guid, game, PlacedBase, new JsonObject { ["Impact.program"] = program.ToString() });
        if (d.Tweak.Count > 0) low.Tweak($"{ctx.Block}, {path}", "templates", game, d.Tweak);
        return new DeliveryLowered(guid, 0, em, d.Look.HandItem);
    }

    private static void CheckPlaced(BlockContext ctx, PlacedDelivery d, string path)
    {
        void Positive(double value, string field)
        {
            if (value <= 0) throw new BlockError(ctx.Block, $"{path}.{field}", $"{value} must be more than 0");
        }
        void Does(IReadOnlyList<EffectRef>? effects)
        {
            if (effects is not { Count: > 0 }) throw new BlockError(ctx.Block, $"{path}.effects", "a delivery that does nothing: give it effects");
        }
        void Angle(float degrees)
        {
            if (degrees is <= 0 or >= 180) throw new BlockError(ctx.Block, $"{path}.angle", $"{degrees} is outside 1..179 degrees");
        }
        switch (d)
        {
            case SelfBlock s:
                Does(s.Effects);
                break;
            case AroundBlock a:
                Positive(a.Radius, "radius");
                Does(a.Effects);
                break;
            case ConeBlock c:
                Positive(c.Range, "range");
                Angle(c.Angle);
                Does(c.Effects);
                break;
            case BeamBlock b:
                Positive(b.Range, "range");
                Positive(b.Width, "width");
                if (b.Seconds < 0) throw new BlockError(ctx.Block, $"{path}.seconds", $"{b.Seconds} is less than 0");
                Positive(b.TickEvery, "tickEvery");
                Does(b.Effects);
                break;
            case ZoneBlock z:
                Positive(z.Radius, "radius");
                Positive(z.Seconds, "seconds");
                Positive(z.TickEvery, "tickEvery");
                Does(z.Effects);
                break;
            case ChainBlock c:
                Positive(c.Range, "range");
                Positive(c.JumpRange, "jumpRange");
                Angle(c.Angle);
                if (c.Jumps is < 0 or > 8) throw new BlockError(ctx.Block, $"{path}.jumps", $"{c.Jumps} is outside 0..8");
                Does(c.Effects);
                break;
            case SummonBlock s:
                if (string.IsNullOrWhiteSpace(s.Template.Value)) throw new BlockError(ctx.Block, $"{path}.template", "a summon needs a template: a GameTemplates constant or a GUID");
                if (s.Count is < 1 or > 16) throw new BlockError(ctx.Block, $"{path}.count", $"{s.Count} is outside 1..16");
                if (s.Seconds is <= 0) throw new BlockError(ctx.Block, $"{path}.seconds", $"{s.Seconds} must be more than 0");
                break;
        }
    }

    private static void CheckHoming(BlockContext ctx, ProjectileBlock p, string path)
    {
        if (p.Homing is not { } h) return;
        if (h.TurnSpeed <= 0) throw new BlockError(ctx.Block, $"{path}.homing.turnSpeed", $"{h.TurnSpeed} must be more than 0");
        if (h.SeekRange <= 0) throw new BlockError(ctx.Block, $"{path}.homing.seekRange", $"{h.SeekRange} must be more than 0");
        if (h.Delay < 0) throw new BlockError(ctx.Block, $"{path}.homing.delay", $"{h.Delay} is less than 0");
    }

    // A clone cannot add a component, so a homing flight starts from the game's homing projectile and takes the look's VFX, as a game VFX named after the look's template.
    private static void HomingFields(BlockContext ctx, Lowered low, JsonObject flight, ProjectileBlock p, HomingBlock h, string path, string template)
    {
        flight["HomeToTarget.turnSpeed"] = h.TurnSpeed;
        flight["HomeToTarget.rayLength"] = h.SeekRange;
        flight["HomeToTarget.minDelay"] = h.Delay;
        flight["HomeToTarget.maxDelay"] = h.Delay;
        foreach (var f in new[] { "minX", "maxX", "minY", "maxY", "minZ", "maxZ" }) flight[$"RandomOrientation.randomOrientationOffset.{f}"] = 0;
        if (p.Vfx != null) return;
        var look = VfxData.Lower(ctx, VfxRef.Game(p.Look.Projectile), path);
        flight["VfxComponentResource.vfx"] = "none";
        low.Late(look, "templates", template, (rules, r) =>
        {
            rules["VfxComponentResource.vfx"] = r.Vfx.ToString();
            for (var i = 0; i < 16; i++) rules[$"VfxParametersTemplateComponent.parameters.data[{i}]"] = r.Parameters is { } q && i < q.Length ? q[i] : 0;
        });
    }

    // ---- perks --------------------------------------------------------------------------------------------------

    private static string StatName(Stat s) => string.Join(' ', s.Key.Split('-').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>What a stat's change says in a tooltip: its sign, its number, its name.</summary>
    private static string StatText(Stat s, double value) => s.Whole ? $"{value:+0;-0} {StatName(s)}" : $"{value * 100:+0.##;-0.##}% {StatName(s)}";

    private static string StatsText(IDictionary<Stat, double> modifies) => string.Join(", ", modifies.Select(m => StatText(m.Key, m.Value)));

    private static JsonObject ValueRow(string format, int value)
    {
        var row = Row(RowPerkValue, format, value);
        row["isSigned"] = true;
        return row;
    }

    // "5%", "12", "2.5", "8s": the formats the game's rows have
    private static JsonObject ParseValue(BlockContext ctx, string text)
    {
        var t = text.Trim();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double Number(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, inv, out var n) ? n
            : throw new BlockError(ctx.Block, "value", $"\"{text}\" is not a number: say \"5%\", \"12\", \"2.5\" or \"8s\"");
        if (t.EndsWith('%')) return ValueRow("Percentage", (int)Math.Round(Number(t[..^1])));
        if (t.EndsWith('s')) return ValueRow("Duration", (int)Math.Round(Number(t[..^1].TrimEnd()) * 1000));
        var v = Number(t);
        return v == Math.Floor(v) ? ValueRow("Normal", (int)v) : ValueRow("Float", (int)Math.Round(v * 100));
    }

    private static BlockHandle DefinePerk(BlockContext ctx, Lowered low, string name, PerkBlock p)
    {
        RequireName(ctx, p.Name, "a perk");
        var key = p.Was ?? name;
        var mod = ctx.Mod.Id;
        var game = $"{mod}.{name}";
        var onHit = ctx.Hits(p.OnHit, "onHit");
        var onKill = ctx.Hits(p.OnKill, "onKill");
        var onHitTaken = ctx.Hits(p.OnHitTaken, "onHitTaken");
        if (p.Modifies.Count == 0 && onHit.Count == 0 && onKill.Count == 0 && onHitTaken.Count == 0)
            throw new BlockError(ctx.Block, "", "a perk that does nothing: give it modifies, onHit, onKill or onHitTaken");
        var (program, blob, _, listing) = EffectEmitter.FollowerProgram(ctx, $"{key}.perk", p.Modifies, 1, null, "", null, "", onHit, onKill, onHitTaken);
        low.Program(program, $"{key}.perk", blob, listing);
        var guid = BlockIds.Derive(mod, "perk", key);
        var description = p.Description ?? (p.Modifies.Count > 1 ? StatsText(p.Modifies) : null);
        var fields = new JsonObject { ["impact"] = program.ToString(), ["simpleAttributeBuff"] = false };
        // No description: none, not the starter's ("Chance to poison enemies on hit"). A loca tag no table has shows as nothing.
        if (string.IsNullOrEmpty(description)) fields["description"] = BlockIds.Derive(mod, "text", $"{key}.perk.description").ToString();
        low.Clone("perks", guid, game, PerkStarter, fields, p.Name, string.IsNullOrEmpty(description) ? null : description);

        // The number in front of its name in a tooltip, and nothing of the starter's.
        var rows = new JsonArray();
        if (p.Value is { Length: > 0 } text) rows.Add(ParseValue(ctx, text));
        else if (p.Modifies.Count == 1 && p.Modifies.First() is var (stat, value))
            rows.Add(stat.Whole ? ValueRow("Normal", (int)value) : ValueRow("Percentage", (int)Math.Round(value * 100)));
        else if (onHit.Count == 1 && ctx.Effect(onHit[0], "onHit[0]").Chance is { } chance) rows.Add(ValueRow("Percentage", (int)Math.Round(chance * 100)));
        var rules = new JsonObject { ["uiValues"] = rows };
        low.Json["perkRules"] = rules.DeepClone();
        low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "perks", game, rules));
        if (p.Icon is { } icon)
        {
            if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                throw new BlockError(ctx.Block, "icon", "a perk cannot have a PNG icon of its own yet: name a game perk, whose icon it takes");
            low.AfterCommit(() => BlockRuntime.AddIcon(ctx.Mod, "perks", game, Content.Perks, icon, ctx.Block));
        }
        return new BlockHandle<PerkBlock>(mod, name, guid, game, p);
    }

    // ---- abilities ----------------------------------------------------------------------------------------------

    private static BlockHandle DefineAbility(BlockContext ctx, Lowered low, string name, AbilityBlock a)
    {
        RequireName(ctx, a.Name, "an ability");
        var key = a.Was ?? name;
        var mod = ctx.Mod.Id;
        var id = $"{mod}:{key}";
        var game = $"{mod}.{name}";
        var cast = a.Cast ?? new CastBlock();
        if (cast.ManaCost < 0) throw new BlockError(ctx.Block, "cast.manaCost", $"{cast.ManaCost} is less than 0");
        if (cast.StaminaCost < 0) throw new BlockError(ctx.Block, "cast.staminaCost", $"{cast.StaminaCost} is less than 0");
        if (a.SpecialCost < 0) throw new BlockError(ctx.Block, "specialCost", $"{a.SpecialCost} is less than 0");
        if (a.Cooldown < 0) throw new BlockError(ctx.Block, "cooldown", $"{a.Cooldown} is less than 0");
        if (a.Range <= 0) throw new BlockError(ctx.Block, "range", $"{a.Range} must be more than 0");
        if (cast.ReleaseAt is <= 0) throw new BlockError(ctx.Block, "cast.releaseAt", $"{cast.ReleaseAt} must be more than 0");
        if (cast.Charges is < 0) throw new BlockError(ctx.Block, "cast.charges", $"{cast.Charges} is less than 0");
        if (cast.Charges != null && a.Item == null) throw new BlockError(ctx.Block, "cast.charges", "an ability with no item has no charges to take: give it an item, or use cooldown");
        if (a.Delivery == null && a.OnCast is not { Count: > 0 }) throw new BlockError(ctx.Block, "delivery", "an ability that does nothing: give it a delivery or onCast");
        if (a.Key != null) ctx.Need(ClientsLevel.Mod, "key", "binding a key needs the mod on the player's game");
        if (cast.Animation != null && cast.Vfx != null) ctx.Need(ClientsLevel.Kiln, "cast.vfx", "new data that names a VFX has to reach the players' games: Kiln clients get it from the server");
        if (cast.Animation == null && cast.Vfx != null) throw new BlockError(ctx.Block, "cast.vfx", "without an animation there is no motion to carry it: put it on the delivery");

        var delivery = a.Delivery ?? new SelfBlock { Effects = a.OnCast! };
        var onCast = a.Delivery == null ? null : a.OnCast;
        var release = cast.ReleaseAt ?? 0;
        Guid? sequence = null;
        Func<AbilityCast, Task> run;
        var info = new JsonObject
        {
            ["id"] = id, ["name"] = a.Name, ["description"] = a.Description, ["key"] = a.Key, ["command"] = a.Command,
            ["mana"] = cast.ManaCost, ["stamina"] = cast.StaminaCost, ["special"] = a.SpecialCost, ["cooldown"] = a.Cooldown, ["aim"] = a.Aim.ToString(), ["range"] = a.Range,
        };

        if (delivery is SelfBlock { Vfx: null, Tweak.Count: 0 } own && cast.Animation == null)
        {
            // Effects on the caster alone need nothing a player's game has to know: the game's generic impact runs them.
            var (program, blob, _, listing) = EffectEmitter.SelfProgram(ctx, $"{key}.cast", onCast is { Count: > 0 } ? [.. onCast, .. own.Effects] : own.Effects, a.Delivery == null ? "onCast" : "delivery.effects");
            low.Program(program, $"{key}.cast", blob, listing);
            run = c => Abilities.RunProgram(program, c.CasterPosition, new ImpactOptions { Root = c.Caster, Tag = id });
        }
        else
        {
            ctx.Need(ClientsLevel.Kiln, "delivery", "what it places is new data: Kiln clients get it from the server");
            var lowered = LowerDelivery(ctx, low, key, name, delivery, "delivery", onCast, "onCast", primary: false);
            if (cast.Animation is { } animation)
            {
                release = cast.ReleaseAt ?? animation.ChargeSeconds;
                var seqGuid = BlockIds.Derive(mod, "sequence", key);
                var seqName = $"{game}.cast";
                var fields = new JsonObject();
                if (cast.ReleaseAt is { } at)
                    foreach (var ev in new[] { "VfxNotifierEvent#0", "SfxNotifierEvent#0", "SpawnEntityEvent#0", "PayUsageCost#0", "PayUsageCost#1" }) fields[$"events[{ev}].time"] = at;
                if (cast.Vfx is { } vfx) Merge(fields, VfxData.Notifier(ctx, VfxData.Lower(ctx, vfx, "cast.vfx", Slot.HandR), seqName));
                low.Clone("sequences", seqGuid, seqName, animation.Sequence, fields);
                // A clone can name another clone once both are registered: a rule. The event names the template itself, as nothing is used to take it from: a wand's attack does the same.
                var rules = new JsonObject
                {
                    ["events[SpawnEntityEvent#0].templateReference"] = lowered.PlacedEntity.ToString(),
                    ["events[SpawnEntityEvent#0].ignoreSpawnVelocity"] = true,
                    ["events[SpawnEntityEvent#0].spawnVelocity.z"] = (double)lowered.Speed,
                    ["events[SpawnEntityEvent#0].requiredEntityTag"] = "none",
                };
                low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "sequences", seqName, rules));
                low.Json["sequenceRules"] = rules.DeepClone();
                sequence = seqGuid;
                run = _ => Task.CompletedTask;
            }
            else
            {
                var placed = lowered.PlacedEntity;
                if (delivery is ProjectileBlock)
                {
                    var launchGuid = BlockIds.Derive(mod, "template", $"{key}.launch");
                    var (program, blob, listing) = EffectEmitter.LauncherProgram(ctx, $"{key}.launch", placed, lowered.Speed);
                    low.Program(program, $"{key}.launch", blob, listing);
                    low.Clone("templates", launchGuid, $"{game}.launch", PlacedBase, new JsonObject { ["Impact.program"] = program.ToString() });
                    placed = launchGuid;
                }
                var targeted = a.Aim == AbilityTargeting.NearestEnemy;
                run = c =>
                {
                    var yaw = c.CasterYaw;
                    ImpactConfigs? configs = null;
                    if (targeted && !c.Target.IsNone)
                    {
                        yaw = (float)(Math.Atan2(c.Position.X - c.CasterPosition.X, c.Position.Z - c.CasterPosition.Z) * 180 / Math.PI);
                        configs = new ImpactConfigs { [$"0x{EffectEmitter.CfgTarget:x8}"] = c.Target };
                    }
                    return Abilities.RunImpact(placed, c.CasterPosition, new ImpactOptions { Root = c.Caster, YawDegrees = yaw, Configs = delivery is ProjectileBlock ? configs : null, Tag = id });
                };
            }
        }

        var handle = new BlockHandle<AbilityBlock>(mod, name, BlockIds.Derive(mod, "ability", key), id, a);
        var definition = new AbilityDefinition
        {
            Id = id, DisplayName = a.Name, Description = a.Description, DefaultKey = a.Key, ChatCommand = a.Command,
            Cost = new AbilityCost(cast.ManaCost, cast.StaminaCost, a.SpecialCost), Cooldown = TimeSpan.FromSeconds(a.Cooldown), CastTime = TimeSpan.FromSeconds(release),
            CastSequence = sequence, Targeting = a.Aim, Range = a.Range, PlayersOnly = a.PlayersOnly,
            OnCast = async c =>
            {
                if (release > 0 && sequence == null) await c.At(TimeSpan.FromSeconds(release));
                await run(c);
            },
        };
        low.Json["ability"] = info;
        low.AfterCommit(() => AbilityRuntime.Register(definition));

        if (a.Item is { } item)
        {
            var charges = cast.Charges ?? 1;
            var itemName = $"{name}.item";
            var itemGame = $"{mod}.{itemName}";
            // The item's use casts this block, which is known by name from here on.
            ctx.Mod.Handles[name] = handle;
            try
            {
                Define(ctx.Mod, itemName, item with
                {
                    Kind = item.Kind ?? ItemKind.Consumable,
                    OnUse = [new EffectBlock { Casts = name, CastsFrom = (itemGame, Math.Min(charges, 1), Math.Max(charges - 1, 0)) }, .. item.OnUse ?? []],
                });
            }
            catch (BlockError e)
            {
                ctx.Mod.Handles.TryRemove(name, out _);
                throw new BlockError(ctx.Block, "item", e.Message);
            }
            info["item"] = itemGame;
            if (charges == 0)
            {
                var keep = new JsonObject { ["events[SpawnImpact#0].consume"] = false };
                low.AfterCommit(() => BlockRuntime.AddRules(ctx.Mod, "sequences", $"{itemGame}.use", keep));
            }
        }
        return handle;
    }

    // ---- talents ------------------------------------------------------------------------------------------------

    private static void CheckTalent(BlockContext ctx, TalentBlock t, string path)
    {
        if (string.IsNullOrWhiteSpace(t.Name)) throw new BlockError(ctx.Block, $"{path}name", "a talent needs a name players can read");
        if (t.Cost < 0) throw new BlockError(ctx.Block, $"{path}cost", $"{t.Cost} is less than 0");
        if (t.MaxLevel < 1) throw new BlockError(ctx.Block, $"{path}maxLevel", $"{t.MaxLevel} is less than 1");
        if (t.PointsSpent < 0) throw new BlockError(ctx.Block, $"{path}pointsSpent", $"{t.PointsSpent} is less than 0");
        if (t.CharacterLevel < 0) throw new BlockError(ctx.Block, $"{path}characterLevel", $"{t.CharacterLevel} is less than 0");
        if (t.Icon is { } icon && !icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new BlockError(ctx.Block, $"{path}icon", $"\"{icon}\" is not a PNG in the mod's folder");
    }

    /// <summary>What a talent does, as one program for as long as a character has it. In a tree and in the game's tree alike the level is its float config. A node of the game's tree
    /// names its program by a GUID from the talent alone: a dedicated server reads the node it kept from its last start before mods load, and has to find the program there.</summary>
    private static Guid TalentProgram(BlockContext ctx, Lowered low, string key, TalentBlock t, string path, bool gameTree = false)
    {
        var (program, blob, _, listing) = EffectEmitter.FollowerProgram(ctx, $"{key}.talent", t.Modifies, t.MaxLevel, t.Keeps, $"{path}keeps", t.OnStart, $"{path}onStart",
            ctx.Hits(t.OnHit, $"{path}onHit"), ctx.Hits(t.OnKill, $"{path}onKill"), ctx.Hits(t.OnHitTaken, $"{path}onHitTaken"), stable: gameTree);
        low.Program(program, $"{key}.talent", blob, listing);
        return program;
    }

    private static string TalentDescription(TalentBlock t) => t.Description ?? StatsText(t.Modifies);

    private static BlockHandle DefineTalent(BlockContext ctx, Lowered low, string name, TalentBlock t)
    {
        CheckTalent(ctx, t, "");
        ctx.Need(ClientsLevel.Mod, null, "every player's game has to build the same tree from the same files");
        var mod = ctx.Mod.Id;
        var key = t.Was ?? name;
        var guid = BlockIds.Derive(mod, "talent", key);
        var game = $"{mod}.{name}";
        if (t.Game is not { } place)
        {
            // compiled where it is used, in a tree; what is wrong with it is said here
            if (t.RequireAll && t.Requires is not { Count: > 0 }) throw new BlockError(ctx.Block, "requireAll", "there is nothing it requires");
            low.Json["part"] = name;
            return new BlockHandle<TalentBlock>(mod, name, guid, name, t);
        }
        if (t.MaxLevel > 7) throw new BlockError(ctx.Block, "maxLevel", $"{t.MaxLevel}: the game's tree keeps at most 7 levels");
        if (t.RequireAll) throw new BlockError(ctx.Block, "requireAll", "the game's tree unlocks along any one link: a tree of your own has this");
        if (t.PointsSpent > 0) throw new BlockError(ctx.Block, "pointsSpent", "the game's tree has no tiers: a tree of your own has this");
        if (t.CharacterLevel > 0) throw new BlockError(ctx.Block, "characterLevel", "the game's tree has no level requirement: a tree of your own has this");
        if (t.Column != null || t.Row != null) throw new BlockError(ctx.Block, t.Column != null ? "column" : "row", "a node of the game's tree has no grid cell: say game.offset or game.position");
        if (place.Class is not ("Warrior" or "Mage" or "Ranger" or "Neutral")) throw new BlockError(ctx.Block, "game.class", $"\"{place.Class}\" is not Warrior, Mage, Ranger or Neutral");
        if (place.Offset is { Length: not 2 }) throw new BlockError(ctx.Block, "game.offset", "takes [x, y]");
        if (place.Position is { Length: not 2 }) throw new BlockError(ctx.Block, "game.position", "takes [x, y]");
        var from = new List<string>(place.From ?? []);
        for (var i = 0; i < (t.Requires?.Count ?? 0); i++)
        {
            var r = t.Requires![i];
            var path = $"requires[{i}]";
            if (r.StartsWith("0x", StringComparison.Ordinal) || Guid.TryParse(r, out _))
            {
                from.Add(r);
                continue;
            }
            var other = ctx.Find(r, path) as BlockHandle<TalentBlock> ?? throw new BlockError(ctx.Block, path, $"no talent \"{r}\" in this mod: a talent by name, or a game node by \"0x<id>\"");
            if (other.Block.Game == null) throw new BlockError(ctx.Block, path, $"\"{r}\" has no place in the game's tree");
            from.Add(other.GameName);
        }
        if (from.Count == 0 && place.Position == null) throw new BlockError(ctx.Block, "game.from", "a node hangs on the tree: say what leads to it, a game node by \"0x<id>\"");
        if (from.Count == 0) throw new BlockError(ctx.Block, "game.from", "a node nothing leads to can never be unlocked: say what leads to it, a game node by \"0x<id>\"");

        var program = TalentProgram(ctx, low, key, t, "", gameTree: true);
        var node = new JsonObject
        {
            ["guid"] = guid.ToString(), ["name"] = game, ["type"] = "PassiveSkill", ["class"] = place.Class, ["like"] = TalentLike, ["program"] = program.ToString(),
            ["cost"] = t.Cost, ["maxLevel"] = t.MaxLevel > 1 ? t.MaxLevel : 0,
            ["from"] = new JsonArray(from.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        };
        if (t.MaxLevel > 1) node["scales"] = new JsonObject { [$"0x{EffectEmitter.CfgLevel:x8}"] = 1.0 };
        if (place.To is { Count: > 0 } to) node["to"] = new JsonArray(to.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        if (place.Offset is { } offset) node["offset"] = new JsonArray(offset[0], offset[1]);
        if (place.Position is { } position) node["position"] = new JsonArray(position[0], position[1]);
        if (t.Icon is { } icon)
        {
            var dir = BlocksBridge.ModDirectory(mod);
            node["iconFile"] = dir.Length > 0 ? Path.Combine(dir, icon) : icon;
        }
        var perLevel = t.MaxLevel > 1 && t.Modifies.Count > 0 ? StatsText(t.Modifies) : null;
        var texts = new JsonObject { ["*"] = NodeText(t.Name, TalentDescription(t), perLevel) };
        foreach (var (language, x) in t.Texts ?? new Dictionary<string, ItemTexts>()) texts[language] = NodeText(x.Name ?? t.Name, x.Description ?? TalentDescription(t), perLevel);
        node["texts"] = texts;
        low.Json["node"] = node.DeepClone();
        if (t.Unlocks is { } unlocks)
        {
            var ability = AbilityRuntime.IdOf(ctx, unlocks, "unlocks");
            low.AfterCommit(() => AbilityRuntime.GateOn(ability, BlockIds.Fnv(guid), t.Name));
            low.Json["unlocks"] = ability;
        }
        // A dedicated server has read its tree by now: kiln.skills keeps the definition, and it is in from the next start. The same node again would only take it out of
        // the tree's state until then: kiln.skills read it at boot from what it kept, so it is left as it is.
        var document = new JsonObject { ["mod"] = mod, ["nodes"] = new JsonArray(node.DeepClone()) };
        var block = ctx.Block;
        low.AfterCommit(() =>
        {
            if (Kept(mod, guid, node)) return;
            foreach (var r in GameTree.DefineJson(document.ToJsonString()))
                if (r.Error is { Length: > 0 } error) lock (ctx.Mod.Errors) ctx.Mod.Errors.Add($"block {block}: {error}");
        });
        return new BlockHandle<TalentBlock>(mod, name, guid, game, t);
    }

    /// <summary>kiln.skills holds this node already, as it is now: from what it kept of an earlier start (kiln/data/skills/defined/&lt;mod&gt;.json).</summary>
    private static bool Kept(string mod, Guid guid, JsonObject node)
    {
        var dir = BlocksBridge.ModDirectory(mod);
        if (dir.Length == 0 || !GameTree.Definitions.Any(d => d.Guid == guid && d.State is not ("error" or "orphaned"))) return false;
        try
        {
            var file = Path.GetFullPath(Path.Combine(dir, "..", "..", "data", "skills", "defined", mod + ".json"));
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file))?["nodes"] is not JsonArray kept) return false;
            var mine = Canonical(JsonNode.Parse(node.ToJsonString()));
            return kept.Any(k => (string?)k?["guid"] == guid.ToString() && Canonical(k) == mine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    // One text for the same JSON however it was written: keys in order, numbers as numbers.
    private static string Canonical(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{Canonical(kv.Value)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        null => "null",
        _ => n.ToJsonString(),
    };

    private static JsonObject NodeText(string name, string description, string? perLevel)
    {
        var text = new JsonObject { ["name"] = name, ["description"] = description };
        if (perLevel != null) text["perLevel"] = perLevel;
        return text;
    }

    private static UiText Text(string fallback, IReadOnlyDictionary<string, ItemTexts>? texts, Func<ItemTexts, string?> of)
    {
        if (texts is not { Count: > 0 }) return fallback;
        var all = new Dictionary<string, string> { ["*"] = fallback };
        foreach (var (language, x) in texts)
            if (of(x) is { } t) all[language] = t;
        return new UiText(all);
    }

    private static BlockHandle DefineTree(BlockContext ctx, Lowered low, string name, TalentTreeBlock tree)
    {
        RequireName(ctx, tree.Name, "a tree");
        ctx.Need(ClientsLevel.Mod, null, "a player's game draws the tree from the same definition and icons");
        if (tree.Nodes.Count == 0) throw new BlockError(ctx.Block, "nodes", "a tree with no nodes");
        var key = tree.Was ?? name;
        var mod = ctx.Mod.Id;
        var id = $"{mod}:{key}";
        var points = TalentPoints.Own(1, 1);
        if (tree.Points is { } p)
        {
            if (p.ShareOfGame is { } share)
            {
                if (share is < 0 or > 1) throw new BlockError(ctx.Block, "points.shareOfGame", $"{share} is outside 0..1");
                points = TalentPoints.ShareOfGame(share);
            }
            else
            {
                if (p.Start < 0) throw new BlockError(ctx.Block, "points.start", $"{p.Start} is less than 0");
                if (p.PerLevel < 0) throw new BlockError(ctx.Block, "points.perLevel", $"{p.PerLevel} is less than 0");
                points = TalentPoints.Own(p.Start, p.PerLevel);
            }
        }

        var parts = new List<(string Written, string Save, TalentBlock Block, string Path)>();
        foreach (var (written, part) in tree.Nodes)
        {
            var path = $"nodes[{written}]";
            if (!ValidName(written)) throw new BlockError(ctx.Block, path, "a node's name takes letters, digits, '-', '_' and '.'");
            var t = ctx.Part(part, path);
            CheckTalent(ctx, t, $"{path}.");
            if (t.Game != null) throw new BlockError(ctx.Block, $"{path}.game", "this node is in this tree: a place in the game's tree is for a talent on its own");
            if (t.MaxLevel > 99) throw new BlockError(ctx.Block, $"{path}.maxLevel", $"{t.MaxLevel} is more than 99");
            parts.Add((written, t.Was ?? written, t, path));
        }
        var saves = parts.ToDictionary(x => x.Written, x => x.Save);

        // What leads to a node decides its row; nodes in a row go side by side.
        var rows = new Dictionary<string, int>();
        int RowOf(string written, HashSet<string> walking)
        {
            if (rows.TryGetValue(written, out var known)) return known;
            var part = parts.First(x => x.Written == written);
            if (!walking.Add(written)) throw new BlockError(ctx.Block, $"{part.Path}.requires", $"\"{written}\" requires itself, by way of what it requires");
            var row = part.Block.Row ?? 0;
            if (part.Block.Row == null)
                foreach (var r in part.Block.Requires ?? [])
                    if (saves.ContainsKey(r)) row = Math.Max(row, RowOf(r, walking) + 1);
            walking.Remove(written);
            return rows[written] = row;
        }
        var cells = new Dictionary<(int, int), string>();
        var nodes = new List<TalentNode>();
        foreach (var (written, save, t, path) in parts)
        {
            for (var i = 0; i < (t.Requires?.Count ?? 0); i++)
                if (!saves.ContainsKey(t.Requires![i]))
                    throw new BlockError(ctx.Block, $"{path}.requires[{i}]", $"\"{t.Requires[i]}\" is no node of this tree; it has: {string.Join(", ", saves.Keys)}");
            var row = RowOf(written, new HashSet<string>());
            var column = t.Column ?? Enumerable.Range(0, 32).First(c => !cells.ContainsKey((c, row)));
            if (row is < 0 or > 31 || column is < 0 or > 31) throw new BlockError(ctx.Block, $"{path}.column", $"cell ({column}, {row}) is outside the grid, 0..31");
            if (cells.TryGetValue((column, row), out var other)) throw new BlockError(ctx.Block, $"{path}.column", $"\"{other}\" is at column {column}, row {row} already");
            cells[(column, row)] = written;

            var effects = new List<TalentEffect> { TalentEffect.LevelledProgram(TalentProgram(ctx, low, $"{key}.{save}", t, $"{path}."), $"0x{EffectEmitter.CfgLevel:x8}") };
            if (t.Unlocks is { } unlocks) effects.Add(TalentEffect.Ability(AbilityRuntime.IdOf(ctx, unlocks, $"{path}.unlocks")));
            nodes.Add(new TalentNode
            {
                Id = save, Name = Text(t.Name, t.Texts, x => x.Name), Description = Text(TalentDescription(t), t.Texts, x => x.Description), Column = column, Row = row, Cost = t.Cost, MaxLevel = t.MaxLevel,
                Requires = (t.Requires ?? []).Select(r => saves[r]).ToArray(), RequireAll = t.RequireAll, PointsSpentRequired = t.PointsSpent, LevelRequired = t.CharacterLevel, Icon = t.Icon, Effects = effects,
                // the block's texts say what it does; its effects are a generated program and the ability it unlocks, nothing a player reads
                ListEffects = false,
            });
        }
        var definition = new TalentTree
        {
            Id = id, Name = tree.Name, Description = tree.Description == null ? null : new UiText(tree.Description), Points = points, Nodes = nodes, MenuEntry = tree.Menu ?? tree.Name, Key = tree.Key, AllowRespec = tree.Respec,
            Directory = BlocksBridge.ModDirectory(mod),
        };
        low.Json["tree"] = new JsonObject
        {
            ["id"] = id, ["name"] = tree.Name, ["points"] = points.ToString(), ["menu"] = tree.Menu ?? tree.Name, ["key"] = tree.Key, ["respec"] = tree.Respec,
            ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode?)new JsonObject
            {
                ["id"] = n.Id, ["name"] = n.Name.ToString(), ["description"] = n.Description?.ToString(), ["column"] = n.Column, ["row"] = n.Row, ["cost"] = n.Cost, ["maxLevel"] = n.MaxLevel,
                ["requires"] = new JsonArray(n.Requires.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()), ["requireAll"] = n.RequireAll,
            }).ToArray()),
        };
        low.AfterCommit(() => Skills.Register(definition));
        return new BlockHandle<TalentTreeBlock>(mod, name, BlockIds.Derive(mod, "tree", key), id, tree);
    }
}
