// Smoke tests in browser-mock mode: every route renders without console errors, the key flows work (import, light a
// mod, resolver preview; the mouth follows the game state; typed confirmation in the arch; the grouped server list),
// every main page fits 1024×640, and screenshots of the main pages at 1024×640 and 1920×1080 go to
// .out/launcher/shots/ (SHOTS=0 skips them).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = join(import.meta.dirname, "..", "..", ".out", "launcher", "shots");

/** The launcher's own routes: must render without console errors. */
const CLIENT_ROUTES = [
  "/", "/profiles", "/profiles/ember-run/mods", "/profiles/ember-run/native", "/profiles/ember-run/emf", "/profiles/ember-run/configs",
  "/profiles/ember-run/saves", "/library", "/runtime", "/logs", "/saves", "/editor", "/settings", "/about", "/servers",
];
/** Server and admin routes: rendered inside the shell, checked softly. */
const SERVER_ROUTES = [
  "/servers/lab/overview", "/servers/lab/settings", "/servers/lab/mods", "/servers/lab/configs", "/servers/lab/backups",
  "/servers/lab/logs", "/servers/lab/ticks", "/servers/lab/modpack",
  ...["players", "map", "zones", "items", "spawn", "world", "combat", "permissions", "content", "configs", "mods", "console", "saves", "crashes"]
    .map((p) => `/servers/lab/admin/${p}`),
];

/** Console errors and page errors while `fn` runs (missing optional editor assets are not errors). */
async function collectErrors(page: Page, fn: () => Promise<void>): Promise<string[]> {
  const errors: string[] = [];
  const onConsole = (m: { type(): string; text(): string; location(): { url: string } }) => {
    if (m.type() !== "error") return;
    const url = m.location().url ?? "";
    if (url.includes("/assets-local/") || /Failed to load resource.*assets-local/.test(m.text())) return;
    errors.push(m.text());
  };
  const onError = (e: Error) => errors.push(String(e));
  page.on("console", onConsole);
  page.on("pageerror", onError);
  try { await fn(); } finally { page.off("console", onConsole); page.off("pageerror", onError); }
  return errors;
}

async function open(page: Page, route: string) {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
  // a goto that only changes the hash can leave the page where it was
  if (await page.evaluate((r) => location.hash !== `#${r}`, route)) await page.evaluate((r) => { location.hash = `#${r}`; }, route);
}

async function routeErrors(page: Page, routes: string[]): Promise<Record<string, string[]>> {
  const out: Record<string, string[]> = {};
  await open(page, "/");
  for (const r of routes) {
    const errs = await collectErrors(page, async () => {
      await page.evaluate((h) => { location.hash = h; }, `#${r}`);
      await expect(page.locator(".k-app")).not.toBeEmpty();
      await page.waitForTimeout(300);
    });
    if (await page.getByText("This page failed").count()) errs.push("error boundary: This page failed");
    if (errs.length) out[r] = errs;
  }
  return out;
}

test("every client route renders without console errors", async ({ page }) => {
  expect(await routeErrors(page, CLIENT_ROUTES)).toEqual({});
});

test("server and admin routes render inside the shell", async ({ page }) => {
  const errs = await routeErrors(page, SERVER_ROUTES);
  // the shell survives every one of them (a broken page stays inside the route error boundary)
  await expect(page.locator(".k-head")).toBeVisible();
  for (const r of SERVER_ROUTES) expect.soft(errs[r] ?? [], r).toEqual([]);
});

