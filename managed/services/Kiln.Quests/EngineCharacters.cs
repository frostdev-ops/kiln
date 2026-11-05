// SPDX-License-Identifier: MIT
// Quest books are per world and per character, keyed as the game files a character in a world (known_users: account +
// CharacterId; docs/re/characters.md). They never travel: a world's quests belong to that world, as the game's world knowledge
// does. Usage: docs/kiln/services/character-data.md.
namespace Kiln;

public static partial class Quests
{
    /// <summary>The book of a character (or of an account, on a build without character ids). As the data owner. The first
    /// character that asks gets the account's book from before characters, once.</summary>
    private static QuestBook CharacterBook(QuestHolder h)
    {
        var c = h.Character;
        if (c.IsNone && h.Entity.IsNone && !QuestInput.IsSynthetic(h)) c = CharacterData.LastOf(h.Account);  // offline, by account
        if (c.IsNone || c.IsAccountOnly) return PlayerData.Get<QuestBook>(h.Account != 0 ? h.Account : c.Account, "book");
        if (!CharacterData.Contains(c, "book")) CharacterData.MigrateFromPlayerData(c, "book", QuestCharacters.LegacyIds(h));
        return CharacterData.Get<QuestBook>(c, "book");
    }
}

internal static class QuestCharacters
{
    public static ulong[] LegacyIds(QuestHolder h)
    {
        var p = h.Entity.IsNone || !Players.Available ? null : Players.Find(h.Entity);
        return [h.Account, p?.SteamId ?? 0, p?.AccountId ?? 0, p is not null && Client.Available && Client.IsHosting && p.Slot == 0 ? QuestHolder.HostKey : 0];
    }

    public static void Start() => Events.On<PlayerJoinedEvent>(OnJoined);

    /// <summary>The game's player knowledge lives in the character's save, so a native quest's mirror (Kiln.Quests/Native) comes
    /// along when the character visits another world, where its book has no such quest. Cleared here, with a line in the log: the
    /// book of this world decides what the journal shows. The mirror keeps it so from then on.</summary>
    private static void OnJoined(PlayerJoinedEvent e)
    {
        if (!Quests.ServerSide || !Knowledge.Available) return;
        var who = QuestHolder.Of(e.Player);
        if (who.Character.IsNone) return;
        var scope = Knowledge.For(e.Player.Entity);
        foreach (var q in Quests.Definitions)
        {
            if (q.Journal != QuestJournalMode.Native || q.Scope == QuestScope.World || Quests.Progress(q.FullId, who) is not null) continue;
            if (NativeQuests.Quest(q.FullId) is not { } n) continue;
            var ids = new List<uint> { n.EntryId, n.Started, n.Active, n.Done };
            foreach (var s in n.Steps) ids.AddRange([s.Visible, s.Active, s.Done, s.Count, s.Ready]);
            var cleared = 0;
            foreach (var id in ids)
            {
                try
                {
                    if (id == 0 || scope.Get(id) is not { Value: not 0 }) continue;
                    var bad = id;
                    scope.Revoke(id).ContinueWith(t => QuestsInternals.Warn($"knowledge 0x{bad:x8} from another world: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
                    cleared++;
                }
                catch (NotSupportedException)
                {
                    return;  // the player left, or knowledge is off
                }
            }
            if (cleared > 0) QuestsInternals.Info($"{who.Key} {q.FullId}: {cleared} knowledge id(s) from another world cleared: no record of this quest in this world");
        }
    }
}
