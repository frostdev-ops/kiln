// SPDX-License-Identifier: MIT
namespace Kiln.Testing;

/// <summary>Sample states of the check screen for the UI preview (tools/ui-preview, <c>--scene checks</c>): no game,
/// no runner, only what <see cref="CheckScreen.Draw"/> reads.</summary>
internal static class CheckSamples
{
    private static ViewItem I(string mod, string id, string title, string area, string? verdict = null, string? note = null,
        string? refusal = null, string needs = "any", string? confirm = null, params string[] tags) =>
        new(mod, id, title, area, tags, needs, verdict, note, refusal, confirm, tags.Contains(ClientCheckTags.Cheats));

    private static readonly IReadOnlyList<ViewItem> Items =
    [
        I("client-pass", "blocks.frost-flash", "Frost flash on hit", "Blocks", "pass", tags: ClientCheckTags.Visual),
        I("client-pass", "blocks.ember-trail", "Ember trail behind a thrown torch", "Blocks", "fail", "the trail stays after the torch lands",
            tags: ClientCheckTags.Visual),
        I("client-pass", "blocks.chime", "Chime on pick-up", "Blocks", "skip", "sound is off on this PC", tags: ClientCheckTags.Audio),
        I("client-pass", "blocks.dummy-hp", "Training dummy shows its health", "Blocks"),
        I("client-pass", "guard.proof", "The achievement guard drops a report", "Achievement guard",
            confirm: "This check levels the character once, which reports an achievement. Run it only if your Steam account already has it.",
            tags: [ClientCheckTags.Cheats, ClientCheckTags.GuardProof]),
        I("client-pass", "skills.points", "Skill points from the admin command", "Achievement guard",
            refusal: "line 4: no guard-proof check has run on game build 5d9ef2f1a0b3… and Kiln 0.2.0-dev.25: run it and pass it first",
            tags: ClientCheckTags.Cheats),
        I("client-pass", "net.second-player", "A second player sees the banner", "Network",
            refusal: "needs a game joined to a server; this session is hosted", needs: "joined", tags: ClientCheckTags.TwoPlayers),
        I("client-pass", "launcher.mod-list", "The launcher lists every loader's mods", "Launcher", "pass", tags: ClientCheckTags.Launcher),
    ];

    private static RunnerView View(ViewCurrent? current, string? warning = null) =>
        new("hosted", Items, current, "protecting, 0 dropped, 0 passed", true, 0, 0, warning, current is null ? null : "running " + current.Mod + "/" + current.Id,
            current is null ? 0 : 3, null);

    public static IEnumerable<(string Name, RunnerView View, ScreenState State)> All(DateTime now)
    {
        var running = new ViewCurrent("client-pass", "blocks.dummy-hp", "Training dummy shows its health", "Blocks", "asking",
            "A training dummy stands 3 m in front of you. Hit it twice. Does the bar over its head go down each time?",
            null, [new LedgerAssertion { What = "the dummy spawned", Ok = true }, new LedgerAssertion { What = "two hits counted", Ok = false, Detail = "counted 1" }],
            "hosted", false);
        yield return ("checks-list", View(running), new ScreenState());
        var confirm = new ViewCurrent("client-pass", "guard.proof", "The achievement guard drops a report", "Achievement guard", "confirming",
            Items[4].Confirm!, Items[4].Confirm, [], "hosted", true);
        yield return ("checks-confirm", View(confirm), new ScreenState { ConfirmKey = "client-pass/guard.proof\n" + Items[4].Confirm, ConfirmShown = now });
        yield return ("checks-note", View(running, "skills.points: the guard passed 1 achievement report(s) to Steam during the check") with
            {
                Changed = CheckRunner.ChangedCharacter,
            },
            new ScreenState { Noting = true, Note = "the bar went down only on the second hit" });
    }
}
