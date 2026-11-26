// SPDX-License-Identifier: MIT
// The owner's game: its cache of the server's view of its pets (channel kiln:pets v1), and the built-in bar and recovery
// markers drawn from one captured snapshot per frame (19d §4.8, §5.4). Drawing never sends or waits; disabling the bar
// changes nothing in the game. A vanilla server, or one without Kiln.Pets, sends nothing: no bar.
namespace Kiln;

/// <summary>
/// This game's view of its owner's pets, as the server sends it, and the built-in pet bar. Read <see cref="Snapshot"/> to
/// draw a HUD of your own (and turn the built-in one off with <see cref="Configure"/>). Since 0.2.0-dev.32.
/// </summary>
[ClientOnly]
public static class PetsClient
{
    private static readonly PetViewCache Cache = new();
    private static PetHudOptions _options = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static NetChannel? _channel;
    private static PetViewState _posted = PetViewState.Disconnected;
    private static readonly object Gate = new();

    /// <summary>The latest view, or null (none yet, disconnected). One immutable snapshot: read it once per frame.</summary>
    [AnyThread, RequiresCapability(Pets.Caps.HudClient)]
    public static PetOwnerView? Snapshot => Cache.View;

    /// <summary>Whether <see cref="Snapshot"/> is current, stale (no keepalive for 3 s) or there is none.</summary>
    [AnyThread]
    public static PetViewState State
    {
        get
        {
            lock (Gate)
            {
                Cache.Tick(Now);
                return Cache.State;
            }
        }
    }

    /// <summary>The built-in bar's options on this game.</summary>
    [AnyThread]
    public static PetHudOptions Options => Volatile.Read(ref _options);

