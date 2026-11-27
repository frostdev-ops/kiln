# Blocks (Kiln.Blocks): npc

## AIs — The game's AI tunings.
- `static readonly Heavy -> AIBlock` — A heavy fighter: the Vukah's AI, slower to turn away, holding its ground longer.
- `static readonly Scavenger -> AIBlock` — The scavenger melee fighter.
- `static readonly Still -> AIBlock` — One that stands where it is put and never starts a fight: guards, townsfolk.
- `static readonly Vukah -> AIBlock` — The Vukah: a melee fighter that sees 18 m by day (11 m at night) and keeps its distance while it waits.

## AnimationBlock — How a body moves: the clips of a glTF, which of them the engine's states play, your own states, and what plays over them.
- `Clips { get; init; } -> string?` — The glTF file of the mod (its path in the mod's folder): its armature is the skeleton, its animations the clips.
- `Dead { get; init; } -> Clip?` — Plays when it dies, and holds its last frame.
- `Fall { get; init; } -> Clip?` — The clip the engine's Fall state plays.
- `Hit { get; init; } -> Clip?` — Plays over everything when it is hit (the engine's hit overlay).
- `Idle { get; init; } -> Clip?` — The clip the engine's Idle state plays.
- `IdleCombat { get; init; } -> Clip?` — The clip the engine's IdleCombat state plays.
- `IdleSneak { get; init; } -> Clip?` — The clip the engine's IdleSneak state plays.
- `Key { get; init; } -> string` — How JSON names a premade.
- `Layers { get; init; } -> IReadOnlyList<LayerBlock>?` — More animations over the states.
- `Like { get; init; } -> string?` — In JSON, the premade this one starts from.
- `Retarget { get; init; } -> RetargetBlock?` — The clips are authored on a rig of their own: play them on `AnimationBlock.Skeleton`, a body's, by pairing joints...
- `Run { get; init; } -> Clip?` — The clip the engine's Run state plays.
- `Skeleton { get; init; } -> string?` — The skeleton's name: the file's name without its extension when left out.
- `Sneak { get; init; } -> Clip?` — The clip the engine's Sneak state plays.
- `Sprint { get; init; } -> Clip?` — The clip the engine's Sprint state plays.
- `Stagger { get; init; } -> Clip?` — What a hit reaction, a stun or a parry plays over the whole body: the base creature's hit sequences play it.
- `States { get; init; } -> IDictionary<AnimState, Clip>` — States of your own: what attacks, rules and sequences play by name.
- `Stun { get; init; } -> Clip?` — The clip the engine's Stun state plays.
- `Template { get; init; } -> string?` — A game creature whose graph this is (the premades).
- `Walk { get; init; } -> Clip?` — The clip the engine's Walk state plays.

## Animations — The game's graphs, as animations to give a creature of that body.
- `static readonly Fisher -> AnimationBlock` — The game's fisher trader animation graph.
- `static readonly Scavenger -> AnimationBlock` — The game's scavenger animation graph (the scavenger melee creature's).
- `static readonly Villager -> AnimationBlock` — A villager's graph: the fisher trader's.
- `static readonly Vukah -> AnimationBlock` — The game's Vukah animation graph (the Vukah melee creature's).

## AnimState — A state of an animation graph: what a sequence, an attack or the AI names to make a body play something.
- `AnimState(string name)` — A state of this mod's animation by its name, or the engine's (`"Idle"`), or one of the game's graphs...
- `static Dead { get; } -> AnimState` — The engine's dead state.
- `Equals(AnimState? other) -> bool` — True when both are the same state.
- `static Fall { get; } -> AnimState` — The engine's fall state.
- `static Game(Guid id) -> AnimState` — A state of one of the game's graphs, by the GUID its sequences name.
- `static Idle { get; } -> AnimState` — The engine's idle state: standing still.
- `static IdleCombat { get; } -> AnimState` — The engine's idle state in combat.
- `static IdleSneak { get; } -> AnimState` — The engine's idle state while sneaking.
- `static implicit operator AnimState(string name) -> AnimState` — A state by its name: the same as `new AnimState(name)`.
- `Name { get; } -> string` — What it was given as: a name, an engine state's name, a GUID or an id.
- `static Run { get; } -> AnimState` — The engine's run state.
- `static Sneak { get; } -> AnimState` — The engine's sneak state.
- `static Sprint { get; } -> AnimState` — The engine's sprint state.
- `static Stun { get; } -> AnimState` — The engine's stun state.
- `static Walk { get; } -> AnimState` — The engine's walk state.

## AttackBlock — One attack: the state it plays, how near it has to be, how often, and what its hit does.
- `Angle { get; init; } -> double` — The hit cone's whole aperture in degrees: more than 0 and at most 360.
- `Chance { get; init; } -> double?` — 0..1: how often it is chosen when it could be.
- `Cooldown { get; init; } -> double?` — Seconds after any attack of the creature before this one is chosen again.
- `Delivery { get; init; } -> IDeliveryBlock?` — A ranged attack: what leaves at `AttackBlock.HitAt`, as a spell's delivery.
- `HitAt { get; init; } -> double` — Seconds into it that the hit happens.
- `Length { get; init; } -> double?` — Seconds it lasts.
- `MinRange { get; init; } -> double?` — The shortest.
- `Name { get; init; } -> string?` — What rules, `PickAttack` and `Act.Attack` call it.
- `OnHit { get; init; } -> IReadOnlyList<EffectRef>?` — What a hit does, to everything in front within `AttackBlock.Range`.
- `Plays { get; init; } -> AnimState?` — The state it plays: one the animation has.
- `Range { get; init; } -> double` — The longest distance to its target it starts from.
- `Sequence { get; init; } -> Guid?` — A game sequence this attack plays as it is, on a game body: its GUID.
- `Style { get; init; } -> IReadOnlyList<AttackStyle>?` — When the game considers it.

## AttackStyle — When the game considers an attack (`keen::enemy01::AttackStyle`).
enum: Normal, Retaliate, CounterAttack, StunBreak, Special, Initiate, InitiateFollowUp, IgnoreThreat, Flanking

