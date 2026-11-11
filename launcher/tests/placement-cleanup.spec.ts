// Retained player files need recovery instructions until the player dismisses them, not just a success toast.
import { expect, test } from "@playwright/test";

test("deactivating a profile keeps retained-folder warnings through later notifications", async ({ page }) => {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.evaluate(() => {
    const mock = (window as unknown as { __kilnMock: { planCommand: (cmd: string, plan: { result: unknown }) => void } }).__kilnMock;
    mock.planCommand("profile_deactivate", { result: {
      removed: ["mods/unchanged-addon"],
      retained: [{ path: "mods/edited-addon", reason: "player-settings.json was added by the player", mayRun: true }],
    } });
  });
  await page.getByRole("button", { name: /^Active profile: .* Change profile\.$/ }).click();
  await page.getByRole("option", { name: "No profile drop-ins" }).click();
  const warning = page.locator(".k-toast", { hasText: "Some mod folders were kept" });
  await expect(warning).toContainText("mods/edited-addon");
  await expect(warning).toContainText("player-settings.json was added by the player");
  await expect(warning).toContainText("may still run through Kiln or another loader");
  await page.clock.install();
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    for (let i = 0; i < 6; i++) toast.ok(`Another operation ${i}`);
  });
  await page.clock.fastForward(10_000);
  await expect(warning).toBeVisible();
  await warning.getByRole("button", { name: "Dismiss", exact: true }).click();
  await expect(warning).toHaveCount(0);
});

test("persistent warnings remain scrollable on a small screen while timed notices stay bounded", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 600 });
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.clock.install();
  await page.evaluate(async () => {
    const { toast } = await import("/src/ui/Toast.tsx");
    for (let i = 0; i < 8; i++) toast.info(`Recovery ${i}`, "Review the retained mod folder before restarting the standalone loader.", { ms: 0 });
    for (let i = 0; i < 6; i++) toast.ok(`Timed ${i}`);
  });
  await expect(page.locator(".k-toast", { hasText: /^Timed/ })).toHaveCount(4);
  await page.clock.fastForward(10_000);
  await expect(page.locator(".k-toast")).toHaveCount(8);
  const bounds = await page.locator(".k-toasts").boundingBox();
  expect(bounds?.y).toBeGreaterThanOrEqual(0);
  expect((bounds?.y ?? 0) + (bounds?.height ?? 0)).toBeLessThanOrEqual(600);
  const last = page.locator(".k-toast", { hasText: "Recovery 7" });
  await last.getByRole("button", { name: "Dismiss", exact: true }).click();
  await expect(last).toHaveCount(0);
  await expect(page.locator(".k-toast", { hasText: "Recovery 0" })).toHaveCount(1);
});

test("an empty cleanup result does not show a retained-folder warning", async ({ page }) => {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.getByRole("button", { name: /^Active profile: .* Change profile\.$/ }).click();
  await page.getByRole("option", { name: "No profile drop-ins" }).click();
  await expect(page.locator(".k-toast", { hasText: "No profile" })).toBeVisible();
  await expect(page.locator(".k-toast", { hasText: "Some mod folders were kept" })).toHaveCount(0);
});

// Every activation path reports the copies it left in the game folder (Activation.retained), the settings it kept and
// its notes, all until closed: the Play page's profile switch, the Profiles list, a profile's own Activate, the re-apply
// after a profile edit (also one before the window listened), Play itself (also when the start fails), a modpack from
// a server's offer and its Launch. The drop, the Deck, an update and a reinstall have theirs in firstrun, deck and
// update specs. Each reason ends with what still runs from that folder (engine_mods::still_runs).
const RETAINED = { path: "mods/XHL-Vein-Mining", reason: "files Kiln placed were changed or removed, or code was added (native-plugin.ini). It is off in the profile, but its own loader may still load it from there.", mayRun: true };
const REPORT = {
  retained: [RETAINED],
  keptFiles: ["xhl:xhl-vein-mining: kept runtime settings (config.txt) in kiln/state/placed-settings/xhl/xhl-vein-mining; they return when the mod is enabled again"],
  warnings: ["shroudforge:sfpack: the setting speed was 7, which version 3.0.0 does not accept; it starts with that version's default 1"],
};
type Mock = { __kilnMock: { planActivation: (r: unknown) => void; planCommand: (cmd: string, plan: { result: unknown }) => void; emit: (n: string, v: unknown) => void } };
const planActivation = (page: import("@playwright/test").Page) => page.evaluate((r) => (window as unknown as Mock).__kilnMock.planActivation(r), REPORT);

