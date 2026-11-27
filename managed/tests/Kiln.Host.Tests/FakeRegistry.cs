// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Kiln.Host;

namespace Kiln.Host.Tests;

// Exercises the real Host transport and dispatcher. This is a copied-state lifecycle fake, not native mapping proof.
internal sealed unsafe class FakeRegistry : IDisposable
{
    private static FakeRegistry? _current;
    internal readonly FakeHost Host = new();
    internal readonly RegistryStageTable* Stage;
    internal readonly RegistryLiveTable* Live;
    internal readonly KilnRegistryHostApi* Internal;
    internal uint Phase = 4;
    internal ulong World = 1, Generation;
    internal int PassReads, StatusCalls;
    internal int Submits, StageError, ReadyError, StatusFailures, WorldRequiredCommits, BeginUnavailable, ReadUnavailable;
    internal int Value => _layers.Count == 0 ? 10 : _layers.Last().Value.Value;
    private ulong _next;
    private readonly Dictionary<ulong, (ulong Owner, uint Point, int? Value, bool Restart)> _drafts = [];
    private readonly Dictionary<ulong, (ulong Owner, int Value, ulong World)> _layers = [];
    internal int LayerCount => _layers.Count;
    internal int DraftCount => _drafts.Count;
    internal void ChangeWorld(ulong world)
    {
        World = world;
        foreach (var handle in _layers.Where(x => x.Value.World != 0).Select(x => x.Key).ToArray()) _layers.Remove(handle);
    }
    private readonly Dictionary<ulong, (nint Fn, nint User, bool Ready)> _callbacks = [];
    internal int Subscriptions => _callbacks.Count;
    internal readonly List<uint> Points = [];
    internal FakeRegistry()
    {
        _current = this;
        Stage = (RegistryStageTable*)NativeMemory.AllocZeroed(88);
        Live = (RegistryLiveTable*)NativeMemory.AllocZeroed(56);
        *Stage = new() { Size = 88, Version = 1, Status = &StageStatus, Subscribe = &Subscribe, Submit = &StageSubmit,
            Read = &ReadStage, Select = &Select, Report = &Report, Ready = &Ready, Unsubscribe = &Unsubscribe };
        *Live = new() { Size = 56, Version = 1, Begin = &Begin, Submit = &Submit, Commit = &Commit,
            Revert = &Revert, Read = &ReadLive, Status = &Status };
        Internal = (KilnRegistryHostApi*)NativeMemory.AllocZeroed((nuint)sizeof(KilnRegistryHostApi));
        *Internal = new() { Size = KilnRegistryHostApi.SizeV2, Version = KilnRegistryHostApi.Version2, ReadPass = &ReadPass };
        Host.Services["kiln.registry"] = (nint)Stage;
        Host.Services["kiln.registry.live"] = (nint)Live;
        Host.Services["kiln.internal.registry"] = (nint)Internal;
    }
    // ---- kiln.internal.registry v3 (dev.32 LOOT-2): Registry.Reauthor's turn prepared off the world tick ----
    /// <summary>Publishes v3: a re-authored pass runs on Kiln.Host's worker and its turn is prepared there, landed on the tick.</summary>
    internal bool OffTick
    {
        set
        {
            Internal->Version = value ? KilnRegistryHostApi.Version3 : KilnRegistryHostApi.Version2;
            Internal->Size = value ? (uint)sizeof(KilnRegistryHostApi) : KilnRegistryHostApi.SizeV2;
            Internal->Epoch = value ? &EpochNow : null; Internal->ReadPassIn = value ? &ReadPassIn : null;
            Internal->Prepare = value ? &Prepare : null; Internal->Install = value ? &Install : null;
            Internal->Discard = value ? &Discard : null; Internal->BuildReport = value ? &BuildReport : null;
        }
    }
    /// <summary>Moves with every composition (a commit, a revert, a landed turn), as kiln-content's epoch.</summary>
    internal ulong Epoch = 1;
    internal int Prepares, Installs, Reverts, ReportsBuilt, PassReadsIn;
    internal readonly List<int> InstallThreads = [], PrepareThreads = [];
    /// <summary>The next install answers UNAVAILABLE with this stale (1: the pass's reads, 2: the preparation), once.</summary>
    internal uint NextStale;
    /// <summary>review of LOOT-2: every install answers stale 2 (the bindings moved) unless asked to land anyway, and every
    /// preparation off the tick thread answers stale 2 (a busy server's drop-table loads between its slices).</summary>
    internal bool InstallStaleUnlessAnyway, PrepareStaleOffTick;
    private readonly Dictionary<ulong, (List<(ulong Owner, int Value)> Calls, List<ulong> Replaced, ulong Epoch)> _plans = [];
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static ulong EpochNow() => Current.Epoch;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadPassIn(ulong owner, ulong world, KilnStr registry, KilnStr id, KilnStr field, byte* output, nuint capacity, nuint* length)
    {
        if (world != Current.World) return KilnAbi.ErrUnavailable;
        ++Current.PassReadsIn;
        var others = Current._layers.Values.Where(l => l.Owner != owner).ToArray();
        var value = others.Length == 0 ? 10 : others.Last().Value;
        return Text(value.ToString(System.Globalization.CultureInfo.InvariantCulture), output, capacity, length);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Prepare(KilnRegistryPassCall* calls, nuint count, KilnRegistryTurnItem* replaced, nuint replacedCount, ulong epoch, ulong* ticket, uint* stale)
    {
        ++Current.Prepares; lock (Current.PrepareThreads) Current.PrepareThreads.Add(Environment.CurrentManagedThreadId);
        if (epoch != Current.Epoch) { *stale = 1; return KilnAbi.ErrUnavailable; }
        if (Current.PrepareStaleOffTick && !Current.Host.IsTickThread) { *stale = 2; return KilnAbi.ErrUnavailable; }
        var plan = (new List<(ulong, int)>(), new List<ulong>(), epoch);
        for (nuint i = 0; i < count; ++i)
        {
            var doc = JsonNode.Parse(Encoding.UTF8.GetString(calls[i].Ir.Ptr, checked((int)calls[i].Ir.Len)))!;
            plan.Item1.Add((calls[i].Owner, doc["edits"]![0]!["op"]!.GetValue<int>()));
        }
        for (nuint i = 0; i < replacedCount; ++i) plan.Item2.Add(replaced[i].Handle);
        *ticket = ++Current._next; Current._plans[*ticket] = plan; return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Install(ulong ticket, uint flags, RegistryLiveReply* results, nuint resultCount, ulong* handles, nuint handleCount, uint* stale)
    {
        ++Current.Installs; Current.InstallThreads.Add(Environment.CurrentManagedThreadId);
        if (!Current._plans.Remove(ticket, out var plan)) return KilnAbi.ErrNotFound;
        if (Current.NextStale != 0) { *stale = Current.NextStale; Current.NextStale = 0; return KilnAbi.ErrUnavailable; }
        if (Current.InstallStaleUnlessAnyway && (flags & KilnRegistryHostApi.InstallAnyway) == 0) { *stale = 2; return KilnAbi.ErrUnavailable; }
        if (plan.Epoch != Current.Epoch) { *stale = 1; return KilnAbi.ErrUnavailable; }
        for (var j = 0; j < plan.Replaced.Count; ++j)
        {
            Current._layers.Remove(plan.Replaced[j]);
            results[plan.Calls.Count + j].Outcome = 5; results[plan.Calls.Count + j].World = Current.World;
        }
        for (var i = 0; i < plan.Calls.Count; ++i)
        {
            var handle = ++Current._next;
            Current._layers.Add(handle, (plan.Calls[i].Owner, plan.Calls[i].Value, Current.World));
            results[i].Outcome = 0; results[i].Accepted = 1; results[i].World = Current.World; handles[i] = handle;
        }
        ++Current.Epoch; ++Current.Generation;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Discard(ulong ticket) { Current._plans.Remove(ticket); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BuildReport()
    {
        Interlocked.Increment(ref Current.ReportBuilds);
        if (Interlocked.Decrement(ref Current.ReportMoves) >= 0) return KilnAbi.ErrUnavailable;  // the registry moved meanwhile
        Interlocked.Increment(ref Current.ReportsBuilt); return 0;
    }
    /// <summary>review of LOOT-2: the next ReportMoves report builds find the registry moved meanwhile (UNAVAILABLE).</summary>
    internal int ReportMoves, ReportBuilds;
    internal void FireStage(RegistryPhase phase)
    {
        var info = new RegistryStageInfo { Size = 24, Stage = (uint)phase };
        foreach (var callback in _callbacks.Values.Where(c => !c.Ready).ToArray())
            ((delegate* unmanaged[Cdecl]<RegistryStageInfo*, void*, void>)callback.Fn)(&info, (void*)callback.User);
    }
    internal void FireReady()
    {
        var info = new RegistryReadyInfo { Size = 24, Phase = Phase, Generation = ++Generation, World = 1 };
        foreach (var callback in _callbacks.Values.Where(c => c.Ready).ToArray())
            ((delegate* unmanaged[Cdecl]<RegistryReadyInfo*, void*, void>)callback.Fn)(&info, (void*)callback.User);
    }
    private static FakeRegistry Current => _current!;
    private static int Text(string value, byte* output, nuint capacity, nuint* length)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (length != null) *length = (nuint)bytes.Length;
        if (output == null || capacity <= (nuint)bytes.Length) return KilnAbi.ErrBufferTooSmall;
        bytes.CopyTo(new Span<byte>(output, bytes.Length)); output[bytes.Length] = 0; return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StageStatus(RegistryStageStatus* value) { ++Current.StatusCalls; if (Current.StageError != 0) return Current.StageError; value->Phase = Current.Phase; value->Generation = Current.Generation; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status(RegistryLiveStatus* value) { ++Current.StatusCalls; if (Current.StatusFailures > 0) { --Current.StatusFailures; return KilnAbi.ErrUnavailable; } value->World = Current.World; value->Kind = (uint)WorldKind.Server; value->Generation = Current.Generation; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadStage(uint view, KilnStr registry, KilnStr id, KilnStr field, byte* output, nuint capacity, nuint* length) => Text(Current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), output, capacity, length);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadLive(ulong owner, KilnStr registry, KilnStr id, KilnStr field, byte* output, nuint capacity, nuint* length)
    {
        if (Current.ReadUnavailable > 0) { --Current.ReadUnavailable; return KilnAbi.ErrUnavailable; }
        return Text(Current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), output, capacity, length);
    }
    // kiln.internal.registry read_pass: the live value without the reading owner's own layers (review4 NS5).
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadPass(ulong owner, KilnStr registry, KilnStr id, KilnStr field, byte* output, nuint capacity, nuint* length)
    {
        if (Current.ReadUnavailable > 0) { --Current.ReadUnavailable; return KilnAbi.ErrUnavailable; }
        ++Current.PassReads;
        var others = Current._layers.Values.Where(l => l.Owner != owner).ToArray();
        var value = others.Length == 0 ? 10 : others.Last().Value;
        return Text(value.ToString(System.Globalization.CultureInfo.InvariantCulture), output, capacity, length);
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Select(uint view, KilnStr registry, KilnStr selector, byte* output, nuint capacity, nuint* length) => Text("[\"game:wood\"]", output, capacity, length);
    /// <summary>registry.json's text kiln.registry's report_json answers with (the reload reply reads it).</summary>
    internal string ReportText = "{\"format\": 1, \"refused\": []}";
    internal int ReportCalls;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Report(byte* output, nuint capacity, nuint* length) { ++Current.ReportCalls; return Text(Current.ReportText, output, capacity, length); }
    private static ulong Add(nint fn, nint user, bool ready)
    {
        var handle = ++Current._next; Current._callbacks.Add(handle, (fn, user, ready)); return handle;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Subscribe(ulong owner, delegate* unmanaged[Cdecl]<RegistryStageInfo*, void*, void> fn, void* user, ulong* handle) { *handle = Add((nint)fn, (nint)user, false); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Ready(ulong owner, delegate* unmanaged[Cdecl]<RegistryReadyInfo*, void*, void> fn, void* user, ulong* handle) { if (Current.ReadyError != 0) return Current.ReadyError; *handle = Add((nint)fn, (nint)user, true); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unsubscribe(ulong handle) { Current._callbacks.Remove(handle); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int StageSubmit(ulong owner, KilnStr ir, RegistrySubmitInfo* value, byte* output, nuint capacity, nuint* length) { ++Current.Submits; value->Accepted = 1; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Begin(ulong owner, uint point, ulong* handle)
    {
        if (Current.BeginUnavailable > 0) { --Current.BeginUnavailable; return KilnAbi.ErrUnavailable; }
        *handle = ++Current._next; Current.Points.Add(point); Current._drafts.Add(*handle, (owner, point, null, false)); return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Submit(ulong owner, ulong handle, KilnStr ir, RegistryLiveReply* result, byte* output, nuint capacity, nuint* length)
    {
        ++Current.Submits;
        var doc = JsonNode.Parse(Encoding.UTF8.GetString(ir.Ptr, checked((int)ir.Len)))!;
        var edit = doc["edits"]![0]!;
        var draft = Current._drafts[handle];
        var restart = edit["registry"]!.GetValue<string>() == "balance";
        Current._drafts[handle] = (owner, draft.Point, edit["op"]!.GetValue<int>(), restart);
        result->Accepted = restart ? 0u : 1u; result->Refused = restart ? 1u : 0u; result->Outcome = restart ? 3u : 0u;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Commit(ulong owner, ulong handle, RegistryLiveReply* result)
    {
        if (Current.WorldRequiredCommits > 0) { --Current.WorldRequiredCommits; result->Outcome = 4; result->World = Current.World; return 0; }
        var draft = Current._drafts[handle]; Current._drafts.Remove(handle);
        result->Outcome = draft.Restart ? 3u : 0u; result->Refused = draft.Restart ? 1u : 0u;
        result->Accepted = draft.Restart ? 0u : 1u; result->World = Current.World;
        if (!draft.Restart) Current._layers.Add(handle, (owner, draft.Value!.Value, draft.Point == 1 ? 0 : Current.World));
        result->Generation = ++Current.Generation; ++Current.Epoch;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Revert(ulong owner, ulong handle, RegistryLiveReply* result)
    {
        if (!Current._drafts.Remove(handle) && !Current._layers.Remove(handle)) return KilnAbi.ErrNotFound;
        ++Current.Reverts; ++Current.Epoch;
        result->Outcome = 5; result->Generation = ++Current.Generation; result->World = Current.World; return 0;
    }
    public void Dispose()
    {
        Host.Services.Clear(); NativeMemory.Free(Stage); NativeMemory.Free(Live); NativeMemory.Free(Internal); _current = null;
    }
}
