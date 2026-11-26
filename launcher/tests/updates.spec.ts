// Update notices for installed mods (docs/kiln/launcher/catalog.md "Update notices"; browser mock: src/api/mockUpdates.ts).
// Three fixture mods of the mock Library: New Items (a GitHub source, 1.1.0 published: update available), Big Stacks
// (no source: unsupported), Night Sky (a GitHub answer two days old: stale, never "up to date"). A notice is words and
// a link: opening it calls the opener and nothing else, and no profile or Library entry changes. Also the source
// dialog with its confirmation, the daily check's setting (off by default), and provider text shown as plain text.
// This is the mod update check; update.spec.ts is the launcher's own self-update.
import { expect, test, type Page } from "@playwright/test";

type Req = { offline?: boolean; refresh?: boolean; automatic?: boolean; providers?: string[]; source?: { action: string; id: string; confirm?: boolean } };

async function desktop(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
const api = <T,>(page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(async ([f, a]) => {
    const m = (await import("/src/api/commands.ts")) as unknown as Record<string, (...x: unknown[]) => Promise<unknown>>;
    return m[f as string](...(a as unknown[]));
  }, [fn, args] as const) as Promise<T>;
const opened = (page: Page) => page.evaluate(() => (window as unknown as { __kilnMock: { opened: () => string[] } }).__kilnMock.opened());
const commands = (page: Page) => page.evaluate(() => (window as unknown as { __kilnMock: { calls: () => string[] } }).__kilnMock.calls());
const requests = (page: Page) => page.evaluate(() => (window as unknown as { __kilnUpdatesMock: { calls: () => unknown[] } }).__kilnUpdatesMock.calls()) as Promise<Req[]>;
const mock = (page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(([f, a]) => (window as unknown as { __kilnUpdatesMock: Record<string, (...x: unknown[]) => unknown> }).__kilnUpdatesMock[f as string](...(a as unknown[])), [fn, args] as const);
const list = (page: Page) => page.locator(".k-hero").getByRole("list", { name: "Mods" });
const row = (page: Page, name: string) => list(page).getByRole("listitem").filter({ has: page.getByText(name, { exact: true }) });
const checkButton = (page: Page) => page.getByRole("button", { name: /^(Check for updates|Checking…)$/ });
async function openUpdates(page: Page, name: string) {
  await row(page, name).getByRole("button", { name: `More for ${name}` }).click();
  await page.getByRole("menuitem", { name: "Updates…", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: `Updates of ${name}` });
  await expect(dialog).toBeVisible();
  return dialog;
}

test("three mods: an update, no source, a stale answer; the notice opens a page and changes nothing", async ({ page }) => {
  await desktop(page);
  // what is remembered shows without asking any provider: the first read is an offline check
  await expect(row(page, "New Items")).toContainText("Update 1.1.0", { timeout: 10_000 });
  expect((await requests(page)).every((r) => r.offline === true)).toBe(true);
  expect(await mock(page, "githubRequests")).toBe(0);
  // Night Sky's answer is two days old: stale, in words, and never as up to date
  await expect(row(page, "Night Sky")).toContainText("Update check is stale");
  await expect(list(page)).not.toContainText(/up to date/i);
  // Big Stacks has no source: its row stays quiet, its dialog says so
  await expect(row(page, "Big Stacks")).not.toContainText(/update/i);
  const none = await openUpdates(page, "Big Stacks");
  await expect(none).toContainText("No update source");
  await expect(none).toContainText("Kiln does not know where this mod is published");
  await expect(none.getByRole("button", { name: "Open page" })).toHaveCount(0);
  await page.keyboard.press("Escape");

  // the profile and the Library before anything is clicked
  const profile = JSON.stringify(await api(page, "profileGet", "ember-run"));
  const library = JSON.stringify(await api(page, "libraryList"));
  const pinned = (await api<{ mods: { kiln: { id: string; version: string }[] } }>(page, "profileGet", "ember-run")).mods.kiln.find((m) => m.id === "new-items")!.version;
  expect(pinned).toBe("1.0.0");

  // "Check for updates" asks now (not offline) and reports; nothing is downloaded
  await checkButton(page).click();
  await expect(page.getByText("2 mods have an update")).toBeVisible();
  expect((await requests(page)).some((r) => !r.offline && !r.automatic)).toBe(true);
  expect(await mock(page, "githubRequests")).toBeGreaterThan(0);
  await expect(row(page, "Weather Plus")).toContainText("Update 1.1.0");

  // the notice is a link to the release page: the opener is called, and no other command
  const before = (await commands(page)).length;
  await row(page, "New Items").getByRole("button", { name: /Update 1\.1\.0 for New Items: open its page/ }).click();
  await expect.poll(() => opened(page)).toEqual(["https://github.com/example-org/new-items/releases/tag/v1.1.0"]);
  await page.waitForTimeout(600);
  // what the page polls or reports by itself (found_scan: every 10 s, GameFolder.tsx; app_render_report: the shell's
  // drawing facts, shell/renderReport.ts, which a loaded machine sends late) is not something the click called
  const after = (await commands(page)).slice(before)
    .filter((c) => !["game_status", "launch_state", "task_list", "found_scan", "app_render_report"].includes(c));
  expect(after).toEqual(["app_open_url"]);
  // the same link from the mod's dialog
  const dialog = await openUpdates(page, "New Items");
  await expect(dialog).toContainText("Version 1.1.0 is published; you have 1.0.0.");
  await expect(dialog).toContainText("GitHub: example-org/new-items (from its manifest, anonymous)");
  await dialog.getByRole("button", { name: "Open page" }).click();
  await expect.poll(async () => (await opened(page)).length).toBe(2);
  await page.keyboard.press("Escape");

  // nothing was installed, switched or re-pinned by any of it
  const all = await commands(page);
  for (const forbidden of ["library_import", "library_add", "library_add_to_profile", "nexus_download", "nexus_link_install", "profile_save", "profile_activate", "runtime_install", "launch_start"]) {
    expect(all, forbidden).not.toContain(forbidden);
  }
  expect(JSON.stringify(await api(page, "profileGet", "ember-run"))).toBe(profile);
  expect(JSON.stringify(await api(page, "libraryList"))).toBe(library);
  expect((await api<{ mods: { kiln: { id: string; version: string }[] } }>(page, "profileGet", "ember-run")).mods.kiln.find((m) => m.id === "new-items")!.version).toBe(pinned);
  await expect(row(page, "New Items")).toContainText("1.0.0");
  console.log("PASS updates.no-auto-install (launcher)");
});

test("offline: what was seen stays, marked stale, and nothing reads as up to date", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "New Items")).toContainText("Update 1.1.0", { timeout: 10_000 });
  await mock(page, "offline", true);
  await checkButton(page).click();
  await expect(row(page, "New Items")).toContainText("Update 1.1.0 (stale)");
  await expect(row(page, "Night Sky")).toContainText("Update check is stale");
  await expect(list(page)).not.toContainText(/up to date/i);
  const dialog = await openUpdates(page, "Night Sky");
  await expect(dialog).toContainText("Unknown (stale)");
  await expect(dialog).toContainText("Kiln cannot tell whether an update exists: no answer");
  await expect(dialog).not.toContainText(/up to date/i);
  console.log("PASS updates.offline (launcher)");
});

