// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public static partial class Coverage
{
    static partial void AddWorld(List<CoverageRow> rows)
    {
        void Row(string area, CoverageKind kind, string note, params string[] caps) => rows.Add(new CoverageRow(area, kind, caps, note));
        Row("World: entities and components", CoverageKind.RecordingFake,
            "Kiln.World's own code on the kit's kiln.world: KilnHarness.Entities (FakeWorld) makes entities and puts components on them, " +
            "laid out by Kiln.Game's types (a name they do not know is refused); World.Exists, Has, TryRead, ReadBytes, Find, Query, " +
            "Position, Transform and WriteRaw read and store them, and no read changes them. Where an entity stands is its " +
            "CurrentTransform's bytes, as in the game; a fake player's follows the player's position when the world steps, and a player's " +
            "components go with the player (the next one in the slot starts with none). Unavailable: teleport, world programs (EcsPrograms: there is no scheduler), and everything behind a " +
            "component (the game's programs do not run: a Health of 0 kills nobody)", WorldAbi.Service);
        Row("World: tracked writes", CoverageKind.RecordingFake,
            "World.Write, Update, WriteField and WriteAt with kiln.world's rules, tested at the native service's boundaries " +
            "(native/services/world/tests/parity.json): the write runs at the next step and answers in it (one step later for a type a fake " +
            "game program writes, with Kept), 512 a step and the rest later, 4,096 not yet answered at most (refusals included), 1,024 " +
            "answers a step; a type the mod's manifest does not list under \"writes\" is refused with the sentence that says what to add " +
            "(the lists are read at the first step, so a hot-loaded mod's are not; a name Kiln.Game does not know takes none of the 91 " +
            "places, and without Kiln.Game every name is refused; a mod whose list cannot be read loads and its writes say why); a write " +
            "carries its entity's generation (SpawnTime) and one for an entity without it is not kept waiting behind a full step; a write " +
            "whose entity is removed while it waits never runs, also when the id comes back (a player's slot); " +
            "ComponentWrittenEvent for a change, FakeWorld.Writes records each; WriteInfo from any thread. Not modelled: the game's " +
            "attribute formulas (no clamp), which types the game saves or replicates (null unless FakeWorld.Track says), a save of the " +
            "world's entities, a second world (a hosted game's)",
            World.TrackedWritesCapability);
        Row("World: reads from tasks (WorldConcurrent)", CoverageKind.RecordingFake,
            "kiln.world's ECS reads from task threads with the native service's rules (shared with Kiln's own unit tests): Open, Validate, " +
            "Read, ReadBytes, ReadMany, Query (bounded or not, complete or nothing) and their statuses, on the fake server world's entities, " +
            "read from a task's worker between the harness's work items; a new entity under a used id (a player's slot) is stale, a " +
            "component added or removed changes the layout. kiln.Capabilities.Override(\"world.ecs_read\", false) is a build with no exact " +
            "profile (everything Unavailable, OrOnTick takes the tick path); FakeWorld.BusyReads makes the next frame-quiet reads Busy. " +
            "Not modelled: the client and prediction worlds, template names (Template is Absent), torn or unreadable copies",
            EcsReadsFake.Row);
        Row("Game types (Kiln.Ecs, Reflected, GameTypes)", CoverageKind.RecordingFake,
            "the kit's kiln.types holds the types of the Kiln.Game beside the tests (the layouts the mod was compiled against: about 1,500 " +
            "structs, enums and arrays of the game's components and events), so Ecs.TryGet, Ecs.Has, Ecs.All, Reflected.Component and " +
            "GameTypes.Find work on the fake world. Without a Kiln.Game in the test output the service is unavailable. Unavailable either " +
            "way: game systems and their hooks (GameSystems), the profiler, the game's event streams (GameEvents), default values, " +
            "alignments, and the game's other 9,500 types", TypesFake.Service);
    }
}
