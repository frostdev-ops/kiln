// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;

namespace Kiln.Testing;

/// <summary>Starts the guided check runner in this process once, when the first mod registers checks: the runner on
/// Kiln's service tick, the control folder, the joined channel and, in a game client, the screen, HUD line and keys.</summary>
internal static class ChecksService
{
    private static readonly object Lock = new();
    private static CheckRunner? _runner;
    private static ControlFolder? _control;
    private static CheckNet? _net;

    /// <summary>Tests turn it off: registration then only fills the registry.</summary>
    internal static bool AutoStart = true;

    public static CheckRunner? Runner => _runner;
    public static CheckNet? Net => _net;

    public static void EnsureStarted()
    {
        if (!AutoStart) return;
        lock (Lock)
        {
            if (_runner is not null) return;
            var env = new LiveEnv();
            var runner = new CheckRunner(env, new Ledger(Path.Combine(KilnPaths.State, "client-checks.json")), Scrubber.ForThisMachine());
            _runner = runner;
            _control = new ControlFolder(runner, Path.Combine(KilnPaths.Control, "checks"), env);
            CheckRegistry.Changed += runner.CatalogueChanged;
            AsKiln(() =>
            {
                Events.On<NetPeerLeftEvent>(e => runner.Scrubber.AddPlayer(e.Peer.Name));
                try
                {
                    _net = CheckNet.Open(runner, env);
                    if (env.IsClient) Client.Tick += static (in TickInfo _) => _net?.Step();
                }
                catch (Exception e)
                {
                    env.Write(LogLevel.Warn, $"checks: the joined channel is not open: {e.Message}");
                }
                if (env.IsClient)
                {
                    try
                    {
                        CheckUiHost.Start(runner, _net);
                    }
                    catch (Exception e)
                    {
                        env.Write(LogLevel.Warn, $"checks: the check screen is not available: {e.Message}");
                    }
                }
                ServiceTick.Add("kiln.checks", static (in TickInfo _) => Tick());
            });
            env.Write(LogLevel.Info, $"checks: guided check runner started ({(env.IsClient ? "client" : "dedicated server")}; ledger {Path.GetFileName(runner.Ledger.Path)}; " +
                                     $"control folder kiln/control/checks/)");
        }
    }

    private static void Tick()
    {
        try
        {
            _control?.Poll();
            if (Mods.Side != ModSide.Client) _net?.Step();
            _runner?.Step();
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"checks: {e}");
        }
    }

    /// <summary>Runs <paramref name="work"/> as Kiln, not as the mod whose OnLoad called Register: what it creates (the
    /// channel, the screen, the keys) must not go away with that mod.</summary>
    private static void AsKiln(Action work)
    {
        var previousMod = ModContext.CurrentMod;
        var previousSync = SynchronizationContext.Current;
        ModContext.CurrentMod = null;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            work();
        }
        finally
        {
            ModContext.CurrentMod = previousMod;
            SynchronizationContext.SetSynchronizationContext(previousSync);
        }
    }
}

/// <summary>The process as the runner sees it.</summary>
internal sealed class LiveEnv : IRunnerEnv
{
    private GameInfo? _game;
    private readonly IAchievementGuard? _guard;

    public LiveEnv()
    {
        IsClient = Mods.Side == ModSide.Client;
        _guard = IsClient ? new SteamAchievementGuard() : null;
        Log = new KilnLogTail(Path.Combine(KilnPaths.Root, "logs", "kiln.log"));
    }

    public bool IsClient { get; }

    public bool Hosting
    {
        get
        {
            try
            {
                return Client.IsHosting;
            }
            catch
            {
                return false;
            }
        }
    }

    public EntityId LocalPlayer
    {
        get
        {
            try
            {
                return Client.LocalPlayer;
            }
            catch
            {
                return EntityId.None;
            }
        }
    }

    public IReadOnlyList<string> PlayerNames
    {
        get
        {
            var names = new List<string>();
            // Players is the server-world platform-name snapshot (including the host). Network adds peers not yet in it.
            try { names.AddRange(Players.All.Select(p => p.Name)); } catch (KilnUnavailableException) { }
            try { names.AddRange(Network.Peers.Select(p => p.Name)); } catch (KilnUnavailableException) { }
            // Kiln.Client exposes the local entity, but currently no local Steam persona-name reader on a joined client.
            return names;
        }
    }

    public IAchievementGuard? Guard => _guard;

    /// <summary>Not readable yet on any side: the server sees a player's platform name and CharacterId, not the
    /// character's name (docs/re/characters.md); the name lives in the client's character save. So line 5 of the cheats
    /// rule refuses everywhere until a reader exists.</summary>
    public string? CharacterName(EntityId player, out string why)
    {
        why = "Kiln has no reader for a character's name yet: the server sees only the platform name and the CharacterId";
        return null;
    }

    public uint WorldSession
    {
        get
        {
            try
            {
                return IsClient ? Client.ClientWorldSession : 1;
            }
            catch
            {
                return 0;
            }
        }
    }

    public ILogTail Log { get; }

    public DateTime Now => DateTime.UtcNow;

    public GameInfo Game => _game ??= ReadGame(IsClient);

    public void Write(LogLevel level, string text) => Backend.Current.Log(0, level, text);

    /// <summary>The exe's sha256 and whether its build is verified, from kiln/state/boot.json (written by the core at
    /// boot); null sha256 when it cannot be read.</summary>
    internal static GameInfo ReadGame(bool client)
    {
        var side = client ? "client" : "server";
        string? sha = null;
        var verified = false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(KilnPaths.State, "boot.json")));
            var root = doc.RootElement;
            if (root.TryGetProperty("side", out var s) && s.GetString() is "client" or "server") side = s.GetString()!;
            if (root.TryGetProperty("exe", out var exe))
            {
                if (exe.TryGetProperty("sha256", out var h) && h.ValueKind == JsonValueKind.String &&
                    h.GetString() is { Length: 64 } x && x.All(Uri.IsHexDigit))
                    sha = x.ToLowerInvariant();
                if (exe.TryGetProperty("verified", out var v) && v.ValueKind is JsonValueKind.True) verified = true;
            }
        }
        catch (Exception)
        {
            // no boot report: the build is not known, and line 4 of the cheats rule refuses
        }
        return new GameInfo(side, sha, verified, Kiln.Game.KilnVersion);
    }
}

/// <summary>The lines kiln.log gained since a mark (its length then).</summary>
internal sealed class KilnLogTail(string path) : ILogTail
{
    public long Mark()
    {
        try
        {
            return new FileInfo(path).Exists ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    public IReadOnlyList<string> Since(long mark, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        if (length < mark) mark = 0;  // a new log since the mark
        var n = (int)Math.Min(length - mark, maxBytes);
        if (n <= 0) return [];
        fs.Seek(mark, SeekOrigin.Begin);
        var buf = new byte[n];
        var read = 0;
        while (read < n)
        {
            var k = fs.Read(buf, read, n - read);
            if (k <= 0) break;
            read += k;
        }
        return Encoding.UTF8.GetString(buf, 0, read).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
    }
}
