// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;
using Kiln;
namespace WorldProgramsTest;
public sealed class WorldProgramsTestMod : Mod
{
    private IDisposable? _binding, _hook;
    private int _passes, _failures, _updates, _sent, _first, _second, _target, _written, _wanted;
    private long _before, _after;
    private nint _probeLibrary;
    [StructLayout(LayoutKind.Sequential, Pack=1)]
    private unsafe struct HealthData { public uint Root; public ushort Offset; public byte Flags, Count; public fixed int Values[8]; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Counter();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResetCounter();
    private void Check(bool ok, string message) { if (ok) _passes++; else _failures++; Log.Info($"world-programs-test: {(ok ? "PASS" : "FAIL")} {message}"); }
    public override void OnLoad()
    {
        try {
            _binding = EcsPrograms.Bind("pulse", Program);
            _hook = GameSystems.Hook("trigger_knowledge_on_kill",()=>Interlocked.Increment(ref _before),()=>Interlocked.Increment(ref _after));
            Check(true,"boot declaration and persistent system hook bound from normal server OnLoad");
        }
        catch (Exception error) { Check(false,$"bind: {error}"); }
        _ = Run();
    }
    public override void OnUnload()
    {
        // Deliberately leave live program/hook registrations for ModContext.Retiring to drain (reload stress).
        if (_probeLibrary != 0) { NativeLibrary.Free(_probeLibrary); _probeLibrary=0; }
    }
    private unsafe void Program(EcsProgramContext context)
    {
        Interlocked.Increment(ref _updates);
        foreach (var data in context.ReadEvents("TestEvent1")) if (BitConverter.ToInt32(data)==0x7141) Interlocked.Increment(ref _first);
        foreach (var data in context.ReadEvents("TestEvent2")) if (BitConverter.ToInt32(data)==0x7142) Interlocked.Increment(ref _second);
        if (_sent < 3)
        {
            var first = context.EmitEvent("TestEvent1",BitConverter.GetBytes(0x7141));
            var second = context.EmitEvent("TestEvent2",BitConverter.GetBytes(0x7142));
            if (first && second) _sent++;
        }
        var target = new EntityId((uint)Volatile.Read(ref _target));
        if (!target.IsNone && Volatile.Read(ref _written)==0 && context.TryReadOther<HealthData>(target,"Health",out var health) &&
            health.Offset==8 && health.Count==8 && (health.Flags&1)!=0 && health.Values[0]>2)
        {
            health.Values[0]--;
            if (context.TryWriteOther(target,"Health",health)) {
                Volatile.Write(ref _wanted,health.Values[0]);
                Volatile.Write(ref _written,1);
            }
        }
    }
    private async Task Wait(int steps) { for (var i=0;i<steps;i++) await Context.Scheduler.NextTick(); }
    private async Task Run()
    {
        EntityId creature = default;
        try
        {
            for (var n=0;n<1500 && !Spawn.WorldRunning;n++) await Context.Scheduler.NextTick();
            await Wait(220); // existing types-test finishes its own profile session first
            Check(EcsPrograms.Available && EcsPrograms.SupportsOtherWrites,"program capabilities available");
            Check(_updates>0 && _sent==3 && _first>=3 && _second>=3,$"managed two-event round trip {_sent} sent, {_first}+{_second} read; {_updates} updates");
            var anchor=World.Find("Health").Select(World.Position).FirstOrDefault(p=>p is not null);
            if (anchor is not { } position) throw new InvalidOperationException("no spawn anchor");
            creature=await Spawn.Entity("Enemy_Vukah_Melee",position with { X=position.X+15 },options:new SpawnOptions {
                Flags=SpawnFlags.NoTrack|SpawnFlags.Transient,
                Components=[SpawnComponent.Of("ScaleComponent",1.6f)] });
            Check(World.TryRead<float>(creature,"ScaleComponent",out var scale) && scale==1.6f,"spawn extra component visible before first sim step");
            Volatile.Write(ref _target,(int)creature.Value);
            await Wait(3);
            var data=World.ReadBytes(creature,"Health");
            var offset=data is {Length:40} ? BitConverter.ToUInt16(data,4):0;
            var observed=data is {Length:40} && offset<=36 ? BitConverter.ToInt32(data,offset):-1;
            Check(_written==1 && observed==_wanted,$"managed tracked other Health write {_wanted}, read back {observed}");
            var system=GameSystems.Find("trigger_knowledge_on_kill") ?? throw new InvalidOperationException("hook system missing");
            if (_hook is null) throw new InvalidOperationException("persistent system hook missing");
            var duplicate=false;
            try { using var unused=GameSystems.Hook(system.Name,()=>{}); } catch (NotSupportedException) { duplicate=true; }
            Check(duplicate,"second hook on same update refused");
            // EMF's hook uses another system. Reset both counters at the exact same tick-thread boundary as the profiler.
            var server=Path.GetFullPath(Path.Combine(Context.Info.Directory,"..","..",".."));
            _probeLibrary=NativeLibrary.Load(Path.Combine(server,"emf","mods","emf_probe","emf_probe.dll"));
            var reset=Marshal.GetDelegateForFunctionPointer<ResetCounter>(NativeLibrary.GetExport(_probeLibrary,"wp14_hook_reset"));
            var emfBefore=Marshal.GetDelegateForFunctionPointer<Counter>(NativeLibrary.GetExport(_probeLibrary,"wp14_hook_before"));
            var emfAfter=Marshal.GetDelegateForFunctionPointer<Counter>(NativeLibrary.GetExport(_probeLibrary,"wp14_hook_after"));
            reset(); Interlocked.Exchange(ref _before,0); Interlocked.Exchange(ref _after,0); GameSystems.Profile(true);
            await Wait(100);
            var calls=GameSystems.Find(system.Name)!.Calls;
            var emfCalls=GameSystems.Find("combat_experience_source")!.Calls;
            Check(calls>0 && (ulong)_before==calls && (ulong)_after==calls,$"C# hook before/after {_before}/{_after} = profiled {calls}");
            Check(emfCalls>0 && emfBefore()==emfCalls && emfAfter()==emfCalls,$"EMF hook_program before/after {emfBefore()}/{emfAfter()} = profiled {emfCalls}");
            GameSystems.Profile(false);
            _hook.Dispose(); _hook=null;
            var stopped=Interlocked.Read(ref _before); await Wait(3);
            Check(stopped==Interlocked.Read(ref _before),"disposed system hook has no further callbacks");
            _binding!.Dispose(); _binding=null;
            var updates=Volatile.Read(ref _updates); await Wait(3);
            Check(updates==Volatile.Read(ref _updates),"disposed program has no further callbacks");
            _binding=EcsPrograms.Bind("pulse",Program); await Wait(3);
            Check(Volatile.Read(ref _updates)>updates,"inert reserved descriptor can bind again without scheduler changes");
            _hook=GameSystems.Hook(system.Name,()=>Interlocked.Increment(ref _before),()=>Interlocked.Increment(ref _after));
        }
        catch (Exception error) { Check(false,$"unexpected exception: {error}"); }
        finally {
            Volatile.Write(ref _target,0);
            if (!creature.IsNone) Spawn.Destroy(creature);
        }
        Log.Info($"world-programs-test: done, {_passes} PASS, {_failures} FAIL");
    }
}
