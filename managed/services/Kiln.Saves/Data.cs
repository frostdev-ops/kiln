// SPDX-License-Identifier: MIT
using System.Diagnostics.CodeAnalysis;

namespace Kiln;

/// <summary>
/// Persistent per-world data of the calling mod (Forge's <c>SavedData</c>): typed objects under string keys, stored as
/// JSON (System.Text.Json, public properties and fields) in Kiln's sidecar of the world,
/// <c>kiln/data/worlds/&lt;world id&gt;/</c>, never in the game's save folder.
/// </summary>
/// <remarks>
/// <para>Loaded when first used (on a dedicated server a mod's <c>OnLoad</c> already sees the data of the world file the server is
/// about to load; a game client's hosted world has its data once the game has loaded it: <see cref="Saves.WorldDataReady"/>).
/// <see cref="Get{T}"/> returns a live object: change it in place; Kiln serializes it at every world save,
/// right after the <see cref="WorldSavingEvent"/> handlers, into the generation of that save (no "mark dirty"
/// needed; unchanged data is not rewritten). A restart, a crash or <c>kill -9</c> at any moment gives back the
/// generation that belongs to the world file on disk: the old one or the new one, never a mix.</para>
/// <para>Keys belong to the calling mod (its id from <c>kiln.mod.json</c>); other mods cannot see them. Tick thread,
/// from mod code. A mod reload keeps the data: objects are serialized when the mod unloads and read again into the
/// new instance's types.</para>
/// </remarks>
public static class WorldData
{
    /// <summary>The object stored under <paramref name="key"/>, created with <c>new T()</c> if there is none.</summary>
    /// <exception cref="InvalidDataException">The stored JSON does not read as <typeparamref name="T"/> (it is kept).</exception>
    public static T Get<T>(string key) where T : class, new()
    {
        var (store, mod, b) = Bundle("WorldData.Get");
        WorldStore.CheckKey(key);
        if (!b.Keys.TryGetValue(key, out var s)) b.Keys[key] = s = new WorldStore.Slot();
        return store.GetOrCreate<T>(s, mod, b.Scope, key, create: true);
    }

    public static bool TryGet<T>(string key, [NotNullWhen(true)] out T? value) where T : class
    {
        var (store, mod, b) = Bundle("WorldData.TryGet");
        value = null;
        if (!b.Keys.TryGetValue(WorldStore.CheckKey(key), out var s) || (s.Value == null && s.Json is null or "null")) return false;
        value = store.GetOrCreate<T>(s, mod, b.Scope, key, create: false);
        return true;
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> (replacing what was there).</summary>
    public static void Set<T>(string key, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var (_, _, b) = Bundle("WorldData.Set");
        b.Keys[WorldStore.CheckKey(key)] = new WorldStore.Slot { Value = value, Type = typeof(T) };
    }

    /// <summary>Removes a key (from the next save on).</summary>
    public static bool Remove(string key)
    {
        var (_, _, b) = Bundle("WorldData.Remove");
        return b.Keys.Remove(WorldStore.CheckKey(key));
    }

    public static bool Contains(string key) => Bundle("WorldData.Contains").b.Keys.ContainsKey(WorldStore.CheckKey(key));

    /// <summary>The calling mod's keys.</summary>
    public static IReadOnlyList<string> Keys => Bundle("WorldData.Keys").b.Keys.Keys.ToList();

    /// <summary>The world these keys belong to (null before a new world's first save).</summary>
    public static string? WorldId => Saves.WorldId;

    /// <summary>Which stored generation was loaded for the world file on disk.</summary>
    public static DataMatch Loaded => SavesRuntime.Ensure() ? SavesRuntime.Store.Match : DataMatch.None;

    private static (WorldStore store, ModContext mod, WorldStore.Bundle b) Bundle(string api)
    {
        if (!SavesRuntime.Ensure()) throw new NotSupportedException("kiln.saves is unavailable");
        var mod = WorldStore.CallerMod(api);
        var store = SavesRuntime.Store;
        return (store, mod, store.GetBundle("world/" + mod.Info.Id, mod));
    }
}

/// <summary>
/// Persistent per-player data of the calling mod, keyed by the player's SteamID64 (Forge's player persistent data /
/// player attachments). Same storage and save rules as <see cref="WorldData"/>: it belongs to the world (a player has
/// separate data in each world), lives in the world's sidecar and is written with every world save.
/// </summary>
/// <remarks>Tick thread, from mod code. The player does not need to be online. <see cref="EntityData"/> on a player
/// entity uses this store (by the player's Steam id).</remarks>
public static class PlayerData
{
    public static T Get<T>(ulong steamId, string key) where T : class, new()
    {
        var (store, mod, b) = Bundle(steamId, "PlayerData.Get");
        WorldStore.CheckKey(key);
        if (!b.Keys.TryGetValue(key, out var s)) b.Keys[key] = s = new WorldStore.Slot();
        return store.GetOrCreate<T>(s, mod, b.Scope, key, create: true);
    }

