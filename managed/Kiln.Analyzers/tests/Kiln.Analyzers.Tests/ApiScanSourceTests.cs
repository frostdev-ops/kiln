// SPDX-License-Identifier: MIT
using Kiln.Analyzers.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Kiln.Analyzers.Tests;

public sealed class ApiScanSourceTests
{
    [Theory]
    [InlineData("public static partial void Missing();", true)]
    [InlineData("[GeneratedRegex(\"x\")] private static partial int Pattern();", true)]
    [InlineData("[GeneratedRegex(\"[\")] private static partial Regex Pattern();", true)]
    [InlineData("[GeneratedRegex(\"x\")] private static partial Regex Pattern();", false)]
    public void Missing_partial_implementations_fail_except_for_the_known_regex_generator(string method, bool error)
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create("ApiScanFixture",
            [CSharpSyntaxTree.ParseText("using System.Text.RegularExpressions; public partial class Sample { " + method + " }", cancellationToken: TestContext.Current.CancellationToken)],
            paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.Contains(compilation.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Id == "CS8795");
        Assert.Equal(error, ApiScanCompilationErrors.Read(compilation).Count != 0);
    }

    [Fact]
    public void Capability_guards_come_from_the_getter_or_the_summary()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            namespace Kiln
            {
                public static class Capabilities { public static bool Is(string name) => true; }
                public static unsafe class Services { public static T* Get<T>(string name, int version) where T : unmanaged => null; }
                public static unsafe class Svc
                {
                    private struct Table { public int X; }
                    private static Table* _t;
                    private static Table* T => _t != null ? _t : (_t = Services.Get<Table>("kiln.svc", 1));
                    private const string Row = "svc.thing";
                    private static bool Can(string capability) => Capabilities.Is(capability);
                    public static bool Available => T != null;
                    public static bool ThingAvailable => Available && Can(Row);
                    public static bool DirectAvailable { get { var on = Capabilities.Is("svc.direct"); return on && T is not null; } }
                    /// <summary>Works on this build (capability <c>svc.doc</c>).</summary>
                    public static bool DocAvailable => _t == null;
                    /// <summary>Needs the game's hook (feature <c>svc.feature</c>).</summary>
                    public static bool FeatureAvailable => false;
                    /// <summary>True when kiln.plain is loaded (it reads kiln.mod.json).</summary>
                    public static bool PlainAvailable => _t == null;
                    /// <summary>Something else entirely.</summary>
                    public static bool OtherAvailable => _t == null || Capabilities.Is("svc.either");
                    public static bool Ready => Capabilities.Is("svc.ready");
                    public static bool Count => Capabilities.Is("svc.count");
                }
            }
            """, new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Parse), cancellationToken: TestContext.Current.CancellationToken);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create("Kiln.Svc", [tree], paths.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Empty(compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        var model = compilation.GetSemanticModel(tree);
        var facts = new Dictionary<string, ApiScanGuards.Guard>(StringComparer.Ordinal);
        foreach (var pd in tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes().OfType<PropertyDeclarationSyntax>())
            if (ApiScanGuards.Read(model, pd, TestContext.Current.CancellationToken) is { } g)
                facts["P:Kiln.Svc." + pd.Identifier.ValueText] = g;
        var rows = ApiScanGuards.Resolve(facts).ToDictionary(r => r.Key.Substring("P:Kiln.Svc.".Length), r => string.Join(",", r.Value.Rows));
        Assert.Equal("kiln.svc", rows["Available"]);              // T != null, T from Services.Get("kiln.svc")
        Assert.Equal("kiln.svc,svc.thing", rows["ThingAvailable"]); // a helper around Capabilities.Is, and another guard
        Assert.Equal("svc.doc", rows["DocAvailable"]);
        Assert.Equal("svc.feature", rows["FeatureAvailable"]);
        Assert.Equal("kiln.plain", rows["PlainAvailable"]);        // a service in plain words; not "kiln.mod" of kiln.mod.json
        Assert.Equal("kiln.svc", rows["DirectAvailable"]);         // T is not null; the local "on" is not followed
        Assert.False(rows.ContainsKey("OtherAvailable"));          // || asks nothing for sure
        Assert.Equal("svc.ready", rows["Ready"]);                  // "...Ready" asks as much
        Assert.False(rows.ContainsKey("Count"));                   // neither "...Available" nor "...Ready"
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sources_follow_compile_removals_and_linked_release_inputs(bool underBinParent)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", "apiscan inputs " + Guid.NewGuid().ToString("N")));
        var dir = Path.Combine(root, underBinParent ? "bin" : "source", "Service");
        Directory.CreateDirectory(Path.Combine(dir, "tests"));
        try
        {
            var project = Path.Combine(dir, "Service.csproj");
            var live = Path.Combine(dir, "Live.cs");
            var linked = Path.Combine(root, "Linked.cs");
            File.WriteAllText(live, "public class Live { }");
            File.WriteAllText(linked, "public class Linked { }");
            File.WriteAllText(Path.Combine(dir, "tests", "Excluded.cs"), "public class ExcludedTestApi { }");
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><NoWarn>$(NoWarn);KILNX002</NoWarn></PropertyGroup>
                  <ItemGroup>
                    <Compile Remove="tests/**/*.cs" />
                    <Compile Include="../../Linked.cs" Condition="'$(Configuration)' == 'Release'" />
                  </ItemGroup>
                </Project>
                """);
            var (files, noWarn) = ApiScanProjectSources.Read(project);
            Assert.Equal(new[] { live, linked }.Order(StringComparer.Ordinal), files);
            Assert.Contains("KILNX002", noWarn);  // the scan compiles with the project's own suppressions
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