test("import → add to profile → light → resolver preview", async ({ page }) => {
  const errors = await collectErrors(page, async () => {
    // the power user's way: Settings "Add new mods to the active profile" off, a drop on Library is detected, then
    // imported only (the default, a drop that lands in the active profile: tests/firstrun.spec.ts)
    await open(page, "/settings");
    await page.getByText("Add new mods to the active profile", { exact: true }).click();
    await expect(page.getByRole("switch", { name: /^Add new mods to the active profile/ })).not.toBeChecked();
    await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Library" }).click();
    // drop a mod zip on the niche (mock mode: the file name stands in for the path)
    const dt = await page.evaluateHandle(() => { const d = new DataTransfer(); d.items.add(new File(["x"], "night-owl-1.2.0.zip")); return d; });
    await page.locator("[data-drop]").dispatchEvent("drop", { dataTransfer: dt });
    const dialog = page.getByRole("dialog", { name: "Import" });
    await expect(dialog.getByText("night-owl")).toBeVisible();
    await dialog.getByRole("button", { name: "Import 1" }).click();
    await expect(page.getByText("Imported 1")).toBeVisible();
    await expect(page.getByRole("table", { name: "Library" }).getByText("Night Owl")).toBeVisible();
    // a pack zip with the mod in a folder inside imports too
    const dt2 = await page.evaluateHandle(() => { const d = new DataTransfer(); d.items.add(new File(["x"], "dawn-lamp-2.0.0-pack.zip")); return d; });
    await page.locator("[data-drop]").dispatchEvent("drop", { dataTransfer: dt2 });
    const dialog2 = page.getByRole("dialog", { name: "Import" });
    await dialog2.getByRole("button", { name: "Import 1" }).click();
    await expect(page.getByRole("table", { name: "Library" }).getByText("Dawn Lamp")).toBeVisible();

    await open(page, "/profiles/ember-run/mods");
    await page.getByRole("button", { name: "Add mod", exact: true }).click();
    const add = page.getByRole("dialog", { name: "Add from the library" });
    await add.getByText("Night Owl").click();
    await add.getByRole("button", { name: "Add 1" }).click();
    const lit = page.locator(".k-order-row.lit", { hasText: "night-owl" });
    await expect(lit).toBeVisible();
    // put it out: it leaves the load order; light it again: back
    const flame = page.getByRole("button", { name: "Light Night Owl" });
    await expect(flame).toHaveAttribute("aria-pressed", "true");
    await flame.click();
    await expect(flame).toHaveAttribute("aria-pressed", "false");
    await expect(lit).toHaveCount(0);
    await flame.click();
    await expect(lit).toBeVisible();
    // rejections carry the resolver's reasons
    await expect(page.getByText("missing dependency ui-lib ^1.0")).toBeVisible();
    // server side: client-only mods stay out with a reason
    await page.getByRole("radio", { name: "Server" }).click();
    await expect(page.getByText("side 'client': not loaded on a server").first()).toBeVisible();
  });
  expect(errors).toEqual([]);
});

test("the mouth is the Play button and follows the game state", async ({ page }) => {
  await open(page, "/");
  const play = page.locator(".k-door");
  await expect(play).toHaveAccessibleName(/^Play /);
  await expect(play).toBeEnabled();
  await play.click();
  await expect(page.locator(".k-hero").getByText("Waiting for Kiln to boot.")).toBeVisible();
  await expect(page.locator(".k-head-state")).toHaveText(/Starting|Running/);
  await expect(play).toBeDisabled();
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 8000 });
  await page.evaluate(() => (window as unknown as { __kilnMock: { stopGame: () => void } }).__kilnMock.stopGame());
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");
  await expect(play).toBeEnabled();
  // just once: vanilla warns in the arch first
  await page.getByRole("button", { name: "Vanilla", exact: true }).click();
  const arch = page.getByRole("dialog", { name: "Play vanilla once" });
  await expect(arch.getByText("A character carrying items from a Kiln mod can crash")).toBeVisible();
  await expect(page.locator(".k-arch")).toBeVisible();
  await arch.getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await page.getByRole("button", { name: "Another profile", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Play a profile once" })).toBeVisible();
  await page.keyboard.press("Escape");
  // on a sheet page the mouth is the pilot light: still the Play button, still labelled
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Library" }).click();
  await expect(play).toHaveClass(/pilot/);
  await expect(play).toHaveAccessibleName(/^Play /);
});

test("Without mods when Steam does not load Kiln: No Kiln and the Steam setting, never Starting forever", async ({ page }) => {
  type M = { __kilnMock: { stopGame: () => void; setNoProxy: (on: boolean) => void } };
  await open(page, "/");
  await page.evaluate(() => (window as unknown as M).__kilnMock.setNoProxy(true));
  const state = page.locator(".k-head-state");
  // before any start: the one Steam setting is offered already
  await expect(page.getByText("One Steam setting")).toBeVisible();
  await page.getByRole("button", { name: "Without mods", exact: true }).click();
  // the process runs past the grace with play-once.ini still there: the proxy never ran
  await expect(state).toHaveText(/^No Kiln/, { timeout: 8000 });
  await expect(page.getByText("Kiln is not loading")).toBeVisible();
  await expect(page.getByText("The game started without Kiln", { exact: false })).toBeVisible();
  await expect(page.locator(".k-hero").getByText("Waiting for Kiln to boot.")).toHaveCount(0);
  await expect(page.locator(".k-hero").getByText("Running without Kiln: Steam did not load it this start.")).toBeVisible();
  await page.evaluate(() => (window as unknown as M).__kilnMock.stopGame());
  await page.evaluate(() => (window as unknown as M).__kilnMock.setNoProxy(false));
  await expect(state).toHaveText("Stopped");
});

