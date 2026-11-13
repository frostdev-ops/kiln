// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;
using Kiln.TestKit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Kiln.PlayerAnimTests;

public sealed unsafe class DefineTests
{
    private static int graph;
    private static int calls;
    [StructLayout(LayoutKind.Sequential)]
    private struct Str { public byte* Ptr; public nuint Len; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int State() => graph;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Define(Str text, byte* output, nuint capacity, nuint* length) { calls++; *length = 0; return 3; }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void DefineUsesUnavailableExceptionOnlyWhenGraphIsOff(int state)
    {
        // Private table cache is the only seam; use the public Define entry point from a real harness mod context.
        var field = typeof(PlayerAnimStates).GetField("_table", BindingFlags.Static | BindingFlags.NonPublic)!;
        var table = (byte*)NativeMemory.AllocZeroed(104);
        *(uint*)table = 104; *(uint*)(table + 4) = 1;
        ((nint*)(table + 8))[0] = (nint)(delegate* unmanaged[Cdecl]<int>)&State;
        ((nint*)(table + 8))[7] = (nint)(delegate* unmanaged[Cdecl]<Str, byte*, nuint, nuint*, int>)&Define;
        graph = state; calls = 0;
        field.SetValue(null, Pointer.Box(table, field.FieldType));
        try
        {
            using var kiln = new KilnHarness();
            var mod = kiln.Load<ProbeMod>(new ModManifest("playeranim-probe"));
            kiln.Step();
            var current = typeof(ModContext).GetField("CurrentMod", BindingFlags.Static | BindingFlags.NonPublic)!;
            var checkedCall = false;
            kiln.Run(() =>
            {
                current.SetValue(null, mod.Context);
                try
                {
                checkedCall = true;
                if (state == 0)
                {
                    var error = Assert.Throws<KilnUnavailableException>(() => PlayerAnimStates.Define(new("slash", "slash", "Parry_FullBody")));
                    Assert.Contains("anim.player_states", error.Message);
                    Assert.Equal(0, calls);
                }
                else
                {
                    var result = PlayerAnimStates.Define(new("slash", "slash", "Parry_FullBody"));
                    Assert.Equal(PlayerAnimStateStatus.Refused, result.Status);
                    Assert.Contains("definitions from code", result.Reason);
                    Assert.Equal(1, calls);
                }
                }
                finally { current.SetValue(null, null); }
            });
            Assert.True(checkedCall);
        }
        finally { field.SetValue(null, Pointer.Box(null, field.FieldType)); NativeMemory.Free(table); }
    }
}

public sealed class ProbeMod : Mod
{
    public override void OnLoad() { }
}

/// <summary>[RequiresCapability] says that a member throws when its row is off: of PlayerAnimStates only Define does; the
/// lookups answer Off, the zero hash, null or empty without the service (dev.26's behaviour), so they carry no mark.</summary>
public sealed unsafe class CapabilityMarkTests
{
    [Fact]
    public void Only_Define_is_marked_and_the_lookups_answer_without_the_service()
    {
        var field = typeof(PlayerAnimStates).GetField("_table", BindingFlags.Static | BindingFlags.NonPublic)!;
        var before = field.GetValue(null);
        field.SetValue(null, Pointer.Box(null, field.FieldType));  // no kiln.playeranim: no backend of this process has one
        try
        {
            Assert.Equal(PlayerAnimGraphState.Off, PlayerAnimStates.Graph);
            Assert.Equal("0000000000000000", PlayerAnimStates.GraphHash);
            Assert.Empty(PlayerAnimStates.FindGameStates("Idle"));
            Assert.Null(PlayerAnimStates.Find("mod", "slash"));
            Assert.Empty(PlayerAnimStates.All);
        }
        finally
        {
            field.SetValue(null, before);
        }
        var marked = typeof(PlayerAnimStates).GetMembers(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<RequiresCapabilityAttribute>() is not null).Select(m => m.Name).ToArray();
        Assert.Equal(["Define"], marked);
    }
}
