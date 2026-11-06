// SPDX-License-Identifier: MIT
// Which Kiln build a mod was compiled against, and whether that is newer than the running one. Self-contained (SemVer.cs
// only): Kiln.Sdk's build task compiles this file too. The Rust port is crates/kiln-manage/src/manifest.rs (KilnBuild).
using System.Reflection;
using System.Text.RegularExpressions;

namespace Kiln.Host;

/// <summary>A Kiln build: its version as the runtime bundle names it (<c>0.2.0-dev+20261106.0f0a3f6</c>, the commit's day and
/// id after the <c>+</c>) and, when known, the commit's time (<c>2026-11-06T08:41:12Z</c>, the bundle's <c>created</c>). Kiln.Api
/// carries its own as assembly metadata (managed/Directory.Build.targets); Kiln.Sdk copies it into a mod's kiln.mod.json
/// (<c>"kilnBuild": {"version", "created"}</c>).</summary>
internal sealed record KilnBuild(string Version, string? Created)
{
    /// <summary>Kiln.Api's assembly metadata keys.</summary>
    public const string VersionKey = "Kiln.Build", CreatedKey = "Kiln.BuildCreated";

    private static readonly Regex Time = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", RegexOptions.CultureInvariant);

    public override string ToString() => Version;

    /// <summary>A stamp from its two texts; null when the version is not <c>semver[+build]</c> (a created time that is not
    /// <c>yyyy-MM-ddTHH:mm:ssZ</c> is dropped).</summary>
    public static KilnBuild? From(string? version, string? created)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        version = version.Trim();
        if (!SemVersion.TryParse(version.Split('+')[0], out _)) return null;
        return new KilnBuild(version, created is { } c && Time.IsMatch(c.Trim()) ? c.Trim() : null);
    }

    /// <summary>The stamp an assembly was built with (Kiln.Api's), or null.</summary>
    public static KilnBuild? Of(Assembly assembly)
    {
        string? version = null, created = null;
        foreach (var a in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            if (a.Key == VersionKey) version = a.Value;
            else if (a.Key == CreatedKey) created = a.Value;
        return From(version, created);
    }

    /// <summary>Whether this build is newer than <paramref name="other"/>: by the semantic version, then by the commit time when both
    /// have one, else by the day in the build part. Two builds of one day without a time can't be told apart: not newer.</summary>
    public bool IsNewerThan(KilnBuild other)
    {
        var (a, ad) = Split(Version);
        var (b, bd) = Split(other.Version);
        var c = a.CompareTo(b);
        if (c != 0) return c > 0;
        if (Created != null && other.Created != null) return string.CompareOrdinal(Created, other.Created) > 0;
        return ad.Length == 8 && bd.Length == 8 && string.CompareOrdinal(ad, bd) > 0;
    }

    // "0.2.0-dev+20261106.0f0a3f6" -> (0.2.0-dev, "20261106")
    private static (SemVersion Version, string Day) Split(string text)
    {
        var plus = text.IndexOf('+');
        var core = SemVersion.Parse(plus < 0 ? text : text[..plus]);
        var day = plus < 0 ? "" : text[(plus + 1)..].Split('.')[0];
        return (core, day.Length == 8 && day.All(char.IsAsciiDigit) ? day : "");
    }
}

/// <summary>A mod compiled against a newer Kiln than the running one fails when the runtime lacks a Kiln member it uses: the
/// runtime throws MissingFieldException, MissingMethodException, TypeLoadException (or cannot find a Kiln assembly) as it
/// loads the mod's types or compiles a method that uses one. Semver can't see it (both builds say 0.2.0-dev).</summary>
internal static class NewerRuntime
{
    private static readonly Regex Quoted = new("'([^']+)'", RegexOptions.CultureInvariant);

    /// <summary>The Kiln member, type or assembly <paramref name="e"/> (or an exception inside it) says this runtime lacks; null
    /// when it is about something else.</summary>
    public static string? MissingKilnMember(Exception? e)
    {
        for (var depth = 0; e != null && depth < 8; depth++)
        {
            switch (e)
            {
                case MissingMemberException m when KilnName(Quoted.Match(m.Message) is { Success: true } q ? q.Groups[1].Value : null) is { } name:
                    return name;
                case TypeLoadException t when IsKiln(t.TypeName) && FromKilnAssembly(t.Message):
                    return t.TypeName;
                case FileNotFoundException f when AssemblyName(f.FileName) is { } asm && asm.StartsWith("Kiln.", StringComparison.Ordinal):
                    return asm + ".dll";
                case ReflectionTypeLoadException r:
                    foreach (var inner in r.LoaderExceptions)
                        if (MissingKilnMember(inner) is { } found) return found;
                    break;
                case AggregateException a:
                    foreach (var inner in a.InnerExceptions)
                        if (MissingKilnMember(inner) is { } found) return found;
                    return null;
            }
            e = e.InnerException;  // TargetInvocationException, TypeInitializationException, ...
        }
        return null;
    }

    /// <summary>What mods.txt and kiln.log say: <c>item-blocks needs a newer Kiln runtime (it uses Kiln.StatusLook.Fire, which
    /// runtime 0.2.0-dev+20261106.10a0d37 doesn't have): update Kiln on the launcher's Runtime page</c>.</summary>
    public static string Explain(string mod, string member, string runtime, KilnBuild? builtWith) =>
        $"{mod} needs a newer Kiln runtime (it uses {member}, which runtime {runtime} doesn't have" +
        (builtWith is { } b && b.Version != runtime ? $"; it was built against {b.Version}" : "") +
        "): update Kiln on the launcher's Runtime page";

    // "Kiln.StatusLook.Fire", "Void Kiln.Blocks.Define(System.String, Kiln.VfxBlock)" -> the member from its declaring type on,
    // when that type is Kiln's (a mod's own method that takes a Kiln parameter is not)
    private static string? KilnName(string? text)
    {
        if (text == null) return null;
        var paren = text.IndexOf('(');
        var head = paren < 0 ? text : text[..paren];
        var name = text[(head.LastIndexOf(' ') + 1)..];
        return IsKiln(name) ? name : null;
    }

    private static bool IsKiln(string? name) => name != null && name.StartsWith("Kiln.", StringComparison.Ordinal);

    // "Could not load type 'Kiln.X' from assembly 'Kiln.Api, Version=...'": the type was expected in a Kiln assembly
    private static bool FromKilnAssembly(string message) =>
        message.Contains("from assembly 'Kiln.", StringComparison.Ordinal) || !message.Contains("from assembly '", StringComparison.Ordinal);

    private static string? AssemblyName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return null;
        try
        {
            return new AssemblyName(fileName).Name;
        }
        catch (Exception e) when (e is ArgumentException or FileLoadException)
        {
            return Path.GetFileNameWithoutExtension(fileName);
        }
    }
}
