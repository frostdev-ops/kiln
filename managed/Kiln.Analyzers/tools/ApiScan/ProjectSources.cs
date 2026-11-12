// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;

namespace Kiln.Analyzers.Tools;

internal static class ApiScanProjectSources
{
    /// <summary>The project's Compile items and its NoWarn ids (the scan compiles with the project's own suppressions,
    /// so that an [Experimental] API the project opts into, e.g. KILNX002, is not a compile error here).</summary>
    internal static (IReadOnlyList<string> Files, IReadOnlyList<string> NoWarn) Read(string project)
    {
        project = Path.GetFullPath(project);
        // The project owns its source list: a directory walk includes excluded tests and misses linked files.
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "msbuild", project, "-getItem:Compile", "-getProperty:NoWarn", "-property:Configuration=Release", "-nologo", "-nodeReuse:false" })
            start.ArgumentList.Add(arg);
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["MSBUILDUSESERVER"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSBuild source evaluation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException($"MSBuild source evaluation timed out: {project}");
        }
        var text = output.GetAwaiter().GetResult();
        var diagnostics = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"MSBuild source evaluation failed for {project}: {diagnostics}\n{text}");
        using var doc = JsonDocument.Parse(text);
        var noWarn = doc.RootElement.GetProperty("Properties").GetProperty("NoWarn").GetString() ?? "";
        var files = doc.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Select(item => item.GetProperty("FullPath").GetString() ?? throw new InvalidDataException("Compile item has no full path."))
            .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var ids = noWarn.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        return (files, ids);
    }
}