test("the running clock counts from the game's own start, also when Steam started it", async ({ page }) => {
  type M = { __kilnMock: { stopGame: () => void; startOutside: (secondsAgo: number) => void } };
  await open(page, "/");
  const state = page.locator(".k-head-state");
  await page.locator(".k-door").click();
  await expect(state).toHaveText(/^Running/, { timeout: 8000 });
  await page.evaluate(() => (window as unknown as M).__kilnMock.stopGame());
  await expect(state).toHaveText("Stopped");
  // started from Steam three minutes ago: the clock counts from then, not from this launcher's last launch
  await page.evaluate(() => (window as unknown as M).__kilnMock.startOutside(180));
  await expect(state).toHaveText(/^Running 03:0\d$/, { timeout: 8000 });
  await page.evaluate(() => (window as unknown as M).__kilnMock.stopGame());
  await expect(state).toHaveText("Stopped");
});

test("an install that replaced files changed by hand says they are kept in backups", async ({ page }) => {
  type M = { __kilnMock: { setModified: (files: string[]) => void } };
  await open(page, "/runtime");
  await page.evaluate(() => (window as unknown as M).__kilnMock.setModified(["kiln/kiln-core.dll"]));
  await page.getByRole("row").filter({ hasText: "0.1.9+20261101.a41c2d0" }).getByRole("button", { name: "Roll back" }).click();
  await page.getByRole("dialog", { name: "Install Kiln 0.1.9" }).getByRole("button", { name: "Install" }).click();
  const done = page.locator(".k-toast").filter({ hasText: "Kiln 0.1.9+20261101.a41c2d0 installed" });
  await expect(done).toBeVisible({ timeout: 8000 });
  await expect(done.locator(".k-toast-detail")).toHaveText("1 changed file kept in backups");
});

test("Runtime shows installed versus missing parts; Reinstall adds a missing default part and keeps a turned-off one off", async ({ page }) => {
  // the mock receipt has core and emf; the installed bundle also has eml as a default (an update that kept an old list)
  await open(page, "/runtime");
  const parts = page.getByRole("list", { name: "Parts", exact: true });
  await expect(parts.getByRole("listitem", { name: "core: Installed" })).toBeVisible();
  await expect(parts.getByRole("listitem", { name: "eml: Missing" })).toBeVisible();
  await expect(parts.getByRole("listitem", { name: /^admin/ })).toHaveCount(0); // a server part, not offered to a client
  const banner = page.locator(".k-banner").filter({ hasText: "Parts missing" });
  await expect(banner).toContainText("eml");
  const row = page.getByRole("row").filter({ hasText: "0.2.0-dev+20261104.0f1ffa6" });
  await expect(row.getByRole("button", { name: "Reinstall" })).toBeEnabled();
  await banner.getByRole("button", { name: "Reinstall" }).click();
  const dialog = page.getByRole("dialog", { name: "Install Kiln 0.2.0-dev" });
  // no choice: the player's parts stay, the missing default is added
  await expect(dialog.getByRole("switch", { name: /^Keep my parts/ })).toBeChecked();
  const plan = dialog.getByRole("list", { name: "Parts this install puts in" });
  await expect(plan.getByRole("listitem").filter({ hasText: "eml" })).toContainText("Adds");
  await expect(plan.getByRole("listitem").filter({ hasText: "emf" })).toContainText("Keeps");
  await dialog.getByRole("button", { name: "Install" }).click();
  await expect(parts.getByRole("listitem", { name: "eml: Installed" })).toBeVisible({ timeout: 8000 });
  await expect(banner).toHaveCount(0);
  await expect(row.getByRole("button", { name: "Installed" })).toBeDisabled();
  // the player turns emf off (an explicit choice); a later rollback and update keep it off
  await page.getByRole("row").filter({ hasText: "0.1.9+20261101.a41c2d0" }).getByRole("button", { name: "Roll back" }).click();
  const back = page.getByRole("dialog", { name: "Install Kiln 0.1.9" });
  await back.getByText("Keep my parts", { exact: true }).click();
  await expect(back.getByRole("switch", { name: /^Keep my parts/ })).not.toBeChecked();
  await back.getByText("emf", { exact: true }).click();
  await back.getByRole("button", { name: "Install" }).click();
  await expect(parts.getByRole("listitem", { name: "emf: Turned off" })).toBeVisible({ timeout: 8000 });
  await expect(parts.getByRole("listitem", { name: /^eml/ })).toHaveCount(0);
  await page.getByRole("row").filter({ hasText: "0.2.0-dev+20261104.0f1ffa6" }).getByRole("button", { name: "Update" }).click();
  const up = page.getByRole("dialog", { name: "Install Kiln 0.2.0-dev" });
  await expect(up.getByRole("switch", { name: /^Keep my parts/ })).toBeChecked();
  await up.getByRole("button", { name: "Install" }).click();
  await expect(parts.getByRole("listitem", { name: "emf: Turned off" })).toBeVisible({ timeout: 8000 });
  await expect(parts.getByRole("listitem", { name: "eml: Installed" })).toBeVisible();
  await expect(page.locator(".k-banner").filter({ hasText: "Parts missing" })).toHaveCount(0);
  await page.getByRole("row").filter({ hasText: "0.1.9+20261101.a41c2d0" }).getByRole("button", { name: "Roll back" }).click();
  await back.getByRole("button", { name: "Install" }).click();
  await expect(parts.getByRole("listitem", { name: "core: Installed" })).toBeVisible({ timeout: 8000 });
  await page.getByRole("row").filter({ hasText: "0.2.0-dev+20261104.0f1ffa6" }).getByRole("button", { name: "Update" }).click();
  await expect(up.getByRole("list", { name: "Parts this install puts in" }).getByRole("listitem").filter({ hasText: "emf" })).toContainText("Off, your choice");
  await up.getByRole("button", { name: "Install" }).click();
  await expect(parts.getByRole("listitem", { name: "emf: Turned off" })).toBeVisible({ timeout: 8000 });
  await expect(parts.getByRole("listitem", { name: "eml: Installed" })).toBeVisible();
});

