// SPDX-License-Identifier: MIT
// Mod.OnBoot's phase on the host side (chapter 08; kiln.boot, native/core/abi/services/kiln_boot.h). Runs inside Kiln.Host's Init, on the
// boot thread, before the game's entry point: in a boot profile kiln.boot is collecting, and Kiln.Host is the owner that commits the one
// generation. Without the commit the core holds the entry point and ends the start (exit code 0x4B01).
//
// This first step commits a generation of the mods' files only (no OnBoot author runs yet; the boot graph, the author instances and the
// IR envelope come with the managed slices of package 08): the six collectors prepare from their own files, as a boot mod the resolver
// refuses would leave it.
using System.Runtime.InteropServices;

namespace Kiln.Host;

[StructLayout(LayoutKind.Sequential)]
internal struct KilnBootCommitResultV1
{
    public uint Size, Status, OwnerCount, IssueCount;
    public ulong Generation;
}

/// <summary>Mirror of <c>KilnBootApiV1</c> (kiln_boot.h): field order is the ABI.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct KilnBootApi
{
    public uint Size, Version;
    public delegate* unmanaged[Cdecl]<KilnBootCommitResultV1*, int> Status;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, uint, ulong*, int> BeginOwner;
    public delegate* unmanaged[Cdecl]<ulong, ulong, KilnStr, KilnStr, KilnStr, void*, int> Stage;
    public delegate* unmanaged[Cdecl]<ulong, ulong, uint, int> FinishOwner;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, byte*, nuint, nuint*, int> ReadResource;
    public delegate* unmanaged[Cdecl]<ulong, ulong, KilnBootCommitResultV1*, int> Commit;
    public delegate* unmanaged[Cdecl]<KilnStr, void*, int> OwnerState;
    public delegate* unmanaged[Cdecl]<ulong, void*, uint, uint, uint*, int> Issues;
    public delegate* unmanaged[Cdecl]<ulong, KilnStr, void*, void*, int> Collector;
    public delegate* unmanaged[Cdecl]<KilnStr, ulong*, void*> ActivePlan;
}

internal static unsafe class BootPhase
{
    internal const uint StatusCollecting = 0, StatusPublished = 2;
    private static readonly string[] StatusNames = ["collecting", "preparing", "published", "aborted", "activation-fault"];

    /// <summary>In a boot profile: commits the generation. Never throws (a failure leaves kiln.boot collecting, and the core stops the
    /// start with its own diagnostics).</summary>
    public static void Run(Func<string, uint, nint> service, ulong host, Action<LogLevel, string> log)
    {
        try
        {
            var t = (KilnBootApi*)service("kiln.boot", 1);
            if (t == null || t->Size < (uint)sizeof(KilnBootApi)) return;
            var st = new KilnBootCommitResultV1 { Size = (uint)sizeof(KilnBootCommitResultV1) };
            if (t->Status(&st) != KilnAbi.Ok) return;  // not a boot profile: nothing to do
            if (st.Status != StatusCollecting)
            {
                log(LogLevel.Warn, $"boot: the generation is {Name(st.Status)} already; Kiln.Host does not commit it");
                return;
            }
            var r = new KilnBootCommitResultV1 { Size = (uint)sizeof(KilnBootCommitResultV1) };
            var rc = t->Commit(host, st.Generation, &r);
            if (rc != KilnAbi.Ok)
            {
                log(LogLevel.Error, $"boot: kiln.boot refused the commit (rc {rc}); the start stops before the game's entry point");
                return;
            }
            log(r.Status == StatusPublished ? LogLevel.Info : LogLevel.Error,
                $"boot: generation {r.Generation} {Name(r.Status)} ({r.OwnerCount} participant(s), {r.IssueCount} issue(s)); " +
                "files only: OnBoot authors are not run by this build");
        }
        catch (Exception e)
        {
            log(LogLevel.Error, $"boot: the commit failed: {e.Message}");
        }
    }

    private static string Name(uint status) => status < StatusNames.Length ? StatusNames[status] : status.ToString();
}
