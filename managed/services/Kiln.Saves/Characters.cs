// SPDX-License-Identifier: MIT
// Per-character mod data, mirroring what the game does (docs/re/characters.md): a world files what it knows about a
// character under (account, CharacterId) in its known_users, and the character's own progress travels in the character's
// save. So CharacterData is the world's record of a character, and CharacterData.Carried is what goes with the character.
// Transport: CharacterSync.cs. Usage: docs/kiln/services/character-data.md.
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Kiln;

/// <summary>Who a character is: the game's CharacterId under the account that plays it. The world's <c>known_users</c>
/// block uses the same pair, up to 10 characters per account.</summary>
/// <param name="Account">The game's account key (<see cref="Player.AccountId"/>).</param>
/// <param name="Id">The game's CharacterId. <see cref="Guid.Empty"/>: the build gave none, so <see cref="IsAccountOnly"/>.</param>
public readonly record struct CharacterKey(ulong Account, Guid Id)
{
    public static CharacterKey None => default;

    public bool IsNone => Id == Guid.Empty && Account == 0;

    /// <summary>The game's character id is unknown on this build: this key stands for the account, so every character of
    /// the account shares it (what Kiln did before it knew characters) and nothing travels.</summary>
    public bool IsAccountOnly => Id == Guid.Empty && Account != 0;

    /// <summary>The text used in storage scopes: "&lt;account hex&gt;.&lt;id&gt;".</summary>
    public string Text => Id == Guid.Empty ? $"{Account:x16}.account" : $"{Account:x16}.{Id:N}";

    public override string ToString() => IsNone ? "(no character)" : Id == Guid.Empty ? $"account {Account:x16}" : Id.ToString();

    /// <summary>Reads <see cref="Text"/> back.</summary>
    public static bool TryParse(string? text, out CharacterKey key)
    {
        key = default;
        if (text is null) return false;
        var dot = text.IndexOf('.');
        if (dot <= 0 || !ulong.TryParse(text.AsSpan(0, dot), System.Globalization.NumberStyles.HexNumber, null, out var account)) return false;
        var rest = text.AsSpan(dot + 1);
        if (rest.SequenceEqual("account")) key = new CharacterKey(account, Guid.Empty);
        else if (Guid.TryParse(rest, out var id)) key = new CharacterKey(account, id);
        else return false;
        return true;
    }
}

/// <summary>Per-character mod data of the calling mod: one world's record of one character (like <see cref="PlayerData"/>,
/// but two characters of one account have separate data). It stays on this world, in the world's sidecar, with every world
/// save. What a character takes along to other worlds goes in <see cref="Carried"/>.</summary>
/// <remarks>
/// <para>Tick thread, from mod code, on the side that runs the world (a dedicated server, a hosting client). Values are live
/// objects, serialized at every world save, as with <see cref="WorldData"/>. Keys belong to the calling mod. The character
/// need not be online when you pass a <see cref="CharacterKey"/>.</para>
/// <para>The native analogue is the world's quest and base state: world knowledge and <c>known_users</c>.</para>
/// </remarks>
public static class CharacterData
{
    internal const string WorldPrefix = "character/", CarriedPrefix = "carried/";

    /// <summary>The character of a connected player (<see cref="CharacterKey.None"/> when the player is unknown). On a build
    /// without the game's character id it is an account-only key: see <see cref="CharacterKey.IsAccountOnly"/>. Any thread.</summary>
    public static CharacterKey KeyOf(EntityId player) => CharacterStore.KeyOf(player);

    /// <summary>The object stored under <paramref name="key"/> for the character, created with <c>new T()</c> if there is none.</summary>
    /// <exception cref="InvalidDataException">The stored JSON does not read as <typeparamref name="T"/> (it is kept).</exception>
    public static T Get<T>(CharacterKey character, string key) where T : class, new() =>
        CharacterStore.Get<T>(WorldPrefix, character, key, "CharacterData.Get");

    /// <summary><see cref="Get{T}(CharacterKey, string)"/> for a player in game. Throws <see cref="ArgumentException"/> when
    /// the player has no character (no account either).</summary>
    public static T Get<T>(EntityId player, string key) where T : class, new() => Get<T>(CharacterStore.Need(player), key);

    public static bool TryGet<T>(CharacterKey character, string key, [NotNullWhen(true)] out T? value) where T : class =>
        CharacterStore.TryGet(WorldPrefix, character, key, "CharacterData.TryGet", out value);

