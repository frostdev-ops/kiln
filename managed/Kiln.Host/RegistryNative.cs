// SPDX-License-Identifier: MIT
// Private Host mirrors of kiln_registry.h and kiln_registry_live.h. Public mods receive RegistryContext, never these pointers.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Kiln.Host;

[StructLayout(LayoutKind.Sequential)]
internal struct RegistryStageStatus
{
    internal uint Size, Phase;
    internal ulong Generation;
    internal uint Edits, Refused, Conflicts, World, Flags, Reserved;
}
[StructLayout(LayoutKind.Sequential)]
internal struct RegistryStageInfo { internal uint Size, Stage; internal KilnStr Mod; }
[StructLayout(LayoutKind.Sequential)]
internal struct RegistryReadyInfo { internal uint Size, Phase; internal ulong Generation; internal uint World, Reserved; }
[StructLayout(LayoutKind.Sequential)]
internal struct RegistrySubmitInfo { internal uint Size, Accepted, Refused, Reserved; }
[StructLayout(LayoutKind.Sequential)]
internal struct RegistryLiveStatus
{
    internal uint Size, Kind;
    internal ulong World, Generation;
    internal uint Drafts, Active;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RegistryLiveReply
{
    internal uint Size, Outcome, Accepted, Refused, Skipped, Reserved;
    internal ulong World, Generation;
    internal fixed byte Reason[192];
    internal RegistryCompletion Copy(ulong handle)
    {
        fixed (byte* p = Reason)
        {
            var length = 0; while (length < 192 && p[length] != 0) ++length;
            return new(Outcome, Accepted, Refused, Skipped, World, Generation, handle, Encoding.UTF8.GetString(p, length));
        }
    }
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RegistryStageTable
{
    internal uint Size, Version;
    internal delegate* unmanaged[Cdecl]<RegistryStageStatus*, int> Status;
    internal nint Schema, FieldInfo;
    internal delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<RegistryStageInfo*, void*, void>, void*, ulong*, int> Subscribe;
    internal delegate* unmanaged[Cdecl]<ulong, KilnStr, RegistrySubmitInfo*, byte*, nuint, nuint*, int> Submit;
    internal delegate* unmanaged[Cdecl]<uint, KilnStr, KilnStr, KilnStr, byte*, nuint, nuint*, int> Read;
    internal delegate* unmanaged[Cdecl]<uint, KilnStr, KilnStr, byte*, nuint, nuint*, int> Select;
    internal delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Report;
    internal delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<RegistryReadyInfo*, void*, void>, void*, ulong*, int> Ready;
    internal delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
}
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RegistryLiveTable
{
    internal uint Size, Version;
    internal delegate* unmanaged[Cdecl]<ulong, uint, ulong*, int> Begin;
    internal delegate* unmanaged[Cdecl]<ulong, ulong, KilnStr, RegistryLiveReply*, byte*, nuint, nuint*, int> Submit;
    internal delegate* unmanaged[Cdecl]<ulong, ulong, RegistryLiveReply*, int> Commit, Revert;
    internal delegate* unmanaged[Cdecl]<ulong, KilnStr, KilnStr, KilnStr, byte*, nuint, nuint*, int> Read;
    internal delegate* unmanaged[Cdecl]<RegistryLiveStatus*, int> Status;
}

internal sealed unsafe class RegistryNative(IHostServices host)
{
    // review4 nit: one lookup per table, not dozens per tick. kiln-content's tables live as long as the process; a missing
    // table is asked for again next time (it may load later).
    private nint _stage, _live, _internal;
    private RegistryStageTable* Stage => (RegistryStageTable*)(_stage != 0 ? _stage : _stage = host.Service("kiln.registry", 1));
    private RegistryLiveTable* Live => (RegistryLiveTable*)(_live != 0 ? _live : _live = host.Service("kiln.registry.live", 1));
    private KilnRegistryHostApi* Internal => (KilnRegistryHostApi*)(_internal != 0 ? _internal
        : _internal = host.Service(KilnRegistryHostApi.Name, KilnRegistryHostApi.Version2));
    internal bool Available => Stage != null && Stage->Size >= 88 && Stage->Version >= 1 && Stage->Status != null &&
        Stage->Subscribe != null && Stage->Submit != null && Stage->Read != null && Stage->Select != null &&
        Stage->Ready != null && Stage->Unsubscribe != null;
    internal bool LiveAvailable => Available && Live != null && Live->Size >= 56 && Live->Version >= 1 && Live->Begin != null && Live->Submit != null && Live->Commit != null && Live->Revert != null && Live->Read != null && Live->Status != null;
    private sealed record Callback(Action<uint, ulong> Invoke, Action<Exception> Fault);
    private static readonly ConcurrentDictionary<long, Callback> Callbacks = new();
    private static long _next;
    private unsafe delegate int TextCall(byte* buffer, nuint cap, nuint* length);
    private static void Check(int rc, string service)
    {
        if (rc == 0) return;
        if (rc == KilnAbi.ErrWrongThread) throw new RegistryContextException(RegistryContextError.WrongThread, "registry access needs the active world's thread");
        if (rc == KilnAbi.ErrRevoked) throw new RegistryContextException(RegistryContextError.Unloaded, "the registry owner retired");
        if (rc == KilnAbi.ErrInvalidArgument) throw new ArgumentException("the registry refused an invalid request");
        if (rc == KilnAbi.ErrNotFound) throw new KeyNotFoundException("the registry entry or change no longer exists");
        throw new KilnUnavailableException(service, $"registry native call returned {rc}");
    }
    private static string Text(TextCall read)
    {
        nuint required = 0;
        var rc = read(null, 0, &required);
        for (var retry = 0; retry < 8; ++retry)
        {
            if (rc != KilnAbi.ErrBufferTooSmall && rc != 0) Check(rc, "kiln.registry");
            if (required > 16 * 1024 * 1024) throw new InvalidDataException("registry response exceeds 16 MiB");
            var buffer = new byte[checked((int)required + 1)];
            fixed (byte* p = buffer) rc = read(p, (nuint)buffer.Length, &required);
            if (rc == KilnAbi.ErrBufferTooSmall) continue;
            Check(rc, "kiln.registry");
            return Encoding.UTF8.GetString(buffer, 0, checked((int)required));
        }
        throw new KilnUnavailableException("kiln.registry", "the registry response kept changing");
    }
    // The small schema snapshot includes bootSync too. Do not fetch the potentially multi-MiB writer report at a join.
    internal string? Fingerprint()
    {
        if (!Available || Stage->Schema == 0) return null;
        var schema = (delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int>)Stage->Schema;
        return Text((buffer, cap, length) => schema(buffer, cap, length));
    }
    internal RegistryStageStatus StageStatus()
    {
        if (!Available) throw new KilnUnavailableException("kiln.registry");
        var status = new RegistryStageStatus { Size = 40 }; Check(Stage->Status(&status), "kiln.registry"); return status;
    }
    internal RegistryLiveStatus Status()
    {
        if (!LiveAvailable) throw new KilnUnavailableException("kiln.registry.live");
        var status = new RegistryLiveStatus { Size = 32 }; Check(Live->Status(&status), "kiln.registry.live"); return status;
    }
    internal string Read(ulong owner, bool stage, string registry, string id, string field) =>
        NativeCalls.WithUtf8(registry, r => NativeCalls.WithUtf8(id, i => NativeCalls.WithUtf8(field, f =>
            Text((buffer, cap, length) => stage ? Stage->Read(1, r, i, f, buffer, cap, length) : Live->Read(owner, r, i, f, buffer, cap, length)))));
    // review4 NS5: an automatic runtime pass reads the live value without the reading mod's own registry layers
    // (kiln.internal.registry v2 read_pass); without it (an older kiln-content.dll) the plain live read.
    internal string ReadPass(ulong owner, string registry, string id, string field)
    {
        var t = Internal;
        if (t == null || t->Version < KilnRegistryHostApi.Version2 || t->Size < 32 || t->ReadPass == null)
            return Read(owner, false, registry, id, field);
        return NativeCalls.WithUtf8(registry, r => NativeCalls.WithUtf8(id, i => NativeCalls.WithUtf8(field, f =>
            Text((buffer, cap, length) => t->ReadPass(owner, r, i, f, buffer, cap, length)))));
    }
    // ---- dev.32 LOOT-2: Registries.Reauthor off the world tick (kiln.internal.registry v3) ----
    /// <summary>A re-authored pass can run on a worker thread: kiln-content.dll publishes v3's preparation and landing.</summary>
    internal bool OffTickAvailable
    {
        get
        {
            var t = Internal;
            return LiveAvailable && t != null && t->Version >= KilnRegistryHostApi.Version3 && t->Size >= (uint)sizeof(KilnRegistryHostApi) &&
                   t->Epoch != null && t->ReadPassIn != null && t->Prepare != null && t->Install != null && t->Discard != null && t->BuildReport != null;
        }
    }
    /// <summary>The registry's epoch: what a pass reads moves it. Any thread.</summary>
    internal ulong Epoch() => Internal->Epoch();
    /// <summary>read_pass for a pass running off the world thread, in that world only. Any thread.</summary>
    internal string ReadPassIn(ulong owner, ulong world, string registry, string id, string field)
    {
        var t = Internal;
        return NativeCalls.WithUtf8(registry, r => NativeCalls.WithUtf8(id, i => NativeCalls.WithUtf8(field, f =>
            Text((buffer, cap, length) => t->ReadPassIn(owner, world, r, i, f, buffer, cap, length)))));
    }
    /// <summary>Prepares a turn off the world thread: the calls (owner, IR) in commit order, the replaced retained changes out.
    /// (rc, ticket, stale): stale 1 the pass's reads are stale, 2 only the preparation is.</summary>
    internal (int Rc, ulong Ticket, uint Stale) Prepare(IReadOnlyList<(ulong Owner, string Ir)> calls, IReadOnlyList<(ulong Owner, ulong Handle)> replaced, ulong epoch)
    {
        var t = Internal;
        var texts = calls.Select(c => Encoding.UTF8.GetBytes(c.Ir)).ToArray();
        var pins = new System.Runtime.InteropServices.GCHandle[texts.Length];
        try
        {
            var items = new KilnRegistryPassCall[calls.Count];
            for (var i = 0; i < texts.Length; ++i)
            {
                pins[i] = System.Runtime.InteropServices.GCHandle.Alloc(texts[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                items[i] = new KilnRegistryPassCall { Owner = calls[i].Owner, Point = Tag(RegistryEntryPoint.WorldLoad),
                    Ir = new KilnStr { Ptr = (byte*)pins[i].AddrOfPinnedObject(), Len = (nuint)texts[i].Length } };
            }
            var turn = replaced.Select(r => new KilnRegistryTurnItem { Owner = r.Owner, Handle = r.Handle }).ToArray();
            ulong ticket = 0; uint stale = 0; int rc;
            fixed (KilnRegistryPassCall* c = items) fixed (KilnRegistryTurnItem* r = turn)
                rc = t->Prepare(c, (nuint)items.Length, r, (nuint)turn.Length, epoch, &ticket, &stale);
            return (rc, ticket, stale);
        }
        finally
        {
            foreach (var pin in pins) if (pin.IsAllocated) pin.Free();
        }
    }
    /// <summary>Lands a prepared turn on the world thread: (rc, stale, one completion per call then per replaced change; a
    /// call's carries its retained handle).</summary>
    internal (int Rc, uint Stale, RegistryCompletion[] Results) Install(ulong ticket, bool anyway, int calls, int replaced)
    {
        var t = Internal;
        var results = new RegistryLiveReply[calls + replaced];
        for (var i = 0; i < results.Length; ++i) results[i].Size = 232;
        var handles = new ulong[calls];
        uint stale = 0; int rc;
        fixed (RegistryLiveReply* r = results) fixed (ulong* h = handles)
            rc = t->Install(ticket, anyway ? KilnRegistryHostApi.InstallAnyway : 0u, r, (nuint)results.Length, h, (nuint)handles.Length, &stale);
        var completions = new RegistryCompletion[results.Length];
        if (rc == 0)
            for (var i = 0; i < results.Length; ++i) completions[i] = results[i].Copy(i < calls ? handles[i] : 0);
        return (rc, stale, completions);
    }
    internal void Discard(ulong ticket) { var t = Internal; if (t != null && t->Discard != null) t->Discard(ticket); }
    /// <summary>Builds the registry.json report a landed turn owes, off the world thread. False: the registry moved meanwhile.</summary>
    internal bool BuildReport() => Internal->BuildReport() == 0;
    /// <summary>registry.json's text as kiln.registry has it now (report_json).</summary>
    internal string ReportJson()
    {
        if (!Available || Stage->Report == null) throw new KilnUnavailableException("kiln.registry");
        return Text((buffer, cap, length) => Stage->Report(buffer, cap, length));
    }
    internal IReadOnlyList<string> Select(bool stage, string registry, string pattern) =>
        NativeCalls.WithUtf8(registry, r => NativeCalls.WithUtf8(JsonSerializer.Serialize(pattern), selector =>
            JsonSerializer.Deserialize<string[]>(Text((buffer, cap, length) => Stage->Select(stage ? 1u : 2u, r, selector, buffer, cap, length))) ?? []));
    internal RegistryCompletion SubmitStage(ulong owner, string ir)
    {
        var bytes = Encoding.UTF8.GetBytes(ir); var result = new RegistrySubmitInfo { Size = 16 };
        fixed (byte* p = bytes) Check(Stage->Submit(owner, new KilnStr { Ptr = p, Len = (nuint)bytes.Length }, &result, null, 0, null), "kiln.registry");
        return new(1, result.Accepted, result.Refused, 0, 0, StageStatus().Generation, 0, "staged; final composition is reported at registry ready");
    }
    internal static uint Tag(RegistryEntryPoint point) => point switch
    {
        RegistryEntryPoint.ModLoad => 1, RegistryEntryPoint.WorldLoad => 2,
        RegistryEntryPoint.HotReload => 3, RegistryEntryPoint.Manual => 4,
        _ => throw new ArgumentException("GameStart uses the stage table"),
    };
    internal ulong Begin(ulong owner, RegistryEntryPoint point)
    {
        ulong handle = 0; Check(Live->Begin(owner, Tag(point), &handle), "kiln.registry.live"); return handle;
    }
    internal RegistryCompletion Submit(ulong owner, ulong handle, string ir)
    {
        var bytes = Encoding.UTF8.GetBytes(ir); var result = new RegistryLiveReply { Size = 232 };
        fixed (byte* p = bytes) Check(Live->Submit(owner, handle, new KilnStr { Ptr = p, Len = (nuint)bytes.Length }, &result, null, 0, null), "kiln.registry.live");
        return result.Copy(handle);
    }
    internal RegistryCompletion Commit(ulong owner, ulong handle)
    {
        var result = new RegistryLiveReply { Size = 232 }; Check(Live->Commit(owner, handle, &result), "kiln.registry.live"); return result.Copy(handle);
    }
    internal RegistryCompletion Revert(ulong owner, ulong handle)
    {
        var result = new RegistryLiveReply { Size = 232 }; var rc = Live->Revert(owner, handle, &result);
        if (rc == KilnAbi.ErrNotFound) return new(2, 0, 1, 0, 0, 0, 0, "the change expired or was already reverted");
        Check(rc, "kiln.registry.live"); return result.Copy(0);
    }
    internal IDisposable Subscribe(ulong owner, bool ready, Action<uint, ulong> callback, Action<Exception> fault)
    {
        var token = Interlocked.Increment(ref _next); Callbacks[token] = new(callback, fault); ulong handle = 0;
        var rc = ready ? Stage->Ready(owner, &OnReady, (void*)(nint)token, &handle) : Stage->Subscribe(owner, &OnStage, (void*)(nint)token, &handle);
        if (rc != 0) { Callbacks.TryRemove(token, out _); Check(rc, "kiln.registry"); }
        return new Subscription(this, token, handle);
    }
    private sealed class Subscription(RegistryNative native, long token, ulong handle) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (native.Available) native.Stage->Unsubscribe(handle);
            Callbacks.TryRemove(token, out _); // opaque integer, not a freed GCHandle: an in-flight lookup remains safe
        }
    }
    private static void Dispatch(nint token, uint phase, ulong generation)
    {
        if (!Callbacks.TryGetValue((long)token, out var callback)) return;
        try { callback.Invoke(phase, generation); }
        catch (Exception e) { try { callback.Fault(e); } catch { /* never escape reverse P/Invoke */ } }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnStage(RegistryStageInfo* info, void* user) { if (info != null && info->Size >= 24) Dispatch((nint)user, info->Stage, 0); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnReady(RegistryReadyInfo* info, void* user) { if (info != null && info->Size >= 24) Dispatch((nint)user, info->Phase, info->Generation); }
}
