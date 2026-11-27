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
    /// <param name="text">The text to look for (case matters).</param>
    /// <param name="modId">Only lines of this mod; null: every line.</param>
    /// <returns>The matching lines, in the order they were written.</returns>
    public IReadOnlyList<LogLine> FindLogs(string text, string? modId = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_logLock)
            return _lines.Where(l => (modId is null || l.ModId == modId) && l.Text.Contains(text, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Whether a line contains <paramref name="text"/>, optionally only one of mod <paramref name="modId"/>.</summary>
    /// <param name="text">The text to look for (case matters).</param>
    /// <param name="modId">Only lines of this mod; null: every line.</param>
    /// <returns>True when one matches.</returns>
    public bool HasLog(string text, string? modId = null) => FindLogs(text, modId).Count > 0;

    /// <summary>The captured lines as text, one per line (for an assertion message).</summary>
    public string DumpLogs()
    {
        lock (_logLock) return string.Join("\n", _lines);
    }

    /// <summary>The path of <c>kiln/logs/kiln.log</c>, the file the harness writes every captured line to.</summary>
    public string LogFile => Path.Combine(LogsDirectory, "kiln.log");

    /// <summary>The text of <see cref="LogFile"/> as written so far ("" when it was not created). The harness keeps the
    /// file open for writing while it lives, and on Windows <c>File.ReadAllText</c> cannot open a file another handle
    /// writes to: this reads it with the sharing Windows needs, on every OS. <see cref="Logs"/> holds the same lines.</summary>
    public string ReadLogFile()
    {
        lock (_logLock)  // no line half written while it is read
        {
            try
            {
                using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return "";
            }
            catch (DirectoryNotFoundException)
            {
                return "";
            }
        }
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
            _logFile = new StreamWriter(new FileStream(LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
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
