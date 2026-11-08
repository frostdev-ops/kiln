// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Analyzers.Tests;

/// <summary>The generated tables (tools/gen-apis.sh) match what the services enforce.</summary>
public sealed class TablesTests
{
    [Theory]
    [InlineData("M:Kiln.Chat.Broadcast(System.String)")]              // Services.RequireTickThread
    [InlineData("M:Kiln.Combat.SetHealth(Kiln.EntityId,System.Int32)")]
    [InlineData("M:Kiln.Events.Post``1(``0)")]                         // Game.AssertTickThread
    public void Tick_thread_members(string id) => Assert.Contains(id, KilnApis.TickThread);

    [Theory]
    [InlineData("M:Kiln.Scheduler.Post(System.Action)")]               // the way back: any thread
    [InlineData("M:Kiln.Mods.Send(System.String,System.String,System.Object)")]
    [InlineData("M:Kiln.Logger.Info(System.String)")]
    [InlineData("M:Kiln.Game.AssertTickThread")]                       // the check itself
    public void Any_thread_members(string id) => Assert.DoesNotContain(id, KilnApis.TickThread);

    [Fact]
    public void Advanced_members_are_kiln_hooks()
    {
        Assert.Contains("M:Kiln.Symbols.Get(System.String)", KilnApis.Advanced);
        Assert.Contains("M:Kiln.Hooks.Detour(System.String,Kiln.DetourHandler,System.Int32)", KilnApis.Advanced);
        Assert.All(KilnApis.Advanced, id => Assert.Matches(@"^M:Kiln\.(Hooks|Symbols|Memory|Constants)\.", id));
    }

    [Fact]
    public void Services_are_the_managed_service_folders()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var repo = Directory.Exists(Path.Combine(root, "managed/services")) ? root : FindRepo();
        var folders = Directory.GetDirectories(Path.Combine(repo, "managed/services")).Select(Path.GetFileName)
            .Where(n => n!.StartsWith("Kiln.")).Select(n => n!.Substring(5)).OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(folders, KilnApis.Services.OrderBy(n => n, StringComparer.Ordinal));
    }

    private static string FindRepo([System.Runtime.CompilerServices.CallerFilePath] string self = "")
    {
        // from the test binaries, else from this source file (an --artifacts-path outside the tree)
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(self) ?? "" })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "VERSION")) && Directory.Exists(Path.Combine(d.FullName, "managed/services"))) return d.FullName;
        var src = new FileInfo(typeof(TablesTests).Assembly.Location).Directory!.FullName;
        throw new DirectoryNotFoundException("repository root not found from " + src);
    }
}
