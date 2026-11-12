// SPDX-License-Identifier: MIT
// Shared by the generator, golden and round-trip tests: where the repository and the built fixtures are.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Kiln.Game.Tests;

static class GenRepo
{
    /// <summary>The repository root. The test assembly may be built outside the tree (--artifacts-path); then the
    /// source file's place is used.</summary>
    public static string Root([CallerFilePath] string source = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(source) ?? "" })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "tools", "typegen", "typegen.py")) &&
                    File.Exists(Path.Combine(d.FullName, "managed", "tests", "Kiln.Game.Tests", "Kiln.Game.Tests.csproj")))
                    return d.FullName;
        throw new InvalidOperationException("repository root not found above the test assembly or its source");
    }

    public static string Path_(params string[] parts) => Path.Combine([Root(), .. parts]);

    public static string Fixture(params string[] parts) =>
        Path.Combine([Root(), "managed", "tests", "Kiln.Game.Tests", "Fixtures", .. parts]);

    /// <summary>A file the test project's build put next to the test assembly (fixture builds).</summary>
    public static string Built(params string[] parts)
    {
        var p = Path.Combine([AppContext.BaseDirectory, .. parts]);
        if (!File.Exists(p)) throw new FileNotFoundException("not built with the tests: " + p);
        return p;
    }

    /// <summary>The Kiln.Game this test assembly was built against.</summary>
    public static Assembly GameAssembly => Assembly.Load(new AssemblyName("Kiln.Game"));

    public static JsonElement ReadJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return doc.RootElement.Clone();
    }
}