test("part previews preserve opt-outs across optional bundles and required dependencies", async () => {
  const { deselectedFor, partRows, resolveParts, selectParts } = await import("../src/shell/parts");
  type Bundle = import("../src/api/commands").RuntimeBundle;
  type Receipt = import("../src/api/commands").InstallReceipt;
  const bundle = {
    parts: [
      { name: "core", sides: ["client"], default: true },
      { name: "eml", sides: ["client"], default: false, requires: ["core"] },
      { name: "bridge", sides: ["client"], default: true, requires: ["eml"] },
      { name: "server", sides: ["server"], default: true },
    ],
  } as Bundle;
  const receipt = { side: "client", runtime: { components: ["core"], deselected: ["eml"] } } as Receipt;
  expect(selectParts(bundle, "client", ["bridge"])).toEqual(["bridge", "core", "eml"]);
  expect(deselectedFor(bundle, "client", ["core"], receipt)).toEqual(["bridge", "eml"]);
  expect(deselectedFor(bundle, "client", ["bridge"], receipt)).toEqual([]);
  expect(resolveParts(bundle, "client", receipt)).toEqual(["bridge", "core", "eml"]);
  expect(partRows(bundle, "client", receipt).find((p) => p.name === "eml")?.state).toBe("missing");
  const older = { ...bundle, parts: bundle.parts.filter((p) => p.name !== "bridge") };
  expect(resolveParts(older, "client", { ...receipt, runtime: { ...receipt.runtime, components: ["core", "eml", "bridge"] } })).toEqual(["core"]);
  expect(() => selectParts(bundle, "client", ["server"])).toThrow(/client side/);
});

test("custom parts select their requirements and keep them checked until their dependent is turned off", async ({ page }) => {
  const bundle: import("../src/api/commands").RuntimeBundle = {
    version: "0.2.1+20261107.1111111", kiln: "0.2.1", file: "runtime/test.zip", sha256: "a".repeat(64),
    created: "2026-11-07T12:00:00Z", sides: ["client", "server"], components: ["core", "emf", "eml", "bridge", "sigdb-server"],
    parts: [
      { name: "core", sides: ["client", "server"], default: true },
      { name: "emf", sides: ["client", "server"], default: true, requires: ["core"] },
      { name: "eml", sides: ["client", "server"], default: false, requires: ["core"] },
      { name: "bridge", sides: ["client", "server"], default: false, requires: ["eml"] },
      { name: "sigdb-server", sides: ["server"], default: true, requires: ["core"] },
    ],
  };
  await page.addInitScript((b) => {
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { runtime_list: Array.from({ length: 12 }, () => ({ result: [b] })) };  // one per call: the shell lists runtimes too
  }, bundle);
  await open(page, "/runtime");
  await page.getByRole("row").filter({ hasText: bundle.version }).getByRole("button", { name: "Update" }).click();
  const dialog = page.getByRole("dialog", { name: "Install Kiln 0.2.1" });
  await dialog.getByText("Keep my parts", { exact: true }).click();
  const eml = dialog.getByRole("switch", { name: /^eml/ });
  await expect(eml).not.toBeChecked();
  await dialog.getByText("bridge", { exact: true }).click();
  await expect(eml).toBeChecked();
  await expect(eml).toBeDisabled();
  await expect(dialog.getByText("sigdb-server", { exact: true })).toHaveCount(0);
  await dialog.getByText("bridge", { exact: true }).click();
  await expect(eml).toBeEnabled();
  await dialog.getByText("eml", { exact: true }).click();
  await expect(eml).not.toBeChecked();
});

