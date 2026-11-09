// SPDX-License-Identifier: MIT
using Kiln;
namespace WorldProgramsSample;
public sealed class WorldProgramsMod : Mod
{
    private IDisposable? _program;
    private int _sent, _seen;
    private bool _reported;
    public override void OnLoad()
    {
        _program=EcsPrograms.Bind("echo",context=> {
            foreach (var bytes in context.ReadEvents("TestEvent1"))
                if (BitConverter.ToInt32(bytes)==42) Interlocked.Increment(ref _seen);
            if (_sent==0 && context.EmitEvent("TestEvent1",BitConverter.GetBytes(42))) _sent=1;
        });
        Context.Tick.Post+=Report;
    }
    private void Report(in TickInfo tick)
    {
        if (!_reported && Volatile.Read(ref _seen)>0) { _reported=true;Log.Info("world program event echoed"); }
    }
    public override void OnUnload() => _program?.Dispose();
}
