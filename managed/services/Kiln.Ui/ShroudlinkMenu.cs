// SPDX-License-Identifier: MIT
// Optional cooperative shroudlink client ABI v1. Never loads a DLL or taps game
// transport. An unchanged addon has no export: no button, lease, or behavior change.
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text;

namespace Kiln;

internal interface IShroudlinkRpc : IDisposable
{
    int Call(byte[] request, byte[] reply, out int written);
}

// Where the bridge scan looks: this process (ShroudlinkRpc), or a test fake.
internal interface IModuleSpace
{
    nint[]? Modules();                            // loaded module bases in load order; null when unknown
    byte[]? Read(nint at, int n);                 // a kernel-checked copy; null when unreadable
    IShroudlinkRpc? Open(nint module, uint rva);  // only for a module whose export table matched
}

// Cheap and rare: export tables are read without a module reference, and a reference is taken only for a match.
// After a scan, an unchanged module list is not read again; only its matching copies are asked again (one that
// has not started yet, was replaced or was busy). A load-order list changes whenever a module loads or unloads.
internal sealed class ShroudlinkFinder(IModuleSpace space)
{
    private nint[]? _scanned;
    private readonly List<(nint Module, uint Rva)> _matches = [];
    public IShroudlinkRpc? Find()
    {
        var modules = space.Modules();
        if (modules is null) return null;
        if (_scanned is null || !modules.AsSpan().SequenceEqual(_scanned))
        {
            _matches.Clear();
            foreach (var module in modules)
                if (ShroudlinkRpc.ExportRva((at, n) => space.Read(module + (nint)at, n), out _) is var rva and not 0) _matches.Add((module, rva));
            _scanned = modules;
        }
        foreach (var (module, rva) in _matches)
            if (space.Open(module, rva) is { } rpc) return rpc;
        return null;
    }
}

// The render thread never scans: it starts at most one worker scan at a time, a second after the last one ended,
// and picks the result up on a later frame.
internal sealed class ShroudlinkDiscovery(Func<IShroudlinkRpc?> find)
{
    private Task<IShroudlinkRpc?>? _scan;
    private long _next;
    internal bool Scanning => _scan is not null;
    public IShroudlinkRpc? Poll(long now)
    {
        if (_scan is { IsCompleted: true } done)
        {
            _scan = null;
            _next = now + 1000;
            return done.IsCompletedSuccessfully ? done.Result : null;
        }
        if (_scan is null && now >= _next)
            _scan = Task.Run<IShroudlinkRpc?>(() => { try { return find(); } catch (Exception) { return null; } });
        return null;
    }
    public void Soon() => _next = 0;
}

