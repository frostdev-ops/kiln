// SPDX-License-Identifier: MIT
// Furniture and interactions -> a template clone of a game item's placed thing, and fields of the game's interaction components:
//  - InteractionOfferRuntime.defaultVerbId / defaultSequence: the prompt's loca tag and the sequence the player plays (FNV-1a of its GUID). Template values start every placed entity.
//  - InteractionToggle (+ ServerToggleState): two states, flipped by the guest's sequence (Interaction_Toggle's ToggleInteractionActionEvent), as a door's. Its words are
//    LocaTagReferences: a game word's GUID, or 16 bytes with its id (the game only hashes them).
//  - a prompt of your own: a tag derived from the template's GUID and "kiln:<slot>", which the template's texts answer (kiln.gamedata publishes them to the loca hooks).
//  - InteractionLock: the game's own refusal (an item in the bag, used up or not; a time of day). A player's game shows it locked.
//  - InteractionAttachment.guestOffset: where a seat's or a bed's guest sits or lies.
// What the game's components can't say (effects on the player, checks of their state, a bed as a spawn point, sleeping through the night) is InteractRuntime's, on the server.
using System.Text.Json.Nodes;

namespace Kiln;

internal static partial class Compiler
{
    // The game items each kind starts from: their templates have the parts that make the kind (docs/re/building.md 2.5).
    private const string ContainerStarter = "Prop_Storage_24_T1_Box", DoorStarter = "Prop_Decoration_T0_Door_Wooden", BedStarter = "Prop_Decoration_Comfort2_T1_Bed", SeatStarter = "Prop_Decoration_Comfort1_T1_Chair";
    private const int MaxContainerSlots = 120;

    /// <summary>Furniture, interactions, shapes, hair and beards. Null: not one of them.</summary>
    private static BlockHandle? DefineFurnitureBlock<T>(BlockContext ctx, Lowered low, string name, T block) where T : class, IBlock => block switch
    {
        ContainerBlock c => DefineFurniture(ctx, low, name, c, ContainerStarter, "a container"),
        DoorBlock d => DefineFurniture(ctx, low, name, d, DoorStarter, "a door"),
        BedBlock b => DefineFurniture(ctx, low, name, b, BedStarter, "a bed"),
        SeatBlock s => DefineFurniture(ctx, low, name, s, SeatStarter, "a seat"),
        InteractBlock i => Part(ctx, low, name, i, (x, b) => CheckInteract(x, b, "")),
        BlueprintBlock b => DefineBlueprint(ctx, low, name, b),
        HairBlock h => DefineBodyLook(ctx, low, name, h.Name, h.Description, h.Look, h.Was, h, BodyLook.Hair),
        BeardBlock b => DefineBodyLook(ctx, low, name, b.Name, b.Description, b.Look, b.Was, b, BodyLook.Beard),
        _ => null,
    };