    public static void Set<T>(CharacterKey character, string key, T value) where T : class =>
        CharacterStore.Set(WorldPrefix, character, key, value, "CharacterData.Set");

    public static bool Remove(CharacterKey character, string key) => CharacterStore.Remove(WorldPrefix, character, key, "CharacterData.Remove");

    public static bool Contains(CharacterKey character, string key) => CharacterStore.Contains(WorldPrefix, character, key, "CharacterData.Contains");

    public static IReadOnlyList<string> Keys(CharacterKey character) => CharacterStore.KeysOf(WorldPrefix, character, "CharacterData.Keys");

    /// <summary>Characters the calling mod has data for in this world.</summary>
    public static IReadOnlyList<CharacterKey> Characters => CharacterStore.Characters(WorldPrefix, "CharacterData.Characters");

    /// <summary>Moves the calling mod's <see cref="PlayerData"/> entry to this character, once: only when the character has
    /// no such key yet. The first character that asks gets the old account-wide data; nothing is copied, so the data can't
    /// end up on two characters. <paramref name="playerIds"/>: the ids it may be filed under (a Steam id, an account id).
    /// Returns true when something moved.</summary>
    public static bool MigrateFromPlayerData(CharacterKey character, string key, params ulong[] playerIds) =>
        CharacterStore.Migrate(WorldPrefix, character, key, playerIds, "CharacterData.MigrateFromPlayerData");

    /// <summary>The character last played by this Steam or account id in this world, none if unknown. For tools that
    /// only know the player (an admin command on someone offline).</summary>
    public static CharacterKey LastOf(ulong playerId) => CharacterStore.LastOf(playerId);

    /// <summary>What travels with the character: the same API, but a Kiln client keeps a copy next to its character and offers it to
    /// the next server, and the server sends changes back at each world save (the points where the game sends the character
    /// back). Vanilla clients have no copy: for them this data stays on the server, per character, like the rest.</summary>
    /// <remarks>Treat it like the game treats a character save: client-side and editable. Whatever you keep here a player can
    /// change on disk, and a server can refuse imports (<c>kiln/config/characters.json</c>). Validate at
    /// <see cref="CharacterImportedEvent"/>. World progress and anything a server must trust belongs in
    /// <see cref="CharacterData"/>.</remarks>
    public static class Carried
    {
        public static T Get<T>(CharacterKey character, string key) where T : class, new() =>
            CharacterStore.Get<T>(CarriedPrefix, character, key, "CharacterData.Carried.Get");

        public static T Get<T>(EntityId player, string key) where T : class, new() => Get<T>(CharacterStore.Need(player), key);

        public static bool TryGet<T>(CharacterKey character, string key, [NotNullWhen(true)] out T? value) where T : class =>
            CharacterStore.TryGet(CarriedPrefix, character, key, "CharacterData.Carried.TryGet", out value);

        public static void Set<T>(CharacterKey character, string key, T value) where T : class =>
            CharacterStore.Set(CarriedPrefix, character, key, value, "CharacterData.Carried.Set");

        public static bool Remove(CharacterKey character, string key) => CharacterStore.Remove(CarriedPrefix, character, key, "CharacterData.Carried.Remove");

        public static bool Contains(CharacterKey character, string key) => CharacterStore.Contains(CarriedPrefix, character, key, "CharacterData.Carried.Contains");

        public static IReadOnlyList<string> Keys(CharacterKey character) => CharacterStore.KeysOf(CarriedPrefix, character, "CharacterData.Carried.Keys");

        public static IReadOnlyList<CharacterKey> Characters => CharacterStore.Characters(CarriedPrefix, "CharacterData.Carried.Characters");

        /// <summary>See <see cref="CharacterData.MigrateFromPlayerData"/>.</summary>
        public static bool MigrateFromPlayerData(CharacterKey character, string key, params ulong[] playerIds) =>
            CharacterStore.Migrate(CarriedPrefix, character, key, playerIds, "CharacterData.Carried.MigrateFromPlayerData");

        /// <summary>The revision of this character's carried data on this server (+1 each time a save finds a change).</summary>
        public static long Revision(CharacterKey character) => CharacterStore.RevisionOf(character);
    }

    /// <summary>Starts the channel and the session tracking (Kiln.Skills does this at Kiln's first service tick). Idempotent.</summary>
    public static void EnsureStarted() => CharacterSync.EnsureStarted();

