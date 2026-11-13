// SPDX-License-Identifier: MIT
// A mod's blocks/ folder is input from the internet: the managed reader has the bounds of kiln.vfx's boot reader for the
// same files, and reads nothing that is reached through a link.
using Xunit;

namespace Kiln.Blocks.Tests;

public sealed class BlockFileLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kiln-block-limits-{Guid.NewGuid():N}");

    public BlockFileLimitsTests() => Directory.CreateDirectory(Path.Combine(_dir, "blocks"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string OneStatus = """{ "statuses": { "NAME": { "name": "Chilled", "seconds": 5 } } }""";

    private void Write(string name, string json) => File.WriteAllText(Path.Combine(_dir, "blocks", name), json);

    private (ModBlocks Mod, List<string> Names) Read()
    {
        var mod = new ModBlocks("limits-test", 1);
        BlockJson.Read(mod, _dir);
        return (mod, mod.Data!.Entries.Select(e => e.Name).ToList());
    }

    [Fact]
    public void OrdinaryFilesAreReadAsBefore()
    {
        Write("a.json", OneStatus.Replace("NAME", "one"));
        Write("b.json", OneStatus.Replace("NAME", "two"));
        var (mod, names) = Read();
        Assert.Equal(["one", "two"], names);
        Assert.Empty(mod.Errors);
    }

    [Fact]
    public void AFileOverTheSizeLimitIsLeftOutAndTheOthersAreRead()
    {
        Write("a.json", OneStatus.Replace("NAME", "one"));
        // valid JSON, one byte over the limit
        Write("big.json", "{ \"comment\": \"" + new string('x', BlockJson.MaxFileBytes) + "\" }");
        Write("c.json", OneStatus.Replace("NAME", "three"));
        var (mod, names) = Read();
        Assert.Equal(["one", "three"], names);
        Assert.Contains(mod.Errors, e => e.Contains("blocks/big.json is not read"));
    }

    [Fact]
    public void AFolderWithTooManyFilesOrEntriesIsNotReadAtAll()
    {
        for (var i = 0; i <= BlockJson.MaxFiles; i++) Write($"f{i:0000}.json", OneStatus.Replace("NAME", $"s{i}"));
        var (mod, names) = Read();
        Assert.Empty(names);
        Assert.Contains(mod.Errors, e => e.Contains($"more than {BlockJson.MaxFiles}"));
        // and the listing itself stops: other entries count too
        foreach (var f in Directory.GetFiles(Path.Combine(_dir, "blocks"))) File.Delete(f);
        Write("a.json", OneStatus.Replace("NAME", "one"));
        for (var i = 0; i < BlockJson.MaxEntries; i++) File.WriteAllBytes(Path.Combine(_dir, "blocks", $"n{i:00000}.txt"), []);
        (mod, names) = Read();
        Assert.Empty(names);
        Assert.Contains(mod.Errors, e => e.Contains($"more than {BlockJson.MaxEntries} entries"));
    }

    [Fact]
    public void AFileOrAFolderReachedThroughALinkIsNotRead()
    {
        if (OperatingSystem.IsWindows()) return;  // making a link needs a privilege there; the reader's check is the same code
        var outside = Path.Combine(Path.GetTempPath(), $"kiln-block-limits-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.json"), OneStatus.Replace("NAME", "outside"));
            Write("a.json", OneStatus.Replace("NAME", "one"));
            File.CreateSymbolicLink(Path.Combine(_dir, "blocks", "link.json"), Path.Combine(outside, "secret.json"));
            var (mod, names) = Read();
            Assert.Equal(["one"], names);
            Assert.Contains(mod.Errors, e => e.Contains("blocks/link.json is not read"));
            // the whole blocks folder as a link: nothing of it is read
            Directory.Delete(Path.Combine(_dir, "blocks"), recursive: true);
            Directory.CreateSymbolicLink(Path.Combine(_dir, "blocks"), outside);
            (mod, names) = Read();
            Assert.Empty(names);
            Assert.NotEmpty(mod.Errors);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
