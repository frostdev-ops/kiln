// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;

namespace Kiln.Analyzers.Tools;

internal static class ApiScanProjectSources
{
    internal static IReadOnlyList<string> Read(string project)
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
        foreach (var arg in new[] { "msbuild", project, "-getItem:Compile", "-property:Configuration=Release", "-nologo", "-nodeReuse:false" })
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
        return doc.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
            .Select(item => item.GetProperty("FullPath").GetString() ?? throw new InvalidDataException("Compile item has no full path."))
            .Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToArray();
    }
}
