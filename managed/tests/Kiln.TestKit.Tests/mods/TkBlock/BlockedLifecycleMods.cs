// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using Kiln;

namespace TkBlock;

public sealed class TkUnloadBlockMod : Mod
{
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    public override void OnUnload() => Thread.Sleep(Timeout.Infinite);
}

public sealed class TkStoppingBlockMod : Mod
{
    public override void OnLoad() => Events.On<ServerStoppingEvent>(Stopping);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private void Stopping(ServerStoppingEvent e) => Thread.Sleep(Timeout.Infinite);
}

public sealed class TkTickBlockMod : Mod
{
    public override void OnLoad() => Context.Tick.Pre += Block;

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private void Block(in TickInfo tick) => Thread.Sleep(Timeout.Infinite);
}