async function expectActivationNotices(page: import("@playwright/test").Page) {
  const kept = page.locator(".k-toast", { hasText: "Some mod folders were kept" });
  await expect(kept).toContainText(RETAINED.path);
  await expect(kept).toContainText(RETAINED.reason);
  await expect(kept).toContainText("Kiln left these folders as they are");
  await expect(kept).not.toContainText("standalone loader");
  const settings = page.locator(".k-toast", { hasText: "Mod settings kept" });
  await expect(settings).toContainText("kiln/state/placed-settings/xhl/xhl-vein-mining");
  const notes = page.locator(".k-toast", { hasText: "Profile notes" });
  await expect(notes).toContainText("the setting speed was 7");
  // none of them goes by itself
  await page.clock.fastForward(10_000);
  for (const t of [kept, settings, notes]) await expect(t).toBeVisible();
  for (const t of [kept, settings, notes]) await t.getByRole("button", { name: "Dismiss", exact: true }).click();
  await expect(page.locator(".k-toast", { hasText: /Some mod folders were kept|Mod settings kept|Profile notes/ })).toHaveCount(0);
}

test("the Play page's profile switch shows what the activation kept in the game folder", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await planActivation(page);
  await page.getByRole("button", { name: /^Active profile: .* Change profile\.$/ }).click();
  await page.getByRole("option", { name: /^Vanilla plus/ }).click();
  await expectActivationNotices(page);
});

test("the Profiles list's Activate shows what the activation kept in the game folder", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/profiles");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await planActivation(page);
  await page.getByRole("row", { name: /Vanilla plus/ }).getByRole("button", { name: "Activate" }).click();
  await expectActivationNotices(page);
});

test("a profile's own Activate shows what the activation kept in the game folder", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/profiles/vanilla-plus/mods");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await planActivation(page);
  await page.getByRole("complementary").getByRole("button", { name: "Activate", exact: true }).click();
  await expectActivationNotices(page);
});

test("the automatic re-apply after a profile edit shows what it kept in the game folder", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  // An ordinary notes-only save reconciles too, but this fixture has nothing to report yet. A retained-copy report
  // on that path is covered by mock-reconcile.spec.ts. A rendered mod change below also writes profile.json.
  const save = (edit: string) => page.evaluate(async (edit) => {
    const api = await import("/src/api/commands.ts");
    const p = await api.profileGet("ember-run");
    await api.profileSave(edit === "notes" ? { ...p, notes: "edited" } : { ...p, mods: { ...p.mods, kiln: p.mods.kiln.map((m, i) => (i === 0 ? { ...m, enabled: !m.enabled } : m)) } });
  }, edit);
  await save("notes");
  await page.clock.fastForward(1000);
  await expect(page.locator(".k-toast", { hasText: "Some mod folders were kept" })).toHaveCount(0);
  await planActivation(page);
  await save("mod");
  await expectActivationNotices(page);
});

test("a re-apply before the window listened is shown once it does", async ({ page }) => {
  // the watcher's start-up pass (a profile.json an older Kiln wrote) runs before the page has its listener: the
  // backend keeps the activation until activation_notices takes it
  await page.addInitScript((a) => { (window as unknown as { __kilnMockNotices: unknown[] }).__kilnMockNotices = [a]; },
    { gameId: "client-steam", profileId: "ember-run", generation: 8, pathMode: "z", profileJson: "/x/kiln/profile.json", copied: 0, fingerprint: "kms1:00", activated: "2026-11-11T10:00:00Z", ...REPORT });
  await page.clock.install();
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expectActivationNotices(page);
  // taken once: a reload of the page asks again and gets nothing new
  expect(await page.evaluate(async () => (await import("/src/api/commands.ts")).activationNotices())).toEqual([]);
});

test("Play shows what writing the profile again kept", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await planActivation(page);
  await page.getByRole("button", { name: /^Play / }).first().click();
  await expectActivationNotices(page);
});

test("a start that fails after the profile was written again still shows what that kept", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  // launch.rs: the error carries the re-apply's activation in detail.activation
  await page.evaluate((r) => (window as unknown as { __kilnMock: { planCommand: (c: string, p: unknown) => void } }).__kilnMock.planCommand("launch_play", {
    error: { code: "io", message: "the save backup before the launch failed: disk full", detail: { activation: { gameId: "client-steam", profileId: "ember-run",
      generation: 9, pathMode: "z", profileJson: "/x", copied: 0, fingerprint: "kms1:00", activated: "2026-11-11T10:00:00Z", ...r } } } }), REPORT);
  await page.getByRole("button", { name: /^Play / }).first().click();
  await expect(page.locator(".k-toast", { hasText: "disk full" })).toBeVisible();
  await expectActivationNotices(page);
});

