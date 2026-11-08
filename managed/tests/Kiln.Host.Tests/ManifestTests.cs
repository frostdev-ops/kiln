// SPDX-License-Identifier: MIT
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void Reads_a_v1_manifest_unchanged()
    {
        var m = Manifest.Parse("""{"id":"hello","name":"Hello Kiln","version":"0.1.0","apiVersion":"0.1","entryAssembly":"Hello.dll","side":"server"}""");
        Assert.Equal(("hello", "Hello Kiln", "0.1.0", "Hello.dll"), (m.Id, m.Name, m.Version, m.EntryAssembly));
        Assert.Equal(ManifestSide.Server, m.Side);
        Assert.True(m.ApiRange.Satisfies(SemVersion.Parse(Kiln.Mods.ApiVersion)), "a 0.1 mod loads on API 0.2");
        Assert.Empty(m.Dependencies);
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void Reads_every_v2_field()
    {
        var m = Manifest.Parse("""
            {
              // comments and trailing commas are fine
              "id": "my-mod", "name": "My Mod", "version": "1.4.0-beta.2", "description": "d", "authors": ["a", "b"],
              "license": "MIT", "url": "https://example.org", "apiVersion": ">=0.2 <1", "side": "both",
              "dependencies": { "lib": "^1.2", "kiln": ">=0.1", "extra": { "version": "~2.0", "optional": true } },
              "loadBefore": ["late"], "loadAfter": ["early"], "incompatible": { "rival": "<3" },
              "features": ["tick", "log.mirror"], "entryAssembly": "MyMod.dll", "entryType": "My.ModClass",
              "exports": ["MyMod.Api.dll"], "advanced": true, "tests": { "autoRun": true, "timeoutSeconds": 5 },
            }
            """);
        Assert.Equal(ManifestSide.Both, m.Side);
        Assert.Equal(["a", "b"], m.Authors);
        Assert.Equal(3, m.Dependencies.Count);
        Assert.True(m.Dependencies.Single(d => d.Id == "extra").Optional);
        Assert.Equal("^1.2", m.Dependencies[0].RangeText);
        Assert.Equal(["late"], m.LoadBefore);
        Assert.Equal(["early"], m.LoadAfter);
        Assert.True(m.Incompatible.Single().Range.Satisfies(SemVersion.Parse("2.9.0")));
        Assert.Equal(["MyMod.Api.dll"], m.Exports);
        Assert.True(m.Advanced);
        Assert.Equal(new Kiln.Host.ManifestTests(true, 5, null), m.Tests);
        Assert.False(m.ApiRange.Satisfies(SemVersion.Parse("1.0.0")));
    }

    [Fact]
    public void Accepts_the_array_dependency_form_and_warns_on_unknown_keys()
    {
        var m = Manifest.Parse("""
            {"id":"x","version":"1.0.0","apiVersion":"0.2","entryAssembly":"X.dll","dependecies":{},
             "dependencies":[{"id":"a","version":"^1"},{"id":"b","optional":true}],"incompatible":["c"]}
            """);
        Assert.Equal(["a", "b"], m.Dependencies.Select(d => d.Id));
        Assert.Equal("*", m.Dependencies[1].RangeText);
        Assert.True(m.Incompatible[0].Range.IsAny);
        Assert.Contains(m.Warnings, w => w.Contains("dependecies"));
    }

    [Theory]
    [InlineData("server", null, "none")]
    [InlineData("client", null, "mod")]
    [InlineData("both", null, "mod")]
    [InlineData("server", "kiln", "kiln")]
    [InlineData("server", "mod", "mod")]
    [InlineData("both", "none", "none")]
    public void Clients_defaults_from_side_and_can_be_set(string side, string? clients, string expected)
    {
        var extra = clients is null ? "" : $$""","clients":"{{clients}}" """;
        var m = Manifest.Parse($$"""{"id":"c","version":"1.0.0","apiVersion":"0.2","entryAssembly":"C.dll","side":"{{side}}"{{extra}}}""");
        Assert.Equal(expected, Manifest.ClientsName(m.Clients));
        Assert.Empty(m.Warnings);
    }

    [Fact]
    public void A_v1_manifest_needs_nothing_on_clients()
    {
        var m = Manifest.Parse("""{"id":"hello","version":"0.1.0","apiVersion":"0.1","entryAssembly":"Hello.dll"}""");
        Assert.Equal(ManifestClients.None, m.Clients);
        Assert.Equal("none", Manifest.ClientsName(m.Clients));
    }

    // The tree above the test binary, or (with --artifacts-path outside the tree) the tree this source file is in.
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string self = "")
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "managed", "Kiln.Host"))) return dir;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", ".."));
    }

    [Fact]
    public void Every_manifest_in_the_repository_still_parses()
    {
        var repo = Repo();
        var files = Directory.EnumerateFiles(Path.Combine(repo, "mods"), Manifest.FileName, SearchOption.AllDirectories)
            .Where(f => !f.Contains("sample-bad-manifest") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(files.Count >= 10, $"{files.Count} manifests");
        foreach (var f in files)
        {
            var m = Manifest.Parse(File.ReadAllText(f));
            Assert.True(m.Warnings.Count == 0, $"{f}: {string.Join("; ", m.Warnings)}");
        }
    }

    [Theory]
    [InlineData("""{"id":"Bad Id","version":"1","apiVersion":"0.1","entryAssembly":"a.dll"}""", "id")]
    [InlineData("""{"id":"kiln","version":"1","apiVersion":"0.1","entryAssembly":"a.dll"}""", "reserved")]
    [InlineData("""{"id":"ok","version":"one","apiVersion":"0.1","entryAssembly":"a.dll"}""", "version")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"^^1","entryAssembly":"a.dll"}""", "apiVersion")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"../a.dll"}""", "entryAssembly")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"a.dll","side":"toaster"}""", "side")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"a.dll","clients":"all"}""", "clients")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"a.dll","clients":true}""", "clients")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"a.dll","dependencies":{"x":"^^1"}}""", "dependency 'x'")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"a.dll","dependencies":{"ok":"*"}}""", "itself")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"a.dll","loadAfter":["a"],"loadBefore":["a"]}""", "both")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1","entryAssembly":"a.dll","exports":["sub/x.dll"]}""", "exports")]
    [InlineData("""{"id":"ok","version":"1","apiVersion":"0.1"}""", "entryAssembly")]
    [InlineData("""[1, 2]""", "one JSON object")]
    [InlineData("""{"id":""", "not valid JSON")]
    public void Rejects(string json, string field)
    {
        var e = Assert.Throws<InvalidDataException>(() => Manifest.Parse(json));
        Assert.Contains(field, e.Message);
    }
}
