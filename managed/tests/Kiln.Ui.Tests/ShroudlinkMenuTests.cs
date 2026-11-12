// SPDX-License-Identifier: MIT
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.ShroudlinkMenuTests;

public sealed class BridgeTests
{
    // Real MIT Node/Window wire shapes; no catalog or invented menu ownership.
    private const string Snapshot = """
    {"abi":1,"module":"opaque-a","session":"1","revision":3,"ready":true,"leased":true,"lease":"lease-a",
     "entries":[{"owner":"storage","id":"settings","label":"Storage"}],
     "windows":[{"owner":"storage","window":{"id":"w","title":"Settings","content":[
      {"type":"text","text":"Message"},{"type":"heading","text":"Heading"},{"type":"separator"},
      {"type":"row","children":[{"type":"button","id":"button","label":"Go##unsafe"}]},
      {"type":"text_input","id":"input","value":"old","hint":"Name"},
      {"type":"toggle","id":"toggle","label":"Enabled","value":false},
      {"type":"item_grid","id":"items","columns":2,"items":[{"id":42,"count":3}]},
      {"type":"list","id":"list","entries":["one\tcolumn"]},
      {"type":"tabs","id":"tabs","tabs":["a","b"],"selected":0}
     ]}}]}
    """;
    private static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed class Rpc : IShroudlinkRpc
    {
        public JsonObject State = JsonNode.Parse(Snapshot)!.AsObject();
        public readonly List<JsonElement> Requests = [];
        public int Error;
        public bool Disposed, InvalidReply, BadAbi;
        public int Call(byte[] request, byte[] reply, out int written)
        {
            var r = JsonDocument.Parse(request).RootElement.Clone(); Requests.Add(r);
            if (Error != 0) { written = 0; return Error; }
            var op = r.GetProperty("op").GetString();
            JsonNode response;
            if (op == "event") response = new JsonObject { ["abi"] = BadAbi ? 9 : 1, ["queued"] = true };
            else
            {
                response = State.DeepClone();
                if (op != "acquire") response["lease"] = null;
                if (op == "release") State["lease"] = null;
            }
            // Raw UTF-8, as the addon's serde_json writes it (not \uXXXX escapes).
            var bytes = InvalidReply ? "{invalid"u8.ToArray() : Encoding.UTF8.GetBytes(response.ToJsonString(Raw));
            if (bytes.Length > reply.Length) { written = bytes.Length; return 1; }
            bytes.CopyTo(reply, 0); written = bytes.Length; return 0;
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class Widgets : IShroudlinkWidgets
    {
        public List<string> Labels = [], Inputs = [];
        public int Texts, Headings, Separators, Rows;
        public bool Click = true, Edit = true;
        public string Edited = "snow 雪";
        public void Text(string text) { Texts++; Labels.Add(text); }
        public void Heading(string text) { Headings++; Labels.Add(text); }
        public void Separator() => Separators++;
        public void SameLine() => Rows++;
        public bool Button(string id, string label) { Labels.Add(label); return Click; }
        public bool Toggle(string id, string label, ref bool value) { Labels.Add(label); value = !value; return Click; }
        public bool Input(string id, ref string value) { Inputs.Add(value); if (Edit) value = Edited; return Edit; }
        public void Hint(string text) => Labels.Add(text);
    }
    [Fact] public void AllTenNodesRenderWithActualValuesAndCorrectEvents()
    {
        var state = BridgeSnapshot.Parse(Encoding.UTF8.GetBytes(Snapshot)); var widgets = new Widgets();
        var events = new List<(string Node, JsonElement Event)>(); var nodes = new ShroudlinkNodes();
        nodes.Reset("epoch"); nodes.Draw(widgets, state.Windows[0].Content, "scope", (id, e) => events.Add((id, JsonSerializer.SerializeToElement(e)))); nodes.EndFrame();
        Assert.Equal(1, widgets.Texts); Assert.Equal(1, widgets.Headings); Assert.Equal(1, widgets.Separators);
        Assert.Contains("Item 42 × 3", widgets.Labels); Assert.Contains("one\tcolumn", widgets.Labels); Assert.Contains("> a", widgets.Labels);
        Assert.Equal(8, events.Count);
        Assert.Equal("click", events.Single(e => e.Node == "button").Event.GetProperty("kind").GetString());
        Assert.True(events.Single(e => e.Node == "toggle").Event.GetProperty("value").GetBoolean());
        Assert.Equal(["change", "submit"], events.Where(e => e.Node == "input").Select(e => e.Event.GetProperty("kind").GetString()!).ToArray());
        Assert.All(events.Where(e => e.Node == "input"), e => Assert.Equal("snow 雪", e.Event.GetProperty("value").GetString()));
        Assert.Equal([0, 1], events.Where(e => e.Node == "tabs").Select(e => e.Event.GetProperty("index").GetInt32()).ToArray());
        Assert.Equal(0, events.Single(e => e.Node == "items").Event.GetProperty("index").GetInt32());
        Assert.Equal(0, events.Single(e => e.Node == "list").Event.GetProperty("index").GetInt32());
    }
    [Fact] public void ClientSendsRealOwnerNestedUiEventAndOpaqueEpochs()
    {
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc);
        Assert.True(client.Refresh());
        Assert.True(client.Send("storage", "w", "input", new { kind = "submit", value = "snow 雪" }));
        var r = rpc.Requests.Last(); Assert.Equal("storage", r.GetProperty("owner").GetString());
        Assert.Equal("opaque-a", r.GetProperty("module").GetString()); Assert.Equal("1", r.GetProperty("session").GetString());
        Assert.Equal(3UL, r.GetProperty("revision").GetUInt64()); Assert.Equal("lease-a", r.GetProperty("lease").GetString());
        Assert.Equal("w", r.GetProperty("event").GetProperty("window").GetString());
        Assert.Equal("snow 雪", r.GetProperty("event").GetProperty("event").GetProperty("value").GetString());
        Assert.DoesNotContain(rpc.Requests, r => r.TryGetProperty("nonce", out _));
    }
    [Fact] public void ReconnectUsesNewEpochsAndNoStaleLease()
    {
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc); Assert.True(client.Refresh());
        rpc.State["module"] = "opaque-b"; rpc.State["session"] = "2"; rpc.State["lease"] = "lease-b";
        Assert.True(client.Refresh()); Assert.True(client.Send("storage", "", "settings", new { kind = "menu" }));
        Assert.Equal("opaque-b", rpc.Requests.Last().GetProperty("module").GetString());
        Assert.Equal("lease-b", rpc.Requests.Last().GetProperty("lease").GetString());
        client.Release(); Assert.Null(client.State); Assert.False(client.Send("storage", "w", "button", new { kind = "click" }));
        Assert.Equal("release", rpc.Requests.Last().GetProperty("op").GetString());
    }
    [Theory] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void DeclinedReadNeverKeepsActionableCachedState(int error)
    {
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc); Assert.True(client.Refresh());
        rpc.Error = error; Assert.False(client.Refresh()); Assert.Null(client.State); Assert.Equal(error, client.LastError);
        var count = rpc.Requests.Count; Assert.False(client.Send("storage", "w", "button", new { kind = "click" })); Assert.Equal(count, rpc.Requests.Count);
    }
    [Fact] public void InvalidRepliesAndUnsupportedAbiFailClosed()
    {
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc); Assert.True(client.Refresh());
        rpc.BadAbi = true; Assert.False(client.Send("storage", "w", "button", new { kind = "click" })); Assert.Equal(2, client.LastError);
        rpc.InvalidReply = true; Assert.False(client.Refresh()); Assert.Null(client.State);
        rpc.InvalidReply = false; rpc.State["abi"] = 2; Assert.False(client.Refresh()); Assert.Null(client.State);
    }
    [Fact] public void UnavailableWorldHasNoFabricatedEntries()
    {
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc);
        rpc.State["ready"] = false; rpc.State["entries"] = new JsonArray(); rpc.State["windows"] = new JsonArray(); rpc.State["lease"] = null;
        Assert.False(client.Refresh()); Assert.Empty(client.State!.Entries); Assert.Single(rpc.Requests);
    }
    [Theory] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("columns")] [InlineData("tabs")] [InlineData("huge")]
    public void InvalidNodeTreesAreRejectedBeforeDrawing(string problem)
    {
        var r = JsonNode.Parse(Snapshot)!; var nodes = r["windows"]![0]!["window"]!["content"]!.AsArray();
        switch (problem)
        {
            case "unknown": nodes[0]!["type"] = "execute"; break;
            case "duplicate": nodes.Add(nodes[4]!.DeepClone()); break;
            case "columns": nodes[6]!["columns"] = 0; break;
            case "tabs": nodes[8]!["selected"] = 2; break;
            case "huge": nodes[0]!["text"] = new string('x', 4097); break;
        }
        Assert.Throws<FormatException>(() => BridgeSnapshot.Parse(Encoding.UTF8.GetBytes(r.ToJsonString())));
    }
    [Fact] public void LocalEditsPersistOnlyForSameServerValueAndVisibleEpoch()
    {
        var state = BridgeSnapshot.Parse(Encoding.UTF8.GetBytes(Snapshot)); var nodes = new ShroudlinkNodes(); var w = new Widgets { Click = false };
        void Draw(string epoch) { nodes.Reset(epoch); nodes.Draw(w, state.Windows[0].Content, "scope", (_, _) => { }); nodes.EndFrame(); }
        Draw("one"); w.Edit = false; Draw("one"); Assert.Equal("snow 雪", w.Inputs.Last());
        Draw("two"); Assert.Equal("old", w.Inputs.Last());
        w.Edit = true; Draw("two"); w.Edit = false; nodes.Reset("two"); nodes.EndFrame(); Draw("two"); Assert.Equal("old", w.Inputs.Last());
    }
    [Fact] public void LargeNonAsciiReplyWithinTheAddonBoundIsAccepted()
    {
        // About 112 KB of Cyrillic window text: within the addon's 128 KiB state and 256 KiB reply bounds, but about
        // three times as large once re-encoded with \uXXXX escapes.
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc);
        var content = new JsonArray();
        for (var i = 0; i < 28; i++) content.Add(new JsonObject { ["type"] = "text", ["text"] = new string('ж', 2000) });
        rpc.State["windows"]![0]!["window"]!["content"] = content;
        var size = Encoding.UTF8.GetByteCount(rpc.State.ToJsonString(Raw));
        Assert.InRange(size, 112_000, 128 * 1024);
        Assert.True(client.Refresh());
        Assert.Equal(28, client.State!.Windows[0].Content.GetArrayLength());
        Assert.Equal(new string('ж', 2000), client.State.Windows[0].Content[27].GetProperty("text").GetString());
    }
    [Fact] public void ComponentIdsCannotAliasBySlashConcatenation()
    { Assert.NotEqual(ShroudlinkNodes.Key("a/b", "c"), ShroudlinkNodes.Key("a", "b/c")); }
    [Fact] public void BusyReadDoesNotLoseReleaseCredentialsAndBusyReleaseIsRetried()
    {
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc); Assert.True(client.Refresh());
        rpc.Error = 5; Assert.False(client.Refresh()); client.Release();
        Assert.Equal("release", rpc.Requests.Last().GetProperty("op").GetString());
        rpc.Error = 0; client.Release(); Assert.Equal("lease-a", rpc.Requests.Last().GetProperty("lease").GetString());
        var count = rpc.Requests.Count; client.Release(); Assert.Equal(count, rpc.Requests.Count);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void HiddenMenuOrFailedFrameReleasesPresentation(bool failed)
    {
        using var rpc = new Rpc(); using var client = new ShroudlinkClient(rpc); Assert.True(client.Refresh());
        var presentation = new ShroudlinkPresentation(client.Release);
        presentation.BeginFrame(); presentation.Shown(); Assert.True(presentation.EndFrame());
        Assert.DoesNotContain(rpc.Requests, r => r.GetProperty("op").GetString() == "release");
        presentation.BeginFrame(); if (failed) presentation.Shown();
        Assert.False(presentation.EndFrame(!failed));
        Assert.Null(client.State); Assert.Equal("release", rpc.Requests.Last().GetProperty("op").GetString());
    }
    [Theory] [InlineData(0x20U, true)] [InlineData(0x04U, false)] [InlineData(0x120U, false)]
    public void ExportMustBeInHeldModuleAndExecutableCommittedMemory(uint protection, bool expected)
    {
        Assert.Equal(expected, ShroudlinkRpc.OwnedExecutable(0x1000, 0x2000, 0x1100, 0x1000, 0x1000, protection));
        Assert.False(ShroudlinkRpc.OwnedExecutable(0x1000, 0x2000, 0x4000, 0x1000, 0x1000, protection));
        Assert.False(ShroudlinkRpc.OwnedExecutable(0x1000, 0x2000, 0x1100, 0x2000, 0x1000, protection));
        Assert.False(ShroudlinkRpc.OwnedExecutable(0x1000, 0x2000, 0x1100, 0x1000, 0, protection));
    }
    private static byte[] ExportFixture(string name = "shroudlink_client_bridge_v1")
    {
        var image = new byte[4096];
        void U16(int at, ushort v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), v);
        void U32(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), v);
        U16(0, 0x5a4d); U32(60, 128); U32(128, 0x4550); U16(132, 0x8664); U16(148, 240); U16(152, 0x20b); U32(208, 4096);
        U32(260, 1); U32(264, 0x300); U32(268, 0x100);
        U32(0x314, 1); U32(0x318, 1); U32(0x31c, 0x340); U32(0x320, 0x344); U32(0x324, 0x348);
        U32(0x340, 0x800); U32(0x344, 0x360); U16(0x348, 0);
        Encoding.ASCII.GetBytes(name + "\0").CopyTo(image, 0x360); return image;
    }
    [Theory] [InlineData("valid", 0x800U)] [InlineData("forwarded", 0U)] [InlineData("ordinal", 0U)]
    [InlineData("overflow", 0U)] [InlineData("name", 0U)] [InlineData("unreadable", 0U)] [InlineData("size", 0U)]
    public void ExportLookupRejectsForwardersWithoutInvokingAnyResolver(string problem, uint expected)
    {
        var image = ExportFixture();
        void U32(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), v);
        if (problem == "forwarded") U32(0x340, 0x380);
        if (problem == "ordinal") image[0x348] = 1;
        if (problem == "overflow") { U32(0x318, 2); U32(0x320, uint.MaxValue - 1); }
        if (problem == "name") image[0x360] = (byte)'x';
        if (problem == "size") U32(208, 0x300); // the header's SizeOfImage ends before the export directory
        var reads = 0;
        var actual = ShroudlinkRpc.ExportRva((at, n) =>
        {
            reads++; Assert.InRange(n, 0, 144); Assert.True((ulong)at + (uint)n <= (uint)image.Length);
            return problem == "unreadable" && at >= 0x300 ? null : image.AsSpan((int)at, n).ToArray();
        }, out var size);
        Assert.Equal(expected, actual); Assert.InRange(reads, 2, 10);
        Assert.Equal(expected == 0 ? 0U : 4096U, size);
    }
    [Fact] public void ActualAddonExportTableWhenSupplied()
    {
        var path = Environment.GetEnvironmentVariable("KILN_SHROUDLINK_BRIDGE_ADDON");
        if (path is null) Assert.Skip("Opt-in: supply the privately built bridge addon for PE export verification.");
        var file = File.ReadAllBytes(path!);
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(file));
        var h = pe.PEHeaders.PEHeader!; var image = new byte[h.SizeOfImage];
        file.AsSpan(0, h.SizeOfHeaders).CopyTo(image);
        foreach (var section in pe.PEHeaders.SectionHeaders)
            file.AsSpan(section.PointerToRawData, section.SizeOfRawData).CopyTo(image.AsSpan(section.VirtualAddress));
        var rva = ShroudlinkRpc.ExportRva((at, n) => image.AsSpan((int)at, n).ToArray(), out var size);
        Assert.NotEqual(0U, rva); Assert.Equal((uint)image.Length, size);
        Assert.Contains(pe.PEHeaders.SectionHeaders, section => rva >= section.VirtualAddress && rva - section.VirtualAddress < section.VirtualSize &&
            (section.SectionCharacteristics & System.Reflection.PortableExecutable.SectionCharacteristics.MemExecute) != 0);
    }
    [Fact] public void ActualAddonSnapshotWhenSupplied()
    {
        var path = Environment.GetEnvironmentVariable("KILN_SHROUDLINK_BRIDGE_SNAPSHOT");
        if (path is null) Assert.Skip("Opt-in: supply synthetic snapshot captured from actual addon wire harness.");
        var state = BridgeSnapshot.Parse(File.ReadAllBytes(path!));
        Assert.True(state.Ready); Assert.Equal("Storage", Assert.Single(state.Entries).Label);
        var widgets = new Widgets(); var events = new List<object>();
        new ShroudlinkNodes().Draw(widgets, Assert.Single(state.Windows).Content, "actual", (_, e) => events.Add(e));
        Assert.Equal(8, events.Count); Assert.Contains("Item 42 × 3", widgets.Labels);
    }
    // A process seen through kernel-checked reads: counts reads, module-list calls and references (Open).
    private sealed class Space : IModuleSpace
    {
        public readonly Dictionary<nint, byte[]> Images = [];
        public readonly List<nint> Order = [], Opened = [];
        public int Reads;
        public Func<nint, IShroudlinkRpc?> Answer = _ => null;
        public nint[]? Modules() => Order.ToArray();
        public byte[]? Read(nint at, int n)
        {
            Reads++;
            foreach (var (b, image) in Images)
                if (at >= b && at - b + n <= image.Length) return image.AsSpan((int)(at - b), n).ToArray();
            return null;
        }
        public IShroudlinkRpc? Open(nint module, uint rva) { Assert.Equal(0x800U, rva); Opened.Add(module); return Answer(module); }
        public nint Load(string export) { var b = (nint)((Order.Count + 1) * 0x10000); Images[b] = ExportFixture(export); Order.Add(b); return b; }
    }
    [Fact] public void FinderTakesNoReferenceWithoutAMatchAndRereadsOnlyAChangedModuleList()
    {
        var space = new Space(); var finder = new ShroudlinkFinder(space);
        for (var i = 0; i < 300; i++) space.Load($"some_other_export_{i:000}");
        Assert.Null(finder.Find());
        Assert.Empty(space.Opened);
        Assert.InRange(space.Reads, 300 * 3, 300 * 6); // header, directory and one name each; no per-module list walk
        var reads = space.Reads;
        for (var i = 0; i < 10; i++) Assert.Null(finder.Find());
        Assert.Equal(reads, space.Reads); Assert.Empty(space.Opened);
        // The cooperating copy loads: one more scan, and a reference for that module only.
        using var rpc = new Rpc(); var bridge = space.Load("shroudlink_client_bridge_v1"); space.Answer = m => m == bridge ? rpc : null;
        Assert.Same(rpc, finder.Find());
        Assert.Equal([bridge], space.Opened);
    }
    [Fact] public void FinderAsksADecliningCopyAgainWithoutReadingTheOthers()
    {
        var space = new Space(); var finder = new ShroudlinkFinder(space);
        for (var i = 0; i < 50; i++) space.Load($"some_other_export_{i:000}");
        var bridge = space.Load("shroudlink_client_bridge_v1");
        Assert.Null(finder.Find()); Assert.Equal([bridge], space.Opened); // not started yet, replaced or busy
        var reads = space.Reads;
        Assert.Null(finder.Find()); Assert.Equal([bridge, bridge], space.Opened); Assert.Equal(reads, space.Reads);
        using var rpc = new Rpc(); space.Answer = _ => rpc;
        Assert.Same(rpc, finder.Find()); Assert.Equal(reads, space.Reads);
        space.Order.RemoveAt(0); space.Answer = _ => null; // a module unloads: the list changed
        Assert.Null(finder.Find()); Assert.True(space.Reads > reads);
    }
    [Fact] public void DiscoveryNeverScansOnTheCallingThreadAndRestsASecondAfterEachScan()
    {
        using var gate = new ManualResetEventSlim(); using var rpc = new Rpc();
        var calls = 0; var thread = Environment.CurrentManagedThreadId; var scanThread = thread;
        var discovery = new ShroudlinkDiscovery(() =>
        {
            scanThread = Environment.CurrentManagedThreadId;
            var call = Interlocked.Increment(ref calls);
            gate.Wait(TimeSpan.FromSeconds(5));
            return call >= 2 ? rpc : null;
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(discovery.Poll(0));
        Assert.True(watch.ElapsedMilliseconds < 1000, "the calling (render) thread waited for the scan");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 1, 5000));
        Assert.NotEqual(thread, scanThread);
        Assert.Null(discovery.Poll(10)); Assert.Equal(1, Volatile.Read(ref calls)); // one scan at a time
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => discovery.Poll(20) is null && !discovery.Scanning, 5000)); // the miss is picked up
        Assert.Null(discovery.Poll(1019)); Assert.False(discovery.Scanning); Assert.Equal(1, calls);
        Assert.Null(discovery.Poll(1020)); Assert.True(discovery.Scanning);
        IShroudlinkRpc? found = null;
        Assert.True(SpinWait.SpinUntil(() => (found = discovery.Poll(1030)) is not null, 5000));
        Assert.Same(rpc, found); Assert.Equal(2, calls);
    }
    [Fact] public void NoWindowsExportSearchOnLinux()
    { if (!OperatingSystem.IsWindows()) Assert.Null(ShroudlinkRpc.Find()); }
}