internal sealed unsafe class ShroudlinkRpc : IShroudlinkRpc
{
    private nint _module;
    private readonly delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint*, uint> _call;
    private ShroudlinkRpc(nint module, nint call) { _module = module; _call = (delegate* unmanaged[Cdecl]<byte*, nuint, byte*, nuint, nuint*, uint>)call; }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleExW")]
    private static extern bool Hold(uint flags, nint address, out nint module);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(nint module);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public nint Base, AllocationBase; public uint AllocationProtect; public ushort Partition; public nuint Size; public uint State, Protect, Type; }
    [DllImport("kernel32.dll")]
    private static extern bool K32EnumProcessModules(nint process, [Out] nint[] modules, uint bytes, out uint needed);
    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemoryInfo info, nuint bytes);
    [DllImport("kernel32.dll")]
    private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nuint length, out nuint read);
    // Do not use GetProcAddress/TryGetExport here: resolving a forwarded export can
    // load its target DLL before ownership can be checked. Parse only the PE's
    // bounded export table, using kernel-checked reads rather than raw dereferences.
    // The image size bounding the reads is the optional header's SizeOfImage, not a
    // module-information call (Wine walks the whole loader list for each of those).
    internal static uint ExportRva(Func<uint, int, byte[]?> source, out uint image)
    {
        var size = uint.MaxValue; image = 0;
        byte[] Read(uint at, int n) => at <= size && n >= 0 && (uint)n <= size - at && source(at, n) is { } b && b.Length == n ? b : throw new FormatException("PE bound");
        static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
        static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));
        try
        {
            checked
            {
                var dos = Read(0, 64); if (U16(dos, 0) != 0x5a4d) return 0;
                var peAt = U32(dos, 60); if (peAt < 64 || peAt > 1024 * 1024) return 0;
                var pe = Read(peAt, 144);
                if (U32(pe, 0) != 0x4550 || U16(pe, 4) != 0x8664 || U16(pe, 20) < 120 || U16(pe, 24) != 0x20b || U32(pe, 132) == 0) return 0;
                size = U32(pe, 80); if (size < peAt + 144) return 0;
                var directory = U32(pe, 136); var length = U32(pe, 140);
                if (length < 40 || directory > size || length > size - directory) return 0;
                var exports = Read(directory, 40); var functions = U32(exports, 20); var names = U32(exports, 24);
                if (functions is 0 or > 65536 || names is 0 or > 65536) return 0;
                var lo = 0U; var hi = names;
                while (lo < hi)
                {
                    var mid = lo + (hi - lo) / 2;
                    var nameAt = U32(Read(U32(exports, 32) + mid * 4, 4), 0);
                    var nameBytes = Read(nameAt, (int)Math.Min(128U, size - Math.Min(nameAt, size)));
                    var nul = System.Array.IndexOf(nameBytes, (byte)0); if (nul < 0) return 0;
                    var order = string.CompareOrdinal(Encoding.ASCII.GetString(nameBytes, 0, nul), "shroudlink_client_bridge_v1");
                    if (order < 0) { lo = mid + 1; continue; }
                    if (order > 0) { hi = mid; continue; }
                    var ordinal = U16(Read(U32(exports, 36) + mid * 2, 2), 0); if (ordinal >= functions) return 0;
                    var fn = U32(Read(U32(exports, 28) + ordinal * 4U, 4), 0);
                    // Export-directory RVAs are forwarder strings, never call targets.
                    if (fn == 0 || fn >= size || (fn >= directory && fn - directory < length)) return 0;
                    image = size;
                    return fn;
                }
            }
        }
        catch (Exception) { /* malformed/unreadable table: do not resolve it */ }
        return 0;
    }
    internal static bool OwnedExecutable(nuint module, uint size, nuint fn, nuint allocation, uint state, uint protect) =>
        module != 0 && fn >= module && fn - module < size && allocation == module && state == 0x1000 &&
        (protect & 0x100) == 0 && (protect & 0xff) is 0x10 or 0x20 or 0x40 or 0x80;
    // This process. Reads are copies the kernel checks, so a module that unloads during a scan fails a read instead
    // of faulting. Only Open takes a module reference, and only for a matching export table.
    private sealed class Process : IModuleSpace
    {
        private readonly nint[] _modules = new nint[4096];
        public nint[]? Modules() =>
            K32EnumProcessModules(-1, _modules, (uint)(_modules.Length * sizeof(nint)), out var needed) && needed <= _modules.Length * sizeof(nint)
                ? _modules[..(int)(needed / sizeof(nint))] : null;
        public byte[]? Read(nint at, int n)
        {
            var bytes = new byte[n];
            return ReadProcessMemory(-1, at, bytes, (nuint)n, out var read) && read == (nuint)n ? bytes : null;
        }
        public IShroudlinkRpc? Open(nint module, uint rva)
        {
            if (!Hold(4, module, out var held)) return null;
            var keep = false;
            try
            {
                // Check again under the reference: the address may have been unloaded and reused since the scan.
                if (held != module || ExportRva((at, n) => Read(held + (nint)at, n), out var size) != rva) return null;
                var fn = held + (nint)rva;
                if (VirtualQuery(fn, out var memory, (nuint)sizeof(MemoryInfo)) == 0 ||
                    !OwnedExecutable((nuint)held, size, (nuint)fn, (nuint)memory.AllocationBase, memory.State, memory.Protect)) return null;
                // Replaced copies stay mapped. Only accept a live ABI response.
                var candidate = new ShroudlinkRpc(held, fn);
                var response = new byte[BridgeSnapshot.MaxBytes];
                if (candidate.Call("{\"op\":\"read\"}"u8.ToArray(), response, out var n) != 0) return null;
                BridgeSnapshot.Parse(response.AsSpan(0, n));
                keep = true;
                return candidate;
            }
            catch (Exception) { return null; /* unrecognised or stale cooperative copy, not a game target */ }
            finally { if (!keep) FreeLibrary(held); }
        }
    }
    private static readonly ShroudlinkFinder Scan = new(new Process());
    // One scan at a time, on ShroudlinkDiscovery's worker; never on the render thread.
    public static IShroudlinkRpc? Find() => OperatingSystem.IsWindows() ? Scan.Find() : null;
    public int Call(byte[] request, byte[] reply, out int written)
    {
        written = 0;
        if (_module == 0 || request.Length is 0 or > 8192 || reply.Length > BridgeSnapshot.MaxBytes) return 2;
        nuint n = 0;
        uint result;
        fixed (byte* input = request, output = reply) result = _call(input, (nuint)request.Length, output, (nuint)reply.Length, &n);
        if (n > (nuint)reply.Length) return 2;
        written = (int)n;
        return (int)result;
    }
    public void Dispose() { var held = _module; _module = 0; if (held != 0) FreeLibrary(held); }
}

