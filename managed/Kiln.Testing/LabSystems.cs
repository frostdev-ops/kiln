// SPDX-License-Identifier: MIT
// The lab's parallel switch (kiln.world v4 lab_parallel, native/core/abi/services/kiln_world.h): forces a dedicated
// server's parallel program path without a player, so a lab test can prove a hook on the game's programs on the game's
// worker threads. Field order and types of the mirrors must match the C header.
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Kiln.Testing;

/// <summary>Lab only: the server world's program threads on demand. A dedicated server runs the game's entity programs
/// on its own thread while no player is in the world, and as tasks on the game's workers once one is; a lab has no
/// player, so a test that has to run where a player's server runs (a hook inside a game program) turns the parallel
/// path on here for a bounded window. Works only when <c>kiln/control/systems.parallel</c> existed when the server
/// started; never on a player's server.</summary>
[Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class LabSystems
{
    /// <summary>The longest window <see cref="ForceParallel"/> takes, in steps (about two minutes).</summary>
    public const int MaxSteps = 3000;

    /// <summary>True when the switch is on in this process (the control file was there at start, a dedicated server,
    /// and the players mask resolved on this build).</summary>
    [AnyThread]
    public static bool Enabled => Status() is { } s && s.ParallelSwitch != 0;

    /// <summary>The server world's players mask at its last step (bit <c>(id - 1) mod 16</c> per player entity; not
    /// zero = the parallel path), or null when it is not known.</summary>
    [AnyThread]
    public static ushort? PlayersMask => Status() is { } s && s.PlayersMask != 0xFFFF ? s.PlayersMask : null;

    /// <summary>True while the switch holds the mask's bit 15.</summary>
    [AnyThread]
    public static bool Forced => Status() is { } s && s.ParallelForced != 0;

    /// <summary>Steps left until the switch lets go by itself.</summary>
    [AnyThread]
    public static int StepsLeft => Status() is { } s ? (int)s.ParallelStepsLeft : 0;

    /// <summary>Forces the parallel path from the next step for <paramref name="steps"/> steps (as if player 16 were
    /// in the world); it then lets go by itself, and at <see cref="StopParallel"/> and world stop. False when the
    /// switch is off in this process. Calling it again while it holds sets a new window.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="steps"/> is not 1..<see cref="MaxSteps"/>.</exception>
    [AnyThread]
    public static bool ForceParallel(int steps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(steps, MaxSteps);
        var t = Table();
        return t != null && t->LabParallel(1, (uint)steps) == 0;
    }

    /// <summary>Lets go of the mask in the next step. False when the switch is off in this process.</summary>
    [AnyThread]
    public static bool StopParallel()
    {
        var t = Table();
        return t != null && t->LabParallel(0, 0) == 0;
    }

    private static ProgramsStatus? Status()
    {
        var t = Table();
        if (t == null) return null;
        var s = new ProgramsStatus { Size = (uint)sizeof(ProgramsStatus) };
        return t->Status(&s) == 0 ? s : null;
    }

    private static WorldV4* Table() => Services.Get<WorldV4>("kiln.world", 4);

    // kiln_world.h KilnWorldApi up to lab_parallel: the v3 table (header + 34 entries) and v4's first four.
    [StructLayout(LayoutKind.Sequential)]
    internal struct WorldV4
    {
        public ServiceHeader Header;
        public fixed long V3[34];
        public nint GameProgramFind, GameProgramInfo;
        public delegate* unmanaged[Cdecl]<ProgramsStatus*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, uint, int> LabParallel;
    }

    // kiln_world.h KilnGameProgramsStatus.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProgramsStatus
    {
        public uint Size;
        public fixed byte Directory[3];
        public byte Threads;
        public fixed uint Programs[3];
        public fixed uint GamePrograms[3];
        public fixed uint NoUpdate[3];
        public ushort PlayersMask;
        public byte ParallelForced, ParallelSwitch;
        public uint ParallelStepsLeft, WrapsActive, WrapsPending;
        public fixed byte Detail[160];
    }
}