## BehaviourBlock — What a creature does: attacks and rules, on top of the game's combat AI (blocking, flanking, hit reactions stay).
- `Attacks { get; init; } -> IReadOnlyList<PartRef<AttackBlock>>?` — The attacks, each a block or the name of one defined once.
- `Brain { get; init; } -> Func<NpcBrain>?` — Your code drives it instead of the game's AI: a new `NpcBrain` for each creature.
- `BrainName { get; init; } -> string?` — In JSON: a brain registered by name with `Blocks.Brain`.
- `Key { get; init; } -> string` — How JSON names a premade.
- `Like { get; init; } -> string?` — In JSON, the premade this one starts from.
- `Perception { get; init; } -> NpcPerceptionPolicy` `0.2.3` — How the brain sees: distance and faction (the default), or also voxel line of sight.
- `Phases { get; init; } -> IReadOnlyList<string>?` — The phases its rules name.
- `PickAttack { get; init; } -> Func<NpcView, string?>?` — Decides the attack each time it would choose, on the tick thread: an attack's name, or null for the game's choice.
- `PickAttackName { get; init; } -> string?` — In JSON: an attack picker registered by name with `Blocks.AttackPicker`.
- `PickEvery { get; init; } -> double?` — Seconds between the decisions of `BehaviourBlock.PickAttack` and of the AI's target picker, more than 0 and at most 60.
- `Rules { get; init; } -> IReadOnlyList<PartRef<RuleBlock>>?` — The rules, each a block or the name of one defined once.
- `Template { get; init; } -> string?` — Keeps this game creature's attacks and rules; the block's come after them.

## Behaviours — The game's behaviours.
- `static readonly None -> BehaviourBlock` — Nothing of its own: no attacks, no rules.
- `static readonly ScavengerMelee -> BehaviourBlock` — The scavenger's.
- `static readonly VukahMelee -> BehaviourBlock` — The Vukah's attacks and rules.

## Blends — Blends for `AnimationBlock`'s states: `Walk = Blends.ByDirection("WalkF", "WalkB")`.
- `static ByDirection(string forward, string back, string? left = null, string? right = null) -> Clip` — The engine's move direction picks the animation: forward at 0 deg, back at +/-180 deg, left and right at -90 deg and...
- `static On(string input, params (double At, string Clip)[] clips) -> Clip` — Any input, with an animation at each value.

## BlendSpace — Animations mixed by an input: at each value of the input its animation, blended in between.
- `Circular { get; init; } -> bool?` — The input wraps around, as a direction does (-180 is 180).
- `Clips { get; init; } -> IDictionary<string, string>` — Value of the input -> the glTF animation that plays there.
- `Input { get; init; } -> string` — An input the engine fills (`moveDirection`, `hitDirection`, `lookYaw`, ...), or a name of your own a sequence sets.

## Bodies — The game's bodies.
- `static readonly Fisher -> BodyBlock` — The fisher trader.
- `static readonly Scavenger -> BodyBlock` — The scavenger's body.
- `static readonly Villager -> BodyBlock` — A villager: the fisher's body.
- `static readonly Vukah -> BodyBlock` — The Vukah's body.

