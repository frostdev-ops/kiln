// The simple first run in browser-mock mode (`?mock=fresh`: no game known, no Kiln, no profiles, an empty library):
// the launcher looks for the game by itself, Install Kiln runs right on Play, a mod dropped anywhere on the window is on
// in the loaded profile ("My mods", never named on Play) with an Undo, a mod dropped before Kiln is installed is added
// after the install, modpack zips ask for consent, and Settings can turn switching new mods on off. Screenshots of the flow go to
// .out/simple/shots/ (SHOTS=0 skips them).
import { expect, test, type Locator, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = join(import.meta.dirname, "..", "..", ".out", "simple", "shots");
const shots = process.env.SHOTS !== "0";
if (shots) mkdirSync(SHOTS, { recursive: true });

async function shot(page: Page, name: string, settle = 700) {
  if (!shots) return;
  await page.waitForTimeout(settle); // the hero's rise and the toast's slide
  await page.screenshot({ path: join(SHOTS, `${name}.png`) });
}

async function start(page: Page, mode: "fresh" | "empty" | null, route = "/") {
  await page.goto(mode ? `/?mock=${mode}#${route}` : `/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

/** Drops files (by name: mock mode) on an element, as the browser does. */
async function dropOn(page: Page, target: Locator, ...files: string[]) {
  const dt = await page.evaluateHandle((names) => { const d = new DataTransfer(); for (const n of names) d.items.add(new File(["x"], n)); return d; }, files);
  await target.dispatchEvent("dragover", { dataTransfer: dt });
  await target.dispatchEvent("drop", { dataTransfer: dt });
}

const mouth = (page: Page) => page.locator(".k-door");
const toastWith = (page: Page, text: string | RegExp) => page.locator(".k-toast").filter({ hasText: text });

const say = (page: Page) => page.locator(".k-hero .k-hero-say");

/** "My mods" is loaded and empty: Play says no mods yet, and names no profile (the default is never named there). */
async function myModsReady(page: Page) {
  await expect(say(page)).toHaveText("No mods yet.", { timeout: 10_000 });
  await expect.poll(async () => (await page.evaluate(async () => (await import("/src/api/commands.ts")).profileActive("client-steam")))?.profileId).toBe("my-mods");
  await expect(page.locator(".k-hero").getByRole("link", { name: /^Profile:/ })).toHaveCount(0);
}

/** "No profile" on purpose (Settings, Profiles): the launcher forgets the profile it had for the game. */
async function loadNoProfile(page: Page) {
  await page.evaluate(() => { location.hash = "#/settings"; });
  const none = page.getByRole("region", { name: "Profiles" }).getByRole("listitem").filter({ hasText: "No profile" });
  await none.getByRole("button", { name: "Load" }).click();
  await expect(none).toContainText("Loaded", { timeout: 5000 });
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(page.locator(".k-hero").getByRole("link", { name: "No profile loaded" })).toBeVisible({ timeout: 5000 });
}

/** First start up to a game with Kiln and "My mods" loaded. */
async function installOnPlay(page: Page) {
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln", { timeout: 8000 });
  await mouth(page).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  await myModsReady(page);
}

test("first run: finds the game, Install Kiln on Play, drop a mod into My mods, Undo", async ({ page }) => {
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(String(e)));
  page.on("console", (m) => { if (m.type() === "error" && !m.location().url.includes("/assets-local/")) errors.push(m.text()); });
  await page.setViewportSize({ width: 1280, height: 800 });
  await start(page, "fresh");
  const hero = page.locator(".k-hero");

  // 1. nothing known yet: the launcher looks by itself (no Detect press)
  await expect(hero.getByText("Looking for Enshrouded in your Steam libraries.")).toBeVisible();
  await expect(mouth(page)).toHaveAccessibleName("Looking for Enshrouded in your Steam libraries");
  await shot(page, "1-looking", 0);

  // 2. found: the main action is Install Kiln, right here
  await expect(hero.getByText("Kiln is not in this game yet.")).toBeVisible({ timeout: 8000 });
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln");
  await expect(hero.getByRole("button", { name: "Install Kiln", exact: true })).toBeVisible();
  await shot(page, "2-install");

  // 3. one confirm, the Runtime page's install task, in place
  await mouth(page).click();
  const confirm = page.getByRole("dialog", { name: "Install Kiln" });
  await expect(confirm.getByText("0.2.0-dev+20261104.0f1ffa6")).toBeVisible();
  await shot(page, "3-confirm");
  await confirm.getByRole("button", { name: "Install" }).click();
  await expect(page).toHaveURL(/#\/$/);
  await expect(mouth(page)).toHaveAccessibleName("Installing Kiln");
  await expect(hero.getByRole("status", { name: /^Install Kiln 0\.2\.0-dev/ })).toBeVisible();
  await shot(page, "4-installing");

  // 4. done: My mods is made and loaded, and Play is ready
  await expect(toastWith(page, "Kiln 0.2.0-dev+20261104.0f1ffa6 installed")).toBeVisible({ timeout: 10_000 });
  await myModsReady(page);
  await expect(hero.getByText("Add your first mod")).toBeVisible();
  await expect(mouth(page)).toHaveAccessibleName("Play My mods");
  await expect(mouth(page)).toBeEnabled();
  await page.waitForTimeout(2700); // the install toast goes
  await shot(page, "5-ready");

  // 5. a mod dragged over the window: the veil says where it goes
  await page.evaluate(() => {
    const d = new DataTransfer();
    d.items.add(new File(["x"], "night-owl-1.2.0.zip"));
    const el = document.querySelector(".k-hero")!;
    (window as unknown as { __drag: ReturnType<typeof setInterval> }).__drag = setInterval(() => el.dispatchEvent(new DragEvent("dragover", { dataTransfer: d, bubbles: true, cancelable: true })), 100);
  });
  const veil = page.locator(".k-dropveil");
  await expect(veil).toBeVisible();
  await expect(veil).toContainText("Added and switched on");
  await shot(page, "6-drag-over", 300);
  await page.evaluate(() => clearInterval((window as unknown as { __drag: ReturnType<typeof setInterval> }).__drag));

  // 6. dropped anywhere (here on Play): imported, on in My mods, applied; the toast says so with Undo
  await dropOn(page, hero, "night-owl-1.2.0.zip");
  await expect(veil).toHaveCount(0);
  const added = toastWith(page, "Added Night Owl");
  await expect(added).toBeVisible({ timeout: 5000 });
  await expect(added.locator(".k-toast-detail")).toHaveText("On at the next Play.");
  await expect(added.getByRole("button", { name: "Undo" })).toBeVisible();
  await expect(say(page)).toHaveText("1 mod on.", { timeout: 5000 });
  await expect(hero.getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "true");
  await shot(page, "7-added");

  // 7. Undo: out of the profile again; the library keeps its copy, so it is in the list, off
  await added.getByRole("button", { name: "Undo" }).click();
  await expect(toastWith(page, "Removed Night Owl")).toBeVisible();
  await expect(say(page)).toHaveText("0 mods on, 1 off.", { timeout: 5000 });
  await expect(hero.getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "false");

  // 8. dropped on another page: the same; Play's list has it, on
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Saves" }).click();
  await dropOn(page, page.locator(".k-sheet"), "night-owl-1.2.0.zip");
  await expect(toastWith(page, "Added Night Owl")).toBeVisible({ timeout: 5000 });
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(page.locator(".k-hero").getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "true", { timeout: 5000 });
  expect(errors).toEqual([]);
});

test("a mod that needs another says which, briefly", async ({ page }) => {
  await start(page, "fresh");
  await installOnPlay(page);
  await dropOn(page, page.locator(".k-hero"), "more-hotbars-2.1.0.zip");
  const t = toastWith(page, "Added More Hotbars");
  await expect(t).toBeVisible({ timeout: 5000 });
  await expect(t.locator(".k-toast-detail")).toHaveText("Needs ui-lib.");
  // the same file again: already there
  await dropOn(page, page.locator(".k-hero"), "more-hotbars-2.1.0.zip");
  await expect(toastWith(page, "More Hotbars is on already")).toBeVisible({ timeout: 5000 });
});

test("a mod dropped before Kiln is installed is added once Install Kiln is done", async ({ page }) => {
  await start(page, "fresh");
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln", { timeout: 8000 });
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip");
  const first = toastWith(page, "Install Kiln first");
  await expect(first).toBeVisible();
  await shot(page, "8-install-first");
  await first.getByRole("button", { name: "Install Kiln" }).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  await expect(toastWith(page, "Added Night Owl")).toBeVisible({ timeout: 12_000 });
  await expect(say(page)).toHaveText("1 mod on.", { timeout: 5000 });
  expect((await page.evaluate(async () => (await import("/src/api/commands.ts")).profileActive("client-steam")))?.profileId).toBe("my-mods");
});

test("nothing found: Add a folder and Look again stay", async ({ page }) => {
  await start(page, "empty");
  const hero = page.locator(".k-hero");
  await expect(hero.getByText("Kiln did not find Enshrouded in your Steam libraries.")).toBeVisible({ timeout: 8000 });
  await expect(hero.getByRole("button", { name: "Add a folder" })).toBeVisible();
  await expect(hero.getByRole("button", { name: "Look again" })).toBeVisible();
  await expect(mouth(page)).toHaveAccessibleName(/^Add game/);
  await shot(page, "0-not-found");
  await hero.getByRole("button", { name: "Add a folder" }).click();
  await expect(page.getByRole("dialog", { name: "Add a game folder" })).toBeVisible();
});

test("a modpack zip asks for consent before anything is installed", async ({ page }) => {
  await start(page, "fresh");
  await installOnPlay(page);
  await dropOn(page, page.locator(".k-hero"), "ember-isles-modpack-3.zip");
  const dialog = page.getByRole("dialog", { name: "Install this modpack?" });
  await expect(dialog).toBeVisible({ timeout: 5000 });
  await expect(dialog.getByText("This runs code on your PC")).toBeVisible();
  const install = dialog.getByRole("button", { name: "Install" });
  await expect(install).toBeDisabled();
  await shot(page, "9-modpack-consent");
  await dialog.getByText("I trust it and want its mods").click();
  await install.click();
  await expect(page.getByRole("dialog", { name: "Modpack installed" })).toBeVisible({ timeout: 8000 });
  await page.getByRole("dialog", { name: "Modpack installed" }).getByRole("button", { name: "Close" }).click();
  // the modpack flow loads its server's profile; Play names it in one small line that leads to Settings
  await expect(page.locator(".k-hero").getByRole("link", { name: "Profile: Server: Ember Isles" })).toBeVisible({ timeout: 5000 });
});

// What the activation a dropped modpack or a picked profile writes kept in the game folder (shell/notices.ts).
const KEPT = { retained: [{ path: "mods/XHL-Vein-Mining", reason: "files Kiln placed were changed or removed, or code was added (native-plugin.ini). It is off in the profile, but its own loader may still load it from there.", mayRun: true }] };
const ACT = { gameId: "client-steam", profileId: "ember-run", generation: 9, pathMode: "z", profileJson: "/x/kiln/profile.json", copied: 0, fingerprint: "kms1:00", activated: "2026-11-11T10:00:00Z", ...KEPT };
type PlanMock = { __kilnMock: { planActivation: (r: unknown) => void; planCommand: (c: string, p: unknown) => void } };

test("a dropped modpack's activation says what it kept in the game folder", async ({ page }) => {
  await start(page, "fresh");
  await installOnPlay(page);
  await page.evaluate((r) => (window as unknown as PlanMock).__kilnMock.planActivation(r), KEPT);
  await dropOn(page, page.locator(".k-hero"), "ember-isles-modpack-3.zip");
  const dialog = page.getByRole("dialog", { name: "Install this modpack?" });
  await dialog.getByText("I trust it and want its mods").click();
  await dialog.getByRole("button", { name: "Install" }).click();
  await expect(toastWith(page, "Some mod folders were kept")).toContainText("mods/XHL-Vein-Mining", { timeout: 8000 });
});

test("a mod added to a picked profile says what writing that profile kept in the game folder", async ({ page }) => {
  await start(page, null, "/");
  await loadNoProfile(page);
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip");
  await toastWith(page, "Added Night Owl, switched off").getByRole("button", { name: "Add to a profile" }).click();
  await page.evaluate((a) => (window as unknown as PlanMock).__kilnMock.planCommand("library_add_to_profile", { result: {
    import: { imported: [], existing: [], skipped: [], notes: [] }, profileId: "ember-run", profileName: "Ember run", created: false, resumed: false,
    added: [{ kind: "kiln", id: "night-owl", version: "1.2.0", name: "Night Owl" }], already: [], changes: [], applied: true, running: false,
    activation: a, missing: [], libraryOnly: null, modpacks: [] } }), ACT);
  await page.getByRole("dialog", { name: "Add to which profile?" }).getByRole("button", { name: /Ember run/ }).click();
  await expect(toastWith(page, "Added Night Owl to Ember run")).toBeVisible({ timeout: 5000 });
  await expect(toastWith(page, "Some mod folders were kept")).toContainText("mods/XHL-Vein-Mining");
});

test("players with profiles: a drop is on in the loaded one; the setting can turn that off", async ({ page }) => {
  await start(page, null, "/");
  const hero = page.locator(".k-hero");
  // setting on (the default): on in the loaded profile (Ember run)
  await dropOn(page, hero, "night-owl-1.2.0.zip");
  await expect(toastWith(page, "Added Night Owl")).toBeVisible({ timeout: 5000 });
  await expect(hero.getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "true", { timeout: 5000 });
  // off: a drop joins the list switched off
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  const sw = page.getByRole("switch", { name: /^Switch new mods on/ });
  await expect(sw).toBeChecked();
  await page.getByText("Switch new mods on", { exact: true }).click();
  await expect(sw).not.toBeChecked();
  await dropOn(page, page.locator(".k-sheet"), "dawn-lamp-2.0.0.zip");
  const imported = toastWith(page, "Added Dawn Lamp, switched off");
  await expect(imported).toBeVisible({ timeout: 5000 });
  await imported.getByRole("button", { name: "Show" }).click();
  await expect(page).toHaveURL(/#\/$/);
  await expect(hero.getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "true", { timeout: 5000 });
  await expect(hero.getByRole("switch", { name: "Dawn Lamp" })).toHaveAttribute("aria-checked", "false");
  const p = await page.evaluate(async () => (await import("/src/api/commands.ts")).profileGet("ember-run"));
  expect(p.mods.kiln.map((m) => m.id)).toContain("night-owl");
  expect(p.mods.kiln.map((m) => m.id)).not.toContain("dawn-lamp");
});

test("the first run's Play states fit 1024×640", async ({ page }) => {
  await page.setViewportSize({ width: 1024, height: 640 });
  await start(page, "fresh");
  const fits = async (what: string) => {
    const over = await page.evaluate(() => ({ x: document.documentElement.scrollWidth > innerWidth + 1, y: document.documentElement.scrollHeight > innerHeight + 1 }));
    expect(over, what).toEqual({ x: false, y: false });
    // the sentence and its links stay clear of the mouth
    const say = await page.locator(".k-hero-say").boundingBox();
    const door = await mouth(page).boundingBox();
    expect(say && door && say.x + say.width <= door.x + 1, what).toBeTruthy();
  };
  await fits("looking");
  await expect(mouth(page)).toHaveAccessibleName("Install Kiln", { timeout: 8000 });
  await fits("install");
  await mouth(page).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install" }).click();
  await expect(page.locator(".k-hero .k-task")).toBeVisible();
  await fits("installing");
  await shot(page, "4b-installing-1024x640");
  await myModsReady(page);
  await fits("ready");
  await shot(page, "5b-ready-1024x640", 2800);
});

test("a drop says what was left out and why; Details lists everything", async ({ page }) => {
  await start(page, null, "/");
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip", "readme.txt");
  const toast = toastWith(page, "Added Night Owl");
  await expect(toast).toBeVisible({ timeout: 5000 });
  // one short line: the file and the reason, with the period the backend leaves out
  await expect(toast.getByText(/readme\.txt: .*\.$/)).toBeVisible();
  await toast.getByRole("button", { name: "Details" }).click();
  const dlg = page.getByRole("dialog", { name: "What was added" });
  await expect(dlg.getByText("Night Owl")).toBeVisible();
  await expect(dlg.getByText("Left out")).toBeVisible();
  await expect(dlg.getByText("readme.txt")).toBeVisible();
  await dlg.getByRole("button", { name: "OK" }).click();
});

test("no profile is loaded and none is remembered: the drop says it is in the list, off, and offers the profiles", async ({ page }) => {
  await start(page, null, "/");
  // "No profile" on purpose: the launcher forgets the profile it had for the game
  await loadNoProfile(page);
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip");
  const toast = toastWith(page, "Added Night Owl, switched off");
  await expect(toast).toBeVisible({ timeout: 5000 });
  await expect(toast.getByText("No profile is loaded in this game, so nothing is switched on.")).toBeVisible();
  await toast.getByRole("button", { name: "Add to a profile" }).click();
  const pick = page.getByRole("dialog", { name: "Add to which profile?" });
  await pick.getByRole("button", { name: /Ember run/ }).click();
  const added = toastWith(page, "Added Night Owl to Ember run");
  await expect(added).toBeVisible({ timeout: 5000 });
  await expect(added.getByText("Ember run is not the loaded profile: Load it switches them on in this game.")).toBeVisible();
  const p = await page.evaluate(async () => (await import("/src/api/commands.ts")).profileGet("ember-run"));
  expect(p.mods.kiln.find((m) => m.id === "night-owl")?.enabled).toBe(true);
  // the toast's way on (it used to say "load it in Settings, Profiles" and lead nowhere): the mod is on, on Play
  const hero = page.locator(".k-hero");
  await expect(hero.getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "false");
  await added.getByRole("button", { name: "Load it" }).click();
  await expect(toastWith(page, "Ember run is loaded")).toBeVisible({ timeout: 5000 });
  await expect(hero.getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "true", { timeout: 5000 });
  await expect.poll(async () => (await page.evaluate(async () => (await import("/src/api/commands.ts")).profileActive("client-steam")))?.profileId).toBe("ember-run");
});

test("Add to which profile? makes a new profile with the dropped mod in it, and Load it plays it", async ({ page }) => {
  await start(page, null, "/");
  await loadNoProfile(page);
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip");
  await toastWith(page, "Added Night Owl, switched off").getByRole("button", { name: "Add to a profile" }).click();
  const pick = page.getByRole("dialog", { name: "Add to which profile?" });
  await pick.getByRole("button", { name: "New profile…" }).click();
  const make = pick.getByRole("button", { name: "Make and add" });
  await expect(make).toBeDisabled();
  await pick.getByLabel("Name of the new profile").fill("Night run");
  await make.click();
  await expect(pick).toBeHidden();
  const added = toastWith(page, "Added Night Owl to Night run");
  await expect(added).toBeVisible({ timeout: 5000 });
  const p = await page.evaluate(async () => (await import("/src/api/commands.ts")).profileGet("night-run"));
  expect(Object.values(p.mods).flat().map((m) => [m.id, m.enabled])).toEqual([["night-owl", true]]);
  await added.getByRole("button", { name: "Load it" }).click();
  await expect(page.locator(".k-hero").getByRole("switch", { name: "Night Owl" })).toHaveAttribute("aria-checked", "true", { timeout: 5000 });
  await expect(page.locator(".k-hero").getByRole("link", { name: "Profile: Night run" })).toBeVisible();
});

// review finding 3: the toast's Load it while a Load from Settings still runs: one activation at a time, the later
// click is refused with a sentence, and the profile the player loaded first stays loaded
test("Load it while another profile is still loading is refused, and the first load stays", async ({ page }) => {
  await start(page, null, "/");
  await loadNoProfile(page);
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip");
  await toastWith(page, "Added Night Owl, switched off").getByRole("button", { name: "Add to a profile" }).click();
  await page.getByRole("dialog", { name: "Add to which profile?" }).getByRole("button", { name: /Ember run/ }).click();
  const added = toastWith(page, "Added Night Owl to Ember run");
  await expect(added).toBeVisible({ timeout: 5000 });
  await page.evaluate(() => { location.hash = "#/settings"; });
  const vanilla = page.getByRole("region", { name: "Profiles" }).getByRole("listitem").filter({ hasText: "Vanilla plus" });
  await vanilla.getByRole("button", { name: "Load" }).click(); // the mock takes 1.5 s
  await added.getByRole("button", { name: "Load it" }).click();
  await expect(toastWith(page, "Could not load Ember run")).toContainText("Kiln is still loading a profile in this game. Try again when that is done.");
  await expect(vanilla).toContainText("Loaded", { timeout: 5000 });
  await page.waitForTimeout(500);
  await expect.poll(async () => (await page.evaluate(async () => (await import("/src/api/commands.ts")).profileActive("client-steam")))?.profileId).toBe("vanilla-plus");
});

// review finding 4: the profile is made, then adding the drop to it fails: the dialog keeps the drop, lists the new
// profile, and says what happened; picking it again adds the mod
test("Make and add that fails after the profile was made keeps the drop, says so, and the new profile takes it on a retry", async ({ page }) => {
  await start(page, null, "/");
  await loadNoProfile(page);
  await dropOn(page, page.locator(".k-hero"), "night-owl-1.2.0.zip");
  await toastWith(page, "Added Night Owl, switched off").getByRole("button", { name: "Add to a profile" }).click();
  const pick = page.getByRole("dialog", { name: "Add to which profile?" });
  await pick.getByRole("button", { name: "New profile…" }).click();
  await pick.getByLabel("Name of the new profile").fill("Night run");
  await page.evaluate(() => (window as unknown as PlanMock).__kilnMock.planCommand("library_add_to_profile", { error: { code: "io", message: "the disk is full" } } as never));
  await pick.getByRole("button", { name: "Make and add" }).click();
  await expect(toastWith(page, "Not added")).toContainText("the disk is full");
  await expect(toastWith(page, "Night run was made, without Night Owl")).toContainText("Pick Night run here to add it again");
  await expect(pick).toBeVisible();
  await pick.getByRole("button", { name: /Night run/ }).click();
  await expect(pick).toBeHidden();
  await expect(toastWith(page, "Added Night Owl to Night run")).toBeVisible({ timeout: 5000 });
  const p = await page.evaluate(async () => (await import("/src/api/commands.ts")).profileGet("night-run"));
  expect(Object.values(p.mods).flat().map((m) => [m.id, m.enabled])).toEqual([["night-owl", true]]);
});