    public static bool TryGet<T>(ulong steamId, string key, [NotNullWhen(true)] out T? value) where T : class
    {
        var (store, mod, b) = Bundle(steamId, "PlayerData.TryGet");
        value = null;
        if (!b.Keys.TryGetValue(WorldStore.CheckKey(key), out var s) || (s.Value == null && s.Json is null or "null")) return false;
        value = store.GetOrCreate<T>(s, mod, b.Scope, key, create: false);
        return true;
    }

    public static void Set<T>(ulong steamId, string key, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var (_, _, b) = Bundle(steamId, "PlayerData.Set");
        b.Keys[WorldStore.CheckKey(key)] = new WorldStore.Slot { Value = value, Type = typeof(T) };
    }

    public static bool Remove(ulong steamId, string key) => Bundle(steamId, "PlayerData.Remove").b.Keys.Remove(WorldStore.CheckKey(key));

    public static bool Contains(ulong steamId, string key) => Bundle(steamId, "PlayerData.Contains").b.Keys.ContainsKey(WorldStore.CheckKey(key));

    public static IReadOnlyList<string> Keys(ulong steamId) => Bundle(steamId, "PlayerData.Keys").b.Keys.Keys.ToList();

    /// <summary>Steam ids the calling mod has data for in this world.</summary>
    public static IReadOnlyList<ulong> Players
    {
        get
        {
            if (!SavesRuntime.Ensure()) throw new NotSupportedException("kiln.saves is unavailable");
            var mod = WorldStore.CallerMod("PlayerData.Players");
            SavesRuntime.Store.EnsureOpen();
            var suffix = "/" + mod.Info.Id;
            return SavesRuntime.Store.BundlesWithPrefix("player/")
                .Where(b => b.Scope.EndsWith(suffix, StringComparison.Ordinal) && b.Keys.Count > 0)
                .Select(b => ulong.TryParse(b.Scope.Split('/')[1], out var id) ? id : 0).Where(id => id != 0).Distinct().ToList();
        }
    }

    internal static (WorldStore store, ModContext mod, WorldStore.Bundle b) Bundle(ulong steamId, string api)
    {
        if (!SavesRuntime.Ensure()) throw new NotSupportedException("kiln.saves is unavailable");
        if (steamId == 0) throw new ArgumentException("a Steam id is required", nameof(steamId));
        var mod = WorldStore.CallerMod(api);
        var store = SavesRuntime.Store;
        return (store, mod, store.GetBundle($"player/{steamId}/{mod.Info.Id}", mod));
    }
}

/// <summary>
/// Data the calling mod attaches to an entity (Forge's capabilities / data attachments). It lives as long as the entity:
/// destroyed entities lose it (checked every second), and entities the game saves get it back after a restart.
/// </summary>
/// <remarks>
/// <para>How it survives a restart: the game renumbers entities on load and keeps no id of its own, so Kiln stores each
/// attachment with the entity's template and position at the save and gives it to the loaded entity of that template
/// at that place (props: within 0.5 m, they come back exactly; creatures and NPCs: within 16 m, nearest unclaimed).
/// Entities whose template never reaches the save (most enemies, the generic loot pickup) keep attachments for the
/// session only (<see cref="IsPersistent"/>).</para>
/// <para>Player entities (ids 1..1023) map to <see cref="PlayerData"/> under the player's Steam id: the data follows the
/// player, not the slot.</para>
/// <para>Tick thread, from mod code. Needs kiln.spawn for entity lookups (<see cref="Available"/>).</para>
/// </remarks>
public static class EntityData
{
    /// <summary>True when kiln.saves and kiln.spawn are both available.</summary>
    public static bool Available => SavesRuntime.Ensure() && SpawnLink.Available;

    public static T Get<T>(EntityId entity, string key) where T : class, new()
    {
        if (entity.IsPlayer) return PlayerData.Get<T>(SteamOfEntity(entity), key);
        var (store, mod, keys) = Keys(entity, "EntityData.Get", create: true);
        WorldStore.CheckKey(key);
        if (!keys!.TryGetValue(key, out var s)) keys[key] = s = new WorldStore.Slot();
        return store.GetOrCreate<T>(s, mod, "entity/" + mod.Info.Id, key, create: true);
    }