    private static BlockHandle DefineFurniture(BlockContext ctx, Lowered low, string name, PropBlock b, string kindStarter, string what)
    {
        RequireName(ctx, b.Name, what);
        var look = b.Visual != null ? ctx.Part(b.Visual, "visual") : null;
        var key = b.Was ?? name;
        var mod = ctx.Mod.Id;
        // Its look's item when it names one, else its kind's: what it starts from keeps the parts that make it a bed, a door, ...
        var starter = look?.Like is { } like ? ctx.ItemName(like, "visual.like") : kindStarter;
        var d = new Draft(ctx, low, key, name, starter);
        var placed = look?.Like is { } self ? ctx.Find(self.Value, "visual.like") : null;
        var p = new Placed(d, $"{mod}.{name}.prop", BlockIds.Derive(mod, "template", $"{key}.prop"), placed switch
        {
            StationHandle => $"{placed.Mod}.{placed.Name}.station",
            BlockHandle<PropBlock> or BlockHandle<ContainerBlock> or BlockHandle<DoorBlock> or BlockHandle<BedBlock> or BlockHandle<SeatBlock> => $"{placed.Mod}.{placed.Name}.prop",
            _ => "item:" + starter,
        });
        var kind = new JsonObject { ["kind"] = b.GetType().Name.Replace("Block", "").ToLowerInvariant(), ["starter"] = starter };
        switch (b)
        {
            case ContainerBlock c:
                if (c.Slots is { } slots)
                {
                    if (slots is < 1 or > MaxContainerSlots) throw new BlockError(ctx.Block, "slots", $"{slots} is outside 1..{MaxContainerSlots}");
                    p.Fields["InventorySetup.genericSlotCount"] = slots;
                    p.Fields["InventorySetup.availableSlotCount"] = slots;
                    kind["slots"] = slots;
                }
                if (c.DefaultLoot is { } defaultLoot)
                {
                    var plan = LootPlanOf(ctx, low, defaultLoot, "defaultLoot");
                    kind["defaultLoot"] = plan.ToJson();
                    var (template, block, mod2) = (p.Guid, ctx.Block, ctx.Mod);
                    low.AfterCommit(() => ContainerRuntime.Register(mod2, template, block, plan));
                }
                if (c.Lid is { } lid) ApplyLid(p, ctx, lid, kind);
                break;
            case DoorBlock door:
                ApplyDoor(p, low, ctx, door, kind);
                break;
            case BedBlock bed:
                if (bed.Lies is { } lies) Spot(p, ctx, lies, "lies");
                kind["spawnPoint"] = bed.SpawnPoint;
                kind["skipsNight"] = bed.SkipsNight;
                if (bed.SpawnPoint || bed.SkipsNight)
                {
                    var guid = p.Guid;
                    var (spawn, night, block) = (bed.SpawnPoint, bed.SkipsNight, ctx.Block);
                    low.AfterCommit(() => InteractRuntime.AddBed(ctx.Mod, guid, block, spawn, night));
                }
                break;
            case SeatBlock seat when seat.Sits is { } sits:
                Spot(p, ctx, sits, "sits");
                break;
        }
        low.Json["furniture"] = kind;
        PlacedParts(p, b, starterIsLook: look?.Like != null);
        CommitPlaced(ctx, low, p, b, name, key);
        return b switch
        {
            ContainerBlock c => new BlockHandle<ContainerBlock>(mod, name, d.Guid, d.Game, c),
            DoorBlock x => new BlockHandle<DoorBlock>(mod, name, d.Guid, d.Game, x),
            BedBlock x => new BlockHandle<BedBlock>(mod, name, d.Guid, d.Game, x),
            SeatBlock x => new BlockHandle<SeatBlock>(mod, name, d.Guid, d.Game, x),
            _ => new BlockHandle<PropBlock>(mod, name, d.Guid, d.Game, b),
        };
    }

    // ---- a container's lid, a door's swing and sides ----------------------------------------------------------------

    // The lid is the game's toggle animation (ToggleAnimation: two states of the entity's animation graph), which the container's use flips. A client sight; the template says it.
    private static void ApplyLid(Placed p, BlockContext ctx, LidBlock lid, JsonObject kind)
    {
        var json = new JsonObject { ["moves"] = lid.Moves };
        if (!lid.Moves)
        {
            if (lid.Graph != null || lid.OpenState != null || lid.ShutState != null)
                throw new BlockError(ctx.Block, "lid", "it doesn't move (moves = false), so a graph and states have nothing to do: leave them out");
            // nothing flips a lid that has no toggle animation to play: the container still opens its screen
            if (!p.Remove.Contains("ToggleAnimation")) p.Remove.Add("ToggleAnimation");
            kind["lid"] = json;
            return;
        }
        foreach (var (field, guid) in new[] { ("graph", lid.Graph), ("openState", lid.OpenState), ("shutState", lid.ShutState) })
            if (guid == Guid.Empty) throw new BlockError(ctx.Block, $"lid.{field}", "a GUID of the game's animation graph or one of its states; the empty GUID names nothing");
        if (lid.Graph is { } graph)
        {
            p.Fields["AnimationGraphResourceComponent2.animationGraph"] = graph.ToString();
            json["graph"] = graph.ToString();
        }
        if (lid.Graph != null || lid.OpenState != null || lid.ShutState != null)
        {
            Components(p, "ToggleAnimation");
            var open = lid.OpenState ?? LidBlock.Open;
            var shut = lid.ShutState ?? LidBlock.Shut;
            p.Fields["ToggleAnimation.animationStateOn"] = open.ToString();
            p.Fields["ToggleAnimation.animationStateOff"] = shut.ToString();
            json["openState"] = open.ToString();
            json["shutState"] = shut.ToString();
        }
        kind["lid"] = json;
    }

    // The game's door has two ways to swing, DirectionalInteraction's front and back: each plays its own state of the door's graph, the one on the side the player is. The game's doors
    // that swing one way only play the same state from both. Sides is Kiln's: a player on the other side is declined, as every check of Kiln's declines.
    private static readonly Guid DoorSwingBack = new("03deda2f-3d10-4127-96bb-12d438533e4f");