test("editing a source preserves its tag pattern, asset and channel", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "New Items")).toContainText("Update 1.1.0");
  let dialog = await openUpdates(page, "New Items");
  await dialog.getByLabel("Release tag").fill("mods/new-items/v{version}");
  await dialog.getByLabel("File name").fill("items-{version}.zip");
  await dialog.getByLabel("Versions", { exact: true }).selectOption("prerelease");
  await dialog.getByRole("button", { name: "Save and check" }).click();
  await expect(dialog).toContainText("set by you");
  await page.keyboard.press("Escape");
  dialog = await openUpdates(page, "New Items");
  await expect(dialog.getByLabel("Release tag")).toHaveValue("mods/new-items/v{version}");
  await expect(dialog.getByLabel("File name")).toHaveValue("items-{version}.zip");
  await expect(dialog.getByLabel("Versions", { exact: true })).toHaveValue("prerelease");
  await dialog.getByLabel("Versions", { exact: true }).selectOption("stable");
  await dialog.getByRole("button", { name: "Save and check" }).click();
  await expect(dialog.getByRole("button", { name: "Save and check" })).toBeEnabled();
  const sent = (await requests(page)).filter((r) => r.source?.action === "map");
  expect((sent.at(-1)!.source as unknown as { spec: unknown }).spec).toEqual({
    provider: "github", repository: "example-org/new-items", tagPattern: "mods/new-items/v{version}", asset: "items-{version}.zip", channel: "stable",
  });
});