    public static bool TryGet<T>(EntityId entity, string key, [NotNullWhen(true)] out T? value) where T : class
    {
        if (entity.IsPlayer) return PlayerData.TryGet(SteamOfEntity(entity), key, out value);
        value = null;
        var (store, mod, keys) = Keys(entity, "EntityData.TryGet", create: false);
        if (keys == null || !keys.TryGetValue(WorldStore.CheckKey(key), out var s) || (s.Value == null && s.Json is null or "null")) return false;
        value = store.GetOrCreate<T>(s, mod, "entity/" + mod.Info.Id, key, create: false);
        return true;
    }

    public static void Set<T>(EntityId entity, string key, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        if (entity.IsPlayer)
        {
            PlayerData.Set(SteamOfEntity(entity), key, value);
            return;
        }
        var (_, _, keys) = Keys(entity, "EntityData.Set", create: true);
        keys![WorldStore.CheckKey(key)] = new WorldStore.Slot { Value = value, Type = typeof(T) };
    }

    public static bool Remove(EntityId entity, string key)
    {
        if (entity.IsPlayer) return PlayerData.Remove(SteamOfEntity(entity), key);
        var (_, _, keys) = Keys(entity, "EntityData.Remove", create: false);
        return keys != null && keys.Remove(WorldStore.CheckKey(key));
    }

    public static bool Has(EntityId entity, string key)
    {
        if (entity.IsPlayer) return PlayerData.Contains(SteamOfEntity(entity), key);
        var (_, _, keys) = Keys(entity, "EntityData.Has", create: false);
        return keys != null && keys.ContainsKey(WorldStore.CheckKey(key));
    }

    /// <summary>Removes everything the calling mod attached to the entity.</summary>
    public static bool Clear(EntityId entity)
    {
        var (_, mod, _) = Keys(entity, "EntityData.Clear", create: false);
        return SavesRuntime.Store.Entities.Find(entity)?.ByMod.Remove(mod.Info.Id) == true;
    }

    /// <summary>Whether the entity's attachments survive a restart (its template is saved by the game).</summary>
    public static bool IsPersistent(EntityId entity)
    {
        if (entity.IsPlayer) return true;
        if (!Available) return false;
        var info = SpawnLink.Inspect(entity);
        return info?.Template is { } g && !SpawnLink.Template(g).neverSaved;
    }

    /// <summary>Live entities the calling mod has attachments on.</summary>
    public static IReadOnlyList<EntityId> Entities
    {
        get
        {
            var (_, mod, _) = Keys(EntityId.None, "EntityData.Entities", create: false);
            return SavesRuntime.Store.Entities.LiveRecs.Where(r => r.ByMod.TryGetValue(mod.Info.Id, out var k) && k.Count > 0)
                .Select(r => new EntityId(r.Entity)).ToList();
        }
    }

    /// <summary>Loaded attachments of the calling mod still waiting for their entity (diagnostics).</summary>
    public static int Waiting
    {
        get
        {
            Keys(EntityId.None, "EntityData.Waiting", create: false);
            return SavesRuntime.Store.Entities.UnresolvedCount;
        }
    }

    private static ulong SteamOfEntity(EntityId e) => PlayersLink.SteamId(e) is var id and not 0
        ? id
        : throw new InvalidOperationException($"entity {e} is a player slot without a known Steam id (EntityData on players needs kiln.players)");

    private static (WorldStore store, ModContext mod, SortedDictionary<string, WorldStore.Slot>? keys) Keys(EntityId e, string api, bool create)
    {
        if (!SavesRuntime.Ensure()) throw new NotSupportedException("kiln.saves is unavailable");
        var mod = WorldStore.CallerMod(api);
        var store = SavesRuntime.Store;
        store.EnsureOpen();
        store.JsonFor(mod);  // watches the mod (its objects are frozen when it unloads)
        if (e.IsNone) return (store, mod, null);
        var rec = create ? store.Entities.Attach(e) : store.Entities.FindOrMatch(e);
        if (rec == null) return (store, mod, null);
        if (!rec.ByMod.TryGetValue(mod.Info.Id, out var keys))
        {
            if (!create) return (store, mod, null);
            rec.ByMod[mod.Info.Id] = keys = new SortedDictionary<string, WorldStore.Slot>(StringComparer.Ordinal);
        }
        return (store, mod, keys);
    }
}
