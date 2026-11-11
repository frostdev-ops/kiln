// shroudlink's client addon is never loaded late (kiln_manage::compat, ChainViewEntry.gated): it has to find the game's
// functions before Kiln hooks them. The native page's chain card offers no late switch for it, as a [chain] entry or
// as a profile native, and one an older Kiln left late can be switched back. Browser mock (src/api/mockChain.ts).
import { expect, test, type Page } from "@playwright/test";
import type { ChainView, ChainViewEntry } from "../src/api/commands";

async function nativePage(page: Page) {
  await page.goto("/#/profiles/ember-run/native");
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
const entries = (page: Page) => page.getByRole("list", { name: "Chain entries" });
const row = (page: Page, file: string) => entries(page).getByRole("listitem").filter({ hasText: file });
const toggle = (page: Page, file: string) => row(page, file).getByRole("switch", { name: "Load after Kiln and ShroudForge" });

test("the addon as a [chain] entry: switched back from late, then no late switch, and the backend refuses late", async ({ page }) => {
  await nativePage(page);
  // the mock's chain.ini still has it under [late] from an older Kiln
  const addon = row(page, "shroudlink-dinput8-2.dll");
  await expect(addon).toContainText("Loaded late, shroudlink's client addon switches its MODS tab off.");
  await expect(toggle(page, "shroudlink-dinput8-2.dll")).toBeChecked();
  await addon.locator("label.k-toggle").click();
  await expect(addon).toContainText("Loads first: shroudlink's client addon has to find the game's functions before Kiln hooks them");
  await expect(toggle(page, "shroudlink-dinput8-2.dll")).toHaveCount(0);
  // another chain entry keeps its switch
  await expect(toggle(page, "xhl-core.dll")).toHaveCount(1);
  const code = await page.evaluate(async () => {
    try { await (await import("/src/api/commands.ts")).chainSetLate("ember-run", "client-steam", "chain\\shroudlink-dinput8-2.dll", true); }
    catch (e) { return `${(e as { code: string }).code}: ${(e as { message: string }).message}`; }
    return "set late";
  });
  expect(code).toContain("invalid_argument");
  expect(code).toContain("MODS tab off");
});

// kiln-manage's view of a profile whose dllmain natives include the addon (compat.rs sets gated for both shapes)
const native = (late: boolean): ChainViewEntry => ({
  path: "Z:\\home\\player\\.local\\share\\kiln\\library\\native\\shroudlink-client\\1.0.0+0a1b2c3d\\dinput8.dll", file: "dinput8.dll",
  section: "profile", engine: null, modId: "shroudlink-client", late, lateBy: late ? "profile" : null, offBy: null, loader: false, gated: true,
});
const fps: ChainViewEntry = { ...native(false), path: "Z:\\mods\\fpsunlock.dll", file: "fpsunlock.dll", modId: "fps-unlock", gated: false };
const planView = (page: Page, late: boolean) => page.addInitScript((result) => {
  (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { chain_view: Array.from({ length: 40 }, () => ({ result })) };
}, { entries: [native(late), fps], rules: [], ruleErrors: [], session: null, overlaps: [], active: true, current: true, error: null } satisfies ChainView);

test("the addon as a profile native gets no late switch", async ({ page }) => {
  await planView(page, false);
  await nativePage(page);
  await expect(row(page, "dinput8.dll")).toContainText("Loads first: shroudlink's client addon has to find the game's functions before Kiln hooks them");
  await expect(toggle(page, "dinput8.dll")).toHaveCount(0);
  await expect(toggle(page, "fpsunlock.dll")).toHaveCount(1);
});

test("the addon as a profile native that an older Kiln set late is switched back in the profile", async ({ page }) => {
  await planView(page, true);
  await nativePage(page);
  // an older Kiln wrote the addon into the profile's late list; the page reads the profile again when it is shown again
  const gets = () => page.evaluate(() => (window as unknown as { __kilnMock: { calls: () => string[] } }).__kilnMock.calls().filter((c) => c === "profile_get").length);
  const before = await gets();
  await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    const p = await api.profileGet("ember-run");
    await api.profileSave({ ...p, late: ["shroudlink-client"] });
    window.dispatchEvent(new Event("visibilitychange"));
  });
  await expect.poll(gets).toBeGreaterThan(before + 1);
  const addon = row(page, "dinput8.dll");
  await expect(addon).toContainText("Loaded late, shroudlink's client addon switches its MODS tab off.");
  await expect(toggle(page, "dinput8.dll")).toBeChecked();
  await addon.locator("label.k-toggle").click();
  await expect.poll(async () => page.evaluate(async () => (await (await import("/src/api/commands.ts")).profileGet("ember-run")).late ?? []))
    .toEqual([]);
});
