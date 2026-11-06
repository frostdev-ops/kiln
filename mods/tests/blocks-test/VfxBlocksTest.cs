// SPDX-License-Identifier: MIT
// The routes a VFX takes from a block: player-free, on a dedicated server, which has no VFX and carries the references for its clients. What shows on a
// screen is a client's to say. With kiln/config/vfx.json {"sim": true} the same run goes through the builder too, and tells registered from pending.
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksTest;

public sealed partial class BlocksTestMod
{
    private const string Fireball = "Explosion_Player_Spell_Fireball_1";
    private readonly List<ImpactSpawnEvent> _spawnsSeen = new();
    private readonly List<(uint Tag, float X, float Z)> _vfxCues = new();

    private static VfxBlock Small => new() { Particles = [new ParticleBlock { Burst = 2 }] };

    // This mod says clients "kiln": a game VFX by name needs no more than that.
    private void DefineVfxBlocks()
    {
        Blocks.Define("game-fx-hit", new EffectBlock { Vfx = VfxRef.Game(Fireball), Damage = Damage.Blunt(1), Flinch = Flinch.None });
        Blocks.Define("game-fx-seconds", new EffectBlock { Vfx = VfxRef.Game(Fireball) with { Seconds = 2.5, Offset = new System.Numerics.Vector3(0, 1, 0) } });
        Blocks.Define("game-fx-area", new EffectBlock { On = EffectOn.Caster, Area = new AreaBlock { Radius = 2, Vfx = VfxRef.Game(Fireball), Effects = [new EffectBlock { Heal = 1 }] } });
        Blocks.Define("game-fx-status", new StatusBlock
        {
            Name = "Game Fx",
            Seconds = 3,
            Vfx = VfxRef.Game(Fireball) with { Slot = VfxSlot.Chest, Offset = new System.Numerics.Vector3(0, 0.5f, 0) },
            OnApply = [new EffectBlock { Vfx = VfxRef.Game(Fireball) }],
            OnTick = [new EffectBlock { Vfx = VfxRef.Game(Fireball) with { Slot = VfxSlot.Head } }],
            TickEvery = 1,
        });
        Blocks.Define("game-fx-bolt", new SpellBlock
        {
            Name = "Game Fx Bolt",
            Cast = new CastBlock { ManaCost = 1, Vfx = VfxRef.Game(Fireball) },
            Delivery = new ProjectileBlock { Vfx = VfxRef.Game(Fireball), OnImpact = [new EffectBlock { Vfx = VfxRef.Game(Fireball) }] },
        });
        Blocks.Define("game-fx-wand", new WandBlock
        {
            Name = "Game Fx Wand",
            Primary = new WandAttack { Cast = new CastBlock { ManaCost = 1, Vfx = VfxRef.Game(Fireball) }, Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Vfx = VfxRef.Game(Fireball) }] } },
        });
        Blocks.Define("no-such-game-vfx", new EffectBlock { Vfx = VfxRef.Game("No_Such_Template") });
        Blocks.Define("fx-with-parameters", new StatusBlock
        {
            Name = "With Parameters",
            Seconds = 1,
            OnApply = [new EffectBlock { Vfx = VfxRef.Game(Fireball) with { Parameters = new() { ["radius"] = 3f, ["color"] = "#ff0000", ["targetPosition"] = new System.Numerics.Vector3(1, 2, 3), ["count"] = VfxValue.Int(7) } } }],
        });
    }

    private static string Refused(Func<object> define)
    {
        try
        {
            define();
            return "";
        }
        catch (BlockError e)
        {
            return e.Message;
        }
    }

    private static ModBlocks SampleMod => Blocks.ModFor(Sample, () => 0);

    private void VfxMistakes()
    {
        void Says(string what, string message, params string[] words) =>
            Check(words.All(w => message.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{what}: \"{message}\"");

        // the clients rule
        Says("a new VFX in a clients kiln mod", Refused(() => Blocks.Define("mine", Small)), "vfx mine", "KilnClients", "mod");
        Says("naming another mod's new VFX in a clients kiln mod", Refused(() => Blocks.Define("theirs", new EffectBlock { Vfx = $"{Sample}:frost_burst" })), "effect theirs", "vfx", "KilnClients", "mod");

        // names
        Says("a VFX that is nothing", Refused(() => Blocks.Define("n1", new EffectBlock { Vfx = "no-such-vfx" })), "effect n1", "effect[0].vfx", "no-such-vfx", "game:no-such-vfx");
        Says("a VFX field naming a status", Refused(() => Blocks.Define("n2", new EffectBlock { Vfx = "hold" })), "effect n2", "\"hold\" is a status");

        // a look has no route yet
        Says("a look's VFX", Refused(() => Blocks.Define("look", new VisualBlock { Vfx = VfxRef.Game(Fireball) })), "visual look", "vfx", "cannot carry");

        // the blocks themselves, by field, on the side with no builder
        ModBlocks m = SampleMod;
        BlockHandle D(string name, VfxBlock b) => Compiler.Define(m, name, b);
        Says("nothing to draw", Refused(() => D("v1", new VfxBlock())), "vfx v1", "particles", "nothing to draw");
        Says("spawns nothing", Refused(() => D("v2", new VfxBlock { Particles = [new ParticleBlock { Lifetime = 1f }] })), "particles[0]", "burst or a rate");
        Says("maxCount", Refused(() => D("v3", new VfxBlock { Particles = [new ParticleBlock { Burst = 1, MaxCount = 4097 }] })), "particles[0].maxCount", "4097");
        Says("a loop with no duration", Refused(() => D("v4", new VfxBlock { Particles = [new ParticleBlock { Rate = 5, Loop = true }] })), "particles[0].loop", "duration");
        Says("a lifetime of 0", Refused(() => D("v5", new VfxBlock { Particles = [new ParticleBlock { Burst = 1, Lifetime = 0f }] })), "particles[0].lifetime", "above 0");
        Says("a colour", Refused(() => D("v6", new VfxBlock { Light = new LightBlock { Color = "blue" } })), "light.color", "#rrggbb");
        Says("not a PNG", Refused(() => D("v7", new VfxBlock { Particles = [new ParticleBlock { Burst = 1, Texture = "vfx/a.jpg" }] })), "particles[0].texture", "PNG");

        Says("a VFX where an effect goes", Refused(() => Compiler.Define(m, "e3", new EffectBlock { Then = ["frost_burst"] })), "effect e3", "frost_burst", "not an effect");

        // what runs until stopped has no end on an effect
        Says("a loop on an effect", Refused(() => Compiler.Define(m, "e1", new EffectBlock { Vfx = "frost_aura" })), "effect e1", "effect[0].vfx", "until it is stopped");
        Check(Refused(() => Compiler.Define(m, "e2", new EffectBlock { Vfx = new VfxRef("frost_aura") { Seconds = 2 } })) == "", "a loop on an effect with Seconds has its end");

        // named parameters: what can't carry them is refused, never ignored
        var named = VfxRef.Game(Fireball) with { Parameters = new() { ["radius"] = 3f } };
        Says("parameters on a cast", Refused(() => Blocks.Define("p1", new SpellBlock { Name = "P", Cast = new CastBlock { ManaCost = 1, Vfx = named }, Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Vfx = VfxRef.Game(Fireball) }] } })), "spell p1", "cast.vfx.parameters", "cannot set");
        Says("parameters on a projectile", Refused(() => Blocks.Define("p2", new SpellBlock { Name = "P", Cast = new CastBlock { ManaCost = 1 }, Delivery = new ProjectileBlock { Vfx = named, OnImpact = [new EffectBlock { Vfx = VfxRef.Game(Fireball) }] } })), "spell p2", "delivery.vfx.parameters", "cannot set");
        Says("parameters on a status", Refused(() => Blocks.Define("p3", new StatusBlock { Name = "P", Seconds = 1, Vfx = named })), "status p3", "vfx.parameters", "cannot set");
        Says("parameters on a mod's VFX", Refused(() => Compiler.Define(m, "p4", new EffectBlock { Vfx = new VfxRef("frost_burst") { Parameters = new() { ["radius"] = 3f } } })), "effect p4", "vfx.parameters", "no named parameters");
        Says("a parameter that is no colour", Refused(() => Blocks.Define("p5", new EffectBlock { Vfx = VfxRef.Game(Fireball) with { Parameters = new() { ["color"] = "red" } } })), "effect p5", "parameters.color", "colour");

        // the builder's report, as it reaches an author: the budget comes from the boot build or from this
        var guid = BlockIds.Derive(Sample, "vfx", "b");
        var over = Compiler.ReadVfxReport("""{"ok":false,"blocks":[],"errors":[],"refusedMods":["fx: over the game's limits, none of its 1 VFX are registered (mesh memory (bytes): needs 300000, 241844 left)"],"warnings":[]}""", "b", guid);
        Says("over the mesh memory", over.Problem?.Text ?? "", "mesh memory", "241844 left");
        var wrong = Compiler.ReadVfxReport("""{"ok":false,"blocks":[],"errors":["fx: code: vfx.b.particles[0].maxCount: 4097 is outside 1..4096"],"refusedMods":[],"warnings":[]}""", "b", guid);
        Check(wrong.Problem?.Field == "particles[0].maxCount" && wrong.Problem?.Text == "4097 is outside 1..4096", $"a builder's error by field: {wrong.Problem}");
        var pending = Compiler.ReadVfxReport($$"""{"ok":true,"blocks":[{"mod":"fx","name":"b","guid":"{{guid}}","status":"pending"}],"errors":[],"refusedMods":[],"warnings":[]}""", "b", guid);
        Check(pending.Status == VfxBlockStatus.Pending && pending.Problem == null, $"pending reads as pending: {pending.Status}");
        var registered = Compiler.ReadVfxReport($$"""{"ok":true,"blocks":[{"mod":"fx","name":"b","guid":"{{guid}}","status":"registered"}],"errors":[],"refusedMods":[],"warnings":[]}""", "b", guid);
        Check(registered.Status == VfxBlockStatus.Registered, $"registered reads as registered: {registered.Status}");
        var refused = Compiler.VfxRefusals("""{"refusedMods":["fx: over the game's limits, none of its 2 VFX are registered (VFX: needs 2, 1 left)","fx2: other"]}""", "fx").ToList();
        Check(refused.Count == 1 && refused[0].Contains("1 left", StringComparison.Ordinal), $"the start's refusal of this mod, not of another: {string.Join("; ", refused)}");
        var other = Compiler.ReadVfxReport("""{"ok":true,"blocks":[{"name":"b","guid":"00000000-0000-0000-0000-000000000001","status":"registered"}],"errors":[],"refusedMods":[],"warnings":[]}""", "b", guid);
        Check(other.Problem != null, $"a GUID the two sides disagree on is an error, not a silent miss: {other.Problem}");

        // the count on the side with no builder: a client would refuse the whole mod
        if (!HasBuilder)
        {
            var had = Blocks.AllMods.Sum(x => x.Handles.Values.Count(h => h is BlockHandle<VfxBlock>));
            var bulk = Blocks.ModFor("blocks-vfx-bulk", () => m.Owner);
            bulk.ManifestClients = "mod";
            var last = "";
            for (var i = had; i <= Compiler.VfxRoom; i++) last = Refused(() => Compiler.Define(bulk, $"bulk{i}", Small));
            Says("one VFX over the game's limit", last, "bulk", "room for 473");
            bulk.Handles.Clear();
        }
    }

    private static bool HasBuilder => Vfx.Status().Native;

    private void VfxIds()
    {
        foreach (var name in new[] { "frost_burst", "frost_trail", "frost_charge", "frost_aura", "frost_burst-json" })
        {
            var h = Blocks.Find(name, Sample) as BlockHandle<VfxBlock>;
            Check(h != null && h.Guid == BlockIds.Derive(Sample, "vfx", name) && h.Guid == VfxId.Named(Sample, name).Guid, $"{name}: VfxId.Named and the handle agree: {h?.Guid}");
        }
        var csharp = Blocks.Find("frost_burst", Sample) as VfxBlockHandle;
        Check(csharp != null && ((VfxId)csharp).Guid == csharp.Guid, "a handle from Define is a VfxId for Vfx's calls");
        var renamed = (VfxBlockHandle)Compiler.Define(SampleMod, "new-name", Small with { Was = "old-name" });
        Check(renamed.Guid == BlockIds.Derive(Sample, "vfx", "old-name"), $"Was keeps the id: {renamed.Guid}");
        Check(Blocks.Find("frost_burst-json", Sample) is VfxBlockHandle, "a block from a data file has the same handle");
    }

    // The same shape: each C# block and its data twin are the same definition.
    private void VfxTwins()
    {
        foreach (var name in new[] { "frost_burst", "frost_charge", "frost_trail", "frost_aura" })
        {
            var cs = JsonNode.Parse(Blocks.Lowered(name, Sample)!)!;
            var json = JsonNode.Parse(Blocks.Lowered($"{name}-json", Sample)!)!;
            var a = cs["definition"]?.ToJsonString();
            var b = json["definition"]?.ToJsonString();
            Check(a != null && a == b, $"{name}: C# and blocks/frostlance.json are the same definition: {a}");
            Check(cs["seconds"]?.ToJsonString() == json["seconds"]?.ToJsonString() && cs["loops"]?.ToJsonString() == json["loops"]?.ToJsonString(), $"{name}: and the same length: {cs["seconds"]}, loops {cs["loops"]}");
            // what the offline builder and the managed rule compare against
            Directory.CreateDirectory(Path.Combine(Context.DataDirectory, "vfx-definitions"));
            File.WriteAllText(Path.Combine(Context.DataDirectory, "vfx-definitions", $"{name}.json"), a);
        }
    }

    // A lab told to behave as a client without kiln.vfx: everything still loads, runs and ends, and the data names no VFX.
    private void NoVfxClient()
    {
        Check(Content.Templates.Get($"{Sample}.frostbolt.projectile")!.Get("VfxComponentResource.vfx").Guid == Guid.Empty, "no kiln.vfx: flight names no VFX its game lacks");
        Check(Content.Sequences.Get($"{Sample}.frostbolt.cast")!.Get("events[VfxNotifierEvent#0].vfx").Guid == Guid.Empty, "cast: the same");
        Check(Content.Buffs.Get($"{Sample}.chilled")!.Get("whileApplied[0].vfx").Guid == Guid.Empty, "status: the same");
        var carrier = Content.Templates.Get((string?)(JsonNode.Parse(Blocks.Lowered("frostbolt", Sample)!)!["templates"] as JsonArray)!.First(t => ((string?)t?["name"])!.Contains("frost_burst", StringComparison.Ordinal))!["name"] ?? "?");
        Check(carrier?.Get("VfxComponentResource.vfx").Guid == Guid.Empty, "hit: the carrier's too, though it is still there to spawn");
    }

    private static bool NoVfxLab(string dataDirectory) => File.Exists(Path.GetFullPath(Path.Combine(dataDirectory, "..", "content", "no-vfx-client")));

    // What the generated data holds. A dedicated server has no VFX: all of this is references it carries.
    private void VfxData_()
    {
        var charge = Blocks.Find("frost_charge", Sample)!.Guid;
        var trail = Blocks.Find("frost_trail", Sample)!.Guid;
        var aura = Blocks.Find("frost_aura", Sample)!.Guid;
        var burst = Blocks.Find("frost_burst", Sample)!.Guid;

        var seq = Content.Sequences.Get($"{Sample}.frostbolt.cast");
        Check(seq?.Get("events[VfxNotifierEvent#0].vfx").Guid == charge, $"cast: the sequence's VFX event names frost_charge: {seq?.Get("events[VfxNotifierEvent#0].vfx").Guid}");
        Check(seq?.Get("events[VfxNotifierEvent#0].slot").Number == (double)Slot.HandR, $"at the hand: slot {seq?.Get("events[VfxNotifierEvent#0].slot").Number}");
        Check(seq?.Get("events[VfxNotifierEvent#0].attach").Number == 1, "following it");
        var game = Content.Sequences.Get(CastAnimation.FastBolt.Sequence)!;
        Check(Math.Abs(seq!.Get("events[VfxNotifierEvent#0].time").Number - game.Get("events[VfxNotifierEvent#0].time").Number) < 1e-6, "at the moment the motion's own VFX has");
        Check(game.Get("events[VfxNotifierEvent#0].vfx").Guid != charge, "and the game's sequence still has its own");

        var flight = Content.Templates.Get($"{Sample}.frostbolt.projectile")!;
        Check(flight.Get("VfxComponentResource.vfx").Guid == trail, $"flight: the projectile's template names frost_trail: {flight.Get("VfxComponentResource.vfx").Guid}");
        var impact = Content.Templates.Get($"{Sample}.frostbolt.impact")!;
        Check(impact.Get("VfxComponentResource.vfx").Guid == Guid.Empty, "hit: the impact's own burst is silent, as an effect says what shows");

        var status = Content.Buffs.Get($"{Sample}.chilled")!;
        Check(status.Get("whileApplied[0].vfx").Guid == aura, $"status: its buff's whileApplied names frost_aura: {status.Get("whileApplied[0].vfx").Guid}");
        Check((status.GetJson("whileApplied")?["value"] as JsonArray)?.Count == 1, "and only that");
        Check(Math.Abs(status.Get("whileApplied[0].offset.orientationOffset.w").Number - 1) < 1e-6, "with an identity rotation");

        var low = JsonNode.Parse(Blocks.Lowered("frostbolt", Sample)!)!;
        var carrierDef = (low["templates"] as JsonArray)?.FirstOrDefault(t => ((string?)t?["name"])?.Contains("frost_burst", StringComparison.Ordinal) == true);
        var carrier = Content.Templates.Get((string?)carrierDef?["name"] ?? "?");
        Check(carrier != null, $"hit: a hidden template carries frost_burst: {carrier?.Name}");
        Check(carrier?.Get("VfxComponentResource.vfx").Guid == burst, $"it names the VFX: {carrier?.Get("VfxComponentResource.vfx").Guid}");
        var seconds = (double?)JsonNode.Parse(Blocks.Lowered("frost_burst", Sample)!)!["seconds"];
        Check(Math.Abs((carrier?.Get("LifeTime.lifeTime").Number ?? 0) - Math.Max(seconds ?? 0, 1)) < 1e-3, $"it lives as long as the VFX takes: {carrier?.Get("LifeTime.lifeTime").Number} s, the block's {seconds}");
        var sword = JsonNode.Parse(Blocks.Lowered("frost-edge", Sample)!)!;
        Check((sword["templates"] as JsonArray)?.Any(t => (string?)t?["name"] == carrier?.Name) == true, "the sword's on-hit VFX rides the same carrier");

        // a game VFX by name: known once the game's data is there
        var mine = Content.Sequences.Get("blocks-test.game-fx-bolt.cast")!;
        var known = VfxId.Game(Fireball).Guid;
        Check(known != Guid.Empty && known.ToString().StartsWith("b0b78a7d", StringComparison.Ordinal), $"the game's name resolves on a server, which has the template and no VFX: {known}");
        Check(mine.Get("events[VfxNotifierEvent#0].vfx").Guid == known, $"cast: by name: {mine.Get("events[VfxNotifierEvent#0].vfx").Guid}");
        Check(Content.Templates.Get("blocks-test.game-fx-bolt.projectile")!.Get("VfxComponentResource.vfx").Guid == known, "flight: by name");
        Check(Content.Sequences.Get("blocks-test.game-fx-wand.primary")?.Get("events[VfxNotifierEvent#0].vfx").Guid == known, "a wand attack's cast: by name");
        var gs = Content.Buffs.Get("blocks-test.game-fx-status")!;
        Check(gs.Get("whileApplied[0].vfx").Guid == known, "status: by name");
        Check(gs.Get("whileApplied[0].slot").Number == (double)Slot.Chest && Math.Abs(gs.Get("whileApplied[0].offset.localOffset.y").Number - 0.5) < 1e-5, "on its slot and offset");
        Check(gs.Get("onCreate[0].vfx").Guid == known, "OnApply's VFX is the buff's own onCreate");

        var gl = JsonNode.Parse(Blocks.Lowered("game-fx-seconds")!)!;
        var gc = Content.Templates.Get((string?)((gl["templates"] as JsonArray)?[0]?["name"]) ?? "?");
        Check(gc?.Get("VfxComponentResource.vfx").Guid == known && Math.Abs(gc!.Get("LifeTime.lifeTime").Number - 2.5) < 1e-3, $"a game VFX's carrier with Seconds lives 2.5 s: {gc?.Get("LifeTime.lifeTime").Number}");
        Check(Math.Abs(gc!.Get("VfxComponent.attachmentOffset.localOffset.y").Number - 1) < 1e-5, "and takes its offset");

        var mineErrors = Blocks.Report().TryGetValue("blocks-test", out var mod) ? mod.Errors : [];
        var none = mineErrors.FirstOrDefault(e => e.Contains("\"No_Such_Template\"", StringComparison.Ordinal));
        Check(none != null && none.Contains("no-such-game-vfx", StringComparison.Ordinal) && none.Contains("vfx", StringComparison.Ordinal), $"a game VFX's name that is nothing is an error by block and field: \"{none}\"");

        // a spell of the sample's whose looks are all the game's, by name, from a data file
        Check(Blocks.Report()[Sample].Errors.Count == 0, $"the sample has no errors: {string.Join("; ", Blocks.Report()[Sample].Errors)}");
        Check(Content.Sequences.Get($"{Sample}.emberlance.cast")?.Get("events[VfxNotifierEvent#0].vfx").Guid == VfxId.Game("Attack_Player_Staff_FixedAnim_Fireball_fast").Guid, "by a sequence's name: the VFX it plays");
        Check(Content.Templates.Get($"{Sample}.emberlance.projectile")?.Get("VfxComponentResource.vfx").Guid == VfxId.Game("Projectile_Player_Staff_Fireball_1").Guid, "by a template's name: the VFX it carries");
        Check(Content.Buffs.Get($"{Sample}.singed")?.Get("whileApplied[0].vfx").Guid == known, "and in a data file's status");

        // the content sync carries the references to clients
        var sync = BlockRuntime.SyncExport() ?? "";
        Check(sync.Contains(trail.ToString(), StringComparison.Ordinal) && sync.Contains(burst.ToString(), StringComparison.Ordinal), "the content sync carries the VFX references");
        Check(sync.Contains("whileApplied", StringComparison.Ordinal) && sync.Contains(aura.ToString(), StringComparison.Ordinal), "and a buff's list, whole");
        File.WriteAllText(Path.Combine(Context.DataDirectory, "sync-export.json"), sync);

        // for tools/kfc: the bytes the game reads
        var dump = Path.Combine(Context.DataDirectory, "vfx-resources");
        Directory.CreateDirectory(dump);
        void Save(string type, GameDataEntry? e)
        {
            if (e == null) return;
            if (Assets.ReadResource(new AssetKey(e.Guid, type)) is { } b) File.WriteAllBytes(Path.Combine(dump, $"{e.Guid}.{type.Replace("::", "_")}.bin"), b);
            else Info($"no resource bytes for {e.Name}");
        }
        Save("keen::ecs::TemplateResource", carrier);
        Save("keen::ecs::TemplateResource", flight);
        Save("keen::actor::ActorSequenceResource", Content.Sequences.Get($"{Sample}.frostbolt.cast"));
    }

    private async Task VfxBlocks()
    {
        // Nothing here needs a VFX: a server carries them, with kiln.vfx there or not.
        Info($"kiln.vfx on this side: {HasBuilder}; available {Vfx.Available}");
        VfxMistakes();
        VfxIds();
        VfxTwins();
        var noVfx = NoVfxLab(Context.DataDirectory);
        if (noVfx) NoVfxClient();
        else VfxData_();


        var report = Blocks.Report();
        if (report.TryGetValue("blocks-vfx-many", out var many))
            Check(many.Errors.Count > 0 && many.Errors[0].Contains("over the game's limits", StringComparison.Ordinal) && many.Errors[0].Contains("473 left", StringComparison.Ordinal) && many.Blocks.Count == 0, $"a mod over the game's VFX limit is refused at boot, and its author reads why: \"{many.Errors.FirstOrDefault()}\"");
        if (!HasBuilder)
            Check((Blocks.Find("frost_burst", Sample) as VfxBlockHandle)?.Status == VfxBlockStatus.NotHere, "on a server with no kiln.vfx nothing is built, and the block still has its id");
        else
            await VfxBuilt();

        // hit: the program spawns the carrier, and it ends
        var caster = await Make("1_Player", _at);
        Combat.SetInvulnerable(caster, true);
        var impact = Content.Templates.Get($"{Sample}.frostbolt.impact")!;
        var low = JsonNode.Parse(Blocks.Lowered("frostbolt", Sample)!)!;
        var carrierGuid = Guid.Parse((string)(low["templates"] as JsonArray)!.First(t => ((string?)t?["name"])!.Contains("frost_burst", StringComparison.Ordinal))!["guid"]!);
        _spawnsSeen.Clear();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Abilities.RunImpact(impact.Guid, _at, new ImpactOptions { Root = caster, Tag = "blocks-test" });
        await Steps(8);
        var spawn = _spawnsSeen.FirstOrDefault(s => s.Template == carrierGuid);
        Check(spawn != null && !spawn.Spawned.IsNone, $"the hit spawned the carrier: entity {spawn?.Spawned}, at {spawn?.Position}");
        Check(spawn != null && Math.Abs(spawn.Position.X - _at.X) < 0.01 && Math.Abs(spawn.Position.Z - _at.Z) < 0.01, "where the effect happened");
        if (spawn != null)
        {
            Check(World.Exists(spawn.Spawned), "it exists on the server, which has no VFX");
            var end = System.Diagnostics.Stopwatch.StartNew();
            while (World.Exists(spawn.Spawned) && end.Elapsed.TotalSeconds < 20) await Steps(2);
            var lived = clock.Elapsed.TotalSeconds;
            Check(!World.Exists(spawn.Spawned), $"and it ends, by itself: gone after {lived:0.00} s");
            Check(lived > 0.8 && lived < 6, $"about as long as the VFX takes (the block's 1.x s; a busy machine steps slowly): {lived:0.00} s");
        }

        // an item's on-hit
        var wearer = await Make("1_Player", new WorldPosition(_at.X - 2, _at.Y, _at.Z - 2));
        Combat.SetInvulnerable(wearer, true);
        var sword = Blocks.Find("frost-edge", Sample)!;
        var give = await Items.Give(wearer, sword.GameName, 1);
        await Steps(20);
        var slot = Items.Read(wearer)?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == sword.Id);
        if (slot != null)
        {
            GameTree.GameRequest(wearer, 5, (uint)slot.Slot.Index);
            await Steps(24);
            var program = BlockIds.Fnv(Guid.Parse((string)JsonNode.Parse(Blocks.Lowered("frost-edge", Sample)!)!["onHitProgram"]!));
            _spawnsSeen.Clear();
            var on = await SwingAt(wearer, program, Blocks.Find("chilled", Sample)!.Guid);
            Check(_spawnsSeen.Count(s => s.Template == carrierGuid) == 1, $"item on-hit: one hit, one carrier: {_spawnsSeen.Count(s => s.Template == carrierGuid)} (burst {on.Burst})");
        }
        else Check(false, $"the sword for the on-hit check: {give}");

        // a game VFX on a status's tick: at the slot's place, for its template's length; onCreate plays in the client
        var victim = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 2, _at.Y, _at.Z - 2));
        var gameStatus = Blocks.Find("game-fx-status")!;
        var tickCarrier = JsonNode.Parse(Blocks.Lowered("game-fx-status")!)!["templates"] as JsonArray;
        var tickGuid = Guid.Parse((string)tickCarrier![0]!["guid"]!);
        Check(Content.Templates.Get(tickGuid)?.Get("VfxComponentResource.vfx").Guid == (noVfx ? Guid.Empty : VfxId.Game(Fireball).Guid), "OnTick's VFX has a carrier by name");
        Check(Math.Abs((Content.Templates.Get(tickGuid)?.Get("LifeTime.lifeTime").Number ?? 0) - Content.Templates.Get(Fireball)!.Get("LifeTime.lifeTime").Number) < 1e-3, $"it lives as the named template does: {Content.Templates.Get(tickGuid)?.Get("LifeTime.lifeTime").Number} s");
        _spawnsSeen.Clear();
        await Abilities.ApplyBuff(victim, gameStatus.Guid, source: caster);
        await Steps(25 * 2);
        var ticks = _spawnsSeen.Where(s => s.Template == tickGuid).ToList();
        Check(ticks.Count >= 1, $"each tick spawns it: {ticks.Count}");
        var hereNow = World.Position(victim);
        Check(ticks.Count > 0 && hereNow is { } v && Math.Abs(ticks[0].Position.X - v.X) < 3 && Math.Abs(ticks[0].Position.Z - v.Z) < 3, $"at the target, or its head where it has one: {ticks.FirstOrDefault()?.Position}, target at {hereNow}");
        Check(!_spawnsSeen.Any(s => s.Template != tickGuid && s.Spawned.IsNone == false && Content.Templates.Get(s.Template)?.Name.Contains("Fireball", StringComparison.Ordinal) == true), "and nothing else named after the game's explosion");
        Spawn.Destroy(victim);

        // parameters: not a carrier, a relayed play. A lab's fake client has no player to be near.
        BlockRuntime.CueRadius = -1;
        var withParameters = await Make("Enemy_Vukah_Melee", new WorldPosition(_at.X + 4, _at.Y, _at.Z - 4));
        var relayed = Blocks.Find("fx-with-parameters")!;
        _vfxCues.Clear();
        _spawnsSeen.Clear();
        var sent = Vfx.Status().RelaySent;
        await Abilities.ApplyBuff(withParameters, relayed.Guid, source: caster);
        await Steps(12);
        Check(_vfxCues.Count == 1, $"a VFX with parameters signals the server, once: {_vfxCues.Count}");
        // only this status's program: other mods' tests spawn things in the same world
        var ownProgram = BlockIds.Fnv(Content.Buffs.Get(relayed.GameName)!.Get("impact").Guid);
        Check(_spawnsSeen.Count(x => x.Impact.ProgramId == ownProgram) == 0, "and spawns no carrier");
        Info($"the relay sent {Vfx.Status().RelaySent - sent} message(s) (nobody may be listening here)");
        Commands.Register("blockstest", ctx =>
        {
            _ = Abilities.ApplyBuff(withParameters, relayed.Guid, source: caster);
            ctx.Reply("applied");
        }, usage: "vfxcue", description: "plays the blocks-test effect with parameters again, for a fake client on the relay");
        Info($"VFX checks done: {_pass} PASS, {_fail} FAIL so far");
    }

    // With kiln.vfx there (a simulated game in a lab): a data file's block was read at boot; a block from code is pending, then registered after a restart.
    private async Task VfxBuilt()
    {
        var json = (VfxBlockHandle)Blocks.Find("frost_burst-json", Sample)!;
        Check(json.Status == VfxBlockStatus.Registered, $"a block in a data file is registered from the first start: {json.Status}");
        var code = (VfxBlockHandle)Blocks.Find("frost_burst", Sample)!;
        var marker = Path.Combine(Context.DataDirectory, "vfx-second-start");
        if (File.Exists(marker))
        {
            Check(code.Status == VfxBlockStatus.Registered, $"after a restart, a block from code is registered: {code.Status}");
            File.Delete(marker);
        }
        else
        {
            Check(code.Status == VfxBlockStatus.Pending, $"a block from code is pending on its first start: {code.Status}");
            Check(Blocks.Warnings(Sample).Any(w => w.Contains("frost_burst", StringComparison.Ordinal) && w.Contains("next start", StringComparison.Ordinal)), "and the author is told plainly");
            File.WriteAllText(marker, "");
        }
        var status = Vfx.StatusJson();
        Info($"kiln.vfx: {status}");
        await Steps(1);
    }
}
