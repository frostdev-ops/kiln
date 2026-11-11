// SPDX-License-Identifier: MIT
// Shared parity fixtures (schema/fixtures/{resolver,semver,manifest}): the C# side. kiln_manage's Rust ports of
// Manifest.cs, SemVer.cs and Resolver.cs run the same files. The "expect" values come from this code:
//   KILN_FIXTURES_WRITE=1 dotnet test managed/tests/Kiln.Host.Tests --filter FixtureTests
// rewrites them (review the diff: a change there is a contract change).
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class FixtureTests
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static bool Write => Environment.GetEnvironmentVariable("KILN_FIXTURES_WRITE") == "1";

    private static string Fixtures([CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", "..", "schema", "fixtures"));

    private static TheoryData<string> Files(string kind)
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(Fixtures(), kind), "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(f));
        return data;
    }

    public static TheoryData<string> ResolverFiles => Files("resolver");
    public static TheoryData<string> ManifestFiles => Files("manifest");
    public static TheoryData<string> SemverFiles => Files("semver");

    /// <summary>Compares (or, in write mode, stores) <paramref name="key"/> of the fixture file.</summary>
    private static void Check(string kind, string file, JsonObject doc, string key, JsonNode actual)
    {
        var path = Path.Combine(Fixtures(), kind, file);
        if (Write)
        {
            doc[key] = actual;
            File.WriteAllText(path, doc.ToJsonString(Pretty) + "\n");
            return;
        }
        Assert.True(doc[key] is not null, $"{kind}/{file}: no '{key}' yet; run with KILN_FIXTURES_WRITE=1");
        var want = doc[key]!.ToJsonString(Pretty);
        var got = actual.ToJsonString(Pretty);
        Assert.True(want == got, $"{kind}/{file}: {key} differs\nexpected {want}\nactual   {got}");
    }

    private static JsonObject Load(string kind, string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Fixtures(), kind, file)))!.AsObject();

    private static JsonArray Strings(IEnumerable<string> items) => new([.. items.Select(s => (JsonNode)s)]);

    private static string Lower<T>(T value) where T : Enum => value.ToString().ToLowerInvariant();

    /// <summary>A manifest as the fixtures describe it (every parsed field, normalized).</summary>
    internal static JsonObject Describe(Manifest m) => new()
    {
        ["id"] = m.Id,
        ["name"] = m.Name,
        ["version"] = m.Version,
        ["parsedVersion"] = m.ParsedVersion.ToString(),
        ["apiVersion"] = m.ApiVersion,
        ["entryAssembly"] = m.EntryAssembly,
        ["entryType"] = m.EntryType,
        ["side"] = Lower(m.Side),
        ["clients"] = Manifest.ClientsName(m.Clients),
        ["authors"] = Strings(m.Authors),
        ["description"] = m.Description,
        ["license"] = m.License,
        ["url"] = m.Url,
        ["dependencies"] = new JsonArray([.. m.Dependencies.Select(d => (JsonNode)new JsonObject
        {
            ["id"] = d.Id, ["range"] = d.RangeText, ["optional"] = d.Optional,
        })]),
        ["loadBefore"] = Strings(m.LoadBefore),
        ["loadAfter"] = Strings(m.LoadAfter),
        ["incompatible"] = new JsonArray([.. m.Incompatible.Select(x => (JsonNode)new JsonObject { ["id"] = x.Id, ["range"] = x.RangeText })]),
        ["features"] = Strings(m.Features),
        ["exports"] = Strings(m.Exports),
        ["advanced"] = m.Advanced,
        ["tests"] = m.Tests is { } t ? new JsonObject { ["autoRun"] = t.AutoRun, ["timeoutSeconds"] = t.TimeoutSeconds, ["filter"] = t.Filter } : null,
        ["kilnBuild"] = m.BuiltWith is { } b ? new JsonObject { ["version"] = b.Version, ["created"] = b.Created } : null,
        ["warnings"] = Strings(m.Warnings),
    };

    [Theory]
    [MemberData(nameof(ManifestFiles))]
    public void Manifest_fixture(string file)
    {
        var doc = Load("manifest", file);
        JsonObject actual;
        try
        {
            actual = new JsonObject { ["ok"] = true, ["manifest"] = Describe(Manifest.Parse(doc["raw"]!.GetValue<string>())) };
        }
        catch (InvalidDataException e)
        {
            actual = new JsonObject { ["ok"] = false, ["error"] = e.Message };
        }
        Check("manifest", file, doc, "expect", actual);
    }

    private static ModCandidate Candidate(JsonObject m)
    {
        var dir = m["dir"]!.GetValue<string>();
        var raw = m["raw"] is JsonValue r ? r.GetValue<string>() : m["manifest"]!.ToJsonString();
        try
        {
            return new ModCandidate { Directory = dir, Manifest = Manifest.Parse(raw) };
        }
        catch (InvalidDataException e)
        {
            return new ModCandidate { Directory = dir, Error = e.Message };
        }
    }

    [Theory]
    [MemberData(nameof(ResolverFiles))]
    public void Resolver_fixture(string file)
    {
        var doc = Load("resolver", file);
        var env = doc["env"]!.AsObject();
        var unavailable = (env["unavailableFeatures"] as JsonArray ?? []).Select(f => f!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var loaded = (env["loaded"] as JsonArray ?? [])
            .Select(n => Manifest.Parse(n!.ToJsonString()))
            .ToDictionary(m => m.Id, m => m, StringComparer.Ordinal);
        var side = env["side"]?.GetValue<string>() == "client" ? ManifestSide.Client : ManifestSide.Server;
        var result = Resolver.Resolve(
            doc["mods"]!.AsArray().Select(m => Candidate(m!.AsObject())).ToList(),
            new ResolveEnvironment(SemVersion.Parse(env["kiln"]!.GetValue<string>()), SemVersion.Parse(env["api"]!.GetValue<string>()),
                side, f => !unavailable.Contains(f), loaded)
            {
                Hosting = env["hosting"]?.GetValue<bool>() ?? false,
            });
        var actual = new JsonObject
        {
            ["order"] = Strings(result.Order.Select(c => c.Id)),
            // compared as a set: sorted by folder, then id
            ["rejected"] = new JsonArray([.. result.Rejected
                .OrderBy(r => r.Candidate.Directory, StringComparer.Ordinal).ThenBy(r => r.Candidate.Id, StringComparer.Ordinal)
                .Select(r => (JsonNode)new JsonObject
                {
                    ["id"] = r.Candidate.Id, ["dir"] = r.Candidate.Directory, ["state"] = Lower(r.State), ["reason"] = r.Reason,
                })]),
        };
        Check("resolver", file, doc, "expect", actual);
    }

    [Theory]
    [MemberData(nameof(SemverFiles))]
    public void Semver_fixture(string file)
    {
        var doc = Load("semver", file);
        switch (file)
        {
            case "parse.json":
                foreach (var c in doc["cases"]!.AsArray().Select(n => n!.AsObject()))
                {
                    var ok = SemVersion.TryParse(c["text"]!.GetValue<string>(), out var v);
                    c["expect"] = Compare(c["expect"], ok
                        ? new JsonObject
                        {
                            ["ok"] = true, ["major"] = v.Major, ["minor"] = v.Minor, ["patch"] = v.Patch, ["pre"] = Strings(v.Pre),
                            ["build"] = v.Build, ["text"] = v.ToString(),
                        }
                        : new JsonObject { ["ok"] = false }, file, c["text"]!.GetValue<string>());
                }
                break;
            case "ranges.json":
                foreach (var c in doc["cases"]!.AsArray().Select(n => n!.AsObject()))
                {
                    var range = c["range"]!.GetValue<string>();
                    var version = c["version"]!.GetValue<string>();
                    c["satisfies"] = Compare(c["satisfies"], VersionRange.Parse(range).Satisfies(SemVersion.Parse(version)), file, $"{range} ~ {version}");
                }
                foreach (var c in doc["invalid"]!.AsArray().Select(n => n!.AsObject()))
                {
                    var range = c["range"]!.GetValue<string>();
                    c["ok"] = Compare(c["ok"], VersionRange.TryParse(range, out _, out _), file, range);
                }
                break;
            case "order.json":
                var ordered = doc["ordered"]!.AsArray().Select(n => SemVersion.Parse(n!.GetValue<string>())).ToList();
                for (var i = 0; i < ordered.Count - 1; i++)
                    Assert.True(ordered[i] < ordered[i + 1], $"{ordered[i]} < {ordered[i + 1]}");
                foreach (var pair in doc["equal"]!.AsArray().Select(n => n!.AsArray()))
                    Assert.True(SemVersion.Parse(pair[0]!.GetValue<string>()) == SemVersion.Parse(pair[1]!.GetValue<string>()), pair.ToJsonString());
                break;
            case "api.json":
                foreach (var c in doc["cases"]!.AsArray().Select(n => n!.AsObject()))
                {
                    var api = c["api"]!.GetValue<string>();
                    var current = c["current"]!.GetValue<string>();
                    c["satisfies"] = Compare(c["satisfies"], VersionRange.AtLeastSameMajor(SemVersion.Parse(api)).Satisfies(SemVersion.Parse(current)), file, $"{api} on {current}");
                }
                break;
            default:
                Assert.Fail($"semver/{file}: no runner for this file");
                break;
        }
        if (Write) File.WriteAllText(Path.Combine(Fixtures(), "semver", file), doc.ToJsonString(Pretty) + "\n");
    }

    /// <summary>Write mode: the actual value. Compare mode: asserts it equals the stored one.</summary>
    private static JsonNode Compare(JsonNode? stored, JsonNode actual, string file, string what)
    {
        if (Write) return actual;
        Assert.True(stored is not null, $"semver/{file} {what}: no expected value yet; run with KILN_FIXTURES_WRITE=1");
        Assert.True(stored!.ToJsonString() == actual.ToJsonString(), $"semver/{file} {what}: expected {stored.ToJsonString()}, actual {actual.ToJsonString()}");
        return stored.DeepClone();
    }
}