internal sealed record BridgeEntry(string Owner, string Id, string Label);
internal sealed record BridgeWindow(string Owner, string Id, string Title, JsonElement Content);
internal sealed record BridgeSnapshot(string Module, string Session, ulong Revision, bool Ready, string? Lease,
    IReadOnlyList<BridgeEntry> Entries, IReadOnlyList<BridgeWindow> Windows)
{
    public const int MaxBytes = 256 * 1024;
    internal static string Text(JsonElement e, string key, int max = 4096)
    {
        var s = e.GetProperty(key).GetString() ?? throw new FormatException("null string");
        if (Encoding.UTF8.GetByteCount(s) > max || s.Contains('\0')) throw new FormatException("string bound");
        return s;
    }
    private static string Identity(JsonElement e, string key)
    {
        var s = Text(e, key, 128); if (s.Length == 0) throw new FormatException("empty identity"); return s;
    }
    internal static JsonElement[] Array(JsonElement e, string key, int max)
    {
        var a = e.GetProperty(key); if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() > max) throw new FormatException("array bound");
        return a.EnumerateArray().ToArray();
    }
    public static BridgeSnapshot Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes) throw new FormatException("reply bound");
        using var doc = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 40 });
        var r = doc.RootElement;
        if (r.GetProperty("abi").GetInt32() != 1) throw new FormatException("ABI version");
        var module = Identity(r, "module"); var session = Identity(r, "session");
        var revision = r.GetProperty("revision").GetUInt64(); var ready = r.GetProperty("ready").GetBoolean();
        string? lease = r.GetProperty("lease").ValueKind == JsonValueKind.Null ? null : Identity(r, "lease");
        var identities = new HashSet<(string, string)>();
        var entries = Array(r, "entries", 128).Select(e => new BridgeEntry(Identity(e, "owner"), Identity(e, "id"), Text(e, "label"))).ToArray();
        foreach (var e in entries) if (!identities.Add((e.Owner, e.Id))) throw new FormatException("duplicate entry");
        identities.Clear();
        var windows = Array(r, "windows", 32).Select(e =>
        {
            var w = e.GetProperty("window"); var owner = Identity(e, "owner"); var id = Identity(w, "id");
            if (!identities.Add((owner, id))) throw new FormatException("duplicate window");
            var count = 0; ValidateNodes(w.GetProperty("content"), 0, ref count, []);
            return new BridgeWindow(owner, id, Text(w, "title"), w.GetProperty("content").Clone());
        }).ToArray();
        if (!ready && (entries.Length != 0 || windows.Length != 0 || lease != null)) throw new FormatException("inactive state");
        return new(module, session, revision, ready, lease, entries, windows);
    }
    private static void ValidateNodes(JsonElement nodes, int depth, ref int count, HashSet<string> ids)
    {
        if (depth > 12 || nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > 128) throw new FormatException("node bound");
        foreach (var n in nodes.EnumerateArray())
        {
            if (++count > 1024) throw new FormatException("node bound");
            var type = Text(n, "type", 32);
            if (type is not ("text" or "heading" or "separator" or "row" or "button" or "toggle" or "text_input" or "item_grid" or "list" or "tabs")) throw new FormatException("unknown node");
            if (type is not ("text" or "heading" or "separator" or "row"))
                if (!ids.Add(Identity(n, "id"))) throw new FormatException("duplicate node");
            switch (type)
            {
                case "text": case "heading": Text(n, "text"); break;
                case "separator": break;
                case "row": ValidateNodes(n.GetProperty("children"), depth + 1, ref count, ids); break;
                case "button": Text(n, "label"); break;
                case "toggle": Text(n, "label"); n.GetProperty("value").GetBoolean(); break;
                case "text_input": Text(n, "value", 256); Text(n, "hint"); break;
                case "item_grid":
                    var columns = n.GetProperty("columns").GetInt32(); if (columns is < 1 or > 16) throw new FormatException("columns");
                    foreach (var item in Array(n, "items", 128)) { item.GetProperty("id").GetUInt32(); item.GetProperty("count").GetUInt32(); } break;
                case "list": foreach (var v in Array(n, "entries", 128)) ValidateString(v); break;
                case "tabs":
                    var tabs = Array(n, "tabs", 32); var selected = n.GetProperty("selected").GetUInt32();
                    if (tabs.Length == 0 || selected >= tabs.Length) throw new FormatException("selected tab");
                    foreach (var v in tabs) ValidateString(v); break;
                default: throw new FormatException("unknown node");
            }
        }
    }
    private static void ValidateString(JsonElement v) { var s = v.GetString() ?? throw new FormatException(); if (s.Contains('\0') || Encoding.UTF8.GetByteCount(s) > 4096) throw new FormatException(); }
}

