// SPDX-License-Identifier: MIT
// Unit-test fakes shared by the kit's tests and Kiln's own (managed/tests/Kiln.Api.Tests/TestKit.cs builds its KilnTest
// fixture on them). Not the harness's backend (KitBackend): these are per test, with tables a test owns.
namespace Kiln.TestKit;

/// <summary>A fake table is published only for its allocation's lifetime; a replacement belongs to its own scope.</summary>
internal sealed class FakeServiceRegistration : IDisposable
{
    private readonly Dictionary<string, nint> _tables;
    private readonly string _name;
    private readonly nint _table;

    internal FakeServiceRegistration(Dictionary<string, nint> tables, string name, nint table)
    {
        _tables = tables;
        _name = name;
        _table = table;
        tables[name] = table;
    }

    public void Dispose()
    {
        if (_tables.TryGetValue(_name, out var current) && current == _table) _tables.Remove(_name);
    }
}

/// <summary>A backend that records log lines, lets a test say whether it is on the tick thread, and serves the native
/// tables a test provides.</summary>
internal class RecordingBackend : IKilnBackend
{
    public readonly List<string> Lines = [];
    public bool TickThread = true;
    public string Version => "test";
    public bool IsTickThread => TickThread;
    public bool HasFeature(string name) => false;

    /// <summary>Native service tables a test provides (a fake kiln.types or kiln.assets).</summary>
    public readonly Dictionary<string, nint> Tables = [];

    public nint Service(string name, uint minVersion) => Tables.GetValueOrDefault(name);
    public ulong OwnerCreate(string id) => 777;

    public void Log(ulong owner, LogLevel level, string text)
    {
        lock (Lines) Lines.Add($"{level}: {text}");
    }

    public bool Logged(string part)
    {
        lock (Lines) return Lines.Any(l => l.Contains(part, StringComparison.Ordinal));
    }
}
