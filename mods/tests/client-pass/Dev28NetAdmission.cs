// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Testing;

namespace ClientPass;

// 15a-net-admission: Kiln.Network's admission controller holds a real Kiln client before it enters the world (gate mode
// "hold all"). Its fixture is the server mod admission-hold-test (mods/tests/admission-hold-test). Nothing is held by the
// client gate: labs need the hold. Orders 6700-6799.
public sealed partial class ClientPassMod
{
    private const string AdmissionArea = "Admission";

    // The fixture's record of joins (server side: a joined check runs on the server).
    private JsonArray AdmissionJoins()
    {
        try
        {
            var file = Path.Combine(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..")), "admission-hold-test", "joins.json");
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonArray ?? [] : [];
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    [ClientCheck("15a-hold-kiln", "A Kiln client held at admission, then let in or refused", Area = AdmissionArea, Since = Dev28,
        Needs = ClientCheckNeeds.Joined, Order = 6700)]
    public async Task AdmissionHoldKiln(CheckContext c)
    {
        if (!await Need(c, "admission-hold-test")) return;
        c.Instruct("The server holds every Kiln client that joins for holdSeconds (kiln/config/admission-hold-test.json on the " +
                   "server, read at each join) before it enters the world. Do this before you continue: 1) set holdSeconds to " +
                   "30 and join: after about 25 s on the loading screen the game should refuse you, with the text \"" +
                   "The test server held you longer than 25 s (admission-hold-test).\" 2) set holdSeconds to 8 and join again: " +
                   "the loading screen should take about 8 s longer than usual, then you are in the world as normal. Press " +
                   "continue when you are in (this is where you are now if you did both).");
        await c.Verdict();
        var name = Players.All.FirstOrDefault(p => p.Entity == c.Player)?.Name ?? "";
        var mine = AdmissionJoins().OfType<JsonObject>().Where(j => (string?)j["name"] == name).ToList();
        var last = mine.LastOrDefault();
        var refused = mine.LastOrDefault(j => (string?)j["outcome"] == "refused");
        c.Expect(last is not null && (string?)last["outcome"] == "let-in" && (double?)last["heldMs"] >= 7000,
            $"the server held this join at least 7 s, then let it in ({last?.ToJsonString() ?? "no join recorded for " + name})");
        c.Expect(refused is not null && (double?)refused["heldMs"] >= 24000,
            $"an earlier join of this player was held about 25 s and refused ({refused?.ToJsonString() ?? "none recorded"})");
        c.Instruct("Did the long hold end with the game showing that refusal text, and did the 8 s hold end with you in the " +
                   "world as normal (your character, the world around you, nothing missing)?");
        await c.Verdict();
    }
}