internal sealed class ShroudlinkClient : IDisposable
{
    private readonly IShroudlinkRpc _rpc;
    private readonly string _client = Guid.NewGuid().ToString("N");
    private readonly byte[] _reply = new byte[BridgeSnapshot.MaxBytes];
    private BridgeSnapshot? _owned;
    public BridgeSnapshot? State { get; private set; }
    public int LastError { get; private set; }
    public ShroudlinkClient(IShroudlinkRpc rpc) { _rpc = rpc; }
    // The length the addon wrote into _reply, or -1 when it declined (LastError).
    private int Call(object request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        LastError = _rpc.Call(bytes, _reply, out var n);
        if (LastError != 0) return -1;
        if (n is < 0 or > BridgeSnapshot.MaxBytes) throw new FormatException("reply bound");
        return n;
    }
    private JsonElement? Request(object request)
    {
        var n = Call(request);
        if (n < 0) return null;
        using var doc = JsonDocument.Parse(_reply.AsMemory(0, n), new JsonDocumentOptions { MaxDepth = 40 });
        return doc.RootElement.Clone();
    }
    // Validate the bytes the addon wrote, not a re-encoded copy: System.Text.Json escapes non-ASCII text as
    // \uXXXX, so a copy of a valid reply can be several times larger than the reply bound.
    private BridgeSnapshot? Snapshot(object request) => Call(request) is var n and >= 0 ? BridgeSnapshot.Parse(_reply.AsSpan(0, n)) : null;
    public bool Refresh()
    {
        try
        {
            var next = Snapshot(new { op = "read" });
            if (next is null) { State = null; return false; }
            if (!next.Ready) { State = next; _owned = null; return false; }
            if (_owned is not null && (_owned.Module != next.Module || _owned.Session != next.Session)) _owned = null;
            State = Snapshot(new { op = "acquire", module = next.Module, session = next.Session, client = _client }) ?? next;
            if (State.Lease is not null) _owned = State;
            return State.Lease is not null;
        }
        catch (Exception) { State = null; LastError = 2; return false; }
    }
    public bool Send(string owner, string window, string node, object evt)
    {
        var s = State;
        if (s is not { Ready: true, Lease: not null }) return false;
        try
        {
            var result = Request(new { op = "event", module = s.Module, session = s.Session, lease = s.Lease, revision = s.Revision,
                owner, @event = new { window, node, @event = evt } });
            if (result is null) return false;
            if (result.Value.GetProperty("abi").GetInt32() != 1 || !result.Value.GetProperty("queued").GetBoolean()) throw new FormatException("event acknowledgement");
            return true;
        }
        catch (Exception) { LastError = 2; return false; }
    }
    public void Release()
    {
        var s = _owned; State = null;
        if (s?.Lease is null) return;
        try
        {
            if (Request(new { op = "release", module = s.Module, session = s.Session, lease = s.Lease }) is not null || LastError is 3 or 4) _owned = null;
            // Busy release is retried on later hidden frames; the addon also expires
            // ownership independently after two seconds if rendering stops entirely.
        }
        catch (Exception) { LastError = 2; }
    }
    public void Dispose() { Release(); _rpc.Dispose(); }
}

