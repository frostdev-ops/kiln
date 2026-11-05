// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>One server-world sim step.</summary>
/// <remarks>
/// A dedicated server steps its world about 24.5 times a second; a client-hosted world steps up to 60 times. Use
/// <see cref="ClockDelta"/> or <see cref="WallDelta"/> for time, never the number of steps.
/// </remarks>
public readonly struct TickInfo
{
    internal TickInfo(ulong step, uint clock, uint clockDelta, long clockPeriod, double wallDeltaMs)
    {
        Step = step;
        Clock = clock;
        ClockDelta = clockDelta;
        ClockPeriod = clockPeriod;
        WallDeltaMs = wallDeltaMs;
    }

    /// <summary>Steps since the server started, from 1.</summary>
    public ulong Step { get; }

    /// <summary>The game's 60 Hz tick clock.</summary>
    public uint Clock { get; }

    /// <summary>Game clock advance since the previous step (60 Hz units; 2-3 on a dedicated server).</summary>
    public uint ClockDelta { get; }

    /// <summary>The game's raw tick period value.</summary>
    public long ClockPeriod { get; }

    /// <summary>Wall-clock milliseconds since the previous step started.</summary>
    public double WallDeltaMs { get; }

    /// <summary>Game time elapsed since the previous step.</summary>
    public TimeSpan ClockDeltaTime => TimeSpan.FromSeconds(ClockDelta / 60.0);

    /// <summary>Wall time elapsed since the previous step.</summary>
    public TimeSpan WallDelta => TimeSpan.FromMilliseconds(WallDeltaMs);
}

public delegate void TickHandler(in TickInfo tick);

/// <summary>Server-world tick events of one mod.</summary>
public sealed class TickEvents
{
    private TickHandler? _pre, _post;

    /// <summary>Before the world's sim step. None of the world's jobs are running.</summary>
    public event TickHandler? Pre
    {
        add => _pre += value;
        remove => _pre -= value;
    }

    /// <summary>After the sim step, once the world's jobs have finished. Costs the server a little parallelism, so
    /// Kiln only waits for the jobs while some mod listens here.</summary>
    public event TickHandler? Post
    {
        add => _post += value;
        remove => _post -= value;
    }

    internal bool HasPost => _post is not null;
    internal void RaisePre(in TickInfo tick) => _pre?.Invoke(in tick);
    internal void RaisePost(in TickInfo tick) => _post?.Invoke(in tick);

    internal void Clear()
    {
        _pre = null;
        _post = null;
    }
}

/// <summary>Lines of the game's own log.</summary>
public sealed class GameLogEvents
{
    private Action<LogLevel, string>? _line;

    /// <summary>One game log line (without trailing newline), on the tick thread, a step or so after it was written.</summary>
    public event Action<LogLevel, string>? Line
    {
        add => _line += value;
        remove => _line -= value;
    }

    internal bool HasListeners => _line is not null;
    internal void Raise(LogLevel level, string text) => _line?.Invoke(level, text);
    internal void Clear() => _line = null;
}
