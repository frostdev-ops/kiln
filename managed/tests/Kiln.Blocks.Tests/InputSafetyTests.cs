// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class InputSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kiln-input-test-" + Guid.NewGuid());
    private static BlockContext Ctx() => new(new ModBlocks("safety-test", 1) { ManifestClients = "mod" }, "test");
    public InputSafetyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("..\\outside.png")]
    [InlineData("C:\\outside.png")]
    [InlineData("/outside.png")]
    public void File_paths_are_mod_relative_on_every_platform(string path) =>
        Assert.Equal("icon", Assert.Throws<BlockError>(() => BlockIcons.CheckPng(Ctx(), path, "icon", _dir)).Field);

    [Theory]
    [InlineData(".. ")]
    [InlineData(".. .")]
    [InlineData("icons.")]
    [InlineData("icons ")]
    public void Windows_aliased_directory_names_are_refused_on_every_platform(string part)
    {
        // Unix can store these spellings literally; Win32 trims them, including aliases of the parent directory.
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Path.Combine(_dir, part));
            File.WriteAllBytes(Path.Combine(_dir, part, "a.png"), Png);
        }
        var error = Assert.Throws<BlockError>(() => BlockFiles.InMod(Ctx(), part + "/a.png", "icon", _dir));
        Assert.Equal("icon", error.Field);
        Assert.Contains("relative path", error.Problem);
    }

    [Fact]
    public void File_and_directory_links_cannot_escape_the_mod()
    {
        var root = Path.Combine(_dir, "mod");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(_dir, "outside.png"), "outside");
        File.CreateSymbolicLink(Path.Combine(root, "file.png"), Path.Combine(_dir, "outside.png"));
        Directory.CreateSymbolicLink(Path.Combine(root, "icons"), _dir);
        File.CreateSymbolicLink(Path.Combine(root, "indirect.png"), Path.Combine(root, "icons", "outside.png"));
        foreach (var path in new[] { "file.png", "icons/outside.png", "indirect.png" })
            Assert.Contains("links cannot leave", Assert.Throws<BlockError>(() => BlockFiles.InMod(Ctx(), path, "icon", root)).Problem);
        File.WriteAllText(Path.Combine(root, "inside.png"), "inside");
        File.CreateSymbolicLink(Path.Combine(root, "safe.png"), Path.Combine(root, "inside.png"));
        Assert.Equal(Path.Combine(root, "inside.png"), BlockFiles.InMod(Ctx(), "safe.png", "icon", root));
    }

    // A complete, valid 1x1 RGBA PNG; invalid headers previously passed even without pixel data.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII=");

    [Fact]
    public void Png_validation_rejects_truncation_bad_crc_and_bad_dimensions()
    {
        var path = Path.Combine(_dir, "a.png");
        File.WriteAllBytes(path, Png);
        Assert.Equal((1, 1), BlockIcons.PngSize(path));
        foreach (var size in new[] { 24, 33, Png.Length - 1 })
        {
            File.WriteAllBytes(path, Png[..size]);
            Assert.Null(BlockIcons.PngSize(path));
        }
        foreach (var at in new[] { 16, 20, 29, 45 })
        {
            var bad = Png.ToArray();
            bad[at] ^= 0xff;
            File.WriteAllBytes(path, bad);
            Assert.Null(BlockIcons.PngSize(path));
        }
    }

    [Theory]
    [InlineData(IconState.None)]
    [InlineData(IconState.Pending)]
    [InlineData(IconState.Failed)]
    public void An_unknown_texture_is_never_named_without_a_working_alias(IconState state)
    {
        var icon = new OwnIcon("buffs", "status", Content.Buffs, "starter", Guid.NewGuid(), "icon.png", "status");
        var retried = false;
        Assert.False(BlockIcons.ReadyToName(icon, true, state, () => Guid.NewGuid(), (_, _) => false, () => retried = true, _ => { }));
        Assert.Equal(state == IconState.Pending, retried);
        Assert.False(icon.Aliased);
        Assert.True(BlockIcons.ReadyToName(icon, true, state, () => Guid.NewGuid(), (_, _) => true, () => { }, _ => { }));
        Assert.True(icon.Aliased);
    }

    [Fact]
    public void A_served_texture_needs_no_starter_icon_or_alias_service()
    {
        var icon = new OwnIcon("buffs", "status", Content.Buffs, "starter", Guid.NewGuid(), "icon.png", "status");
        Assert.True(BlockIcons.ReadyToName(icon, true, IconState.Served, () => throw new Exception("not needed"),
            (_, _) => throw new Exception("not needed"), () => { }, _ => { }));
        Assert.False(BlockIcons.ReadyToName(icon, true, IconState.Pending, () => null, (_, _) => true, () => { }, _ => { }));
        Assert.False(BlockIcons.ReadyToName(icon, true, IconState.Pending, () => Guid.Empty, (_, _) => true, () => { }, _ => { }));
    }

    private (Vector3 Min, Vector3 Max)? Bounds(string edits)
    {
        var doc = JsonNode.Parse("""
            { "nodes": [{ "mesh": 0 }], "meshes": [{"primitives": [{"attributes": {"POSITION": 0}}]}],
              "accessors": [{"min": [-1,0,-2], "max": [1,2,2]}], "scenes": [{"nodes": [0]}] }
            """)!;
        foreach (var (key, value) in JsonNode.Parse(edits)!.AsObject()) doc[key] = value?.DeepClone();
        var path = Path.Combine(_dir, "bounds.gltf");
        File.WriteAllText(path, doc.ToJsonString());
        return GltfBounds.Of(path);
    }

    [Fact]
    public void Bounds_apply_the_default_scenes_world_transform_once()
    {
        Assert.Equal((new Vector3(-4, 0, -2), new Vector3(-2, 2, 2)), Bounds("""{"nodes":[{"translation":[3,0,0],"children":[1]},{"mesh":0}]}"""));
        Assert.Equal((new Vector3(-4, 0, -2), new Vector3(-2, 2, 2)), Bounds("""{"scenes":null,"nodes":[{"translation":[3,0,0],"children":[1]},{"mesh":0}]}"""));
    }

    [Theory]
    [InlineData("{\"scene\":-1}")]
    [InlineData("{\"nodes\":[{\"mesh\":0,\"translation\":[null,0,0]}]}")]
    [InlineData("{\"nodes\":[{\"mesh\":0,\"rotation\":[0,0,0,0]}]}")]
    [InlineData("{\"nodes\":[{\"mesh\":0,\"scale\":[1e100,1,1]}]}")]
    [InlineData("{\"nodes\":[{\"mesh\":0,\"children\":[0,0]}]}")]
    [InlineData("{\"meshes\":[42]}")]
    [InlineData("{\"accessors\":[{\"min\":[2,0,0],\"max\":[1,1,1]}]}")]
    public void Malformed_bounds_fail_soft_without_partial_geometry(string edits) => Assert.Null(Bounds(edits));

    [Fact]
    public void Glb_lengths_cannot_overflow_the_json_chunk_bounds_check()
    {
        var path = Path.Combine(_dir, "bad.glb");
        var bytes = new byte[24];
        "glTF"u8.CopyTo(bytes);
        BitConverter.GetBytes(2u).CopyTo(bytes, 4);
        BitConverter.GetBytes(24u).CopyTo(bytes, 8);
        BitConverter.GetBytes(0x4e4f534au).CopyTo(bytes, 16);
        foreach (var length in new[] { int.MaxValue, -1, 100, 3 })
        {
            BitConverter.GetBytes(length).CopyTo(bytes, 12);
            File.WriteAllBytes(path, bytes);
            Assert.Null(GltfBounds.Of(path));
        }
    }

    [Fact]
    public unsafe void The_old_v4_content_table_remains_usable_but_the_new_slot_is_optional()
    {
        var oldSize = (int)Marshal.OffsetOf<ContentNative.Table>(nameof(ContentNative.Table.IconAlias));
        var bytes = stackalloc byte[oldSize];
        var table = (ContentNative.Table*)bytes;
        *(uint*)bytes = (uint)oldSize;
        Assert.Equal((nint)table, (nint)ContentNative.FromService((nint)table));
        *(uint*)bytes = (uint)oldSize - 1;
        Assert.Equal(0, (nint)ContentNative.FromService((nint)table));
        Assert.Equal(0, (nint)ContentNative.FromService(0));
    }
}
