// SPDX-License-Identifier: MIT
// KilnBoot: the SDK's MSBuild property for a boot mod writes "boot" into the generated kiln.mod.json (true / "required"), as the other
// manifest properties do; another value is refused by the loader's own parse (KILN001). The KilnManifest task runs on a stub project
// out of process, as the SDK's targets run it in repository mode.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.SdkTests;

public sealed class ManifestBootTests
{
    private static (int Code, string Output, JsonObject? Manifest) Generate(string? boot)
    {
        var dir = Directory.CreateTempSubdirectory("kiln-manifest-").FullName;
        try
        {
            var output = Path.Combine(dir, "kiln.mod.json");
            File.WriteAllText(Path.Combine(dir, "stub.proj"), $"""
                <Project>
                  <UsingTask TaskName="Kiln.Sdk.KilnManifest" AssemblyFile="{AppContext.BaseDirectory}Kiln.Sdk.Tasks.dll" TaskFactory="TaskHostFactory" />
                  <Target Name="Manifest">
                    <KilnManifest Output="{output}" Id="more-altars" Name="More Altars" Version="1.0.0" ApiVersion="0.2" Side="server"
                                  EntryAssembly="MoreAltars.dll" Boot="$(KilnBoot)" />
                  </Target>
                </Project>
                """);
            var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "msbuild", Path.Combine(dir, "stub.proj"), "-nologo", "-v:m", "-t:Manifest" }) psi.ArgumentList.Add(a);
            if (boot != null) psi.ArgumentList.Add($"-p:KilnBoot={boot}");
            foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList()) psi.Environment.Remove(k);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit(120_000);
            var json = File.Exists(output) ? JsonNode.Parse(File.ReadAllText(output)) as JsonObject : null;
            return (p.ExitCode, o.Result + e.Result, json);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void KilnBoot_true_writes_boot_true()
    {
        var (code, output, manifest) = Generate("true");
        Assert.True(code == 0, output);
        Assert.True((bool?)manifest?["boot"]);
    }

    [Fact]
    public void KilnBoot_required_writes_boot_required()
    {
        var (code, output, manifest) = Generate("required");
        Assert.True(code == 0, output);
        Assert.Equal("required", (string?)manifest?["boot"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void No_KilnBoot_or_false_writes_no_boot_key(string? boot)
    {
        var (code, output, manifest) = Generate(boot);
        Assert.True(code == 0, output);
        Assert.NotNull(manifest);
        Assert.False(manifest!.ContainsKey("boot"));
    }

    [Fact]
    public void Another_value_is_the_loaders_KILN001()
    {
        var (code, output, _) = Generate("yes");
        Assert.NotEqual(0, code);
        Assert.Contains("KILN001", output);
        Assert.Contains("boot", output);
    }
}
