// SPDX-License-Identifier: MIT
using Kiln.Analyzers.Tools;
using Xunit;

namespace Kiln.Analyzers.Tests;

public sealed class ApiScanSourceTests
{
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
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Remove="tests/**/*.cs" />
                    <Compile Include="../../Linked.cs" Condition="'$(Configuration)' == 'Release'" />
                  </ItemGroup>
                </Project>
                """);
            Assert.Equal(new[] { live, linked }.Order(StringComparer.Ordinal), ApiScanProjectSources.Read(project));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