test("a modpack from a server's offer, and its Launch, show what the activation kept", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.locator(".k-hero-banner .k-banner").getByRole("button", { name: "Get modpack" }).click();
  const offer = page.getByRole("dialog", { name: /uses mods$/ });
  await offer.getByText(/^I trust .* and want its mods$/).click();
  await planActivation(page);
  await offer.getByRole("button", { name: "Download and install" }).click();
  await expect(page.getByRole("dialog", { name: "Modpack installed" })).toBeVisible({ timeout: 8000 });
  await page.clock.fastForward(3000);
  await expectActivationNotices(page);
  await planActivation(page);
  await page.getByRole("dialog", { name: "Modpack installed" }).getByRole("button", { name: "Launch" }).click();
  await expectActivationNotices(page);
});

test("a server deploy's warnings stay until closed", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/servers/lab/overview");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.evaluate(() => {
    const mock = (window as unknown as Mock & { __kilnMock: { setServer: (o: unknown) => void } }).__kilnMock;
    mock.setServer({ running: false, installed: null });
    mock.planCommand("server_deploy", { result: "t-planned-deploy" });
  });
  await page.locator(".k-banner").filter({ hasText: "Kiln isn't installed" }).getByRole("button", { name: "Install Kiln" }).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.emit("kiln://task", {
    id: "t-planned-deploy", kind: "server_deploy", title: "Install Kiln", state: "done", progress: 1, message: "Done",
    started: "2026-11-11T10:00:00Z", finished: "2026-11-11T10:00:05Z",
    result: { changedBackedUp: [], backup: null, warnings: ["Kept current dinput8.dll because it changed outside Kiln."] },
  }));
  // an install's own warnings have their own title (an uninstall's say "Some files were kept")
  const kept = page.locator(".k-toast", { hasText: "Install notes" });
  await expect(kept).toContainText("Kept current dinput8.dll");
  await page.clock.fastForward(10_000);
  await expect(kept).toBeVisible();
});

test("adding a mod shows what writing the profile again kept in the game folder", async ({ page }) => {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.evaluate(() => {
    const mock = (window as unknown as { __kilnMock: { planActivation: (report: unknown) => void } }).__kilnMock;
    mock.planActivation({ retained: [{ path: "greeneye/unlock-map-markers", reason: "unlock-map-markers.dll was changed", mayRun: true }] });
  });
  const dt = await page.evaluateHandle(() => { const d = new DataTransfer(); d.items.add(new File(["x"], "night-owl-1.2.0.zip")); return d; });
  await page.locator(".k-hero").dispatchEvent("drop", { dataTransfer: dt });
  await expect(page.locator(".k-toast", { hasText: "Added Night Owl" })).toBeVisible();
  const warning = page.locator(".k-toast", { hasText: "Some mod folders were kept" });
  await expect(warning).toContainText("greeneye/unlock-map-markers");
  await expect(warning).toContainText("unlock-map-markers.dll was changed");
});

// AddReport.notices (quick::merge): what an add did in the profile besides adding. They stay until closed.
const ADD_NOTICE = "AutoLootPlus 1.1.9 took the place of Auto Loot Plus: Kiln reads the two as one mod (native autolootplus). Undo puts Auto Loot Plus back.";

test("a dropped mod's add says what else it changed in the profile, until closed", async ({ page }) => {
  await page.clock.install();
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.evaluate((n) => (window as unknown as { __kilnMock: { planAddNotices: (l: string[]) => void } }).__kilnMock.planAddNotices([n]), ADD_NOTICE);
  const dt = await page.evaluateHandle(() => { const d = new DataTransfer(); d.items.add(new File(["x"], "night-owl-1.2.0.zip")); return d; });
  await page.locator(".k-hero").dispatchEvent("drop", { dataTransfer: dt });
  await expect(page.locator(".k-toast", { hasText: "Added Night Owl" })).toBeVisible();
  const changed = page.locator(".k-toast", { hasText: "Changes in the profile" });
  await expect(changed).toContainText(ADD_NOTICE);
  await page.clock.fastForward(10_000);
  await expect(changed).toBeVisible();
});
