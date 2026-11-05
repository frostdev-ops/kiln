// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Api.Tests;

/// <summary>Code loaded again in a test load context, standing in for a mod.</summary>
public static class OwnerWork
{
    public static Action Closure()
    {
        var n = 0;
        return () => n++;
    }

    /// <summary>What an await hands to its awaiter: the runtime box, not a delegate of the mod.</summary>
    public static Action Continuation()
    {
        var capture = new Capture();
        _ = Await(capture);
        return capture.Next!;
    }

    private static async Task Await(Capture c) => await c;

    private sealed class Capture : ICriticalNotifyCompletion
    {
        public Action? Next;
        public Capture GetAwaiter() => this;
        public bool IsCompleted => false;
        public void GetResult() { }
        public void OnCompleted(Action a) => Next = a;
        public void UnsafeOnCompleted(Action a) => Next = a;
    }
}

public sealed class ModOwnerTests
{
    private static Action Load(AssemblyLoadContext alc, string method) =>
        (Action)alc.LoadFromAssemblyPath(typeof(OwnerWork).Assembly.Location)
            .GetType(typeof(OwnerWork).FullName!)!.GetMethod(method)!.Invoke(null, null)!;

    private static ModContext NewContext() =>
        HostAccess.CreateContext(new ModInfo("owner-test", "Owner test", "1.0.0", "."), 7, ".");

    [Theory]
    [InlineData(nameof(OwnerWork.Closure))]
    [InlineData(nameof(OwnerWork.Continuation))]
    public void Work_of_a_mod_is_attributed_to_that_mod(string method)
    {
        var alc = new AssemblyLoadContext("owner-test", isCollectible: true);
        var mod = NewContext();
        ModContext.Remember(alc.LoadFromAssemblyPath(typeof(OwnerWork).Assembly.Location), mod);
        Assert.Same(mod, ModContext.OwnerOf(Load(alc, method)));
    }

    [Theory]
    [InlineData(nameof(OwnerWork.Closure))]
    [InlineData(nameof(OwnerWork.Continuation))]
    public void Work_of_an_unknown_load_context_has_no_owner(string method) =>
        Assert.Null(ModContext.OwnerOf(Load(new AssemblyLoadContext("owner-none", isCollectible: true), method)));

    [Fact]
    public void Work_outside_a_mod_has_no_owner() => Assert.Null(ModContext.OwnerOf(OwnerWork.Closure()));
}
