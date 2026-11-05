// Spike: .NET hosted inside the Wine'd dedicated server, called from the server thread every SimStep.
// Allocates on purpose so GC behaviour under Wine shows up in the numbers.
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace Kiln.Spike;

public static class Entry
{
    static readonly List<byte[]> Window = new();
    static StreamWriter? _log;
    static long _calls, _maxTicks;
    static readonly List<double> Samples = new();
    static TimeSpan _lastPause;

    [UnmanagedCallersOnly]
    public static int Init(IntPtr logPathUtf16)
    {
        var path = Marshal.PtrToStringUni(logPathUtf16)!;
        _log = new StreamWriter(path, append: true) { AutoFlush = true };
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Log($"CLR {Environment.Version} up; GC server={GCSettings.IsServerGC} latency={GCSettings.LatencyMode}; " +
            $"OS={RuntimeInformation.OSDescription}; thread={Environment.CurrentManagedThreadId}");
        return Environment.Version.Major;
    }

    [UnmanagedCallersOnly]
    public static long Tick(int tick)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            for (int i = 0; i < 2000; i++) _ = new byte[128];   // ~256 KB short-lived garbage per tick
            Window.Add(new byte[64 * 1024]);                   // rolling ~8 MB of survivors
            if (Window.Count > 128) Window.RemoveAt(0);
            if (tick % 500 == 0) _ = Enumerable.Range(0, 20000).Select(x => x.ToString()).ToList();
        }
        catch (Exception e) { Log("tick failed: " + e); }
        long dt = Stopwatch.GetTimestamp() - t0;
        _calls++;
        _maxTicks = Math.Max(_maxTicks, dt);
        Samples.Add(dt * 1000.0 / Stopwatch.Frequency);
        if (_calls % 250 == 0) Report(tick);
        return dt;
    }

    static void Report(int tick)
    {
        Samples.Sort();
        double P(double q) => Samples[Math.Min(Samples.Count - 1, (int)(q * Samples.Count))];
        var pause = GC.GetTotalPauseDuration();
        Log($"tick={tick} calls={_calls} managed p50={P(0.5):F3}ms p99={P(0.99):F3}ms max={Samples[^1]:F3}ms " +
            $"gc0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} " +
            $"pause+={(pause - _lastPause).TotalMilliseconds:F2}ms heap={GC.GetTotalMemory(false) / 1048576.0:F1}MB " +
            $"thread={Environment.CurrentManagedThreadId}");
        _lastPause = pause;
        Samples.Clear();
    }

    static void Log(string s) => _log?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
}