test("a server without Kiln offers Install Kiln on Overview; one with Kiln offers Update Kiln", async ({ page }) => {
  type M = { __kilnMock: { setServer: (o: { running?: boolean; installed?: string | null }) => void } };
  await open(page, "/servers/lab/overview");
  const more = page.getByRole("button", { name: "More server actions" });
  const banner = page.locator(".k-banner").filter({ hasText: "Kiln isn't installed" });
  // running with Kiln: no banner, the menu updates it
  await expect(page.locator(".k-overview")).toBeVisible();
  await expect(banner).toHaveCount(0);
  await more.click();
  await expect(page.getByRole("menuitem", { name: "Update Kiln…" })).toBeVisible();
  await page.keyboard.press("Escape");
  // stopped, nothing of Kiln in the folder: a title and a button, nothing more
  await page.evaluate(() => (window as unknown as M).__kilnMock.setServer({ running: false, installed: null }));
  await expect(banner).toBeVisible();
  await expect(banner.locator(".k-banner-text")).toHaveCount(0);
  await more.click();
  await expect(page.getByRole("menuitem", { name: "Install Kiln…" })).toBeVisible();
  await page.keyboard.press("Escape");
  await banner.getByRole("button", { name: "Install Kiln" }).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  const done = page.locator(".k-toast").filter({ hasText: "Kiln 0.2.0-dev+20261104.0f1ffa6 installed" });
  await expect(done).toBeVisible({ timeout: 8000 });
  await expect(done.locator(".k-toast-detail")).toHaveCount(0);
  await expect(banner).toHaveCount(0);
  await more.click();
  await expect(page.getByRole("menuitem", { name: "Update Kiln…" })).toBeVisible();
  await page.keyboard.press("Escape");
  // running without Kiln: the banner says why its button waits
  await page.evaluate(() => (window as unknown as M).__kilnMock.setServer({ running: true, installed: null }));
  await expect(banner.locator(".k-banner-text")).toHaveText("Stop the server to install it.");
  await expect(banner.getByRole("button", { name: "Install Kiln" })).toBeDisabled();
});

test("the active profile is chosen from its name", async ({ page }) => {
  await open(page, "/");
  await page.getByRole("button", { name: /^Active profile: / }).click();
  const list = page.getByRole("listbox", { name: "Profiles" });
  await expect(list.getByRole("option").first()).toBeVisible();
  await list.getByRole("option", { name: /Vanilla plus/ }).click();
  // the name changes at once: the mock's activation answers after 1.5 s, like a real one with big mods
  await expect(page.getByRole("button", { name: "Active profile: Vanilla plus. Change profile." })).toBeVisible({ timeout: 500 });
  await expect(page.getByText("Active profile: Vanilla plus", { exact: true })).toBeVisible();
  // the Profiles page agrees without a reload
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Profiles" }).click();
  await expect(page.getByRole("row").filter({ hasText: "Vanilla plus" }).getByText("Active", { exact: true })).toBeVisible();
});

