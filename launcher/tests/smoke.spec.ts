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
    await open(page, "/library");
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