// Widget seam: the real renderer and headless tests walk the same ten node types.
internal interface IShroudlinkWidgets
{
    void Text(string text); void Heading(string text); void Separator(); void SameLine();
    bool Button(string id, string label); bool Toggle(string id, string label, ref bool value);
    bool Input(string id, ref string value); void Hint(string text);
}
internal sealed class ShroudlinkNodes
{
    private readonly Dictionary<string, (string Server, string Edit)> _edits = [];
    private string _epoch = "";
    private readonly HashSet<string> _visible = [];
    internal static string Key(params string[] parts) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(parts));
    public void Reset(string epoch) { if (_epoch != epoch) { _edits.Clear(); _epoch = epoch; } _visible.Clear(); }
    public void EndFrame() { foreach (var key in _edits.Keys.Where(k => !_visible.Contains(k)).ToArray()) _edits.Remove(key); }
    public void Draw(IShroudlinkWidgets ui, JsonElement nodes, string scope, Action<string, object> send)
    {
        foreach (var n in nodes.EnumerateArray())
        {
            var type = n.GetProperty("type").GetString();
            var id = n.TryGetProperty("id", out var ident) ? ident.GetString()! : "";
            var key = Key(scope, id);
            string T(string name) => n.GetProperty(name).GetString()!;
            switch (type)
            {
                case "text": ui.Text(T("text")); break;
                case "heading": ui.Heading(T("text")); break;
                case "separator": ui.Separator(); break;
                case "row":
                    var first = true;
                    foreach (var child in n.GetProperty("children").EnumerateArray())
                    { if (!first) ui.SameLine(); first = false; using var one = JsonDocument.Parse("[" + child.GetRawText() + "]"); Draw(ui, one.RootElement, scope, send); } break;
                case "button": if (ui.Button(key, T("label"))) send(id, new { kind = "click" }); break;
                case "toggle": var value = n.GetProperty("value").GetBoolean(); if (ui.Toggle(key, T("label"), ref value)) send(id, new { kind = "change", value }); break;
                case "text_input":
                    _visible.Add(key);
                    var server = T("value");
                    var edit = _edits.TryGetValue(key, out var saved) && saved.Server == server ? saved.Edit : server;
                    ui.Hint(T("hint"));
                    if (ui.Input(key, ref edit)) send(id, new { kind = "change", value = edit });
                    _edits[key] = (server, edit);
                    if (ui.Button(key + "/submit", "Apply")) send(id, new { kind = "submit", value = edit }); break;
                case "item_grid":
                    var cols = n.GetProperty("columns").GetInt32(); var index = 0;
                    foreach (var item in n.GetProperty("items").EnumerateArray())
                    { if (index % cols != 0) ui.SameLine(); if (ui.Button(key + "/" + index, $"Item {item.GetProperty("id").GetUInt32()} × {item.GetProperty("count").GetUInt32()}")) send(id, new { kind = "click", index }); index++; } break;
                case "list": case "tabs":
                    var i = 0;
                    foreach (var label in n.GetProperty(type == "tabs" ? "tabs" : "entries").EnumerateArray())
                    { if (type == "tabs" && i > 0) ui.SameLine(); var chosen = type == "tabs" && i == n.GetProperty("selected").GetUInt32(); if (ui.Button(key + "/" + i, (chosen ? "> " : "") + label.GetString())) send(id, new { kind = "click", index = i }); i++; } break;
            }
        }
    }
}
internal sealed class ShroudlinkWidgets(UiDraw ui) : IShroudlinkWidgets
{
    public void Text(string s) => ui.TextWrapped(s);
    public void Heading(string s) => ui.Separator(s);
    public void Separator() => ui.Separator();
    public void SameLine() => ui.SameLine();
    public void Hint(string s) { if (s.Length > 0) ui.TextDisabled(s); }
    // Server labels are not ImGui IDs: use a local ID and strip label delimiters.
    private static string Label(string s) => s.Replace("##", "＃＃");
    public bool Button(string id, string label) { using var scope = ui.Id(id); return ui.Button(Label(label) + "###action"); }
    public bool Toggle(string id, string label, ref bool value) { using var scope = ui.Id(id); return ui.Checkbox(Label(label) + "###toggle", ref value); }
    public bool Input(string id, ref string value) { using var scope = ui.Id(id); return ui.InputText("##input", ref value, 256); }
}

