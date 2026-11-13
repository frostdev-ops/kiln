// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Kiln;

/// <summary>Why the game saved the world.</summary>
public enum SaveReason
{
    /// <summary>Another caller (or a hook chained in front of Kiln's).</summary>
    Unknown = 0,
    /// <summary>The game's timer: every 30 s of real time while the world runs.</summary>
    Autosave = 1,
    /// <summary>The server is stopping (SIGINT, console quit).</summary>
    Shutdown = 2,
    /// <summary>A player's session ended (the game saves when someone leaves).</summary>
    PlayerLeft = 3,
    /// <summary><see cref="Saves.Request"/> (an admin "save now").</summary>
    Request = 4,
    /// <summary><see cref="Saves.Backup"/>.</summary>
    Backup = 5,
}

/// <summary>Which sidecar generation <see cref="WorldData"/> loaded for the world file on disk.</summary>
public enum DataMatch
{
    /// <summary>No sidecar data for this world yet (first run with Kiln, or a new world).</summary>
    None = 0,
    /// <summary>The generation written for exactly this world file (revision and save time match).</summary>
    Exact = 1,
    /// <summary>The generation staged for this revision, not yet confirmed (the server stopped right after the
    /// write, before Kiln confirmed it). Same data as Exact in practice.</summary>
    Staged = 2,
    /// <summary>An older generation: the world file is newer than any sidecar (it was saved without Kiln, or a save
    /// was lost). Logged as a warning.</summary>
    Older = 3,
}

/// <summary>
/// Fired on the tick thread when the game has accepted a world save, before it snapshots the world (Forge's
/// <c>LevelEvent.Save</c>). Data written to <see cref="WorldData"/>, <see cref="PlayerData"/> or <see cref="EntityData"/>
/// by a handler belongs to this save: Kiln writes the mod data right after the last handler, as generation
/// <see cref="Revision"/>, and the game writes the world file with the same revision a few ms later.
/// </summary>
/// <remarks>
/// <para>Thread: the tick thread. Usually (<see cref="BeforeSnapshot"/> true) it runs inside the game's save request,
/// after the sim step and after Kiln waited for the world's jobs, so handlers may read game state like in
/// <c>Tick.Post</c>. If Kiln could not run handlers there, it runs them at the start of the next step (Pre).</para>
/// <para>The shutdown save: Kiln unloads mods on a stop signal before the game makes its final save, so mods get
/// this event (reason <see cref="SaveReason.Shutdown"/>) right after <c>ServerStoppingEvent</c>, while they still
/// run; <see cref="Revision"/> is then the expected revision. What they write here, and what their data objects hold
/// when they unload, is written with the final save.</para>
/// <para>Not cancelable: the game's save always happens. Fields are read-only.</para>
/// </remarks>
public sealed class WorldSavingEvent : KilnEvent
{
    internal WorldSavingEvent(SaveReason reason, uint revision, string? worldId, string? worldFile, bool beforeSnapshot, ulong step)
    {
        Reason = reason;
        Revision = revision;
        WorldId = worldId;
        WorldFile = worldFile;
        BeforeSnapshot = beforeSnapshot;
        Step = step;
    }

    public SaveReason Reason { get; }

    /// <summary>The revision the world file will have (+0x1C of its header) once this save is written.</summary>
    public uint Revision { get; }

    public string? WorldId { get; }
    public string? WorldFile { get; }

    /// <summary>True when handlers run inside the game's request, before its snapshot (the normal case).</summary>
    public bool BeforeSnapshot { get; }

    public ulong Step { get; }
    public override string ToString() => $"saving ({Reason}, revision {Revision})";
}

