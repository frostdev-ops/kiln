using Kiln.TestKit.Xunit;

namespace KilnUnitTest1;

/// <summary>Every [KilnTest] method of the mod's assembly as an xunit test: the same test body that runs in a server
/// (kiln test &lt;mod id&gt;) runs here, on one harness and one load of the mod, in the runner's order. A test that asks for
/// a service the kit does not fake offline is skipped with the capability named, not passed. A mod with no [KilnTest]
/// method yet gives one skipped test. Filter: dotnet test --filter "DisplayName~Meter".</summary>
public sealed class ModKilnTests : KilnTestSuite<KILN_MOD_TYPE>;