    /// <summary>Sets where and whether the built-in bar draws (this game only; never a server state).</summary>
    /// <exception cref="ArgumentException">An anchor outside 0..1, a width outside 80..600, or a value that is not finite.</exception>
    [AnyThread, RequiresCapability(Pets.Caps.HudClient)]
    public static void Configure(PetHudOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.AnchorX) || options.AnchorX is < 0 or > 1) throw new ArgumentException($"AnchorX {options.AnchorX} is outside 0..1", nameof(options));
        if (!double.IsFinite(options.AnchorY) || options.AnchorY is < 0 or > 1) throw new ArgumentException($"AnchorY {options.AnchorY} is outside 0..1", nameof(options));
        if (!double.IsFinite(options.WidthPixels) || options.WidthPixels is < 80 or > 600) throw new ArgumentException($"WidthPixels {options.WidthPixels} is outside 80..600", nameof(options));
        Volatile.Write(ref _options, options);
        Events.Enqueue(new PetHudOptionsChangedEvent(options));
    }

    private static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>Opens the channel on a game client (server: the publisher's own side) and subscribes the bar.</summary>
    internal static void Start()
    {
        lock (Gate)
        {
            if (_channel is not null || !Network.Available) return;
            try
            {
                _channel = Network.Channel("kiln:pets", "1", required: false, maxMessageBytes: PetViewCodec.MaxBytes);
                _channel.OnRaw(OnRaw);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: the channel kiln:pets did not open: {e.Message}");
            }
        }
    }

    internal static NetChannel? Channel => _channel;

    // client main thread: a snapshot from the server (only the server peer's are taken)
    private static void OnRaw(NetContext ctx, ushort id, ReadOnlySpan<byte> body)
    {
        if (id != PetViewCodec.ViewMessage || !ctx.Sender.IsServer) return;
        var view = PetViewCodec.Read(body, out var error);
        if (view is null)
        {
            PetsRuntime.Log(LogLevel.Debug, $"Kiln.Pets: a pet view was refused: {error}");
            return;
        }
        Receive(view);
    }

    internal static bool Receive(PetOwnerView view)
    {
        bool taken;
        lock (Gate) taken = Cache.Accept(view, Now);
        if (!taken) return false;
        Events.Enqueue(new PetOwnerViewChangedEvent(view));
        PostState();
        return true;
    }

    internal static void Disconnected()
    {
        bool had;
        lock (Gate) had = Cache.Clear();
        if (had) PostState();
    }

    private static void PostState()
    {
        var now = State;
        if (now == _posted) return;
        _posted = now;
        Events.Enqueue(new PetViewStateChangedEvent(now));
    }

    internal static void ResetForTests()
    {
        lock (Gate) Cache.Clear();
        _options = new PetHudOptions();
        _posted = PetViewState.Disconnected;
    }

    // render thread: the bar and the markers, from one captured snapshot; nothing is sent or awaited here
    internal static void Draw(RenderFrameEvent f)
    {
        if (Network.Server is not { State: NetPeerState.Ready })
        {
            if (Cache.View is not null) Disconnected();
            return;
        }
        var state = State;
        if (state != _posted) PostState();
        if (state != PetViewState.Current) return;   // stale or none: the bar is hidden until the next snapshot
        var view = Cache.View;
        var o = Options;
        foreach (var row in PetHudLayout.Rows(view, o, f.Width, f.Height, f.Scale))
        {
            var d = f.Draw;
            d.FillRect(row.X, row.Y, row.X + row.Width, row.Y + row.BarHeight, new Color(0, 0, 0, 160), 3);
            var fill = row.Entry.Life == PetLife.Downed ? new Color(150, 60, 60, 220) : new Color(70, 190, 90, 230);
            d.FillRect(row.X, row.Y, row.X + row.Width * row.Fraction, row.Y + row.BarHeight, fill, 3);
            if (row.Entry.Transition == PetTransition.Reviving && row.Entry.ReviveDurationMs > 0)
            {
                var p = Math.Clamp((float)row.Entry.ReviveElapsedMs / row.Entry.ReviveDurationMs, 0f, 1f);
                d.FillRect(row.X, row.Y + row.BarHeight, row.X + row.Width * p, row.Y + row.BarHeight + 2 * f.Scale, new Color(80, 200, 255, 230));
            }
            d.Text(row.X + 4 * f.Scale, row.Y + 1, $"{row.Entry.Label}  {row.Entry.Current}/{row.Entry.Maximum}  {row.Status}", new Color(240, 240, 240, 255), 12 * f.Scale);
        }
        if (!o.RecoveryMarkers || view is null || !f.Camera.Valid) return;
        foreach (var e in view.Entries)
            if (e.ShowMarker && e.Presence == PetPresence.RecoveryAnchor && e.RecoveryAnchor is { } a)
            {
                // a UI marker only: never a target or an interaction entity
                f.World.Circle(a, 0.8, new Color(80, 220, 255, 220));
                f.World.Label(a, e.Label, new Color(80, 220, 255, 255));
            }
    }
}

/// <summary>This game got a new view of its owner's pets. Client main thread. Since 0.2.0-dev.32.</summary>
[ClientOnly]
public sealed class PetOwnerViewChangedEvent : KilnEvent
{
    internal PetOwnerViewChangedEvent(PetOwnerView view) => View = view;
    /// <summary>The new view.</summary>
    public PetOwnerView View { get; }
}

/// <summary>This game's view went current, stale or away. Client main thread. Since 0.2.0-dev.32.</summary>
[ClientOnly]
public sealed class PetViewStateChangedEvent : KilnEvent
{
    internal PetViewStateChangedEvent(PetViewState state) => State = state;
    /// <summary>The state now.</summary>
    public PetViewState State { get; }
}

/// <summary>The built-in bar's options changed (<see cref="PetsClient.Configure"/>). Client main thread. Since 0.2.0-dev.32.</summary>
[ClientOnly]
public sealed class PetHudOptionsChangedEvent : KilnEvent
{
    internal PetHudOptionsChangedEvent(PetHudOptions options) => Options = options;
    /// <summary>The options now.</summary>
    public PetHudOptions Options { get; }
}
