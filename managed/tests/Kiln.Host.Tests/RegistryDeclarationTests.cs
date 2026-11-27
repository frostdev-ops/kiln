// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using Kiln;
using Kiln.Host;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class RegistryDeclarationTests
{
    private static int _constructed;

    [Fact]
    public void MetadataDeclaresPointsWithoutConstructingTheMod()
    {
        _constructed = 0;
        Assert.Equal(RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload, RegistryDeclarations.PointsOf(typeof(Declared)));
        Assert.Equal(0, _constructed);
        Assert.Equal(RegistryEntryPoint.GameStart, RegistryDeclarations.PointsOf(typeof(Default)));
        Assert.Equal((RegistryEntryPoint)0, RegistryDeclarations.PointsOf(typeof(NoOverride)));
    }

    [Fact]
    public void InvalidDeclarationsFailClosed()
    {
        Assert.Throws<InvalidDataException>(() => RegistryDeclarations.PointsOf(typeof(Unknown)));
        Assert.Throws<InvalidDataException>(() => RegistryDeclarations.PointsOf(typeof(WithoutOverride)));
    }

    [Fact]
    public void GameStartExportLookupHasATypedRefusalAndPrivateHelpersFallThrough()
    {
        var exports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SharedRuntime.dll"] = "dependency" };
        var error = Assert.Throws<RegistryContextException>(() => RegistryDeclarations.GameStartExport("author", exports, new AssemblyName("SharedRuntime")));
        Assert.Equal(RegistryContextError.RuntimeExportUnavailable, error.Code);
        Assert.Contains("ModLoad or later", error.Message);
        Assert.Null(RegistryDeclarations.GameStartExport("author", exports, new AssemblyName("OwnHelper")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid\0.dll")]
    public void MetadataPrefilterRefusesMalformedPathsWithoutThrowing(string file)
        => Assert.False(RegistryDeclarations.MightOverride(file, []));

    [Fact]
    public void MetadataPrefilterFollowsExternalGenericBases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "registry-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Image(directory, "Base", false);
            var child = Image(directory, "Child", true);
            Assert.True(RegistryDeclarations.MightOverride(child, new[] { directory }));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    // review4 recheck nit (NS1): a mod that only calls Registry.Change / Registry.Read (Kiln.Registry's Registry type) is
    // a registry user, so the stage decodes its base at resources-ready; GameStart authorship stays MightOverride's.
    [Fact]
    public void AModThatOnlyCallsRegistryChangeIsARegistryUser()
    {
        var directory = Path.Combine(Path.GetTempPath(), "registry-users-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var change = Reference(directory, "ChangeOnly", "Kiln.Registry", "Kiln.Registry", "Registry");
            Assert.True(RegistryDeclarations.UsesRegistry(change, new[] { directory }));
            Assert.False(RegistryDeclarations.MightOverride(change, new[] { directory }), "not a GameStart author");
            var context = Image(directory, "ContextUser", false);
            Assert.True(RegistryDeclarations.UsesRegistry(context, new[] { directory }), "an OnRegistry author is one too");
            var other = Path.Combine(directory, "plain");
            Directory.CreateDirectory(other);
            var plain = Reference(other, "Plain", "Kiln.Api", "Kiln", "Mod");
            Assert.False(RegistryDeclarations.UsesRegistry(plain, new[] { other }), "a mod without the registry API is none");
            Reference(other, "Helper", "Kiln.Registry", "Kiln.Registry", "Registry");
            Assert.True(RegistryDeclarations.UsesRegistry(plain, new[] { other }), "a helper DLL of the mod's own folder that calls it counts");
            Assert.False(RegistryDeclarations.UsesRegistry(Path.Combine(directory, "missing.dll"), new[] { directory }));
            // review of api-shape: since 0.2.0-dev.32 the entry class is Registries (Registry is its old name): a mod that only
            // calls Registries.Change / Read / Ready names no Registry, and is a registry user all the same; a helper DLL too
            var renamed = Path.Combine(directory, "renamed");
            Directory.CreateDirectory(renamed);
            var registries = Reference(renamed, "RegistriesOnly", "Kiln.Registry", "Kiln.Registry", "Registries");
            Assert.True(RegistryDeclarations.UsesRegistry(registries, new[] { renamed }), "a mod on the new name Registries is one");
            var helped = Path.Combine(directory, "helped");
            Directory.CreateDirectory(helped);
            var entry = Reference(helped, "Entry", "Kiln.Api", "Kiln", "Mod");
            Assert.False(RegistryDeclarations.UsesRegistry(entry, new[] { helped }));
            Reference(helped, "RegistriesHelper", "Kiln.Registry", "Kiln.Registry", "Registries");
            Assert.True(RegistryDeclarations.UsesRegistry(entry, new[] { helped }), "a helper DLL on the new name counts");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static string Reference(string directory, string name, string assembly, string ns, string type)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(assembly), new Version(1, 0), default, default, 0, default);
        metadata.AddTypeReference(reference, metadata.GetOrAddString(ns), metadata.GetOrAddString(type));
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default, default, default);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        var file = Path.Combine(directory, name + ".dll"); File.WriteAllBytes(file, image.ToArray()); return file;
    }

    // review4 nit: the code identity hashes the mod's DLLs only when a context's Identity is first read (never when the mod
    // loads on the tick thread), and a DLL whose path, size and write time are unchanged is not read again.
    [Fact]
    public void TheCodeIdentityIsHashedAtFirstUseAndCached()
    {
        var directory = Path.Combine(Path.GetTempPath(), "registry-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var dll = Path.Combine(directory, "Mod.dll");
            File.WriteAllBytes(dll, [1, 2, 3]);
            File.SetLastWriteTimeUtc(dll, new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc));
            var code = RegistryDispatch.Code(directory);
            Assert.Equal(0, RegistryDispatch.FileHashReads(dll));
            var first = code();
            Assert.Equal(1, RegistryDispatch.FileHashReads(dll));
            Assert.Equal(first, code());
            Assert.Equal(first, RegistryDispatch.Code(directory)());  // a reload of the same files
            Assert.Equal(1, RegistryDispatch.FileHashReads(dll));
            File.WriteAllBytes(dll, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(dll, new DateTime(2026, 11, 25, 1, 0, 0, DateTimeKind.Utc));
            Assert.NotEqual(first, RegistryDispatch.Code(directory)());
            Assert.Equal(2, RegistryDispatch.FileHashReads(dll));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Image(string directory, string name, bool genericBase)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0), default, default, 0, AssemblyHashAlgorithm.None);
        var reference = metadata.AddAssemblyReference(metadata.GetOrAddString(genericBase ? "Base" : "Kiln.Api"), new Version(1, 0), default, default, 0, default);
        var parent = metadata.AddTypeReference(reference, metadata.GetOrAddString(genericBase ? "Fixture" : "Kiln"), metadata.GetOrAddString(genericBase ? "Author`1" : "RegistryContext"));
        EntityHandle baseType = default;
        if (genericBase)
        {
            var signature = new BlobBuilder();
            new BlobEncoder(signature).TypeSpecificationSignature().GenericInstantiation(parent, 1, false).AddArgument().Int32();
            baseType = metadata.AddTypeSpecification(metadata.GetOrAddBlob(signature));
        }
        metadata.AddTypeDefinition(0, default, metadata.GetOrAddString("<Module>"), default, default, default);
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("Fixture"), metadata.GetOrAddString("Derived"), baseType, default, default);
        var image = new BlobBuilder();
        new ManagedPEBuilder(new PEHeaderBuilder(), new MetadataRootBuilder(metadata), new BlobBuilder()).Serialize(image);
        var file = Path.Combine(directory, name + ".dll"); File.WriteAllBytes(file, image.ToArray()); return file;
    }

    [RegistryEntryPoints(RegistryEntryPoint.WorldLoad | RegistryEntryPoint.HotReload)]
    private sealed class Declared : Mod
    {
        public Declared() => ++_constructed;
        public override void OnRegistry(RegistryContext context) { }
    }
    private sealed class Default : Mod { public override void OnRegistry(RegistryContext context) { } }
    private sealed class NoOverride : Mod;
    [RegistryEntryPoints((RegistryEntryPoint)32)]
    private sealed class Unknown : Mod { public override void OnRegistry(RegistryContext context) { } }
    [RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
    private sealed class WithoutOverride : Mod;
}
