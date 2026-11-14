// SPDX-License-Identifier: MIT
// Lab test of block kinds, the consumer (chapter 16d §6). Public SDK only. Three loads in one server run: the first does the
// checks and reloads itself, the second (the consumer alone came back) reloads the provider, the third ends the test. The
// phase is a file in the mod's data folder, so the lab must start with that folder empty.
using Kiln;
using KindsProviderTest;

namespace KindsConsumerTest;

public sealed class KindsConsumerTestMod : Mod
{
    private const string Provider = "kinds-provider-test";
    private int _pass, _fail, _changes;
    private bool? _foreign;
    private BlockHandle? _camp;

    private void Info(string s) => Log.Info($"kinds-consumer-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"kinds-consumer-test: PASS {what}"); }
        else { _fail++; Log.Error($"kinds-consumer-test: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    /// <summary>Steps until <paramref name="done"/> holds, at most <paramref name="max"/>. A plan with parts at content ready (a lantern's item rules)
    /// is PendingContent until they are done: one pass and the check of its definitions, about 150 steps after content is ready.</summary>
    private async Task Until(Func<bool> done, int max = 600)
    {
        for (var i = 0; i < max && !done(); i++) await Steps(1);
    }

    private static bool Waiting(BlockApplyResult r) => r.State == BlockApplyState.PendingContent;

    /// <summary>The game's items this mod's blocks made: other test mods of the lab add items of their own meanwhile.</summary>
    private int OwnItems => Content.Items.Count(i => i.Name.StartsWith(Context.Info.Id + ".", StringComparison.Ordinal));

    private string PhaseFile
    {
        get
        {
            Directory.CreateDirectory(Context.DataDirectory);
            return Path.Combine(Context.DataDirectory, "phase.txt");
        }
    }
    private string[] Phase => File.Exists(PhaseFile) ? File.ReadAllLines(PhaseFile) : [];
    private static LanternBlock Camp => new() { Name = "Camp lantern", Seconds = 600 };
    private static string Canon(BlockPlan p) => string.Join("\n", p.Products.Select(x => $"{x.Key}|{x.Kind}|{x.CanonicalJson}"));

    public override void OnLoad()
    {
        if (Mods.Side != ModSide.Server) return;   // a dedicated server
        Events.On<BlockApplicationChangedEvent>(e =>
        {
            if (e.ModId != Context.Info.Id) return;
            _changes++;
            Info($"application of {e.Name}: {e.Before.State} -> {e.After.State} generation {e.After.Generation} {e.After.Reason}");
        });
        Context.Messages.On("foreign-result", m => _foreign = m.Payload is true);
        try { _camp = Blocks.Define("camp", Camp); }
        catch (Exception e)
        {
            Check(false, $"Blocks.Define of a lantern threw: {e.GetType().Name}: {e.Message}");
            Log.Info($"kinds-consumer-test: DONE pass={_pass} fail={_fail}");
            return;
        }
        Check(Capabilities.Is("blocks.kinds") && Capabilities.Is("blocks.compile") && Capabilities.Is("blocks.apply") && Capabilities.Is("blocks.apply.item"),
            $"the rows read available after the first use (blocks.apply: {Capabilities.Why("blocks.apply")})");
        if (Mods.Get("client-pass") != null)
        {
            Info("the guided client pass is in this profile: the lanterns are defined, the lab sequence (with its reloads) is not run");
            return;
        }
        _ = Run();
    }

    private async Task Run()
    {
        var phase = Phase;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Content.Status() is not { Ready: true } && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            await Until(() => !Waiting(Blocks.Application(_camp!)) && Blocks.Find("long-night") is { } night && !Waiting(Blocks.Application(night)));
            if (phase.Length == 0) await First();
            else if (phase[0] == "1") await AfterOwnReload(phase);
            else await AfterProviderReload(phase);
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
            Log.Info($"kinds-consumer-test: DONE pass={_pass} fail={_fail}");
        }
    }

    /// <summary>The lantern from code and the one from blocks/lanterns.json are in the game: item and recipe.</summary>
    private (BlockApplyResult Camp, BlockApplyResult Night) Lanterns(string when)
    {
        var camp = Blocks.Application(_camp!);
        var nightHandle = Blocks.Find("long-night");
        var night = nightHandle != null ? Blocks.Application(nightHandle) : new BlockApplyResult { Reason = "no block long-night" };
        Info($"{when}: camp {camp.State} {camp.GameName} {camp.Guid} id {camp.Id} generation {camp.Generation} {camp.Reason}; long-night {night.State} {night.GameName} {night.Guid} {night.Reason}");
        var item = camp.Ok ? Content.Items.Get(camp.GameName) : null;
        var nightItem = night.Ok ? Content.Items.Get(night.GameName) : null;
        Check(camp.Ok && camp.State == BlockApplyState.Applied && item != null && night.Ok && nightItem != null,
            $"external-kind-lantern ({when}): the C# lantern is item {item?.Id} \"{camp.GameName}\", the JSON lantern is item {nightItem?.Id} \"{night.GameName}\"");
        var recipes = Content.Recipes.Count(r => r.Name.StartsWith(camp.GameName, StringComparison.Ordinal));
        Check(camp.Ok && recipes == 1, $"external-kind-lantern ({when}): one recipe for the camp lantern (found {recipes})");
        return (camp, night);
    }

    private async Task First()
    {
        var (camp, night) = Lanterns("first load");

        // ---- frozen-plan ----
        var compilesBefore = LanternProbe.Compiles;
        var block = new LanternBlock { Name = "Probe lantern", Seconds = 120, Extras = { ["Material_T1_Wood_Logs"] = 1 } };
        var p1 = Blocks.Compile("probe", block);
        var (hash, canon) = (p1.InputHash, Canon(p1));
        block.Extras["Material_T1_Stone"] = 5;   // the author's list changes after the compile
        var p2 = Blocks.Compile("probe", new LanternBlock { Name = "Probe lantern", Seconds = 120, Extras = { ["Material_T1_Wood_Logs"] = 1 } });
        Check(p1.InputHash == hash && Canon(p1) == canon && !canon.Contains("Material_T1_Stone"), "frozen-plan: changing the author's record after Compile changes nothing in the plan");
        Check(p2.InputHash == hash && Canon(p2) == canon, $"frozen-plan: the same record compiles to the same plan (hash {hash})");
        var p3 = Blocks.Compile("probe", block);
        Check(p3.InputHash != hash, "frozen-plan: the changed record compiles to another plan");
        Check(LanternProbe.Compiles == compilesBefore + 3, $"frozen-plan: three compiles ran the provider three times ({LanternProbe.Compiles - compilesBefore})");
        Check(p1.KindId == "kinds-provider-test:lantern" && p1.ConsumerModId == Context.Info.Id && p1.PrimaryKey == "light" && p1.Products.Count == 1 && p1.Products[0].Kind == BuiltInBlockProductKind.Item,
            $"frozen-plan: kind {p1.KindId}, consumer {p1.ConsumerModId}, primary {p1.PrimaryKey}, {p1.Products.Count} product(s)");

        // the JSON lantern and the same lantern from C#: applying the C# plan under the applied JSON name answers the JSON
        // application only when the two plans hash the same (another plan for an applied name is NameAlreadyApplied)
        var twin = Blocks.Compile("long-night", new LanternBlock { Name = "Long-night lantern", Seconds = 1800, Twigs = 6 });
        var changes = _changes;
        var twinApplied = Blocks.Apply(twin);
        Check(twinApplied.Ok && twinApplied.Generation == night.Generation && twinApplied.Guid == night.Guid,
            $"frozen-plan: the C# plan and the JSON plan of long-night are the same bytes ({twinApplied.State} generation {twinApplied.Generation} vs {night.Generation} {twinApplied.Reason})");
        var other = Blocks.Apply(Blocks.Compile("long-night", new LanternBlock { Name = "Long-night lantern", Seconds = 1801, Twigs = 6 }));
        Check(!other.Ok && other.State == BlockApplyState.Refused && other.Reason.StartsWith("NameAlreadyApplied", StringComparison.Ordinal),
            $"frozen-plan: another plan for an applied name is refused ({other.State} {other.Reason})");

        // ---- replay-no-layer ----
        var first = Blocks.Apply(p1);
        await Until(() => !Waiting(Blocks.Application(p1)));
        await Steps(3);   // its change event arrives on a later tick
        first = Blocks.Application(p1);
        var itemsBefore = OwnItems;
        var changesBefore = _changes;
        var compiles = LanternProbe.Compiles;
        var again = Blocks.Apply(p1);
        var again2 = Blocks.Apply(p2);   // the same bytes from another compile
        await Steps(3);
        Check(first.Ok && again.Ok && again2.Ok && again.Generation == first.Generation && again2.Generation == first.Generation && again.Guid == first.Guid,
            $"replay-no-layer: the same plan applied again answers the same application (generation {first.Generation}, {again.Generation}, {again2.Generation})");
        Check(OwnItems == itemsBefore && _changes == changesBefore && LanternProbe.Compiles == compiles,
            $"replay-no-layer: no new item ({itemsBefore} -> {OwnItems}), no event ({_changes - changesBefore}), no provider call ({LanternProbe.Compiles - compiles})");
        Check(_changes >= changes, "replay-no-layer: events only for real changes");

        // ---- consumer-binding ----
        Mods.Send(Provider, "apply-foreign", Blocks.Compile("foreign", new LanternBlock { Name = "Foreign lantern" }));
        for (var i = 0; i < 120 && _foreign == null; i++) await Steps(1);
        Check(_foreign == true, "consumer-binding: the provider could not apply this mod's plan (its own PASS line says how)");
        Check(Blocks.Find("foreign") == null && Content.Items.All(i => !i.Name.Contains("kind-foreign", StringComparison.Ordinal)), "consumer-binding: nothing of the foreign attempt is in the game");

        // ---- refusal-no-publish (a plan refused before it registers: the kind asks for a capability that is off) ----
        var needy = Blocks.Compile("needy", new NeedyBlock { Name = "Never" });
        itemsBefore = OwnItems;
        var refused = Blocks.Apply(needy);
        await Steps(3);
        refused = Blocks.Application(needy);
        Check(!refused.Ok && refused.State == BlockApplyState.Refused && refused.Reason.StartsWith("RequiresOff", StringComparison.Ordinal), $"refusal-no-publish: the plan is refused ({refused.State} {refused.Reason})");
        Check(Blocks.Find("needy") == null && OwnItems == itemsBefore && Content.Items.All(i => !i.Name.Contains("kind-needy", StringComparison.Ordinal)),
            "refusal-no-publish: no block handle, no item, nothing published of the refused plan");

        // ---- provider-retirement-no-callback ----
        var unapplied = Blocks.Compile("late", new LanternBlock { Name = "Late lantern" });
        Mods.Send(Provider, "retire");
        await Steps(5);
        compiles = LanternProbe.Compiles;
        var kept = Blocks.Application(_camp!);
        Check(kept.Ok && kept.Generation == camp.Generation && Content.Items.Get(camp.GameName) != null, $"provider-retirement-no-callback: the applied lantern stays ({kept.State} generation {kept.Generation})");
        var compileSaid = "no exception";
        try { Blocks.Compile("after", new LanternBlock { Name = "After" }); }
        catch (Exception e) { compileSaid = $"{e.GetType().Name}: {e.Message}"; }
        Check(compileSaid != "no exception", $"provider-retirement-no-callback: a new compile is refused ({compileSaid})");
        var lateSaid = "";
        try
        {
            var late = Blocks.Apply(unapplied);
            await Steps(3);
            late = Blocks.Application(unapplied);
            lateSaid = $"{late.State} {late.Reason}";
            Check(!late.Ok && late.State == BlockApplyState.Refused, $"provider-retirement-no-callback: a plan compiled before the retirement is not applied after it ({lateSaid})");
        }
        catch (Exception e) when (e is ArgumentException or BlockError or KilnUnavailableException)
        {
            Check(true, $"provider-retirement-no-callback: a plan compiled before the retirement is not applied after it ({e.GetType().Name}: {e.Message})");
        }
        Check(Blocks.Find("late") == null, "provider-retirement-no-callback: no block of the unapplied plan");
        var replay = Blocks.Apply(p1);
        Check(replay.Ok && replay.Generation == first.Generation, $"provider-retirement-no-callback: an applied plan still answers ({replay.State} generation {replay.Generation})");
        Check(LanternProbe.Compiles == compiles, $"provider-retirement-no-callback: the provider's code did not run after its kinds were taken away ({LanternProbe.Compiles - compiles} call(s))");
        Mods.Send(Provider, "restore");
        await Steps(5);
        var afterPlan = Blocks.Compile("after", new LanternBlock { Name = "After" });
        var back = Blocks.Apply(afterPlan);
        await Until(() => !Waiting(Blocks.Application(afterPlan)));
        back = Blocks.Application(afterPlan);
        Check(Blocks.Application(Blocks.Find("after") ?? _camp!).Ok && Blocks.Find("after") != null, $"provider-retirement-no-callback: with the kind registered again a new lantern applies ({back.State} {back.Reason})");

        // ---- reload the consumer alone ----
        LanternProbe.Left = p1;
        File.WriteAllLines(PhaseFile, ["1", camp.Guid.ToString(), night.Guid.ToString(), first.Guid.ToString(), _pass.ToString(), _fail.ToString(), OwnItems.ToString()]);
        Info("reloading this mod alone");
        Mods.Reload(Context.Info.Id, r => { });
    }

    private void Resume(string[] phase)
    {
        _pass = int.Parse(phase[4]);
        _fail = int.Parse(phase[5]);
    }

    private async Task AfterOwnReload(string[] phase)
    {
        Resume(phase);
        var (camp, night) = Lanterns("consumer reloaded alone");
        Check(camp.Guid.ToString() == phase[1] && night.Guid.ToString() == phase[2], "consumer-reload: the lanterns have the identities they had before the reload");
        // what the earlier instance applied with Blocks.Apply went with its owner: probe and after are no blocks, and their
        // definitions are orphaned. kiln.content keeps an orphaned definition listed until the game restarts (an item or a
        // recipe cannot be taken out of a running game's registries), so the counts stay.
        var items = OwnItems;
        var gone = NewContent.Definitions().Where(d => d.Name.Contains("kind-probe", StringComparison.Ordinal) || d.Name.Contains("kind-after", StringComparison.Ordinal)).ToList();
        Info($"after the reload: {phase[6]} item(s) before, {items} now; of probe and after: {string.Join(", ", gone.Select(d => $"{d.Kind} {d.Name} {d.State}"))}");
        Check(Blocks.Find("probe") == null && Blocks.Find("after") == null && gone.Count >= 2 && gone.All(d => d.State == "orphaned"),
            $"consumer-reload: the earlier instance's applied plans were taken back with it (no block, {gone.Count} definition(s), all orphaned: {gone.All(d => d.State == "orphaned")})");
        var kept = NewContent.Definitions().Where(d => d.Name.Contains("kind-camp", StringComparison.Ordinal) || d.Name.Contains("kind-long-night", StringComparison.Ordinal)).ToList();
        Check(kept.Count >= 2 && kept.All(d => d.State == "registered"), $"consumer-reload: this load's lanterns are registered ({string.Join(", ", kept.Select(d => $"{d.Name} {d.State}"))})");
        var left = LanternProbe.Left;
        var said = "no plan was left";
        if (left != null)
        {
            try { said = $"no exception, state {Blocks.Apply(left).State}"; }
            catch (ArgumentException e) { said = "ArgumentException: " + e.Message; }
        }
        Check(said.StartsWith("ArgumentException", StringComparison.Ordinal), $"consumer-binding: the earlier instance's plan is refused after the reload ({said})");
        Check(Blocks.Application(left!).State == BlockApplyState.Removed, $"consumer-binding: its application reads Removed ({Blocks.Application(left!).Reason})");
        LanternProbe.Left = null;
        File.WriteAllLines(PhaseFile, ["2", phase[1], phase[2], phase[3], _pass.ToString(), _fail.ToString(), items.ToString()]);
        Info("reloading the provider (this mod reloads with it)");
        Mods.Reload(Provider, r => { });
        await Steps(1);
    }

    private async Task AfterProviderReload(string[] phase)
    {
        Resume(phase);
        var (camp, night) = Lanterns("provider reloaded");
        Check(camp.Guid.ToString() == phase[1] && night.Guid.ToString() == phase[2], "provider-reload: the lanterns have the identities they had before the reload");
        Check(OwnItems == int.Parse(phase[6]), $"provider-reload: as many items as before it ({phase[6]} -> {OwnItems}): each owner's products went and came once");
        Check(LanternProbe.Compiles == 2, $"provider-reload: the new provider compiled this load's two lanterns and nothing else ({LanternProbe.Compiles})");
        File.Delete(PhaseFile);
        await Steps(1);
        Log.Info($"kinds-consumer-test: DONE pass={_pass} fail={_fail}");
    }
}