    private static void ApplyDoor(Placed p, Lowered low, BlockContext ctx, DoorBlock door, JsonObject kind)
    {
        if (door.Opens is { } opens)
        {
            if (!Enum.IsDefined(opens)) throw new BlockError(ctx.Block, "opens", $"{(int)opens} is not both, front or back");
            var (front, back) = opens switch
            {
                DoorSide.Front => (LidBlock.Open, LidBlock.Open),
                DoorSide.Back => (DoorSwingBack, DoorSwingBack),
                _ => (LidBlock.Open, DoorSwingBack),
            };
            p.Fields["DirectionalInteraction.frontInteraction.animationState"] = front.ToString();
            p.Fields["DirectionalInteraction.backInteraction.animationState"] = back.ToString();
            kind["opens"] = opens.ToString().ToLowerInvariant();
            // a look with no directional interaction (a game item that is no door) has nothing to turn
            var (template, block, mod) = (p.Template, ctx.Block, ctx.Mod);
            low.AfterCommit(() => BlockRuntime.AddReady(mod, problem =>
            {
                if (Content.Templates.Get(template) is { } t && !HasComponent(t, "DirectionalInteraction"))
                    problem($"block {block}, opens: {template} has no DirectionalInteraction: start from a game door (visual.like), whose swing it turns");
            }));
        }
        if (door.Sides is { } sides)
        {
            if (!Enum.IsDefined(sides)) throw new BlockError(ctx.Block, "sides", $"{(int)sides} is not both, front or back");
            kind["sides"] = sides.ToString().ToLowerInvariant();
            if (sides != DoorSide.Both)
            {
                var (template, block, mod) = (p.Guid, ctx.Block, ctx.Mod);
                low.AfterCommit(() => InteractRuntime.AddDoor(mod, template, block, sides));
            }
        }
    }

    private static bool HasComponent(GameDataEntry t, string component)
    {
        try { return t.GetJson(component) != null; }
        catch (KeyNotFoundException) { return false; }
    }

    // The guest's offset: in the thing's own axes, turned about its vertical.
    private static void Spot(Placed p, BlockContext ctx, SpotBlock s, string path)
    {
        if (!float.IsFinite(s.At.X) || !float.IsFinite(s.At.Y) || !float.IsFinite(s.At.Z) || Math.Abs(s.At.X) > 10 || Math.Abs(s.At.Y) > 10 || Math.Abs(s.At.Z) > 10)
            throw new BlockError(ctx.Block, $"{path}.at", $"[{s.At.X}, {s.At.Y}, {s.At.Z}]: metres from the thing's origin, within 10 each way");
        if (!double.IsFinite(s.Facing)) throw new BlockError(ctx.Block, $"{path}.facing", $"{s.Facing} is not a number of degrees");
        const string O = "InteractionAttachment.guestOffset";
        p.Fields[$"{O}.localOffset.x"] = (double)s.At.X;
        p.Fields[$"{O}.localOffset.y"] = (double)s.At.Y;
        p.Fields[$"{O}.localOffset.z"] = (double)s.At.Z;
        var half = s.Facing * Math.PI / 360;
        p.Fields[$"{O}.orientationOffset.x"] = 0.0;
        p.Fields[$"{O}.orientationOffset.y"] = Math.Sin(half);
        p.Fields[$"{O}.orientationOffset.z"] = 0.0;
        p.Fields[$"{O}.orientationOffset.w"] = Math.Cos(half);
    }

    // ---- interactions ---------------------------------------------------------------------------------------------

    /// <summary>A text of the template's own, for every language: its tag, as kiln.gamedata derives it from the template's GUID and "kiln:&lt;slot&gt;".</summary>
    private static Guid Text(Placed p, string slot, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new BlockError(p.Item.Ctx.Block, slot == "verb" ? "onInteract.prompt" : $"onInteract.toggle.{slot}Prompt", "a prompt needs words");
        ((p.Texts["*"] ??= new JsonObject()).AsObject())[slot] = text;
        return LocaTag(p.Guid, "kiln:" + slot);
    }

    /// <summary>kiln.gamedata's tag of a clone's text (gamedata_clones.cpp derive_guid): two FNV-64 lanes over the GUID's bytes and the salt, version 4.</summary>
    internal static Guid LocaTag(Guid clone, string salt)
    {
        ulong a = 0xcbf29ce484222325, b = 0x84222325cbf29ce4;
        void Eat(uint c)
        {
            a = (a ^ c) * 0x100000001b3;
            b = (b ^ (c + 0x5b)) * 0x100000001b3;
        }
        Span<byte> g = stackalloc byte[16];
        clone.TryWriteBytes(g);
        foreach (var c in g) Eat(c);
        foreach (var c in System.Text.Encoding.UTF8.GetBytes(salt)) Eat(c);
        var o = new byte[16];
        BitConverter.TryWriteBytes(o.AsSpan(0, 8), a);
        BitConverter.TryWriteBytes(o.AsSpan(8, 8), b);
        o[7] = (byte)((o[7] & 0x0F) | 0x40);
        return new Guid(o);
    }