/// <summary>
/// Fired on the tick thread at the start of the step after the game finished writing the world file (or failed to).
/// By then Kiln has confirmed the mod data of this generation; backups made from here are consistent.
/// </summary>
/// <remarks>Thread: the tick thread (Pre). Not cancelable; fields are read-only. A failed write is retried by the game
/// (up to 4 times): a later event with <see cref="Success"/> true follows when a retry works.</remarks>
public sealed class WorldSavedEvent : KilnEvent
{
    internal WorldSavedEvent(SaveReason reason, bool success, uint revision, string? worldId, string? worldFile,
        TimeSpan duration, int result, bool timedOut)
    {
        Reason = reason;
        Success = success;
        Revision = revision;
        WorldId = worldId;
        WorldFile = worldFile;
        Duration = duration;
        Result = result;
        TimedOut = timedOut;
    }

    public SaveReason Reason { get; }
    public bool Success { get; }

    /// <summary>The world file's revision after the write.</summary>
    public uint Revision { get; }

    public string? WorldId { get; }
    public string? WorldFile { get; }

    /// <summary>From the game accepting the request to the file being complete.</summary>
    public TimeSpan Duration { get; }

    /// <summary>The game's result byte (0 = ok).</summary>
    public int Result { get; }

    /// <summary>A Kiln request the game did not accept within 60 s (nothing was written).</summary>
    public bool TimedOut { get; }

    public override string ToString() => Success ? $"saved ({Reason}, revision {Revision}, {Duration.TotalMilliseconds:F0} ms)"
        : $"save failed ({Reason}, result {Result}{(TimedOut ? ", timed out" : "")})";
}

/// <summary>Fired on the tick thread once the server finished loading the world save (Forge's <c>LevelEvent.Load</c>).
/// <see cref="WorldData"/> is already loaded (it is readable before this, from <c>OnLoad</c> on).</summary>
/// <remarks>Thread: the tick thread (Pre). Not cancelable. Entities from the save appear over the next seconds;
/// <see cref="EntityData"/> keeps re-matching them for 10 minutes.</remarks>
public sealed class WorldLoadedEvent : KilnEvent
{
    internal WorldLoadedEvent(string? worldId, uint revision, bool newWorld, string? worldFile, DataMatch data)
    {
        WorldId = worldId;
        Revision = revision;
        NewWorld = newWorld;
        WorldFile = worldFile;
        Data = data;
    }

    public string? WorldId { get; }
    public uint Revision { get; }

    /// <summary>No world file existed: a new world (its file and id appear with the first save).</summary>
    public bool NewWorld { get; }

    public string? WorldFile { get; }

    /// <summary>Which sidecar generation was loaded for this world file.</summary>
    public DataMatch Data { get; }

    public override string ToString() => $"loaded {WorldId} revision {Revision} (data {Data})";
}

/// <summary>The outcome of <see cref="Saves.Request"/>.</summary>
public sealed record SaveResult(bool Success, SaveReason Reason, uint Revision, TimeSpan Duration, string? Error) : IKilnResult
{
    /// <summary>The save was written: <see cref="Success"/> (the contract's name for it).</summary>
    public bool Ok => Success;

    // IKilnResult's reason is null when the save was written; this record's own Reason is what asked for the save.
    string? IKilnResult.Reason => Success ? null : Error ?? "not saved";

    public override string ToString() => Success ? $"saved revision {Revision} in {Duration.TotalMilliseconds:F0} ms" : $"not saved: {Error}";
}

