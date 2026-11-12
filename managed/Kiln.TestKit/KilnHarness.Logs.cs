// SPDX-License-Identifier: MIT
using System.Globalization;

namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private readonly object _logLock = new();
    private readonly List<LogLine> _lines = [];
    private long _logSequence;
    private StreamWriter? _logFile;

    /// <summary>Every line Kiln wrote while this harness lived, in order. A snapshot: read it again for new lines.</summary>
    public IReadOnlyList<LogLine> Logs
    {
        get
        {
            lock (_logLock) return _lines.ToArray();
        }
    }

    /// <summary>The lines mod <paramref name="modId"/> wrote (its <see cref="Logger"/>, and Kiln's lines in its name such
    /// as an event handler that threw).</summary>
    public IReadOnlyList<LogLine> LogsOf(string modId)
    {
        ArgumentNullException.ThrowIfNull(modId);
        lock (_logLock) return _lines.Where(l => l.ModId == modId).ToArray();
    }

    /// <inheritdoc cref="LogsOf(string)"/>
    public IReadOnlyList<LogLine> LogsOf(ModHandle mod) => LogsOf(mod.Id);

    /// <summary>The lines whose text contains <paramref name="text"/> (ordinal), optionally only those of one mod.</summary>
    public IReadOnlyList<LogLine> FindLogs(string text, string? modId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_logLock)
            return _lines.Where(l => (modId is null || l.ModId == modId) && l.Text.Contains(text, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Whether a line contains <paramref name="text"/>, optionally only one of mod <paramref name="modId"/>.</summary>
    public bool HasLog(string text, string? modId = null) => FindLogs(text, modId).Count > 0;

    /// <summary>The captured lines as text, one per line (for an assertion message).</summary>
    public string DumpLogs()
    {
        lock (_logLock) return string.Join("\n", _lines);
    }

    /// <summary>From the kit's backend (any thread): one line of Kiln's log.</summary>
    internal void Capture(LogLevel level, string source, string text)
    {
        LogLine line;
        lock (_logLock)
        {
            line = new LogLine(++_logSequence, Volatile.Read(ref _step), level, source, text);
            _lines.Add(line);
            try
            {
                _logFile?.WriteLine($"{DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {line}");
            }
            catch (IOException)
            {
                // the log file is a convenience; the captured lines are the record
            }
        }
        _options.LogSink?.Invoke(line);
    }

    private void OpenLogFile()
    {
        try
        {
            _logFile = new StreamWriter(new FileStream(Path.Combine(LogsDirectory, "kiln.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                AutoFlush = true,
            };
        }
        catch (IOException)
        {
            _logFile = null;
        }
    }

    private void CloseLogFile()
    {
        lock (_logLock)
        {
            _logFile?.Dispose();
            _logFile = null;
        }
    }
}
