// SPDX-License-Identifier: MIT
// Lab test, second mod: two mods and one resource. types-test holds a field of the Bronze Sword's ItemInfo from step 43 to
// step 150. This mod (1) asks for that field and gets a Conflict that names both mods, (2) changes another field of the same
// resource while types-test's change stays in effect (the game gets both), (3) takes the first field once types-test gave
// it back, and (4) gives everything back.
using Kiln;

namespace TypesPeerTest;

public sealed class PeerMod : Mod
{
    private static readonly Guid BronzeSword = Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95");
    private const string ItemInfo = "keen::ItemInfo";
    private const string FloatField = "damageSetup.dmgMod";
    private int _state, _pass, _fail;
    private string _field = "";
    private long _theirValue;
    private ulong _wait;

    private void Check(string name, bool ok, object? detail = null)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : ": " + detail)}");
    }

    public override void OnLoad() => Context.Tick.Post += OnPost;

    private void OnPost(in TickInfo tick)
    {
        if (tick.Step % 5 != 0 || _state >= 3) return;
        try
        {
            var sword = ReflectedResources.Get(BronzeSword, ItemInfo);
            if (sword == null) return;
            if (_state == 0)
            {
                // wait for types-test's claim: "maxStackSize = 77"
                var theirs = Overrides.Active.FirstOrDefault(i => i.Mod == "types-test" && i.Target.Contains("keen::ItemInfo") && !i.Detail.StartsWith(FloatField));
                if (theirs == null) return;
                var parts = theirs.Detail.Split(" = ");
                _field = parts[0];
                _theirValue = long.Parse(parts[1]);
                Log.Info($"types-peer-test: types-test holds {_field} = {_theirValue}; asking for it");

                var refused = sword.Set(_field, 5);
                Check("peer: claiming a field another mod holds is refused", refused.Status == OverrideStatus.Conflict && refused.HeldBy == "types-test", refused.Message);
                Check("peer: the refusal names both mods", refused.Message.Contains("types-test") && refused.Message.Contains("types-peer-test"));
                Check("peer: the holder's value is still what the game gets", sword.Get<long>(_field) == _theirValue && sword.HeldBy(_field) == "types-test");
                _state = 1;
                _wait = tick.Step;
            }
            else if (_state == 1)
            {
                // a different field of the same resource, once types-test gave its own back (step 50)
                if (sword.HeldBy(FloatField) != null) return;
                var original = sword.Get<double>(FloatField);
                var ok = sword.Set(FloatField, 2.0);
                Check("peer: another field of the same resource is applied", ok.Ok && sword.Get<double>(FloatField) == 2.0, ok.Message);
                Check("peer: the other mod's field is still in effect (both changes compose)", sword.Get<long>(_field) == _theirValue && sword.HeldBy(_field) == "types-test");
                Check("peer: Overrides.Active names both mods", Overrides.Active.Select(i => i.Mod).Distinct().Count() >= 2, string.Join("; ", Overrides.Active.Select(i => $"{i.Mod}: {i.Detail}")));
                Log.Info($"types-peer-test: {FloatField} {original} -> {sword[FloatField]}");
                _state = 2;
            }
            else if (_state == 2)
            {
                // types-test gives the field back at step 150: then the first field is free
                if (Overrides.Active.Any(i => i.Mod == "types-test")) return;
                var mine = sword.Set(_field, 5);
                Check("peer: the field is free after its holder gave it back", mine.Ok && sword.Get<long>(_field) == 5 && sword.HeldBy(_field) == "types-peer-test", mine.Message);
                var back = sword.RestoreAll();
                Check("peer: RestoreAll gives the original values back", back == 2 && sword.HeldBy(_field) == null && sword.Get<double>(FloatField) != 2.0,
                      $"{back} fields; {_field} = {sword[_field]}, {FloatField} = {sword[FloatField]}");
                Log.Info($"types-peer-test: done: {_pass} PASS, {_fail} FAIL");
                _state = 3;
            }
        }
        catch (Exception e)
        {
            Check($"peer step {tick.Step}", false, e);
            _state = 3;
        }
    }
}
