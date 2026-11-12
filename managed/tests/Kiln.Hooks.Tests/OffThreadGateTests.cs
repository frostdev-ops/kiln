// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Xunit;

namespace Kiln.Hooks.Tests;

[Collection("kiln.hooks state")]
public sealed class OffThreadGateTests
{
    [Fact]
    public void ARememberedAdvancedAssemblyStillNeedsTheCallingThreadContext()
    {
        var alc = new AssemblyLoadContext("advanced-background-probe", isCollectible: true);
        var mod = TestMods.Make("advanced-background-probe", advanced: true);
        try
        {
            var assembly = alc.LoadFromAssemblyPath(typeof(OffThreadGateProbe).Assembly.Location);
            typeof(ModContext).GetMethod("Remember", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [assembly, mod]);
            var probe = assembly.GetType(typeof(OffThreadGateProbe).FullName!)!;
            var run = probe.GetMethod(nameof(OffThreadGateProbe.Run))!.CreateDelegate<Func<string[]>>();
            // Kiln.Api can recover the owner of a supplied delegate, but exposes no assembly/ALC lookup to the gates.
            Assert.Same(mod, typeof(ModContext).GetMethod("OwnerOf", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [run]));
            string[]? errors = null;
            var worker = new Thread(() => errors = run());
            TestMods.As(mod, () => { worker.Start(); Assert.True(worker.Join(5000)); });
            Assert.Equal(5, errors!.Length);
            Assert.All(errors, error => Assert.EndsWith("must be called from a mod (on the tick thread or in OnLoad)", error));

            var native = probe.GetMethod(nameof(OffThreadGateProbe.RunNative))!.CreateDelegate<Func<uint>>();
            uint caught = 0;
            var job = new Thread(() => caught = native());
            job.Start();
            Assert.True(job.Join(5000));
            Assert.Equal(1u, caught); // the raw callback caught the gate exception before returning to native
        }
        finally
        {
            TestMods.Retire(mod);
            alc.Unload();
        }
    }
}

// Loaded in a collectible mod context by the test, sharing Kiln.Api and Kiln.Hooks with the host.
public static unsafe class OffThreadGateProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string[] Run() =>
    [
        Refusal(() => Keep.Block("off-thread-probe", 4)),
        Refusal(() => { Keep.Span<int>("off-thread-probe", 1); }),
        Refusal(() => OriginalCode.Read(0, 1)),
        Refusal(() => OriginalCode.FindUnique("48 8B")),
        Refusal(() => OriginalCode.TryFindUnique("48 8B", out _, out _)),
    ];

    static string Refusal(Action call)
    {
        try { call(); return "not refused"; }
        catch (InvalidOperationException e) { return e.Message; }
    }

    public static uint RunNative() => ((delegate* unmanaged[Cdecl]<uint, uint, uint, nint, uint>)&Job)(0, 0, 0, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static uint Job(uint entity, uint id, uint arg, nint user)
    {
        try { Keep.Block("off-thread-probe", 4); return 0; }
        catch (InvalidOperationException) { return 1; }
        catch { return 2; }
    }
}
