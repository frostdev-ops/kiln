// SPDX-License-Identifier: MIT
// Server modpack checks (docs/kiln/network.md "Server modpacks"): REJECT/WELCOME trailing properties, the 0.1 decoders
// still reading new frames, required[] truncation under the 7 KB frame budget, the client's last-server.json writer.
// Runs at OnLoad (codec level, no client needed); tools/nettest checks the same over the lab transport.
using System.Text.Json.Nodes;
using Kiln;

namespace NetTest;

internal static class AdvertChecks
{
    public static void Run(Action<string, bool, string> check, string dataDir)
    {
        var cfg = Network.Config with
        {
            Modpack = new NetModpack("https://example.org/pack.zip", new string('a', 64), "Test pack", "3", 1234),
            ServerName = "Net test", ServerId = "srv-nettest",
        };
        // a small required list
        var few = new List<NetRequiredMod>
        {
            new("isle-icons", "1.1.0", "kdh1:" + new string('b', 64)),
            new("fps-unlock", "2.0.0", "kdh1:" + new string('c', 64), "native"),
        };
        var props = NetAdvertise.Properties(cfg, 200, few, true, out var truncated);
        var reject = Wire.EncodeReject(new Wire.Reject("needs mods", ["isle-icons 1.1.0 (missing)"]) { Properties = props });
        var v1 = Wire.DecodeRejectV1(reject);
        check("advert.reject-old-decoder", v1.Reason == "needs mods" && v1.Details.SequenceEqual(["isle-icons 1.1.0 (missing)"]),
            $"{reject.Length} bytes; old decoder: '{v1.Reason}' [{string.Join(", ", v1.Details)}]");
        var j = Wire.DecodeReject(reject);
        var adv = NetAdvertise.Parse(j.Properties, null);
        check("advert.reject-props", !truncated && adv.ServerId == "srv-nettest" && adv.ServerName == "Net test" &&
                                      adv.Modpack?.Url == "https://example.org/pack.zip" && adv.Modpack.Size == 1234 &&
                                      adv.Required.Count == 2 && adv.Required[1].Kind == "native" && adv.RequiredCount == 2 && !adv.Truncated,
            string.Join(", ", j.Properties.Select(kv => kv.Key + "=" + (kv.Value.Length > 40 ? kv.Value[..40] + "…" : kv.Value))));
        // the game transport pads frames with zero bytes to a multiple of 4
        var old = Wire.EncodeReject(new Wire.Reject("old", ["a"]));
        var padded = old.Concat(new byte[(4 - old.Length % 4) % 4 + 4]).ToArray();
        var jp = Wire.DecodeReject(padded);
        check("advert.reject-padded-old-server", jp.Reason == "old" && jp.Properties.Count == 0, $"{padded.Length} bytes");

        // many required mods: truncated so the frame stays <= 7 KB, every value within the 0.1 decoder's limits
        var many = Enumerable.Range(0, 300)
            .Select(i => new NetRequiredMod($"some-long-mod-name-{i:D3}", $"1.{i}.0", "kdh1:" + Convert.ToHexStringLower(BitConverter.GetBytes(i)).PadRight(64, '0')))
            .ToList();
        var welcome = new Wire.Welcome(Wire.Protocol, "0.2.0-dev", 1, "refuse", 262144,
            [new Wire.ModEntry("net-test", "0.1.0", ModSide.Both)], [new Wire.ChannelEntry(1, "net-test:echo", "1", true)], ["w"],
            new Dictionary<string, string> { ["net-test.hello"] = "x" });
        var baseLen = Wire.EncodeWelcome(welcome).Length;
        var big = NetAdvertise.Properties(cfg, baseLen, many, true, out var cut);
        var merged = new Dictionary<string, string>(welcome.Properties);
        foreach (var (k, v) in big) merged[k] = v;
        var frame = Wire.EncodeWelcome(welcome with { Properties = merged });
        var back = Wire.DecodeWelcome(frame);  // the 0.1 decoder is this one: props <= 256, key <= 128, value <= 4096 bytes
        var parsed = NetAdvertise.Parse(back.Properties, back.KilnVersion);
        var maxValue = merged.Values.Max(v => System.Text.Encoding.UTF8.GetByteCount(v));
        check("advert.welcome-truncated", cut && frame.Length <= NetAdvertise.FrameBudget && parsed.Truncated && parsed.RequiredCount == 300 &&
                                          parsed.Required.Count > 20 && parsed.Required.Count + int.Parse(back.Properties[NetAdvertise.KeyMore]) == 300 &&
                                          maxValue <= 4096 && back.Properties.Count <= 256 && parsed.Required[0].Id == "some-long-mod-name-000",
            $"frame {frame.Length} bytes, {parsed.Required.Count} of 300 sent in {back.Properties.Keys.Count(k => k.StartsWith(NetAdvertise.KeyRequired + ".") || k == NetAdvertise.KeyRequired) - 2} chunk(s), " +
            $"more {back.Properties.GetValueOrDefault(NetAdvertise.KeyMore)}, largest value {maxValue} bytes");
        var none = NetAdvertise.Parse(new Dictionary<string, string> { ["net-test.hello"] = "x" }, "0.1.0");
        check("advert.absent", !none.Present && none.Required.Count == 0 && none.Modpack is null && none.Kiln == "0.1.0", "");

        // a refusal under the default policy names only the mods that state clients: "mod"; the required list (what a
        // modpack holds) also has a side-both mod without clients. The client's missing[] follows the refusal.
        var required = new List<NetRequiredMod>
        {
            new("net-test", "0.1.0", "kdh1:" + new string('d', 64)), new("npcsample", "0.1.0", "kdh1:" + new string('e', 64)),
            new("quests-sample", "2.0.0", "kdh1:" + new string('f', 64)),
        };
        var refusal = Wire.EncodeReject(new Wire.Reject("This server requires mods you do not have: npcsample 0.1.0 (missing), quests-sample 2.0.0 (missing)",
            ["npcsample 0.1.0 (missing)", "quests-sample 2.0.0 (missing)"]) { Properties = NetAdvertise.Properties(cfg, 200, required, true, out _) });
        var rj = Wire.DecodeReject(refusal.Concat(new byte[(4 - refusal.Length % 4) % 4]).ToArray());
        var radv = NetAdvertise.Parse(rj.Properties, null);
        var missing = NetRefusal.Missing(rj.Details, radv.Required.Select(r => r.Id).ToList());
        check("lastserver.refusal-missing", radv.Required.Count == 3 && missing.SequenceEqual(["npcsample", "quests-sample"]) &&
                                            NetRefusal.Missing([], radv.Required.Select(r => r.Id).ToList()).Count == 3,
            $"required [{string.Join(", ", radv.Required.Select(r => r.Id))}], missing [{string.Join(", ", missing)}]");

        // the client's last-server.json: history newest first, at most 20, entries without their own history
        var path = Path.Combine(dataDir, "last-server-test.json");
        if (File.Exists(path)) File.Delete(path);
        for (var i = 0; i < 23; i++)
        {
            var e = LastServer.Build(i % 2 == 0 ? "rejected" : "welcome", i % 2 == 0 ? "needs mods" : null, adv, "0.2.0-dev",
                i % 2 == 0 ? ["isle-icons"] : [], DateTime.UtcNow.AddSeconds(i));
            e["n"] = i;
            LastServer.Write(path, e);
        }
        var doc = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var hist = doc["history"]!.AsArray();
        check("lastserver.history", doc["n"]!.GetValue<int>() == 22 && hist.Count == 20 && hist[0]!["n"]!.GetValue<int>() == 21 &&
                                    hist[19]!["n"]!.GetValue<int>() == 2 && hist.All(h => h!["history"] is null) &&
                                    doc["outcome"]!.GetValue<string>() == "rejected" && doc["missing"]!.AsArray().Count == 1 &&
                                    doc["modpack"]!["sha256"]!.GetValue<string>() == new string('a', 64) &&
                                    doc["required"]!.AsArray()[0]!["kind"]!.GetValue<string>() == "kiln" &&
                                    doc["server"]!["id"]!.GetValue<string>() == "srv-nettest",
            $"{hist.Count} history entries, newest {hist[0]!["n"]}, oldest {hist[^1]!["n"]}");
    }
}
