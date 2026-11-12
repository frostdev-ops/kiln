// SPDX-License-Identifier: MIT
using Kiln.Analyzers.Tools;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
