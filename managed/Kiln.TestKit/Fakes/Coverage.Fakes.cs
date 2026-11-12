// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public static partial class Coverage
{
    static partial void AddFakes(List<CoverageRow> rows)
    {
        void Row(string area, CoverageKind kind, string note, params string[] caps) => rows.Add(new CoverageRow(area, kind, caps, note));
        Row("Players", CoverageKind.RecordingFake,
            "Kiln.Players' own code on the kit's kiln.players: KilnHarness.Players.Join/Leave take effect at the next step " +
            "(Players.All, Find, Joined/Left, PlayerJoinedEvent/PlayerLeftEvent); entity slot + 1, Steam id, admin bit, user-group " +
            "rights, the test's positions; 16 slots. Unavailable: raw records, the game-log replay, the position probe for " +
            "non-players, native subscribers", FakeTables.PlayersService);
        Row("Commands as a player, permissions", CoverageKind.Real,
            "Kiln.Chat's Commands.Execute with a fake player as source (KilnHarness.RunCommand(player, line)) and Kiln.Permissions' " +
            "real rules on permissions.json and enshrouded_server.json in the temp folder (WritePermissions, WriteServerConfig); " +
            "@p and player names resolve against the fake players. Replies come back in the result, chat is not faked");
        Row("Saved data (WorldData, PlayerData)", CoverageKind.RecordingFake,
            "Kiln.Saves' store on the kit's kiln.saves: a world id and revision per server folder, the save cycle on the steps " +
            "(WorldSaving, staged generation, WorldSaved), the game's autosave (30 s virtual), player-left and shutdown saves; " +
            "a harness on the same folder reads the last save (KilnHarness.Kill: no shutdown save). Unavailable: backups, the " +
            "world file's path, the snapshot stage, a client's hosted world", FakeTables.SavesService);
        Row("EntityData", CoverageKind.RecordingFake,
            "on player entities: stored as PlayerData under the fake player's Steam id. On every other entity unavailable " +
            "(EntityData.Available is false: it needs kiln.spawn, which is not faked before dev.26's fake world)", FakeTables.SavesService);
        Row("Config (per world)", CoverageKind.Real,
            "Mod.WorldConfig<T>() binds to the harness world's folder (kiln/data/worlds/<id>/config) through Kiln.Saves and " +
            "differs between world ids");
        Row("kiln.net", CoverageKind.Unavailable,
            "no fake: a true pair is a client hosting its world (a kiln.client fake, a client-side harness, a client tick) and " +
            "shares one channel registry, so no handshake refusal could be tested; Kiln.Network reports itself unavailable",
            "kiln.net");
    }
}