    /// <summary>Counters of the transport.</summary>
    public static CharacterSyncStatus Status => CharacterSync.Status;
}

/// <summary>The transport's counters.</summary>
public sealed record CharacterSyncStatus(bool Started, bool ChannelOpen, bool ImportsAllowed, int Sessions, long Imports, long Refused, long Kept,
    long Dropped, long Updates, long UpdateBytes, long SidecarWrites);

/// <summary>A client offered newer carried data for a character. Tick thread, before anything is applied. Cancel refuses the
/// whole offer (the server keeps its copy and leaves the client's alone). Not raised when the server setting refuses imports.</summary>
public sealed class CharacterImportingEvent(CharacterKey character, EntityId player, long revision, long serverRevision,
    IReadOnlyDictionary<string, IReadOnlyList<string>> mods) : CancelableEvent
{
    public CharacterKey Character { get; } = character;

    /// <summary>The player, none for a lab peer.</summary>
    public EntityId Player { get; } = player;

    /// <summary>The client's revision and the server's.</summary>
    public long Revision { get; } = revision;

    public long ServerRevision { get; } = serverRevision;

    /// <summary>Mod id -> its keys in the offer.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Mods { get; } = mods;

    /// <summary>Shown in the log and sent to the client.</summary>
    public string Reason { get; set; } = "canceled by a handler";
}

/// <summary>A character's carried data was replaced by what its client offered. Tick thread, the values are in place.
/// Check them here, as the game checks a character at login (the data is client-side and editable). Not cancelable.</summary>
public sealed class CharacterImportedEvent(CharacterKey character, EntityId player, long revision, IReadOnlyList<string> applied, IReadOnlyList<string> kept) : KilnEvent
{
    public CharacterKey Character { get; } = character;

    public EntityId Player { get; } = player;

    public long Revision { get; } = revision;

    /// <summary>Mods loaded here whose data came in.</summary>
    public IReadOnlyList<string> Applied { get; } = applied;

    /// <summary>Mods not loaded here: their data is stored and passed on, nothing reads it.</summary>
    public IReadOnlyList<string> Kept { get; } = kept;
}

/// <summary><c>kiln/config/characters.json</c> (server): what a server takes from clients. Unknown keys are ignored; a missing
/// file gives the defaults. Read again whenever a character joins.</summary>
public sealed record CharacterSettings
{
    /// <summary>Take carried data from clients, and send it back. False: this server keeps its own copy per character,
    /// clients' copies are neither read nor changed.</summary>
    public bool ImportProgress { get; init; } = true;

    /// <summary>The largest offer, in bytes of JSON values.</summary>
    public int MaxImportBytes { get; init; } = 200 * 1024;

    /// <summary>The largest single value.</summary>
    public int MaxValueBytes { get; init; } = 64 * 1024;

    /// <summary>Most keys in an offer.</summary>
    public int MaxKeys { get; init; } = 256;

    /// <summary>Characters one account may bring to this world. The game's own <c>known_users</c> keeps 10.</summary>
    public int MaxCharactersPerAccount { get; init; } = 32;