internal sealed class ShroudlinkPresentation(Action release)
{
    private bool _shown;
    public void BeginFrame() => _shown = false;
    public void Shown() => _shown = true;
    public bool EndFrame(bool completed = true)
    {
        if (_shown && completed) return true;
        release(); return false;
    }
}

internal static class ShroudlinkMenu
{
    private static ShroudlinkClient? _client;
    private static readonly ShroudlinkNodes Nodes = new();
    private static readonly ShroudlinkDiscovery Discovery = new(ShroudlinkRpc.Find);
    private static long _nextRead;
    private static readonly ShroudlinkPresentation Presentation = new(() => _client?.Release());
    public static bool Open;
    public static bool Available
    {
        get
        {
            if (_client == null && Discovery.Poll(Environment.TickCount64) is { } rpc) _client = new(rpc);
            return _client != null;
        }
    }
    public static void BeginFrame() => Presentation.BeginFrame();
    public static void EndFrame(bool completed = true)
    {
        if (!Presentation.EndFrame(completed)) { Open = false; _nextRead = 0; Nodes.Reset(""); }
    }
    public static void Draw(UiDraw ui)
    {
        Presentation.Shown();
        if (!Available) { ui.TextWrapped("This requires a cooperating shroudlink client addon with bridge ABI v1. The stock addon is unchanged."); return; }
        if (Environment.TickCount64 >= _nextRead) { _client!.Refresh(); _nextRead = Environment.TickCount64 + 100; }
        if (_client!.LastError == 3) { _client.Dispose(); _client = null; Discovery.Soon(); return; }
        if (_client.State is not { Ready: true, Lease: not null } state)
        { ui.TextWrapped("Waiting for a live shroudlink host, UI protocol 2, recognized world, and presentation lease."); return; }
        Nodes.Reset(ShroudlinkNodes.Key(state.Module, state.Session));
        if (state.Entries.Count == 0) ui.TextDisabled("The host has not sent any menu entries.");
        foreach (var e in state.Entries)
            if (new ShroudlinkWidgets(ui).Button(ShroudlinkNodes.Key(e.Owner, "menu", e.Id), e.Label))
                _client.Send(e.Owner, "", e.Id, new { kind = "menu" });
        foreach (var w in state.Windows)
        {
            using var id = ui.Id(ShroudlinkNodes.Key(state.Module, state.Session, w.Owner, w.Id));
            ui.Separator(w.Title);
            Nodes.Draw(new ShroudlinkWidgets(ui), w.Content, ShroudlinkNodes.Key(w.Owner, w.Id), (node, evt) => _client.Send(w.Owner, w.Id, node, evt));
            if (ui.SmallButton("Close window")) _client.Send(w.Owner, w.Id, "", new { kind = "closed" });
        }
        Nodes.EndFrame();
        if (_client.LastError != 0) { ui.TextDisabled($"shroudlink declined the last request ({_client.LastError}); refreshing its current state."); _nextRead = 0; }
    }
}