test("a source set by hand, and the yes a changed source needs", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "New Items")).toContainText("Update 1.1.0", { timeout: 10_000 });
  // a mod without a source gets one
  let dialog = await openUpdates(page, "Big Stacks");
  await dialog.getByLabel("Repository").fill("not a repository");
  await dialog.getByRole("button", { name: "Save and check" }).click();
  await expect(dialog).toContainText("Not saved");
  await dialog.getByLabel("Repository").fill("example-org/big-stacks");
  await dialog.getByRole("button", { name: "Save and check" }).click();
  await expect(dialog).toContainText("GitHub: example-org/big-stacks (set by you, anonymous)");
  await expect(dialog.getByRole("button", { name: "Remove my source" })).toBeVisible();
  await page.keyboard.press("Escape");
  // a mod that is checked against its author's repository: another place is asked about first
  dialog = await openUpdates(page, "New Items");
  await dialog.getByLabel("Repository").fill("someone-else/new-items");
  await dialog.getByRole("button", { name: "Save and check" }).click();
  await expect(dialog).toContainText("Change where this mod is checked?");
  await expect(dialog).toContainText("new-items is checked against example-org/new-items");
  await expect(dialog).toContainText("GitHub: example-org/new-items (from its manifest", { timeout: 2000 });
  let sent = (await requests(page)).filter((r) => r.source?.id === "new-items");
  expect(sent.map((r) => r.source!.confirm)).toEqual([false]);
  await dialog.getByRole("button", { name: "Change it" }).click();
  await expect(dialog).toContainText("GitHub: someone-else/new-items (set by you, anonymous)");
  sent = (await requests(page)).filter((r) => r.source?.id === "new-items");
  expect(sent.map((r) => r.source!.confirm)).toEqual([false, true]);
});

// Official builds run with Nexus Mods switched off (app_info.nexusEnabled false): the form offers GitHub only, and a
// Nexus Mods source saved before (a build with Nexus Mods on, a mod's manifest) is shown and kept, not dropped.
const mapNexus = (page: Page, id: string) =>
  page.evaluate(async (mod) => {
    const m = (await import("/src/api/updates.ts")) as unknown as { updatesCheck: (r: unknown) => Promise<unknown> };
    await m.updatesCheck({ offline: true, source: { action: "map", kind: "kiln", id: mod, spec: { provider: "nexus", gameId: 5811, modId: 316, fileChainId: "1001" }, confirm: false } });
  }, id);
const whereOptions = (dialog: ReturnType<Page["getByRole"]>) => dialog.getByLabel("Where it is published").locator("option");

test("with Nexus Mods on, a source can be on GitHub or Nexus Mods", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "New Items")).toContainText("Update 1.1.0", { timeout: 10_000 });
  await mapNexus(page, "big-stacks");
  const dialog = await openUpdates(page, "Big Stacks");
  await expect(whereOptions(dialog)).toHaveText(["GitHub releases", "A file on Nexus Mods"]);
  await expect(dialog.getByLabel("Where it is published")).toHaveValue("nexus");
  await expect(dialog.getByLabel("Mod page number")).toHaveValue("316");
  await expect(dialog.getByLabel("File id")).toHaveValue("1001");
  await expect(dialog.getByText("Nexus Mods is switched off in this build")).toHaveCount(0);
});