    /// <summary>Components the clone needs: those its base has stay as they are, the others are added zeroed for its fields to fill.</summary>
    private static void Components(Placed p, params string[] names)
    {
        foreach (var n in names)
            if (!p.Add.Contains(n)) p.Add.Add(n);
    }

    private static void CheckInteract(BlockContext ctx, InteractBlock i, string path)
    {
        if (i.Requires is { } r)
        {
            if (r.Count is < 1 or > 65535) throw new BlockError(ctx.Block, $"{path}requires.count", $"{r.Count} is outside 1..65535");
            if (r.Uses && r.Item == null) throw new BlockError(ctx.Block, $"{path}requires.uses", "uses takes the item it requires: say which, in item");
            if (r.Count != 1 && r.Item == null) throw new BlockError(ctx.Block, $"{path}requires.count", "a count of an item: say which, in item");
            if (r.Item is { Value: { Length: 0 } }) throw new BlockError(ctx.Block, $"{path}requires.item", "takes an item: a GameItems name or an item of this mod");
            if (r.Time is { } time && !Enum.IsDefined(time)) throw new BlockError(ctx.Block, $"{path}requires.time", $"{(int)time} is not day or night");
            if (r.Item == null && r.Time == null && r.When is not { Count: > 0 } && r.Unless is not { Count: > 0 } && r.If == null)
                throw new BlockError(ctx.Block, $"{path}requires", "a requirement with nothing in it: give it item, time, when, unless or if");
        }
        if (i.OnRefused is { Count: > 0 } && i.Requires is not { When: { Count: > 0 } } and not { Unless: { Count: > 0 } } and not { If: not null })
            throw new BlockError(ctx.Block, $"{path}onRefused", "only Kiln's checks (requires.when, unless, if) can refuse with effects: the game's own lock refuses in the player's game, where nothing runs");
        if (i.Verb != null && i.Prompt != null) throw new BlockError(ctx.Block, $"{path}prompt", "a prompt of your own or the game's verb: not both");
        if (i.Verb == null && i.Prompt == null && i.Plays == null && i.Toggle == null && i.Requires == null && i.Effects is not { Count: > 0 })
            throw new BlockError(ctx.Block, path.TrimEnd('.'), "an interaction that changes nothing: give it verb, prompt, plays, toggle, requires or effects");
    }