/// <summary>What <see cref="Saves.Status"/> reports.</summary>
/// <param name="State">"none", "loading", "running" or "saving".</param>
/// <param name="WorldId">The world's GUID text (null before a new world's first save).</param>
/// <param name="LastSaved">When the newest world write of this session finished (null: none yet).</param>
/// <param name="Revision">The world file's newest written (or loaded) revision.</param>
/// <param name="AutosaveInterval">The game's autosave interval (30 s on this build; null if unknown).</param>
/// <param name="LastReason">Reason of the newest accepted save.</param>
/// <param name="LastDuration">Accepted request to finished file, newest save.</param>
/// <param name="Accepted">Saves the game accepted this session.</param>
/// <param name="Written">Successful world writes this session.</param>
/// <param name="Failed">Failed writes and timed-out requests.</param>
/// <param name="Refused">Requests the game refused (a save was running, or the world was not running).</param>
/// <param name="RequestPending">A Kiln request waits for the next step.</param>
/// <param name="Slot">The game's world slot id (low byte = index + 1, high byte = storage).</param>
/// <param name="DataGeneration">The sidecar generation Kiln last loaded or wrote (= a world revision).</param>
/// <param name="DataMatch">How the loaded sidecar matched the world file.</param>
/// <param name="CanRequest">The game's save request was found (Request and Backup work).</param>
/// <param name="CanObserveWrites">World writes are observed (WorldSavedEvent).</param>
public sealed record SaveStatus(
    string State,
    string? WorldId,
    DateTimeOffset? LastSaved,
    uint Revision,
    TimeSpan? AutosaveInterval,
    SaveReason LastReason,
    TimeSpan LastDuration,
    long Accepted,
    long Written,
    long Failed,
    long Refused,
    bool RequestPending,
    int Slot,
    uint DataGeneration,
    DataMatch DataMatch,
    bool CanRequest,
    bool CanObserveWrites);

/// <summary>A backup made by <see cref="Saves.Backup"/>: the world file and the matching Kiln sidecar generation.</summary>
public sealed record BackupInfo(
    string Name,
    string Directory,
    DateTimeOffset Created,
    string WorldId,
    uint Revision,
    uint WorldUnix,
    SaveReason Reason,
    int Files,
    long Bytes,
    int DataBundles)
{
    public override string ToString() => $"{Name}: world {WorldId} revision {Revision}, {Files} files, {Bytes / 1024} KB";
}

/// <summary>
/// The world save: events (<see cref="WorldSavingEvent"/>, <see cref="WorldSavedEvent"/>, <see cref="WorldLoadedEvent"/>
/// on the event bus), "save now", the world's identity and paths, and consistent backups.
/// </summary>
/// <remarks>Members are thread-safe unless noted. Subscribing to a save event (with <c>[Subscribe]</c> or
/// <c>Events.On</c>) or touching any member starts Kiln.Saves.</remarks>
public static class Saves
{
    /// <summary>True when the native service is loaded on this build.</summary>
    public static bool Available => SavesRuntime.Ensure();

    /// <summary>The runtime restages WorldData after the request's final simulation and before the game's pending snapshot.
    /// Inventory journals need this as well as observation of native inventory commits.</summary>
    public static bool SnapshotStagingAvailable => SavesRuntime.Ensure() && SavesNative.ReadStatus() is { } status && (status.Features & SavesNative.CanSnapshotStage) != 0;

    /// <summary>The world's GUID (from the world file header, or the game's memory once it saved), as text like
    /// <c>11111111-2222-4333-8444-555555555555</c>; null before a new world's first save or when unavailable.</summary>
    public static string? WorldId => SavesRuntime.Ensure() ? SavesRuntime.WorldId : null;

    /// <summary>True when <see cref="WorldData"/>, <see cref="PlayerData"/>, <see cref="CharacterData"/> and <see cref="EntityData"/> read
    /// this world's data. Always on a dedicated server. A game client's hosted world starts (<see cref="ServerStartedEvent"/>,
    /// server-side mods' <c>OnLoad</c>) about 10 s before the game loads it: until <see cref="WorldLoadedEvent"/> they are empty, and what is
    /// written to them goes, unless it is a new world. Decide nothing from "this key is not there" before this is true.</summary>
    public static bool WorldDataReady => SavesRuntime.WorldDataReady;

    /// <summary>Lab, tick thread: plays what closing a hosted world does to the mod data, on a dedicated server, where a world never
    /// closes. Until <see cref="LabWorldLoaded"/> the data is empty, and <see cref="WorldDataReady"/> is false. For lab mods
    /// only (<c>KILNX001</c>).</summary>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void LabCloseWorld() => SavesRuntime.LabCloseWorld();

