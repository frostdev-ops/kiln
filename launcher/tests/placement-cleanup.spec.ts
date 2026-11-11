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
  await expect(warning).toContainText("may still run in the standalone loader");
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

// Every activation path reports the copies it left in the game folder (Activation.retained) with the same warning, and
// the settings it kept and its notes, all until closed: the Play page's profile switch, the Profiles list, a profile's
// own Activate, the re-apply after a profile edit, Play itself, a reinstall's resume, the Deck's profile dialog.
const RETAINED = { path: "mods/XHL-Vein-Mining", reason: "files Kiln placed were changed or removed, or code was added (native-plugin.ini)", mayRun: true };
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
  await expect(kept).toContainText("may still run in the standalone loader");
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
  await planActivation(page);
  // what every profile page does on a change (useProfileEdit): profile_save, which writes the active profile again
  await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    const p = await api.profileGet("ember-run");
    await api.profileSave({ ...p, notes: "edited" });
  });
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
  const kept = page.locator(".k-toast", { hasText: "Some files were kept" });
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