    /// <summary>What a placed thing does when used, on its template; and what Kiln does on the server.</summary>
    private static void ApplyInteract(Placed p, InteractBlock i, string path)
    {
        var ctx = p.Item.Ctx;
        var low = p.Item.Low;
        CheckInteract(ctx, i, path);
        var json = new JsonObject();
        if (i.Verb is { } verb)
        {
            p.Fields["InteractionOfferRuntime.defaultVerbId"] = verb.Id;
            json["verb"] = verb.Key;
        }
        if (i.Prompt is { } prompt)
        {
            p.Fields["InteractionOfferRuntime.defaultVerbId"] = BlockIds.Fnv(Text(p, "verb", prompt));
            json["prompt"] = prompt;
        }
        if (i.Plays is { } plays)
        {
            p.Fields["InteractionOfferRuntime.defaultSequence"] = plays.Id;
            json["plays"] = plays.Sequence.ToString();
        }
        if (i.Toggle is { } t)
        {
            Components(p, "InteractionToggle", "ServerToggleState");
            var words = new JsonObject();
            void Word(string field, string slot, InteractVerb? verb, string? text)
            {
                if (text != null) words[slot] = text;
                else if (verb != null) words[slot] = verb.Key;
                else return;
                p.Fields[$"InteractionToggle.{field}"] = (text != null ? Text(p, slot, text) : verb!.Reference is { } r ? Guid.Parse(r) : BlockIds.WithFnv(verb.Id)).ToString();
            }
            Word("offToOnVerb", "on", t.On, t.OnPrompt);
            Word("onToOffVerb", "off", t.Off, t.OffPrompt);
            if (t.StartsOn) p.Fields["ServerToggleState.state"] = true;
            // a toggle with no sequence of its own: the hand on it, as a door's
            if (i.Plays == null) p.Fields["InteractionOfferRuntime.defaultSequence"] = InteractPlay.Toggle.Id;
            words["startsOn"] = t.StartsOn;
            json["toggle"] = words;
        }
        Guid? effects = null, refused = null;
        PlayerStateId[]? when = null, unless = null;
        if (i.Requires is { } r)
        {
            var req = new JsonObject();
            if (r.Item != null || r.Time != null)
            {
                Components(p, "InteractionLock", "ClientInteractionLock");
                const string L = "InteractionLock";
                if (r.Item is { } item)
                {
                    var name = ctx.ItemName(item, $"{path}requires.item");
                    p.Fields[$"{L}.requiredItemAmount"] = r.Count;
                    p.Fields[$"{L}.lockMask"] = r.Uses ? new JsonArray(JsonValue.Create("Consume")) : new JsonArray();
                    req["item"] = name;
                    req["count"] = r.Count;
                    req["uses"] = r.Uses;
                    // the lock compares the item's id; the id of a game item is known when the game's items are
                    var template = p.Template;
                    var block = ctx.Block;
                    var mod = ctx.Mod;
                    low.AfterCommit(() => BlockRuntime.AddReady(mod, problem =>
                    {
                        if (Content.Items.Get(name) is not { } entry)
                        {
                            problem($"block {block}, {path}requires.item: the game has no item \"{name}\"");
                            return;
                        }
                        BlockRuntime.AddRules(mod, "templates", template, new JsonObject { [$"{L}.requiredItem"] = entry.Id });
                    }));
                }
                if (r.Time is { } time)
                {
                    p.Fields[$"{L}.enableDaytimeLock"] = true;
                    // the game's lock names the time it is locked in
                    p.Fields[$"{L}.daytimeLock.lockedTime"] = time == DayPart.Day ? "Nighttime" : "Daytime";
                    req["time"] = time == DayPart.Day ? "day" : "night";
                }
            }
            when = r.When is { Count: > 0 } ? r.When.Select((s, n) => PlayerTriggers.Resolve(ctx, s, $"{path}requires.when[{n}]")).ToArray() : null;
            unless = r.Unless is { Count: > 0 } ? r.Unless.Select((s, n) => PlayerTriggers.Resolve(ctx, s, $"{path}requires.unless[{n}]")).ToArray() : null;
            if (when != null) req["when"] = new JsonArray(when.Select(s => (JsonNode?)JsonValue.Create(PlayerStates.NameOf(s) is { Length: > 0 } n ? n : $"0x{(ushort)s:x4}")).ToArray());
            if (unless != null) req["unless"] = new JsonArray(unless.Select(s => (JsonNode?)JsonValue.Create(PlayerStates.NameOf(s) is { Length: > 0 } n ? n : $"0x{(ushort)s:x4}")).ToArray());
            if (r.If != null) req["if"] = "code";
            json["requires"] = req;
        }
        var key = $"{p.Item.Key}.interact";
        if (i.Effects is { Count: > 0 })
        {
            var (program, blob, _, listing) = EffectEmitter.SelfProgram(ctx, $"{key}.effects", i.Effects, $"{path}effects");
            low.Program(program, $"{key}.effects", blob, listing);
            effects = program;
        }
        if (i.OnRefused is { Count: > 0 })
        {
            var (program, blob, _, listing) = EffectEmitter.SelfProgram(ctx, $"{key}.refused", i.OnRefused, $"{path}onRefused");
            low.Program(program, $"{key}.refused", blob, listing);
            refused = program;
        }
        low.Json["interact"] = json;
        if (effects != null || refused != null || when != null || unless != null || i.Requires?.If != null)
        {
            var guid = p.Guid;
            var entry = new InteractRuntime.Entry(ctx.Mod.Id, ctx.Block, effects, refused, when, unless, i.Requires?.If);
            low.AfterCommit(() => InteractRuntime.Add(ctx.Mod, guid, entry));
        }
        // a look with no interaction of its own says what the player plays, or there is nothing to start
        if (i.Plays == null && i.Toggle == null)
        {
            var (template, block, mod) = (p.Template, ctx.Block, ctx.Mod);
            low.AfterCommit(() => BlockRuntime.AddReady(mod, problem =>
            {
                long sequence;
                try { sequence = Content.Templates.Get(template)?.Get("InteractionOfferRuntime.defaultSequence").Integer ?? -1; }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or ArgumentException) { sequence = 0; }
                if (sequence == 0)
                    problem($"block {block}, {path.TrimEnd('.')}: {template} starts from a look with no interaction of its own: say what the player plays (plays), or start from a game item that has one");
            }));
        }
        if (i.Toggle != null || i.Verb != null || i.Prompt != null || i.Plays != null || i.Requires is { Item: not null } or { Time: not null })
            Components(p, "InteractionOfferRuntime", "ClientInteractionOffer", "CheckPlayerInRange");
    }
}