test("with Nexus Mods off, only GitHub is offered, and a saved Nexus Mods source stays and is said", async ({ page }) => {
  await page.goto("/?nexus=disabled#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(row(page, "New Items")).toContainText("Update 1.1.0", { timeout: 10_000 });
  let dialog = await openUpdates(page, "Big Stacks");
  await expect(whereOptions(dialog)).toHaveText(["GitHub releases"]);
  await page.keyboard.press("Escape");

  await mapNexus(page, "big-stacks");
  dialog = await openUpdates(page, "Big Stacks");
  // the saved source, as it is, and why it is not checked
  await expect(dialog).toContainText("Nexus Mods: enshrouded/316/1001 (set by you, anonymous)");
  await expect(dialog).toContainText("Checks off");
  await expect(dialog).toContainText("Nexus Mods is switched off in this build");
  // a new choice starts at GitHub; the Nexus Mods fields are not offered
  await expect(whereOptions(dialog)).toHaveText(["GitHub releases"]);
  await expect(dialog.getByLabel("Where it is published")).toHaveValue("github");
  await expect(dialog.getByLabel("Repository")).toHaveValue("");
  await expect(dialog.getByLabel("Mod page number")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Save and check" })).toBeDisabled();
  await expect(dialog.getByRole("button", { name: "Remove my source" })).toBeVisible();
  await page.keyboard.press("Escape");
  // opening the dialog sent nothing: the saved source is still there
  expect((await requests(page)).filter((r) => r.source?.id === "big-stacks").map((r) => r.source!.action)).toEqual(["map"]);
  dialog = await openUpdates(page, "Big Stacks");
  await expect(dialog).toContainText("Nexus Mods: enshrouded/316/1001 (set by you, anonymous)");
});

test("the daily check is off by default, and on it runs by itself as an automatic check", async ({ page }) => {
  // the test's clock: the first automatic check 300 ms after the setting is on (the real one waits 2 to 15 minutes)
  await page.addInitScript(() => { (window as unknown as { __kilnDailyMs: number }).__kilnDailyMs = 300; });
  await desktop(page, "/settings/mods");
  const toggle = page.getByRole("switch", { name: "Check for mod updates once a day" });
  await expect(toggle).not.toBeChecked({ timeout: 10_000 });
  await page.waitForTimeout(1200);
  expect((await requests(page)).filter((r) => r.automatic)).toEqual([]);
  // the input is drawn as a track: click its card, as render.spec.ts does
  await page.locator(".k-toggle", { has: toggle }).click();
  await expect(toggle).toBeChecked();
  await expect.poll(async () => (await requests(page)).filter((r) => r.automatic).length, { timeout: 10_000 }).toBe(1);
  expect((await api<{ modUpdatesDaily?: boolean }>(page, "appSettingsGet")).modUpdatesDaily).toBe(true);
  // one check, not a loop: the next one is a day away
  await page.waitForTimeout(1200);
  expect((await requests(page)).filter((r) => r.automatic).length).toBe(1);
});

test("text from a provider is shown as text, and only a page Kiln built is ever opened", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "New Items")).toContainText("Update 1.1.0", { timeout: 10_000 });
  await mock(page, "set", "new-items", { candidateVersion: '<img src=x onerror="window.__pwned=1">', pageUrl: "javascript:window.__pwned=1" });
  await checkButton(page).click();
  await expect(row(page, "New Items")).toContainText('Update <img src=x onerror="window.__pwned=1">');
  await expect(row(page, "New Items").locator("img")).toHaveCount(0);
  // no page to open: the notice is words, not a link
  await expect(row(page, "New Items").getByRole("button", { name: /open its page/ })).toHaveCount(0);
  for (const url of ["https://evil.example/releases", "https://github.com.evil.example/x", "http://github.com/a/b", "https://github.com/a/b\" onclick=\"x"]) {
    await mock(page, "set", "new-items", { pageUrl: url });
    await checkButton(page).click();
    await expect(checkButton(page)).toHaveText("Check for updates");
    await expect(row(page, "New Items").getByRole("button", { name: /open its page/ }), url).toHaveCount(0);
  }
  expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined();
  expect(await opened(page)).toEqual([]);
});

test("the Catalog's banner comes from the same result", async ({ page }) => {
  await desktop(page, "/catalog");
  await expect(page.getByText("Update ready: Weather Plus")).toBeVisible({ timeout: 10_000 });
  await expect(page.getByText("Weather Plus 1.0.0 → 1.1.0")).toBeVisible();
  // the page asked through updates_check (once, not offline, Nexus Mods only), and New Items' GitHub update is not a
  // Catalog matter: opening the page sends nothing to GitHub
  const live = (await requests(page)).filter((r) => !r.offline);
  expect(live.map((r) => r.providers)).toEqual([["nexus"]]);
  expect(await mock(page, "githubRequests")).toBe(0);
  await expect(page.getByText(/updates ready/)).toHaveCount(0);
  await page.getByRole("button", { name: "Open page" }).click();
  await expect.poll(() => opened(page)).toEqual(["https://www.nexusmods.com/enshrouded/mods/316?tab=files"]);
});