test("Last boot counts the mods that failed to load and later errors, and opens the logs", async ({ page }) => {
  await open(page, "/");
  // the example run: boot.json has no errors, mods.json has a mod that failed, kiln.log two errors after the boot
  const last = page.getByRole("button", { name: /^Last boot: .*1 mod failed, 2 errors since start\. Open the logs\.$/ });
  await expect(last).toHaveAttribute("title", /kiln creature brains' threw \(2x\)/);
  await expect(last).toBeVisible();
  await last.click();
  await expect(page).toHaveURL(/#\/logs$/);
});

test("Runtime orders builds of one version and day by when they were built, not by their git id", async ({ page }) => {
  await open(page, "/runtime");
  // a later build of the installed version and day, whose git id sorts before the installed one's as text
  const dt = await page.evaluateHandle(() => { const d = new DataTransfer(); d.items.add(new File(["x"], "kiln-runtime-0.2.0-dev+20261104.0000aaa.zip")); return d; });
  await page.locator(".k-drop").dispatchEvent("drop", { dataTransfer: dt });
  const table = page.getByRole("table", { name: "Runtime bundles" });
  const later = table.getByRole("row").filter({ hasText: "0.2.0-dev+20261104.0000aaa" });
  await expect(later.getByRole("button", { name: "Update" })).toBeVisible();
  await expect(table.getByRole("row").filter({ hasText: "0.1.9+20261101.a41c2d0" }).getByRole("button", { name: "Roll back" })).toBeVisible();
  // newest first
  await expect(table.getByRole("row").nth(1)).toContainText("0.2.0-dev+20261104.0000aaa");
});

test("a runtime change that fails says so on Runtime and on Play until dismissed", async ({ page }) => {
  await open(page, "/");
  // Play names the build, so two dev builds of the same version differ
  await expect(page.locator(".k-hero-facts")).toContainText("0.2.0-dev (20261104)");
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Runtime" }).click();
  await page.getByRole("row").filter({ hasText: "0.1.9+20261101.a41c2d0" }).getByRole("button", { name: "Roll back" }).click();
  const dialog = page.getByRole("dialog", { name: "Install Kiln 0.1.9" });
  // a client is offered only the parts a client can take
  await expect(dialog.getByText("core", { exact: true })).toBeVisible();
  for (const part of ["admin", "samples"]) await expect(dialog.getByText(part, { exact: true })).toHaveCount(0);
  await page.evaluate(() => (window as unknown as { __kilnMock: { failNextInstall: (m: string) => void } }).__kilnMock.failNextInstall("No space left on device"));
  await dialog.getByRole("button", { name: "Install" }).click();
  const failed = page.locator(".k-banner").filter({ hasText: "Rollback to 0.1.9+20261101.a41c2d0 failed" });
  await expect(failed).toContainText("No space left on device", { timeout: 8000 });
  await expect(failed).toContainText("The game keeps Kiln 0.2.0-dev+20261104.0f1ffa6");
  // it stays after leaving the page: Play says it too, next to the version
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  const onPlay = page.locator(".k-hero-banner .k-banner").filter({ hasText: "Rollback to 0.1.9+20261101.a41c2d0 failed" });
  await expect(onPlay).toContainText("The game still has Kiln 0.2.0-dev+20261104.0f1ffa6.");
  await expect(page.getByRole("button", { name: "update failed" })).toBeVisible();
  await onPlay.getByRole("button", { name: "Dismiss" }).click();
  await expect(onPlay).toHaveCount(0);
  await expect(page.getByRole("button", { name: "update failed" })).toHaveCount(0);
});

test("the fire can be turned off: Play draws the plain button, as without WebGL", async ({ page }) => {
  await open(page, "/");
  const app = page.locator(".k-app");
  await page.waitForTimeout(600);
  const withGl = (await app.getAttribute("data-gl")) !== null; // the headless browser may have no WebGL at all
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  const fire = page.getByRole("switch", { name: /^Animated fire/ });
  await expect(fire).toBeChecked();
  await page.getByText("Animated fire", { exact: true }).click();
  await expect(fire).not.toBeChecked();
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await expect(app).not.toHaveAttribute("data-gl");
  await expect(page.locator(".k-stage-gl")).toBeHidden();
  const play = page.locator(".k-door");
  await expect(play).toBeVisible();
  await expect(play).toHaveAccessibleName(/^Play /);
  // on again: the fire comes back where there is WebGL
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  await page.getByText("Animated fire", { exact: true }).click();
  await expect(fire).toBeChecked();
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  if (withGl) await expect(app).toHaveAttribute("data-gl", "1");
});

test("a refusal shows on Play, with or without a modpack, until dismissed", async ({ page }) => {
  await open(page, "/");
  const refused = page.locator(".k-hero-banner .k-banner").filter({ has: page.locator(".k-banner-title", { hasText: /^Refused$/ }) });
  // the contract example: refused by a server that advertises a modpack
  await expect(refused).toHaveCount(1);
  await expect(refused.getByRole("button", { name: "Get modpack" })).toBeVisible();
  // the game wrote a refusal from a server without a modpack (a lab refusing by the default policy)
  await page.evaluate(() => (window as unknown as { __kilnMock: { setLastServer: (l: unknown) => void } }).__kilnMock.setLastServer({
    format: 1, written: "2026-11-06T05:33:51Z", outcome: "rejected",
    reason: "This server requires mods you do not have: npcsample 0.1.0 (missing), quests-sample 2.0.0 (missing)",
    server: { id: "srv-d09b032bfe63", name: "Kiln lab mylab", address: null, kiln: "0.2.0-dev" }, modpack: null,
    required: [{ id: "net-test", version: "0.1.0", hash: `kdh1:${"3".repeat(64)}`, kind: "kiln" },
      { id: "npcsample", version: "0.1.0", hash: `kdh1:${"b".repeat(64)}`, kind: "kiln" },
      { id: "quests-sample", version: "2.0.0", hash: `kdh1:${"a".repeat(64)}`, kind: "kiln" }],
    truncated: false, missing: ["npcsample", "quests-sample"], history: [],
  }));
  await expect(refused.getByText("Kiln lab mylab: This server requires mods you do not have: npcsample 0.1.0 (missing), quests-sample 2.0.0 (missing)")).toBeVisible();
  await expect(refused.getByRole("button", { name: "Get modpack" })).toHaveCount(0);
  await refused.getByRole("button", { name: "Dismiss" }).click();
  await expect(refused).toHaveCount(0);
  // the next refusal from that server shows again
  await page.evaluate(() => {
    const m = (window as unknown as { __kilnMock: { setLastServer: (l: unknown) => void } }).__kilnMock;
    m.setLastServer({ format: 1, written: "2026-11-06T06:10:00Z", outcome: "rejected", reason: null,
      server: { id: "srv-d09b032bfe63", name: "Kiln lab mylab", address: null, kiln: "0.2.0-dev" }, modpack: null, required: [],
      truncated: false, missing: ["npcsample"], history: [] });
  });
  await expect(refused.getByText("Kiln lab mylab: missing npcsample.")).toBeVisible();
});

test("a mod built for a newer Kiln than the game has says Update Kiln on Play", async ({ page }) => {
  await open(page, "/");
  const banner = page.locator(".k-hero-banner .k-banner").filter({ has: page.locator(".k-banner-title", { hasText: /^Update Kiln$/ }) });
  await expect(banner).toHaveCount(0);
  await page.evaluate(() => (window as unknown as { __kilnMock: { setNewerMods: (m: unknown) => void } }).__kilnMock.setNewerMods([
    { id: "item-blocks", version: "0.1.1", builtWith: "0.2.0-dev+20261106.0f0a3f6" },
  ]));
  await expect(banner.getByText("item-blocks needs a newer Kiln.")).toBeVisible();
  await banner.getByRole("button", { name: "Update" }).click();
  await expect(page).toHaveURL(/\/runtime/);
});

test("destructive actions need the typed word, in the arch", async ({ page }) => {
  await open(page, "/profiles");
  await page.getByRole("button", { name: "New profile" }).first().click();
  await page.getByLabel("Name").fill("Throwaway");
  await page.getByRole("button", { name: "Create" }).click();
  await expect(page.getByRole("heading", { name: "Throwaway" })).toBeVisible();
  await open(page, "/profiles");
  const row = page.getByRole("row").filter({ hasText: "Throwaway" });
  await row.getByLabel("More for Throwaway").click();
  await page.getByRole("menuitem", { name: "Delete" }).click();
  const dialog = page.getByRole("dialog");
  await expect(page.locator(".k-arch")).toBeVisible();
  const confirm = dialog.getByRole("button", { name: "Delete" });
  await expect(confirm).toBeDisabled();
  await dialog.getByRole("textbox").fill("throw");
  await expect(confirm).toBeDisabled();
  await dialog.getByRole("textbox").fill("throwaway");
  await confirm.click();
  await expect(page.getByRole("row").filter({ hasText: "Throwaway" })).toHaveCount(0);
});

test("one grouped list per server: Run, World, Data, Files", async ({ page }) => {
  await open(page, "/servers/lab/admin/players");
  const list = page.getByRole("navigation", { name: "Server" });
  for (const g of ["Run", "World", "Data", "Files"]) await expect(list.getByRole("group", { name: g })).toBeVisible();
  await expect(list.getByRole("link")).toHaveCount(21);
  await expect(list.getByRole("link", { name: "Players" })).toHaveAttribute("aria-current", "page");
  await list.getByRole("link", { name: "Map", exact: true }).click();
  await expect(page).toHaveURL(/#\/servers\/lab\/admin\/map$/);
  await expect(list.getByRole("link", { name: "Map", exact: true })).toHaveAttribute("aria-current", "page");
  // server pages and admin pages share the list
  await list.getByRole("link", { name: "Backups", exact: true }).click();
  await expect(page).toHaveURL(/#\/servers\/lab\/backups$/);
  // the merged pages: the old admin routes land on them
  await open(page, "/servers/lab/admin/console");
  await expect(page).toHaveURL(/#\/servers\/lab\/overview$/);
  await expect(page.getByRole("log", { name: "Server console" })).toBeVisible();
  await open(page, "/servers/lab/admin/mods");
  await expect(page).toHaveURL(/#\/servers\/lab\/mods$/);
  // the list fits 800 high without scrolling
  const box = await list.evaluate((el) => ({ scroll: el.scrollHeight, client: el.clientHeight }));
  expect(box.scroll).toBeLessThanOrEqual(box.client + 1);
});

test("schema form edits a config value", async ({ page }) => {
  await open(page, "/profiles/ember-run/configs");
  await expect(page.getByText("Rarity boost")).toBeVisible();
  await expect(page.getByText("restart").first()).toBeVisible();
  await expect(page.getByText("synced").first()).toBeVisible();
  const tier = page.locator("#sf-loot-tier");
  await tier.selectOption("high");
  await expect(page.locator(".k-sform-row.changed", { hasText: "Tier" })).toBeVisible();
  await page.getByRole("radio", { name: "File" }).click();
  await expect(page.getByRole("textbox", { name: "File" })).toHaveValue(/"tier": "high"/);
});

test("every main page fits 1024×640 without a window scrollbar", async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 640 });
  for (const r of ["/", "/profiles", "/profiles/ember-run/mods", "/library", "/runtime", "/logs", "/saves", "/settings", "/about", "/servers", "/servers/lab/overview", "/servers/lab/admin/items"]) {
    await open(page, r);
    await page.waitForTimeout(400);
    const over = await page.evaluate(() => ({ x: document.documentElement.scrollWidth > innerWidth + 1, y: document.documentElement.scrollHeight > innerHeight + 1 }));
    expect(over, r).toEqual({ x: false, y: false });
    // the header's words stay inside the window
    const right = await page.locator(".k-head-state").evaluate((el) => el.getBoundingClientRect().right);
    expect(right, r).toBeLessThanOrEqual(1024);
  }
});

test("every page reaches all of its content at 1024×640 (nothing cut off without a scrollbar)", async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 640 });
  const cut: Record<string, string[]> = {};
  for (const r of [...CLIENT_ROUTES.filter((x) => x !== "/"), ...SERVER_ROUTES]) {
    await open(page, r);
    await page.waitForTimeout(500);
    // text or a control whose nearest clipping ancestor is overflow: hidden and ends above it can never be seen
    const lost = await page.evaluate(() => [...document.querySelectorAll(".k-sheet *")].flatMap((el) => {
      if (el.children.length || !el.textContent?.trim() || el.closest(".k-niche, .leaflet-container, [aria-hidden='true']")) return [];
      const r = el.getBoundingClientRect();
      if (!r.height) return [];
      let a = el.parentElement;
      while (a && getComputedStyle(a).overflowY === "visible") a = a.parentElement;
      if (!a || !["hidden", "clip"].includes(getComputedStyle(a).overflowY)) return [];
      const box = a.getBoundingClientRect();
      return box.height >= 60 && r.bottom > box.bottom + 2 ? [el.textContent.trim().slice(0, 40)] : [];
    }));
    if (lost.length) cut[r] = lost.slice(0, 3);
  }
  expect(cut).toEqual({});
});

