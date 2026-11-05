using Kiln;

namespace KilnTest1;

/// <summary>A test mod is a mod too: Kiln needs an entry class. Its tests are in KilnTest1Tests.cs; tests that need
/// the live mod instance can also be instance methods with [KilnTest] right here.</summary>
public sealed class KilnTest1Mod : Mod
{
    public override void OnLoad() => Log.Info("test mod loaded; its tests run once the server has started");
}
