// SPDX-License-Identifier: MIT
// identity.players with no reference to Kiln.Players. Deploy it as the lab's only mod: then nothing but
// ServiceTick.AutoStart loads Kiln.Players, so PlayerRef.Of throwing KilnUnavailableException(identity.players) in
// OnLoad and answering from the first tick on proves the AutoStart entry. Output: "players-autostart-test: PASS ..." /
// "FAIL ..." lines in kiln/logs/kiln.log, then a summary.
using Kiln;

namespace PlayersAutostartTest;

public sealed class PlayersAutostartTestMod : Mod
{
    private int _pass, _fail;
    private string _onLoad = "";
    private bool _done;

    public override void OnLoad()
    {
        _onLoad = TryOf();
        Log.Info($"players-autostart-test: OnLoad: PlayerRef.Of(1) -> {_onLoad}; Kiln.Players loaded: {Loaded()}");
        Context.Tick.Pre += OnPre;
    }

    private static bool Loaded() => AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Kiln.Players");

    private static string TryOf()
    {
        try
        {
            var r = PlayerRef.Of(new EntityId(1));
            return r.IsNone ? "none" : r.ToString();
        }
        catch (KilnUnavailableException e)
        {
            return $"unavailable {e.Capability}";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private void OnPre(in TickInfo tick)
    {
        if (_done) return;
        _done = true;
        var first = TryOf();
        Check(_onLoad == "unavailable identity.players", "OnLoad: PlayerRef.Of throws KilnUnavailableException(identity.players)", _onLoad);
        Check(first == "none", "first tick: PlayerRef.Of answers (entity 1: none on an empty server)", first);
        Check(Loaded(), "first tick: Kiln.Players was loaded by AutoStart");
        string resolve;
        try { resolve = new PlayerRef(76561190000000001, Guid.Empty, EntityRef.None).Resolve().ToString(); }
        catch (Exception e) { resolve = $"{e.GetType().Name}: {e.Message}"; }
        Check(resolve == "76561190000000001", "first tick: Resolve of an absent player is offline", resolve);
        Log.Info($"players-autostart-test: {_pass} PASS, {_fail} FAIL");
    }

    private void Check(bool ok, string what, string why = "")
    {
        if (ok) { _pass++; Log.Info($"players-autostart-test: PASS {what}"); }
        else { _fail++; Log.Error($"players-autostart-test: FAIL {what}: {why}"); }
    }
}