## BodyBlock — What a creature is: a model of yours on its skeleton (cooked when the mod builds), or a game creature's body (`Bodies`).
- `Chains { get; init; } -> IReadOnlyList<ModelChainBlock>?` `0.2.17` — Joint chains of `BodyBlock.Model`'s skeleton the game swings, cooked into the model when the mod builds.
- `Cloth { get; init; } -> IReadOnlyList<ModelClothBlock>?` `0.2.17` — Primitives of `BodyBlock.Model` the game simulates as cloth, cooked into the model when the mod builds (a body in...
- `Colliders { get; init; } -> IReadOnlyList<ColliderBlock>?` — Its collision: the first shape for hits, the second for what it bumps into; one shape is both.
- `Key { get; init; } -> string` — How JSON names a premade.
- `Like { get; init; } -> string?` — In JSON, the premade this one starts from.
- `Model { get; init; } -> string?` — A skinned glTF of the mod (its path in the mod's folder).
- `Scale { get; init; } -> double?` — Its size for each creature that has it (1 = as modelled).
- `Size { get; init; } -> BodySize?` — How much room it needs to walk: radius and height in metres, for paths and steps.
- `Skeleton { get; init; } -> string?` — The skeleton's name: the model file's name without its extension when left out.
- `Slots { get; init; } -> IDictionary<Slot, string>?` — The game's attachment slots on the joints of its skeleton: where gear hangs and hits come from.
- `Template { get; init; } -> string?` — A game creature whose body this is (the premades).
- `Tool { get; init; } -> string?` — The item that summons a roster NPC of this body, which a `RosterBlock`'s tool starts from.
- `Workshop { get; init; } -> string?` — A game NPC's workshop: what a vendor on this body sells from (`0x...`).

## BodySize — How much room a body needs: what paths are searched with.
- `BodySize(double Radius, double Height)`
- `Height { get; init; } -> double` — Its height in metres, above 0.
- `Radius { get; init; } -> double` — Half its width in metres, 0 or more.

## Capsule — A capsule standing on the ground.
- `static Of(double radius, double height) -> ColliderBlock` — A capsule collider of this radius and height in metres.

## Clip — What a state plays: an animation of the glTF by its name, or a blend of several (`Blends`).
- `Clip()` — An empty clip: set `Clip.Name` or `Clip.Blend`.
- `Clip(string name)` — The glTF animation by its name.
- `Blend { get; init; } -> BlendSpace?` — Several animations mixed by an input, such as the direction a creature walks.
- `BlendIn { get; init; } -> double?` — Seconds it blends in over.
- `Bones { get; init; } -> IReadOnlyList<string>?` — On `AnimationBlock.Hit`: only these joints.
- `static implicit operator Clip(string name) -> Clip` — The glTF animation by its name: the same as `new Clip(name)`.
- `Loop { get; init; } -> bool?` — Plays again from the start.
- `Name { get; init; } -> string?` — The glTF animation.
- `Reference { get; init; } -> string?` — On `AnimationBlock.Hit`: the animation whose pose it is relative to, so it adds to whatever plays.
- `RootMotion { get; init; } -> bool?` — The motion of the root joint moves the creature instead.
- `Speed { get; init; } -> double?` — Plays faster (above 1) or slower.
- `Then { get; init; } -> AnimState?` — Moves on to this state of the same animation when its clip ends.

## ClothCapsuleBlock `0.2.17` — A capsule collider of cloth: two spheres and the space between them.
- `ClothCapsuleBlock(ClothSphereBlock A, ClothSphereBlock B)`
- `A { get; init; } -> ClothSphereBlock` — One end: a sphere with its centre, radius and joint.
- `B { get; init; } -> ClothSphereBlock` — The other end: a sphere at a different centre that follows the same joint as `A`.

## ClothColliderBlock `0.2.17` — A shape a body's cloth does not pass through: a `ClothSphereBlock` or a `ClothCapsuleBlock`.
- `ClothColliderBlock(ClothColliderBlock original)`

## ClothSphereBlock `0.2.17` — A sphere collider of cloth, in the model's bind pose: metres, glTF axes (y up), as the model file has them.
- `ClothSphereBlock(double X, double Y, double Z, double Radius, string? Joint = null)`
- `Joint { get; init; } -> string?` — The skeleton joint it follows with all its weight (the joint's name, or `j<index>`).
- `Radius { get; init; } -> double` — Metres, above 0 and at most 8.
- `X { get; init; } -> double` — Its centre.
- `Y { get; init; } -> double` — Its centre.
- `Z { get; init; } -> double` — Its centre.

## ColliderBlock — A collision shape of a body, centred on its root.
- `Height { get; init; } -> double` — A capsule's full height, its caps included.
- `Radius { get; init; } -> double` — The radius in metres.
- `Shape { get; init; } -> string` — `capsule` or `sphere`.

## CreatureBlock — A creature: its body, how it moves, how it sees, what it does.
- `AI { get; init; } -> PartRef<AIBlock>?` — Default: the body's, for a game body; the Vukah's for one of your own.
- `Animation { get; init; } -> PartRef<AnimationBlock>?` — Default: the body's own, for a game body.
- `Behaviour { get; init; } -> PartRef<BehaviourBlock>?` — Default: the body's, for a game body; none for one of your own.
- `Body { get; init; } -> PartRef<BodyBlock>` — The body: a premade (`Bodies`) or one of your own, as a block or the name of one defined once.
- `Drops { get; init; } -> PartRef<LootTableBlock>?` — What it drops when it dies, besides what the game gives it: a `LootTableBlock` of this mod (`"other-mod:name"` for...
- `Name { get; init; } -> string?` — Its name, for logs and Kiln players (vanilla players see the body's own).
- `Spawns { get; init; } -> IReadOnlyList<SpawnBlock>?` — Where it turns up by itself, in place of the game's creatures.
- `Stats { get; init; } -> CreatureStats?` — What it is worth in a fight: its level, its damage and experience factors, and the damage it takes of each kind.
- `Was { get; init; } -> string?` — The name before a rename: ids come from the oldest name.

## CreatureHandle — What `Blocks.Define` gives back for a creature: its variant, template and arsenal, and a way to spawn it.
- `Arsenal { get; } -> Guid?` — Its arsenal, when its behaviour has attacks or rules; null otherwise.
- `Spawn(WorldPosition at, float yawDegrees = 0, CreatureSpawnOptions? options = null) -> Task<EntityId>` — Spawns one with all its parts.
- `States { get; } -> IReadOnlyDictionary<string, uint>` — The state ids its animation answers to, by name.
- `Template { get; } -> string` — The template its creatures are: a game creature's, or a new one for a body of its own.
- `Variant { get; } -> string` — The kiln.creatures variant every creature of this block is: `/creature spawn` takes it.

## CreatureStats — What a creature is worth in a fight.
- `Damage { get; init; } -> double?` — Its share of damage dealt, 1 as the game has it.
- `Experience { get; init; } -> double?` — Its share of experience given, 1 as the game has it.
- `Level { get; init; } -> int?` — 1..60: its health and damage follow from it as the game scales them.
- `Takes { get; init; } -> IDictionary<DamageKind, double>` — Damage it takes of a kind, 1 as normal, 0 immune.

## IdleTask `0.2.17` — Stand idle, for the time the game's villager rolls for an idle.

## INpcBlock — The blocks of a creature: what `Blocks.Define` takes for them asks players' games for no more than the parts need.

## InspectTask `0.2.17` — Find something nearby to look at or work on, walk to it and interact (the game's "inspect").

## LayerBlock — An animation played over the states: always, or each time an overlay fires (the engine fires `hit` on a hit reaction).
- `Blend { get; init; } -> BlendSpace?` — Several animations mixed by an input, in place of `LayerBlock.Clip`.
- `Bones { get; init; } -> IReadOnlyList<string>?` — Only these joints.
- `Clip { get; init; } -> string?` — The glTF animation.
- `FadeIn { get; init; } -> double?` — Seconds it fades in over.
- `FadeOut { get; init; } -> double?` — Seconds it fades out over.
- `Name { get; init; } -> string?` — A name for the layer.
- `Overlay { get; init; } -> string?` — `hit`, or a name of your own a sequence fires.
- `Reference { get; init; } -> string?` — The animation whose pose it is relative to: then it adds to what plays.
- `Speed { get; init; } -> double?` — Plays faster (above 1) or slower.
- `Weight { get; init; } -> double?` — How strongly the layer applies.

## ModelChainBlock `0.2.17` — A chain of the skeleton's joints the game swings (its chains2): from `ModelChainBlock.RootJoint`, whose parent stays animated, to `ModelChainBlock.TipJoint` along one...
- `BendStiffness { get; init; } -> double` — The game's own coefficient, [0, 1000000].
- `Damping { get; init; } -> double` — [0, 0.99].
- `ParticleMassKg { get; init; } -> double` — The mass of one moving joint in kg (not of the chain), [0.001, 100].
- `ParticleRadius { get; init; } -> double` — Metres, [0, 1] (stored in millimetres).
- `RootJoint { get; init; } -> string` — The first joint that swings: a name of the model's skeleton.
- `Stiffness { get; init; } -> double` — The game's own coefficient, [1, 1000000].
- `TipJoint { get; init; } -> string` — The last joint, below the root.

## ModelClothBlock `0.2.17` — One primitive of a body's model the game simulates as cloth.
- `Colliders { get; init; } -> IReadOnlyList<ClothColliderBlock>` — What it does not pass through.
- `Damping { get; init; } -> double` — [0, 0.99].
- `Gravity { get; init; } -> double` — m/s2 downwards, [0, 50].
- `MassKg { get; init; } -> double` — The whole cloth's mass in kg, [0.001, 100], spread over its particles by the area around each.
- `Mesh { get; init; } -> int` — The glTF mesh, by its index in the file.
- `ParticleIds { get; init; } -> IReadOnlyList<int>?` — One id (0 or more) per POSITION element of the primitive.
- `Pin { get; init; } -> IReadOnlyList<double>` — One value in [0, 1] per particle, in particle order: 1 holds it on the model (it follows its joint), 0 lets it hang...
- `Primitive { get; init; } -> int` — The primitive of that mesh, by its index.
- `Stiffness { get; init; } -> double` — One stiffness for stretch and bend, above 0 and at most 1.

## NpcBlock — A friendly NPC: a creature with a conversation (`NpcBlock.Talk`, the quests' `DialogueDefinition`), wares and a place in the roster.
- `AI { get; init; } -> PartRef<AIBlock>?` — Not supported: setting it is refused.
- `Animation { get; init; } -> PartRef<AnimationBlock>?` — Only with an authored roster body (`BodyBlock.Model`): its cooked clips and graph.
- `Behaviour { get; init; } -> PartRef<BehaviourBlock>?` — Not supported: setting it is refused.
- `Body { get; init; } -> PartRef<BodyBlock>` — The body, as a block or the name of one defined once: a game NPC's (`Bodies.Villager`, `Bodies.Fisher`), or an...
- `Motions { get; init; } -> PartRef<NpcMotionsBlock>?` `0.2.17` — The authored graph states for this roster body's sit, sleep, dialogue and navigation motions.
- `Name { get; init; } -> string` — The name players read, in English; `NpcBlock.Names` adds the other languages.
- `Names { get; init; } -> IDictionary<string, string>?` — Its names in other languages: `{ ["de"] = "Tobin der Wanderer" }`.
- `Place { get; init; } -> NpcPlace?` — Where it is put in the world.
- `Roster { get; init; } -> RosterBlock?` — Its place in the game's roster of NPCs.
- `Routine { get; init; } -> PartRef<RoutineBlock>?` `0.2.17` — Its day plan: a routine of this mod (a handle from `Blocks.Define`, or its name), or a plan written here, which...
- `Talk { get; init; } -> DialogueDefinition?` — What it says: a dialogue (`Dialogues.Define`), or the handle of a `DialogueBlock`...
- `TalkName { get; init; } -> string?` — A dialogue by name, in JSON `"talk"`: a `DialogueBlock` of this mod by its block name, or one of the mod's...
- `Vendor { get; init; } -> VendorBlock?` — What it sells, at its workshop.
- `Was { get; init; } -> string?` — The name before a rename: ids come from the oldest name.

## NpcBlocksApi — What creature blocks add to `Blocks` and `Act`.
Extension members (C# 14): call them on the receiver (`Act.X`, `Blocks.X`), not on `NpcBlocksApi`.
- `Blocks.Action(string name, Action<NpcView> run) -> void` — A rule action for data files: `{ "custom": "name" }`.
- `Blocks.AttackPicker(string name, Func<NpcView, string?> pick) -> void` — An attack picker for data files: `"pickAttack": "name"`.
- `Blocks.Brain(string name, Func<NpcBrain> make) -> void` — A brain for blocks in data files to name: `"brain": "name"`.
- `Blocks.Condition(string name, Func<NpcView, bool> check, double every = 1) -> void` — A rule condition for data files: `{ "custom": "name" }`.
- `Act.Effect(EffectRef effect, NpcEffectOn on = NpcEffectOn.Target) -> RuleAction` — Runs an effect block: on the creature's combat target (it did it), or on itself.
- `Blocks.Spawn(string creature, WorldPosition at, float yawDegrees = 0, CreatureSpawnOptions? options = null) -> Task<EntityId>` — Spawns a creature block of this mod (or `"other-mod:name"`) with all its parts.
- `Blocks.Target(string name, Func<NpcView, EntityId> pick) -> void` — A target picker for data files: `"pickTarget": "name"`.

## NpcBodies `0.2.17` — The build status of NPC blocks with a body of their own (`body.model`): whether this start's boot build made it, why not, or whether it waits for a restart.
- `static Status(BlockHandle<NpcBlock> npc) -> NpcBodyBuildResult` — The status of the NPC block `npc`.
- `static Status(string qualifiedBlockKey) -> NpcBodyBuildResult` — The status of the NPC block `qualifiedBlockKey`, `mod:block`: also for a block whose definition failed (no handle).

## NpcBodyBuildChangedEvent `0.2.17` — An NPC body's build status changed (`NpcBodies`): one event per change of state or generation, Failed and RemovalPendingRestart included.
- `After { get; } -> NpcBodyBuildResult` — The status now.
- `Before { get; } -> NpcBodyBuildResult` — The status before the change.
- `ClientSide { get; } -> bool` — Raised on a game client's main thread (else on a dedicated server's tick thread).

## NpcBodyBuildResult `0.2.17` — An NPC body's build status.
- `NpcBodyBuildResult(bool Ok, string? Reason, NpcBodyBuildState State, string ModId, string BlockKey, Guid Template, Guid Model, Guid Graph, ulong Generation)`
- `BlockKey { get; init; } -> string` — The NPC block's key, `mod:block`.
- `Generation { get; init; } -> ulong` — The boot build's generation for the body; 0 when none.
- `Graph { get; init; } -> Guid` — The animation graph the build used; empty when it has none.
- `Model { get; init; } -> Guid` — The model the build used; empty when it has none.
- `ModId { get; init; } -> string` — The id of the mod that defines the NPC block.
- `Ok { get; init; } -> bool` — True only when `State` is `NpcBodyBuildState.Ready`.
- `Reason { get; init; } -> string?` — Why the body is not ready (the boot build's refusal, `NoDefinition`, or a restart it waits for); null when Ok.
- `State { get; init; } -> NpcBodyBuildState` — Where the body stands: ready, failed, waiting for a restart, or removed.
- `Template { get; init; } -> Guid` — The clone the build used; empty when it has none.

## NpcBodyBuildState `0.2.17` — Where an NPC block's body of its own stands (`NpcBodies.Status`).
enum: PendingRestart, Ready, Failed, RemovalPendingRestart, Removed

## NpcBrain — Your code in place of the game's AI, for each creature whose behaviour names it: `Behaviour = new BehaviourBlock { Brain = () => new MyBrain() }`.
- `virtual Start(NpcControl npc) -> void` — Once, when it takes the creature over.
- `virtual Stop(NpcControl npc) -> void` — Once, when the creature is gone or the mod unloads.
- `abstract Tick(NpcControl npc) -> void` — Every step while the creature lives.

## NpcControl — One creature as a brain sees and drives it.
- `Alive { get; } -> bool` — True while the creature exists and is not dead.
- `Attack(string name) -> bool` — Starts one of its attacks by name: its sequence plays, and its hit lands where it lands.
- `Creature { get; } -> CreatureRef` — The creature's own changes, as a mod's layers: level, faction, AI values, overrides.
- `Effect(string effect, EntityId? on = null) -> void` — Runs an effect block on what it is after (or on `on`), with this creature as the one who did it.
- `Entity { get; } -> EntityId` — The creature's entity.
- `Face(WorldPosition to) -> void` — Turns toward a place where it stands.
- `MoveTo(WorldPosition to, bool run = false) -> void` — Walks (or runs) toward a place: the body turns and moves as its walk or run state plays.
- `Name { get; } -> string` — The creature block's name.
- `PerceptionPolicy { get; } -> NpcPerceptionPolicy` `0.2.3` — How this creature sees: its behaviour's `BehaviourBlock.Perception` as it was when the creature was spawned.
- `PerceptionStats { get; } -> NpcPerceptionStats` `0.2.3` — What its sight did so far: how many looks, and how each candidate's line of sight ended.
- `Pose(AnimState state) -> void` — Holds a state of its animation while no sequence plays (the state input when nothing else sets one)...
- `Position { get; } -> WorldPosition?` — Where the creature is in metres, or null when it does not exist or has no transform.
- `Seen { get; } -> IReadOnlyList<EntityId>` — Enemies within its sight, nearest first, looked for four times a second.
- `Stop() -> void` — Stands still.
- `Target { get; set; } -> EntityId` — What it is after: the nearest of `NpcControl.Seen` until you set another.
- `TargetDistance { get; } -> double?` — Metres to `NpcControl.Target`, or null.
- `Time { get; } -> double` — Seconds since the brain took over.
- `View { get; } -> NpcView` — The creature as rules see it: health, phase, position.

## NpcEffectOn — Where a rule's effect lands.
enum: Target, Self

## NpcLineContext `0.2.26` — What a `SpeechPick.By` callback is asked: which of the lines this player sees next.
- `NpcLineContext(EntityId Npc, EntityId Player, NpcLineKind Kind, int Opens, int Last, int Lines)`
- `Kind { get; init; } -> NpcLineKind` — The greeting, the farewell or the menu's line.
- `Last { get; init; } -> int` — The index this player saw last; -1 when none (or the game's own line).
- `Lines { get; init; } -> int` — How many lines the override has.
- `Npc { get; init; } -> EntityId` — The NPC.
- `Opens { get; init; } -> int` — How many times this player has seen this kind of line of this NPC since the override began.
- `Player { get; init; } -> EntityId` — The player whose game shows the line (the host's own player in a hosted world).

## NpcLineKind `0.2.26` — Which line of an NPC an override is for (since 0.2.0-dev.32).
enum: Greeting, Farewell, MenuLine

## NpcLines `0.2.26` — One mod's override of one NPC's farewell (`Npcs.Farewells`) or menu line (`Npcs.MenuLine`): its lines replace the game's, or `NpcLines.Clear` gives them back.
- `Clear() -> void` — Ends this override: the next mod's shows, else the game's own line.
- `Kind { get; } -> NpcLineKind` — Which line it is for: `NpcLineKind.Farewell` or `NpcLineKind.MenuLine`.
- `Mode { get; } -> SpeechMode` — What this override does now (`SpeechMode.Game` when it is clear).
- `ModId { get; } -> string` — The mod this override belongs to ("kiln" outside a mod).
- `Npc { get; } -> EntityId` — The NPC.
- `Pick { get; set; } -> SpeechPick` — How `NpcLines.Replace` picks among its lines each time (as `NpcSpeech.Pick`).
- `Priority { get; set; } -> int` — Its rank against other mods' overrides of the same NPC and line (higher wins).
- `Refresh() -> void` — Asks a `SpeechPick.By` callback again for every player now.
- `Replace(IEnumerable<UiText> lines) -> SpeechResult` — As `NpcLines.Replace`, with a text per language for each line.
- `Replace(params string[] lines) -> SpeechResult` — The NPC shows one of these lines instead of the game's (1 to 32 lines of up to 200 characters, the same in every...

## NpcMotionBinding `0.2.17` — Binds one chassis motion to an authored graph state.
- `NpcMotionBinding(NpcMotionRole Role, int Variant, AnimState State, Guid? InteractionKey = null)`
- `InteractionKey { get; init; } -> Guid?` — For `NpcMotionRole.Interaction`: the chassis interaction key, null to use the chassis's own (an explicit key must...
- `Role { get; init; } -> NpcMotionRole` — The motion this binding plays.
- `State { get; init; } -> AnimState` — The state of the body's authored animation graph that plays it.
- `Variant { get; init; } -> int` — Which variant of the role: 0, 1 or 2 for Sit, Sleep and Door (see `NpcMotionRole`), 0 for the others.

## NpcMotionRole `0.2.17` — A roster villager's motion.
enum: Sit, Sleep, Inspect, Greet, Talk, Reaction0, Reaction1, Refuse, IdleGesture0, IdleGesture1, Interaction, GreetingExtra, Farewell, DialogOption, AttentionGreeting, TurnLeftSmall, TurnLeftBig, TurnRightSmall, TurnRightBig, Jump, Door

## NpcMotionsBlock `0.2.17` — A complete roster body's motion bindings.
- `Bindings { get; init; } -> IReadOnlyList<NpcMotionBinding>` — One binding per role and variant, never last-wins.
- `OptionalFallback { get; init; } -> AnimState?` — A state to clamp/hold for missing optional motions.

## NpcOcclusionMode `0.2.3` — What stands between a brain and what it sees.
enum: DistanceAndFaction, Voxels

## NpcPerceptionPolicy `0.2.3` — How a creature with a brain sees.
- `FromOffset { get; init; } -> WorldPosition` — Added to the creature's position: where it looks from.
- `Occlusion { get; init; } -> NpcOcclusionMode` — What stands between the brain and what it sees.
- `ToOffset { get; init; } -> WorldPosition` — Added to a candidate's position: what of it must be visible.

## NpcPerceptionStats `0.2.3` — What a brain's sight did since the creature was spawned.
- `NpcPerceptionStats(ulong Updates, ulong Checks, ulong Visible, ulong Occluded, ulong NotLoaded, ulong OutOfGrid, ulong BudgetExceeded, ulong OutOfRange, ulong EntityGone, ulong OtherRefused, ulong CapabilityUnavailableUpdates, string? LastRefusal)`
- `BudgetExceeded { get; init; } -> ulong` — Candidates whose line could not be checked because the step's ray budget was used up.
- `CapabilityUnavailableUpdates { get; init; } -> ulong` — Looks made while `queries.line_of_sight` was off.
- `Checks { get; init; } -> ulong` — How many candidates were put to the line-of-sight question, in `NpcOcclusionMode.Voxels`.
- `EntityGone { get; init; } -> ulong` — Candidates that had left the world when the line was checked.
- `LastRefusal { get; init; } -> string?` — The latest look's last refusal, such as `OutOfGrid`, or null.
- `NotLoaded { get; init; } -> ulong` — Candidates whose line could not be checked because the terrain there was not loaded.
- `Occluded { get; init; } -> ulong` — Candidates the line of sight found blocked by terrain or a building.
- `OtherRefused { get; init; } -> ulong` — Candidates whose line was refused for any other reason, or whose ray was degenerate (no length, or not a number).
- `OutOfGrid { get; init; } -> ulong` — Candidates whose line could not be checked because it left the grid the query covers.
- `OutOfRange { get; init; } -> ulong` — Candidates farther than a ray reaches, counted without asking the query.
- `Updates { get; init; } -> ulong` — How many looks the brain made (four a second).
- `Visible { get; init; } -> ulong` — Candidates the line of sight found clear: they are seen.

## NpcPlace — Where an NPC is put: next to the first player, or at a spot.
- `At { get; init; } -> double[]?` — The spot, as x, y and z.
- `Distance { get; init; } -> double[]?` — Metres from the first player, between these two.
- `Exact { get; init; } -> bool` — Exactly `NpcPlace.At`: no ground, no base check.
- `InBase { get; init; } -> bool` — A base is fine (the default): a friendly NPC may stand in one, so it comes as near as `NpcPlace.Distance` asks while...
- `Once { get; init; } -> bool` — Only the first time in a world.

## Npcs `0.2.23` — Friendly NPCs' speech: a mod's own lines in the game's bubble over a villager, craftsperson or assistant, and, since 0.2.0-dev.32, in the NPC's dialogue menu: a...
- `static Available { get; } -> bool` — This game shows Kiln's speech in its own bubbles (a game client whose kiln.npcspeech found the bubble on this build...
- `static CanSpeak(EntityId npc) -> bool` — The entity is a friendly NPC with dialogue lines, the kind that has a speech bubble (a villager, a craftsperson, an...
- `static Farewells(EntityId npc) -> NpcLines` `0.2.26` — The calling mod's override of the NPC's farewell: the line in the dialogue menu's speech box when a player leaves...
- `static IsSaying(EntityId npc) -> bool` — True when a Say is showing over the NPC now.
- `static readonly MaxSay -> TimeSpan` — The shortest and longest `Npcs.Say`.
- `static MenuLine(EntityId npc) -> NpcLines` `0.2.26` — The calling mod's override of the NPC's own line in its dialogue menu: the speech box's text when a player opens the...
- `static MenuSay(EntityId npc, EntityId player, UiText text) -> SpeechResult` `0.2.26` — As `Npcs.MenuSay`, with a text per language.
- `static MenuSay(EntityId npc, EntityId player, string text) -> SpeechResult` `0.2.26` — Shows `text` in the speech box of `player`'s dialogue menu with the NPC: now if it is open, else when the player...
- `static readonly MinSay -> TimeSpan` — The shortest and longest `Npcs.Say`.
- `static Say(EntityId npc, UiText text, TimeSpan duration, SayOptions? options = null) -> SpeechResult` — As `Npcs.Say`, with a text per language (`UiText.Of(("en", "I'm starving"), ("de", "Ich verhungere"))`).
- `static Say(EntityId npc, string text, TimeSpan duration, SayOptions? options = null) -> SpeechResult` — Shows `text` in the NPC's speech bubble for `duration` (0.5 s to 60 s), with no voice-over, also when the NPC is not...
- `static Speech(EntityId npc) -> NpcSpeech` — The calling mod's override of the NPC's greeting bubble (a new handle each call; they all name the same override).
- `static SpeechOf(EntityId npc) -> SpeechMode` — What the NPC's greeting bubble does now, from every mod's overrides (a Say aside).
- `static StopSaying(EntityId npc) -> void` — Ends the calling mod's Say over the NPC before its time (another mod's Say stays).
- `static Topics(EntityId npc) -> NpcTopics` `0.2.26` — The calling mod's topics on the NPC's talk page in the game's dialogue menu: rows after the NPC's own (never...

## NpcSpeech `0.2.23` — One mod's override of one NPC's greeting bubble (`Npcs.Speech`; a farewell or the menu line is an `NpcLines`).
- `Clear() -> void` — Ends this override: the next mod's shows, else the game's own lines.
- `Kind { get; } -> NpcLineKind` `0.2.26` — Which line it is for: the bubble's greeting, the menu's farewell or the menu's own line.
- `Mode { get; } -> SpeechMode` — What this override does now (`SpeechMode.Game` when it is clear).
- `ModId { get; } -> string` — The mod this override belongs to ("kiln" outside a mod).
- `Npc { get; } -> EntityId` — The NPC.
- `Pick { get; set; } -> SpeechPick` `0.2.26` — How `NpcSpeech.Replace` picks among its lines each time the NPC greets (says farewell, opens its menu)...
- `Priority { get; set; } -> int` — Its rank against other mods' overrides of the same NPC (higher wins).
- `Refresh() -> void` `0.2.26` — Asks a `SpeechPick.By` callback again for every player now (its condition changed), instead of at each player's next...
- `Replace(IEnumerable<UiText> lines) -> SpeechResult` — As `NpcSpeech.Replace`, with a text per language for each line (`UiText.Of(("en", "..."), ("de", "..."))`).
- `Replace(params string[] lines) -> SpeechResult` — The NPC shows one of these lines instead of its own (a new pick each time it greets, as `NpcSpeech.Pick` says), with...
- `Suppress() -> SpeechResult` — No greeting bubble while this override is on, and no greeting voice-over (a player who presses E still gets the...

## NpcTopic `0.2.26` — One topic of a mod on an NPC's talk page: its `NpcTopic.Label` in the list, the NPC's `NpcTopic.Reply` in the speech box when a player picks it, and...
- `NpcTopic(string id, UiText label)` — A topic with a label per language.
- `NpcTopic(string id, string label)` — A topic with the same label in every language.
- `Close { get; init; } -> bool` — The dialogue ends after the `NpcTopic.Reply` (the game's outro).
- `Hint { get; init; } -> NpcTopicHint` — The marker in the list.
- `Id { get; } -> string` — The topic's id within its mod: the same id names the same topic (Remove, the pick).
- `Label { get; } -> UiText` — Its text in the topic list.
- `const MaxIdLength = 64 -> int` — The longest topic id, in characters: 64.
- `OnPick { get; init; } -> Action<NpcTopicContext>?` — Runs on the host's tick thread when a player picks the topic (the host's own player too), as the mod that set it.
- `Reply { get; init; } -> UiText?` — What the NPC answers in the speech box when a player picks it (the game's reveal, no voice-over).

## NpcTopicContext `0.2.26` — A pick of a topic: which NPC, which player, which topic.
- `ModId { get; } -> string` — The mod whose topic it is.
- `Npc { get; } -> EntityId` — The NPC.
- `Player { get; } -> EntityId` — The player who picked it (the host's own player, or a joined player).
- `Reply(UiText text) -> SpeechResult` — As `NpcTopicContext.Reply`, with a text per language.
- `Reply(string text) -> SpeechResult` — Answers this player in the speech box of their menu with the NPC (`Npcs.MenuSay`).
- `Topic { get; } -> NpcTopic` — The topic picked.

## NpcTopicHint `0.2.26` — The marker a topic gets in the dialogue menu's list (the game's uiHintType).
enum: None, Hint, Attention, MainQuest

## NpcTopics `0.2.26` — One mod's topics on one NPC's talk page (`Npcs.Topics`).
- `Add(NpcTopic topic) -> SpeechResult` — Adds a topic at the end, or replaces this mod's topic with the same id where it is.
- `Clear() -> void` — Removes all of this mod's topics of the NPC.
- `List { get; } -> IReadOnlyList<NpcTopic>` — This mod's topics of the NPC now, in their order.
- `const MaxTopics = 16 -> int` — The most topics a page shows (every mod's together).
- `ModId { get; } -> string` — The mod these topics belong to ("kiln" outside a mod).
- `Npc { get; } -> EntityId` — The NPC.
- `Remove(string id) -> SpeechResult` — Removes this mod's topic with that id (nothing when there is none).
- `Set(params NpcTopic[] topics) -> SpeechResult` — This mod's topics of the NPC become these (none: `NpcTopics.Clear`).

## Price — A price: so many of an item.
- `Price(ItemRef Item, int Count)`
- `Count { get; init; } -> int` — How many of it.
- `Item { get; init; } -> ItemRef` — The item asked for.
- `static Of(ItemRef item, int count = 1) -> Price` — A price of `count` of `item`.

## RetargetBlock — How `AnimationBlock.Clips` authored on another rig play on `AnimationBlock.Skeleton`: joints are paired by name, each paired joint turns as its source turns away from...
- `ByName { get; init; } -> bool?` — Pair joints of the same name that `RetargetBlock.Map` leaves out.
- `Map { get; init; } -> IDictionary<string, string>` — The clip file's joint -> the skeleton's joint.
- `Reference { get; init; } -> RetargetPose?` — A pose of the clip file that shows the skeleton's rest posture, when the file's own rest pose doesn't (an A-pose rig...
- `Scale { get; init; } -> double?` — How far translations go on the skeleton.
- `Translate { get; init; } -> IReadOnlyList<string>?` — The clip file's joints whose movement carries over.

## RetargetPose — A frame of one of the clip file's animations: `new("TPose")`.
- `RetargetPose(string Animation, double At = 0)`
- `Animation { get; init; } -> string` — The name of one of the clip file's animations.
- `At { get; init; } -> double` — Seconds into it, from 0.

## RoamTask `0.2.17` — Walk to a random spot around home, by the villager's own roaming range.

## RosterBlock — A place in the game's roster of NPCs: unlocked, summoned with a tool, given a bed.
- `Description { get; init; } -> string?` — The roster page's text about it.
- `RoamingRange { get; init; } -> double?` — How far it wanders from where it is placed.
- `SummonTool { get; init; } -> string?` — What the tool that summons it is called under its name ("Tobin's Whistle").
- `UnlockedBy { get; init; } -> string?` — A knowledge name of the mod's quests that unlocks it; null: always.

## RoutineAssignmentChangedEvent `0.2.17` — A villager's routine assignment changed: assigned, replaced, suspended by a manual command, resumed, or removed.
- `After { get; } -> RoutineSnapshot` — The assignment after it (state `RoutineAssignmentState.Removed` for a removal).
- `Before { get; } -> RoutineSnapshot?` — The assignment before the change; null when the villager had none.
- `Reason { get; } -> string` — What happened: `Assigned`, `Replaced`, `ManualSuspended` (a Reset or GoTo command reached the villager), `Resumed`...
- `Teardown { get; } -> bool` — The world is stopping: see the remarks.

## RoutineAssignmentState `0.2.17` — Where a villager's routine assignment stands.
enum: Assigned, ManualSuspended, PendingSpawn, VersionMismatch, Removed

## RoutineBlock `0.2.17` — A day plan for a villager: `RoutineBlock.Windows` of the game day, each with a task, and the `RoutineBlock.Fallback` for the minutes no window covers.
- `Fallback { get; init; } -> RoutineTask` — The task for a minute no window covers, and after a `SleepTask` the game does not allow now: `VanillaTask` (the...
- `Version { get; init; } -> uint` — The version of the plan's content, 1 or more.
- `Was { get; init; } -> string?` — The block name this plan had before a rename: its identity stays `<mod>:<Was>`, so saved assignments still find it.
- `Windows { get; init; } -> IReadOnlyList<RoutineWindow>` — The windows of the day, at most 64.

## RoutineResult `0.2.17` — The answer to a change of a villager's routine (assign, remove, resume).
- `RoutineResult(bool Ok, string? Reason, uint NpcId, ulong Revision)`
- `NpcId { get; init; } -> uint` — The villager.
- `Ok { get; init; } -> bool` — True when the change happened or was already in effect.
- `Reason { get; init; } -> string?` — Why not: `OwnedBy:<mod>` (another mod's routine is on this villager), `WorldStopped`, `Revoked`, `QueueFull`.
- `Revision { get; init; } -> ulong` — The assignment's revision after the change: it rises with every change of the assignment and never falls.

## Routines `0.2.17` — Villager routines: bind a day plan (`RoutineBlock`, defined with `Blocks.Define` or in the `routines` section of blocks/*.json) to a villager.
- `static Assign(Villager npc, BlockHandle<RoutineBlock> plan) -> Task<RoutineResult>` — Binds `plan` to `npc`, or replaces the calling mod's plan on it.
- `static Inspect(Villager npc) -> RoutineSnapshot?` — The routine on `npc` now (any mod's), or null when it has none.
- `static Remove(Villager npc) -> Task<RoutineResult>` — Takes the calling mod's routine off `npc`: the game chooses for it again.
- `static Resume(Villager npc) -> Task<RoutineResult>` — Ends the suspension a manual command put on `npc`'s routine: it decides again at the villager's next decision...

## RoutineSnapshot `0.2.17` — A villager's routine at one moment.
- `RoutineSnapshot(uint NpcId, string Owner, string Key, uint PlanVersion, ulong Revision, RoutineAssignmentState Assignment, int WindowIndex, ulong Invocation, RoutineTask? Task, RoutineTaskState TaskState, VillagerState NativeState, string? LastReason)`
- `Assignment { get; init; } -> RoutineAssignmentState` — Where the assignment stands.
- `Invocation { get; init; } -> ulong` — The serial of the current task: each task the routine starts has the next one.
- `Key { get; init; } -> string` — The plan's identity, `<mod>:<name>`.
- `LastReason { get; init; } -> string?` — The last refusal or suspension, or null: a task's refusal (`NativeSleepNotCurrentlyAllowed`, `TaskFailed`, ...) or...
- `NativeState { get; init; } -> VillagerState` — The game's state of the villager.
- `NpcId { get; init; } -> uint` — The villager.
- `Owner { get; init; } -> string` — The mod whose plan it is.
- `PlanVersion { get; init; } -> uint` — The plan's `RoutineBlock.Version`.
- `Revision { get; init; } -> ulong` — The assignment's revision.
- `Task { get; init; } -> RoutineTask?` — The current task, or null.
- `TaskState { get; init; } -> RoutineTaskState` — Where it stands.
- `WindowIndex { get; init; } -> int` — The window of the current task; -1: the fallback, or no task.

## RoutineTask `0.2.17` — What a villager does in a window: one of the game's own villager tasks.
- `RoutineTask(RoutineTask original)`

## RoutineTaskChangedEvent `0.2.17` — A task of a villager's routine changed state: chosen at a decision, started, completed, refused by the game (no seat, no bed, sleep not allowed now), or suspended.
- `From { get; } -> RoutineTaskState` — The task's state before the change.
- `Invocation { get; } -> ulong` — The task's serial: every task a routine starts has its own.
- `Key { get; } -> string` — The plan's identity, `<mod>:<name>`.
- `NpcId { get; } -> uint` — The villager.
- `PlanVersion { get; } -> uint` — The plan's Version (the one its definition declares) of the assignment the task belongs to.
- `Reason { get; } -> string?` — Why it was refused or suspended (`NativeSleepNotCurrentlyAllowed`, `TaskFailed`, `NoTransition`, `SafetyInterrupt`...
- `Revision { get; } -> ulong` — The assignment's revision when the change was reported.
- `Task { get; } -> RoutineTask?` — The task; null when there is none.
- `Teardown { get; } -> bool` — The world is stopping.
- `To { get; } -> RoutineTaskState` — The task's state after the change.

## RoutineTaskState `0.2.17` — Where one task of a routine stands.
enum: None, Chosen, Started, Completed, Refused, Suspended

## RoutineWindow `0.2.17` — One window of a `RoutineBlock`: from `StartMinute` up to, not including, `EndMinute`, in minutes of the game day (0..1439; 0 is midnight).
- `RoutineWindow(int StartMinute, int EndMinute, RoutineTask Task, int Priority = 0, bool AllDay = false)`
- `AllDay { get; init; } -> bool` — The whole day; then both minutes are 0.
- `EndMinute { get; init; } -> int` — The first minute after the window.
- `Priority { get; init; } -> int` — Among the windows that cover a minute the highest wins; equal ones go to the window written first.
- `StartMinute { get; init; } -> int` — The first minute of the window.
- `Task { get; init; } -> RoutineTask` — What the villager does in it.

## RuleBlock — A rule: on a trigger, every so often, when its conditions hold, do something.
- `Chance { get; init; } -> double` — 0..1.
- `Cooldown { get; init; } -> double` — Seconds before it can fire again.
- `Do { get; init; } -> IReadOnlyList<RuleAction>` — What it does when it fires, in order: kiln.creatures' `Act` actions.
- `Every { get; init; } -> double` — Seconds between looks at the conditions, more than 0 and at most 3600.
- `Name { get; init; } -> string?` — What the rule is called.
- `On { get; init; } -> RuleMode` — When it is looked at: every `RuleBlock.Every` seconds by default, or on entering a phase, starting an attack, ...
- `When { get; init; } -> IReadOnlyList<RuleCondition>` — All of these hold.

## SayOptions `0.2.23` — How a `Npcs.Say` goes.
- `SayOptions(bool Interrupt = true)`
- `Interrupt { get; init; } -> bool` — True (the default): it replaces a Say that is still showing over that NPC, also another mod's.

## Shape — Where particles head at spawn: `Shape.Up`, `Shape.Sphere`, `Shape.Outward`, or `Shape.Cone`.
- `static Cone(float degrees) -> Shape` — A cone around up: half angle in degrees, 0 to 180.
- `static readonly Outward -> Shape` — Away from the centre of the `ParticleBlock.Shape`: out of a sphere, along a disc's plane, across a line.
- `static readonly Sphere -> Shape` — In any direction.
- `static readonly Up -> Shape` — Straight up along the effect's up.

## SitTask `0.2.17` — Find a seat nearby, walk to it and sit.

## SleepTask `0.2.17` — Go to the assigned bed and sleep, when the game lets this villager sleep now (its own sleep and wake times).

## SpawnBlock — Where a creature turns up by itself: in place of the game's creatures of a template.
- `Chance { get; init; } -> double` — 0..1 of them.
- `DayOnly { get; init; } -> bool` — Only by day.
- `NightOnly { get; init; } -> bool` — Only at night.
- `Replaces { get; init; } -> string` — A game creature: `GameTemplates` constant or name.

## SpeechMode `0.2.23` — What an NPC's greeting bubble does now.
enum: Game, Suppress, Replace

## SpeechPick `0.2.26` — How a `NpcSpeech.Replace` picks its line each time the NPC greets (says farewell, opens its menu).
- `static By(Func<NpcLineContext, int> choose) -> SpeechPick` — The host decides for each player: `choose` runs on the host's tick thread and returns the index of the line that...
- `static InOrder { get; } -> SpeechPick` — The lines in turn, one per greeting, from the first, picked by each player's own game.
- `static Random { get; } -> SpeechPick` — A new line at random each time (never the same line twice in a row when there are two or more), picked by each...
- `static Seeded(int seed) -> SpeechPick` — The same line for the same greeting everywhere: a mix of `seed`, the NPC and how many times the player has seen it...

## SpeechResult `0.2.23` — The answer to a speech call.
- `SpeechResult(bool Ok, string? Reason)`
- `Ok { get; init; } -> bool` — True when it was accepted.
- `Reason { get; init; } -> string?` — Why not (null when `Ok`), or, when accepted, a note on where it cannot show (null when nothing to say).

## Sphere — A sphere.
- `static Of(double radius) -> ColliderBlock` — A sphere collider of this radius in metres.

## VanillaTask `0.2.17` — The game's own choice: its sleep check, its social targets and its weighted pick run as without a plan.

## VendorBlock — What an NPC sells: item -> its price.
- `Gives { get; init; } -> IDictionary<ItemRef, int>?` — How many one trade gives, per item; 1 when left out.
- `Sells { get; init; } -> IDictionary<ItemRef, Price>` — The wares: each item to its price, which becomes a recipe at the workshop.
- `Workshop { get; init; } -> string?` — The workshop (`0x...`).
