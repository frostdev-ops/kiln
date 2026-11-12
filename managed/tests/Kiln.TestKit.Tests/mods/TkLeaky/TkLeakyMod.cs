// SPDX-License-Identifier: MIT
using Kiln;

namespace TkLeaky;

/// <summary>Leaks on purpose: a handler on a process-wide .NET event that it never removes keeps its load context alive.</summary>
public sealed class TkLeakyMod : Mod
{
    public override void OnLoad() => AppDomain.CurrentDomain.ProcessExit += OnExit;

    private void OnExit(object? sender, EventArgs e) => Log.Info("exit");
}
