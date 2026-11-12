// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>ItemId moved from Kiln.Items to Kiln.Api in dev.25, with a type forward left in Kiln.Items. The fixture
/// ItemIdConsumer was compiled against a stand-in of the old Kiln.Items (fixtures/OldItems), so it references
/// [Kiln.Items]Kiln.ItemId; loaded next to the real assemblies it must see Kiln.Api's ItemId.</summary>
public sealed class ItemIdForwardTests
{
    private const string Consumer = "ItemIdConsumer";

    /// <summary>Like a mod's context: Kiln's assemblies come from the default context.</summary>
    private sealed class ConsumerAlc() : AssemblyLoadContext("itemid-consumer", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => null;
    }

    private static string FindConsumer()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            foreach (var candidate in new[] { Path.Combine(d.FullName, Consumer), Path.Combine(d.FullName, "fixtures", Consumer, "bin") })
                if (Directory.Exists(candidate))
                {
                    var f = Directory.EnumerateFiles(candidate, Consumer + ".dll", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (f is not null) return f;
                }
        throw new InvalidOperationException(Consumer + ".dll not built");
    }

    [Fact]
    public void The_consumer_references_ItemId_in_Kiln_Items()
    {
        using var pe = new PEReader(File.OpenRead(FindConsumer()));
        var md = pe.GetMetadataReader();
        var scopes = new List<string>();
        foreach (var h in md.TypeReferences)
        {
            var t = md.GetTypeReference(h);
            if (md.GetString(t.Name) != "ItemId" || md.GetString(t.Namespace) != "Kiln") continue;
            Assert.Equal(HandleKind.AssemblyReference, t.ResolutionScope.Kind);
            scopes.Add(md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)t.ResolutionScope).Name));
        }
        Assert.Equal(["Kiln.Items"], scopes);
        Assert.DoesNotContain(md.AssemblyReferences, a => md.GetString(md.GetAssemblyReference(a).Name) == "Kiln.Api");
    }

    [Fact]
    public void Kiln_Items_forwards_ItemId_to_Kiln_Api()
    {
        var items = typeof(SlotId).Assembly;
        Assert.Equal("Kiln.Items", items.GetName().Name);
        Assert.Equal("Kiln.Api", typeof(ItemId).Assembly.GetName().Name);
        Assert.Contains(typeof(ItemId), items.GetForwardedTypes());
        Assert.DoesNotContain(items.GetTypes(), t => t.FullName == "Kiln.ItemId");
    }

    [Fact]
    public void An_assembly_built_against_the_old_layout_sees_Kiln_Api_ItemId()
    {
        var dll = FindConsumer();
        var alc = new ConsumerAlc();
        try
        {
            var asm = alc.LoadFromAssemblyPath(dll);
            var user = asm.GetType("ItemIdConsumer.OldLayoutUser", throwOnError: true)!;
            object? Call(string name, params object?[] args) => user.GetMethod(name)!.Invoke(null, args);

            Assert.Same(typeof(ItemId), Call("TypeSeen"));
            Assert.Equal(5u, Call("FieldValue"));
            Assert.Equal(new ItemId(5), user.GetField("Field")!.GetValue(null));
            Assert.Equal(new ItemId(9), Call("Make", 9u));
            Assert.Equal(77u, Call("Echo", new ItemId(77)));
            var guid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            Assert.Equal(ItemId.FromGuidBytes(guid).Value, Call("FromGuid", guid));
            Assert.NotEqual(0u, ItemId.FromGuidBytes(guid).Value);
        }
        finally
        {
            alc.Unload();
        }

        // The old Kiln.Items stand-in was never loaded and never copied next to the tests.
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "Kiln.Items"))
            Assert.Contains(typeof(ItemId), a.GetForwardedTypes());
        using var pe = new PEReader(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Kiln.Items.dll")));
        var md = pe.GetMetadataReader();
        Assert.DoesNotContain(md.TypeDefinitions, h => md.GetString(md.GetTypeDefinition(h).Name) == "ItemId");
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, Consumer + ".dll")));
    }
}
