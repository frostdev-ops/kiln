// SPDX-License-Identifier: MIT
using Kiln;

static void Throws<T>(Action call) where T : Exception
{
    try { call(); } catch (T) { return; }
    throw new Exception("expected " + typeof(T).Name);
}
var backend = new TestBackend();
Backend.Current = backend;
backend.Tick = false;
Throws<InvalidOperationException>(() => Nearby.Within([], new WorldPosition(), 0));
Throws<InvalidOperationException>(() => Nearby.Within([default(EntityId)], new WorldPosition(), 0));
backend.Tick = true;
foreach (var radius in new[] { -1.0, double.NaN, double.PositiveInfinity })
{
    Throws<ArgumentOutOfRangeException>(() => Nearby.Within([], new WorldPosition(), radius));
    Throws<ArgumentOutOfRangeException>(() => Nearby.Within([], default(EntityId), radius));
    Throws<ArgumentOutOfRangeException>(() => Nearby.Entities(default(EntityId), radius, "x"));
    Throws<ArgumentOutOfRangeException>(() => Nearby.Entities(default(WorldPosition), radius, "x"));
}
Throws<ArgumentOutOfRangeException>(() => Nearby.Within([], new WorldPosition(double.NaN, 0, 0), 1));
if (Nearby.Within([], new WorldPosition(), 0).Count != 0) throw new Exception("empty search");
Console.WriteLine("Nearby: 16 checks passed");

sealed class TestBackend : IKilnBackend
{
    public bool Tick;
    public string Version => "test";
    public bool IsTickThread => Tick;
    public bool HasFeature(string name) => false;
    public nint Service(string name, uint version) => 0;
    public void Log(ulong owner, LogLevel level, string text) { }
}