/** Drops files (by name: mock mode) on Play, as the browser does (add-updated.spec.ts). */
async function drop(page: Page, ...files: string[]) {
  const dt = await page.evaluateHandle((names) => { const d = new DataTransfer(); for (const n of names) d.items.add(new File(["x"], n)); return d; }, files);
  await page.locator(".k-hero").dispatchEvent("dragover", { dataTransfer: dt });
  await page.locator(".k-hero").dispatchEvent("drop", { dataTransfer: dt });
}

// netjunk (Discord, 2026-11-26): "Update 0.2.10" beside Kiln Auto Loot 0.2.10, still there after he updated by drag and
// drop and by Add mod, and gone only after a restart. The notice clears as soon as the update is in, with no request
// to any provider, and the next "Check for updates" names the newest version published by then.
test("an update dropped on the launcher clears its notice at once, and the next check names the newest version", async ({ page }) => {
  await desktop(page);
  await expect(row(page, "New Items")).toContainText("Update 1.1.0", { timeout: 10_000 });
  await expect(row(page, "New Items")).toContainText("1.0.0");
  const asked = await mock(page, "githubRequests");
  // the player downloads 1.1.0 from the release page and drops it on Play
  await drop(page, "new-items-1.1.0.zip");
  await expect(page.locator(".k-toast").filter({ hasText: "Updated New Items: 1.0.0 → 1.1.0" })).toBeVisible({ timeout: 8000 });
  await expect(row(page, "New Items")).toContainText("1.1.0");
  await expect(row(page, "New Items")).not.toContainText(/Update 1\.1\.0/, { timeout: 5000 });
  await expect(row(page, "New Items").getByRole("button", { name: /^Update / })).toHaveCount(0);
  // worked out again from what is remembered: an offline check, nothing asked of GitHub
  await expect.poll(async () => (await requests(page)).filter((r) => r.offline === true).length).toBeGreaterThan(1);
  expect(await mock(page, "githubRequests")).toBe(asked);
  // the dialog says up to date with the installed version
  const dialog = await openUpdates(page, "New Items");
  await expect(dialog).toContainText("Up to date");
  await page.keyboard.press("Escape");
  // 1.2.0 is out; "Check for updates" asks and names it
  await mock(page, "publish", "example-org/new-items", "1.2.0");
  await checkButton(page).click();
  await expect(row(page, "New Items")).toContainText("Update 1.2.0");
  await expect(row(page, "New Items")).not.toContainText("Update 1.1.0");
  // and it clears the same way after that update is dropped too
  await drop(page, "new-items-1.2.0.zip");
  await expect(page.locator(".k-toast").filter({ hasText: "Updated New Items: 1.1.0 → 1.2.0" })).toBeVisible({ timeout: 8000 });
  await expect(row(page, "New Items")).not.toContainText(/Update 1\.2\.0/, { timeout: 5000 });
  console.log("PASS updates.clears-after-update (launcher)");
});

// An update from the Catalog itself, with no page that reads the Library open (only the Catalog): the Catalog's banner
// is worked out again at once (offline: nothing asked), not only on the way back to Play.
test("an update from the Catalog clears the Catalog's banner at once", async ({ page }) => {
  await page.goto("/?nexus=premium#/catalog");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(page.getByText("Update ready: Weather Plus")).toBeVisible({ timeout: 10_000 });
  const asked = (await requests(page)).filter((r) => !r.offline).length;
  await page.getByRole("list", { name: "Mods" }).getByRole("listitem", { name: "Weather Plus", exact: true }).click();
  const d = page.getByRole("dialog", { name: "Weather Plus" });
  await d.getByRole("button", { name: "Update to 1.1.0" }).click();
  await expect(page.locator(".k-toast").filter({ hasText: "Updated Weather Plus" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByText("Update ready: Weather Plus")).toHaveCount(0, { timeout: 5000 });
  expect((await requests(page)).filter((r) => !r.offline).length, "worked out offline").toBe(asked);
  expect(await mock(page, "githubRequests")).toBe(0);
  console.log("PASS updates.catalog-banner-clears (launcher)");
});