    internal static CharacterSettings Load()
    {
        var path = Path.Combine(KilnPaths.Config, "characters.json");
        try
        {
            if (!File.Exists(path)) return new CharacterSettings();
            return JsonSerializer.Deserialize<CharacterSettings>(File.ReadAllBytes(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? new CharacterSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: {path}: {e.Message}; imports stay refused until it is fixed");
            return new CharacterSettings { ImportProgress = false };
        }
    }
}

/// <summary>The store's side of <see cref="CharacterData"/>: scopes "character/&lt;key&gt;/&lt;mod&gt;" and
/// "carried/&lt;key&gt;/&lt;mod&gt;" in the world's sidecar, the migration, and the raw access the transport uses.</summary>
internal static class CharacterStore
{
    private const string MetaPrefix = "carried-meta/", OwnMod = "kiln-saves", LastKey = "lastCharacter";
    private static bool _accountOnlyWarned;

    internal static CharacterKey KeyOf(EntityId player)
    {
        if (player.IsNone || !Players.Available) return default;
        return Players.Find(player) is { } p ? KeyOf(p) : default;
    }

    internal static CharacterKey KeyOf(Player p)
    {
        var id = PlayerCharacters.IdOf(p);
        if (id != Guid.Empty) return new CharacterKey(PlayerCharacters.AccountOf(p), id);
        var fallback = LegacyIds(p).FirstOrDefault();
        if (fallback == 0) return default;
        if (!_accountOnlyWarned)
        {
            _accountOnlyWarned = true;
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: {p.Name} has no character id on this build: characters of one account share their data here, and nothing travels");
        }
        return new CharacterKey(fallback, Guid.Empty);
    }

    internal static CharacterKey Need(EntityId player)
    {
        var k = KeyOf(player);
        return k.IsNone ? throw new ArgumentException($"entity {player} is not a player with a known character", nameof(player)) : k;
    }

    /// <summary>The save keys a player's account-wide data may be under, as Kiln's services chose them before characters:
    /// the Steam id, the game's account id, the host key.</summary>
    internal static IReadOnlyList<ulong> LegacyIds(Player p)
    {
        var list = new List<ulong>(3);
        if (p.SteamId != 0) list.Add(p.SteamId);
        if (p.AccountId != 0) list.Add(p.AccountId);
        if (Client.Available && Client.IsHosting && p.Slot == 0) list.Add(HostKey);
        return list;
    }

    /// <summary>What Kiln.Skills and Kiln.Quests filed a local game's host under when the game gave no id.</summary>
    internal const ulong HostKey = 1;

    private static WorldStore Store(string api)
    {
        if (!SavesRuntime.Ensure()) throw new NotSupportedException("kiln.saves is unavailable");
        return SavesRuntime.Store;
    }

    private static string Scope(string prefix, CharacterKey c, string mod) => $"{prefix}{c.Text}/{mod}";

    private static void CheckCharacter(CharacterKey c)
    {
        if (c.IsNone) throw new ArgumentException("no character", nameof(c));
    }

    private static (WorldStore store, ModContext mod, WorldStore.Bundle b) Bundle(string prefix, CharacterKey c, string api)
    {
        var store = Store(api);
        var mod = WorldStore.CallerMod(api);
        CheckCharacter(c);
        if (prefix == CharacterData.CarriedPrefix) CharacterSync.EnsureStarted();  // a mod's carried data travels, whichever service started first
        return (store, mod, store.GetBundle(Scope(prefix, c, mod.Info.Id), mod));
    }

    internal static T Get<T>(string prefix, CharacterKey c, string key, string api) where T : class, new()
    {
        var (store, mod, b) = Bundle(prefix, c, api);
        WorldStore.CheckKey(key);
        if (!b.Keys.TryGetValue(key, out var s)) b.Keys[key] = s = new WorldStore.Slot();
        return store.GetOrCreate<T>(s, mod, b.Scope, key, create: true);
    }

    internal static bool TryGet<T>(string prefix, CharacterKey c, string key, string api, [NotNullWhen(true)] out T? value) where T : class
    {
        var (store, mod, b) = Bundle(prefix, c, api);
        value = null;
        if (!b.Keys.TryGetValue(WorldStore.CheckKey(key), out var s) || (s.Value == null && s.Json is null or "null")) return false;
        value = store.GetOrCreate<T>(s, mod, b.Scope, key, create: false);
        return true;
    }

    internal static void Set<T>(string prefix, CharacterKey c, string key, T value, string api) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var (_, _, b) = Bundle(prefix, c, api);
        b.Keys[WorldStore.CheckKey(key)] = new WorldStore.Slot { Value = value, Type = typeof(T) };
    }

    internal static bool Remove(string prefix, CharacterKey c, string key, string api) => Bundle(prefix, c, api).b.Keys.Remove(WorldStore.CheckKey(key));

    internal static bool Contains(string prefix, CharacterKey c, string key, string api) => Bundle(prefix, c, api).b.Keys.ContainsKey(WorldStore.CheckKey(key));

    internal static IReadOnlyList<string> KeysOf(string prefix, CharacterKey c, string api) => Bundle(prefix, c, api).b.Keys.Keys.ToList();

    internal static IReadOnlyList<CharacterKey> Characters(string prefix, string api)
    {
        var store = Store(api);
        var mod = WorldStore.CallerMod(api);
        store.EnsureOpen();
        var suffix = "/" + mod.Info.Id;
        var list = new List<CharacterKey>();
        foreach (var b in store.BundlesWithPrefix(prefix))
            if (b.Scope.EndsWith(suffix, StringComparison.Ordinal) && b.Keys.Count > 0 && CharacterKey.TryParse(b.Scope.Split('/')[1], out var k)) list.Add(k);
        return list;
    }

    internal static bool Migrate(string prefix, CharacterKey c, string key, IReadOnlyList<ulong> ids, string api)
    {
        var (store, mod, b) = Bundle(prefix, c, api);
        WorldStore.CheckKey(key);
        if (b.Keys.TryGetValue(key, out var have) && (have.Value != null || have.Json != null)) return false;
        foreach (var id in ids)
        {
            if (id == 0) continue;
            var old = store.FindBundle($"player/{id}/{mod.Info.Id}");
            if (old is null || !old.Keys.Remove(key, out var slot)) continue;
            b.Keys[key] = slot;
            SavesRuntime.Log(LogLevel.Info, $"Kiln.Saves: {mod.Info.Id} key '{key}': the account-wide data of {id} now belongs to character {c} (migrated once; other characters of the account start without it)");
            return true;
        }
        return false;
    }

    // ---- the transport's raw access (tick thread, no mod context needed) ----

    internal static long RevisionOf(CharacterKey c)
    {
        var store = Store("CharacterData.Carried.Revision");
        store.EnsureOpen();
        return store.FindBundle(Scope(MetaPrefix, c, OwnMod))?.Keys.GetValueOrDefault("revision")?.Json is { } j && long.TryParse(j, out var v) ? v : 0;
    }

    internal static void SetRevision(CharacterKey c, long revision)
    {
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        var b = store.GetBundleRaw(Scope(MetaPrefix, c, OwnMod), OwnMod);
        if (!b.Keys.TryGetValue("revision", out var s)) b.Keys["revision"] = s = new WorldStore.Slot();
        s.Json = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>mod -> key -> JSON of everything the character carries, as of now (live objects are serialized).</summary>
    internal static Dictionary<string, Dictionary<string, string>> Snapshot(CharacterKey c)
    {
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        var prefix = $"{CharacterData.CarriedPrefix}{c.Text}/";
        var all = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var b in store.BundlesWithPrefix(prefix))
        {
            Dictionary<string, string>? map = null;
            foreach (var (k, s) in b.Keys)
            {
                var json = store.JsonOf(b, s, k);
                if (json is null) continue;
                (map ??= all[b.Mod] = new Dictionary<string, string>(StringComparer.Ordinal))[k] = json;
            }
        }
        return all;
    }

    /// <summary>Replaces what the character carries (live objects a service holds are detached: it asks again).</summary>
    internal static void Replace(CharacterKey c, IReadOnlyDictionary<string, Dictionary<string, string>> mods)
    {
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        var prefix = $"{CharacterData.CarriedPrefix}{c.Text}/";
        foreach (var b in store.BundlesWithPrefix(prefix).ToArray()) b.Keys.Clear();
        foreach (var (mod, keys) in mods)
        {
            var b = store.GetBundleRaw(prefix + mod, mod);
            foreach (var (k, json) in keys) b.Keys[k] = new WorldStore.Slot { Json = json };
        }
    }

    /// <summary>How many characters of this account carry data in this world (the import cap).</summary>
    internal static int CharactersOfAccount(ulong account)
    {
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        var prefix = $"{CharacterData.CarriedPrefix}{account:x16}.";
        return store.BundlesWithPrefix(prefix).Select(b => b.Scope.Split('/')[1]).Distinct(StringComparer.Ordinal).Count();
    }

    internal static void RememberLast(CharacterKey c, IEnumerable<ulong> playerIds)
    {
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        foreach (var id in playerIds)
        {
            if (id == 0) continue;
            var b = store.GetBundleRaw($"player/{id}/{OwnMod}", OwnMod);
            if (!b.Keys.TryGetValue(LastKey, out var s)) b.Keys[LastKey] = s = new WorldStore.Slot();
            s.Json = JsonSerializer.Serialize(c.Text);
        }
    }

    internal static CharacterKey LastOf(ulong playerId)
    {
        if (playerId == 0 || !SavesRuntime.Ensure()) return default;
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        var json = store.FindBundle($"player/{playerId}/{OwnMod}")?.Keys.GetValueOrDefault(LastKey)?.Json;
        if (json is null) return default;
        try
        {
            return CharacterKey.TryParse(JsonSerializer.Deserialize<string>(json), out var k) ? k : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