test("screenshots of the main pages", async ({ page }) => {
  test.skip(process.env.SHOTS === "0", "SHOTS=0");
  mkdirSync(SHOTS, { recursive: true });
  const shots: [string, string][] = [
    ["play", "/"], ["profiles", "/profiles"], ["profile-mods", "/profiles/ember-run/mods"], ["profile-native", "/profiles/ember-run/native"],
    ["profile-configs", "/profiles/ember-run/configs"], ["library", "/library"], ["runtime", "/runtime"], ["logs", "/logs"],
    ["saves", "/saves"], ["settings", "/settings"], ["about", "/about"], ["editor", "/editor"], ["servers", "/servers"],
    ["server-overview", "/servers/lab/overview"], ["server-items", "/servers/lab/admin/items"], ["server-map", "/servers/lab/admin/map"],
    ["server-mods", "/servers/lab/mods"], ["server-players", "/servers/lab/admin/players"],
  ];
  for (const [w, h] of [[1024, 640], [1920, 1080]]) {
    await page.goto("about:blank");
    await page.setViewportSize({ width: w, height: h });
    for (const [name, route] of shots) {
      await open(page, route);
      await page.waitForTimeout(name === "editor" ? 2500 : 900);
      await page.screenshot({ path: join(SHOTS, `mock-${name}-${w}x${h}.png`) });
    }
  }
  // a typed confirmation and the tick stats
  await page.setViewportSize({ width: 1280, height: 800 });
  await open(page, "/logs");
  await page.getByRole("tab", { name: "Tick stats" }).click();
  await page.waitForTimeout(500);
  await page.screenshot({ path: join(SHOTS, "mock-logs-ticks.png") });
  await open(page, "/runtime");
  await page.getByRole("button", { name: "Uninstall" }).click();
  await page.waitForTimeout(600);
  await page.screenshot({ path: join(SHOTS, "mock-confirm-danger.png") });
});
