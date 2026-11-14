// SPDX-License-Identifier: MIT
// The guided check 15a-hold-kiln's fixture (mods/tests/client-pass/Dev28NetAdmission.cs). On a test server it holds each
// joining Kiln client through Kiln.Network's admission controller for holdSeconds (kiln/config/admission-hold-test.json,
// read at each join; default 8), then lets it in. With 25 or more the hold's deadline (always 25 s) passes first and the
// client is refused with this mod's text. Every outcome is written to kiln/data/admission-hold-test/joins.json, which the
// check reads on the server.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace AdmissionHoldTest;

public sealed class AdmissionHoldTestMod : Mod
{
    private const string Me = "admission-hold-test";
    internal const string RefusalText = "The test server held you longer than 25 s (admission-hold-test).";

    private sealed class Join(NetPeer peer, AdmissionHold hold, double seconds)
    {
        public readonly NetPeer Peer = peer;
        public readonly AdmissionHold Hold = hold;
        public readonly double Seconds = seconds;
        public readonly DateTime Since = DateTime.UtcNow;
        public bool EndedByUs;
    }

    private readonly List<Join> _joins = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private IDisposable? _decider;

    public override void OnLoad()
    {
        try
        {
            _decider = NetAdmission.OnJoin(Me, Decide);
            Log.Info($"{Me}: holds each joining Kiln client for {HoldSeconds():0.#} s (kiln/config/{Me}.json)");
        }
        catch (KilnUnavailableException e)
        {
            Log.Warn($"{Me}: this server cannot hold Kiln clients ({e.Capability}: {e.Reason}): the check 15a-hold-kiln cannot run");
            return;
        }
        Context.Tick.Pre += OnTick;
    }

    public override void OnUnload() => _decider?.Dispose();

    private double HoldSeconds()
    {
        try
        {
            var file = Path.Combine(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "..")), "config", Me + ".json");
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["holdSeconds"] is JsonValue v && v.TryGetValue<double>(out var s) && s > 0)
                return s;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            Log.Warn($"{Me}: config unreadable ({e.Message}): 8 s");
        }
        return 8;
    }

    private AdmissionHold? Decide(NetPeer peer)
    {
        var seconds = HoldSeconds();
        // The deadline is always the longest one: under 25 s this mod ends the hold itself (a deadline of holdSeconds
        // would fall due in the same step and refuse the join), from 25 s the deadline refuses it.
        var hold = NetAdmission.HoldPeer(peer, Me, RefusalText, NetAdmission.MaxDeadline);
        if (hold.Ended) return null;
        _joins.Add(new Join(peer, hold, seconds));
        Log.Info($"{Me}: holding {peer.Name} (0x{peer.Id:x}) for {seconds:0.#} s");
        return hold;
    }

    private void OnTick(in TickInfo tick)
    {
        var now = DateTime.UtcNow;
        foreach (var j in _joins.ToArray())
        {
            var held = (now - j.Since).TotalMilliseconds;
            if (!j.Hold.Ended && j.Seconds < NetAdmission.MaxDeadline.TotalSeconds && held >= j.Seconds * 1000)
            {
                j.EndedByUs = true;
                j.Hold.End();
            }
            if (!j.Hold.Ended) continue;
            _joins.Remove(j);
            var outcome = j.EndedByUs ? "let-in" : j.Peer.State == NetPeerState.Rejected ? "refused" : "left";
            Log.Info($"{Me}: {j.Peer.Name} (0x{j.Peer.Id:x}) {outcome} after {held:0} ms held");
            Record(j.Peer, outcome, held);
        }
    }

    private void Record(NetPeer peer, string outcome, double heldMs)
    {
        try
        {
            Directory.CreateDirectory(Context.DataDirectory);
            var file = Path.Combine(Context.DataDirectory, "joins.json");
            var list = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonArray ?? new JsonArray() : new JsonArray();
            list.Add(new JsonObject
            {
                ["name"] = peer.Name, ["peer"] = $"0x{peer.Id:x}", ["outcome"] = outcome, ["heldMs"] = Math.Round(heldMs),
                ["session"] = _session,
                ["utc"] = DateTime.UtcNow.ToString("O"),
            });
            while (list.Count > 50) list.RemoveAt(0);
            File.WriteAllText(file, list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException e)
        {
            Log.Warn($"{Me}: could not record the join: {e.Message}");
        }
    }
}
