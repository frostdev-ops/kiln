// SPDX-License-Identifier: MIT
namespace Kiln.Testing;

/// <summary>A check as the runner's list shows it.</summary>
internal sealed record ViewItem(string Mod, string Id, string Title, string Area, IReadOnlyList<string> Tags, string Needs,
    string? Verdict, string? Note, string? Refusal, string? Confirm, bool Cheats);

/// <summary>The check that runs now. <c>Phase</c>: confirming (waiting for yes or no), running (the body sets the
/// scene), asking (waiting for the verdict) or finishing (verdict given, clean-up running).</summary>
internal sealed record ViewCurrent(string Mod, string Id, string Title, string Area, string Phase, string Instruction,
    string? Confirm, IReadOnlyList<LedgerAssertion> Assertions, string Session, bool Cheats);

/// <summary>
/// What the runner's screen and HUD line draw: an immutable snapshot the tick thread publishes, so the render thread
/// reads it without sharing the runner's state. Also the sample state of the UI preview. <c>Session</c>: hosted, joined,
/// server, or menu (a game client with no world). <c>Guard</c>: the achievement guard's state in one line;
/// <c>Warning</c>: a guard warning that stays until the game exits; <c>Message</c>: the last thing that happened;
/// <c>Queued</c>: checks left in "run all"; <c>Ledger</c>: a problem with the ledger file.
/// </summary>
internal sealed record RunnerView(string Session, IReadOnlyList<ViewItem> Items, ViewCurrent? Current, string Guard,
    bool Protecting, long Dropped, long Passed, string? Warning, string? Message, int Queued, string? Ledger)
{
    public static readonly RunnerView Empty = new("menu", [], null, "not read yet", false, 0, 0, null, null, 0, null);

    public int Passes => Items.Count(i => i.Verdict == "pass");
    public int Fails => Items.Count(i => i.Verdict == "fail");
    public int Skips => Items.Count(i => i.Verdict == "skip");
    public int NeverRun => Items.Count(i => i.Verdict is null);
}