    /// <summary>Lab, tick thread: plays the next world's load (a world id and revision of the test's choosing). For lab mods
    /// only (<c>KILNX001</c>).</summary>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void LabWorldLoaded(string? worldId, uint revision, uint worldUnix = 0, bool newWorld = false) =>
        SavesRuntime.LabWorldLoaded(worldId, revision, worldUnix, newWorld);

    /// <summary>The server's save directory (enshrouded_server.json <c>saveDirectory</c>).</summary>
    public static string? SaveDirectory => SavesRuntime.Ensure() ? SavesNative.GetPath(SavesNative.PathSaveDir) : null;

    /// <summary>The running world's file (<c>&lt;save dir&gt;/world0.ksd</c> on a dedicated server).</summary>
    public static string? WorldFile => SavesRuntime.Ensure() ? SavesRuntime.WorldFile : null;

    /// <summary>Kiln's sidecar folder of this world: <c>kiln/data/worlds/&lt;world id&gt;</c>.</summary>
    public static string? DataDirectory => SavesRuntime.Ensure() ? SavesRuntime.Store.Root : null;

    /// <summary>Where <see cref="Backup"/> writes: <c>kiln/backups</c>.</summary>
    public static string? BackupDirectory => SavesRuntime.Ensure() ? SavesNative.GetPath(SavesNative.PathBackups) : null;

    /// <summary>Last save, interval, counters; null when unavailable.</summary>
    public static SaveStatus? Status => SavesRuntime.Ensure() ? SavesRuntime.ReadStatus() : null;

    /// <summary>Asks the game to save the world now (its own save, the one the 30 s timer makes). Completes on the tick
    /// thread when a save that started after this call is written (or failed: the game refuses while the world is not
    /// running; the request is dropped after 60 s). Call from mod code.</summary>
    [RequiresCapability("kiln.saves")]
    public static Task<SaveResult> Request() => SavesRuntime.Request(SaveReason.Request);

    /// <summary>Saves the world now and then copies the world file and the matching Kiln sidecar generation to
    /// <c>kiln/backups/&lt;UTC stamp&gt;-&lt;name&gt;/</c>. The copy is made on the tick thread right after the
    /// write, before the game can start another save, so it is consistent. Call from mod code.</summary>
    [RequiresCapability("kiln.saves")]
    public static Task<BackupInfo> Backup(string name) => SavesRuntime.Backup(name);

    /// <summary>Backups in <c>kiln/backups</c>, oldest first.</summary>
    public static IReadOnlyList<BackupInfo> Backups => SavesRuntime.Ensure() ? SavesBackups.List() : [];

    /// <summary>Stages a restore of backup <paramref name="name"/> (its folder name or its given name, newest match)
    /// for the next server start: kiln.saves applies it before the game opens its save (the files it replaces are kept
    /// in <c>kiln/backups/_before-restore-&lt;stamp&gt;</c>). Stop the server afterwards (it saves on the way out: that
    /// is fine, the restore replaces it). Returns the backup that will be restored.</summary>
    public static BackupInfo RestoreOnNextStart(string name) => SavesBackups.StageRestore(name);

    /// <summary>Cancels a staged restore; false if none was staged.</summary>
    public static bool CancelRestore() => SavesBackups.CancelRestore();

    /// <summary>The staged restore, or null.</summary>
    public static string? PendingRestore => SavesBackups.PendingRestore();

    internal static string Text(SaveReason r) => r switch
    {
        SaveReason.Autosave => "autosave",
        SaveReason.Shutdown => "shutdown",
        SaveReason.PlayerLeft => "player-left",
        SaveReason.Request => "request",
        SaveReason.Backup => "backup",
        _ => "unknown",
    };

    internal static bool TryParse(string? s, [NotNullWhen(true)] out SaveReason? r)
    {
        r = s switch
        {
            "autosave" => SaveReason.Autosave,
            "shutdown" => SaveReason.Shutdown,
            "player-left" => SaveReason.PlayerLeft,
            "request" => SaveReason.Request,
            "backup" => SaveReason.Backup,
            "unknown" => SaveReason.Unknown,
            _ => null,
        };
        return r != null;
    }
}
