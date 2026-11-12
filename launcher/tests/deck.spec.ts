// Deck mode (src/deck/, docs/kiln/launcher/deck.md) at the Steam Deck's 1280×800, in mock mode: how it is chosen,
// spatial focus, every screen reachable and left with B, Play, adding a mod from Downloads, the hold to uninstall,
// dialogs, touch, and every input source (keyboard, the native reader's kiln://gamepad events, the browser Gamepad
// API). `KILN_DECK_SHOTS=<dir>` also saves a screenshot of every screen there.
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";

test.use({ viewport: { width: 1280, height: 800 } });

const DECK = "/?deck=steamdeck#/";

/** A press and release on the native reader's event (what src-tauri/src/deck/gamepad.rs emits). */
async function pad(page: Page, button: string, holdMs = 0) {
  const send = (pressed: boolean) => page.evaluate(([b, p]) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
    .emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
  await send(true);
  if (holdMs) await page.waitForTimeout(holdMs);
  await send(false);
  await page.waitForTimeout(60);
}

const focused = (page: Page) => page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset?.deckId ?? null);
const screen = (page: Page) => page.evaluate(() => document.querySelector("[data-deck-screen]")?.getAttribute("data-deck-screen") ?? null);

async function openDeck(page: Page, url = DECK) {
  await page.goto(url);
  await expect(page.locator('[data-deck-screen="home"]')).toBeVisible();
  await expect.poll(() => focused(page)).toBe("mouth");
}

test.describe("choosing the UI", () => {
  test("a Steam Deck start (Game Mode env) lands in Deck mode, never mounting the desktop shell", async ({ page }) => {
    await page.addInitScript(() => {
      const get = HTMLCanvasElement.prototype.getContext;
      (window as unknown as { __gl: string[] }).__gl = [];
      HTMLCanvasElement.prototype.getContext = function (this: HTMLCanvasElement, ...a: Parameters<typeof get>) {
        if (String(a[0]).startsWith("webgl")) (window as unknown as { __gl: string[] }).__gl.push(this.className);
        return get.apply(this, a);
      } as typeof get;
    });
    await openDeck(page);
    expect(page.url()).toContain("#/deck");
    await expect(page.locator(".k-app")).toHaveCount(0);
    // one WebGL context: the Deck's fire, never the desktop stage's
    await expect(page.locator(".k-stage-gl")).toHaveCount(0);
    const canvases = await page.evaluate(() => (window as unknown as { __gl: string[] }).__gl);
    expect(canvases.filter((c) => c !== "deck-fire-gl")).toEqual([]);
    // the detection is shown for testers
    await page.goto("/?deck=steamdeck#/deck/settings");
    await expect(page.getByTestId("deck-detection")).toContainText("SteamDeck=1");
  });

  test("--deck forces it; --desktop and a plain desktop do not", async ({ page }) => {
    await page.goto("/?deck=forced#/");
    await expect(page.locator(".deck")).toBeVisible();
    await page.goto("/?deck=desktop#/");
    await expect(page.locator(".k-app")).toBeVisible();
    await expect(page.locator(".deck")).toHaveCount(0);
    await page.goto("/#/");
    await expect(page.locator(".k-app")).toBeVisible();
  });

  test("the setting wins over detection, and both UIs switch to each other", async ({ page }) => {
    await openDeck(page);
    // Deck Settings: Start in Auto -> Deck mode -> Desktop
    for (let i = 0; i < 6; i++) await pad(page, "rb");
    await expect.poll(() => screen(page)).toBe("settings");
    await expect.poll(() => focused(page)).toBe("set-desktop");
    await page.keyboard.press("ArrowDown");
    await expect.poll(() => focused(page)).toBe("set-start");
    await pad(page, "a");
    await pad(page, "a");
    await expect(page.locator('[data-deck-id="set-start"]')).toContainText("Desktop");
    // the order (the mock forgets settings on reload, so the rule itself is checked here): flag, setting, detection
    const picks = await page.evaluate(async () => {
      const { chooseUi } = await import("/src/deck/mode.tsx");
      const deck = { detected: true, signals: ["SteamDeck=1"], gamescope: true, device: "jupiter", forced: null };
      const plain = { ...deck, detected: false, signals: [], gamescope: false, device: null };
      const info = (d: unknown) => ({ version: "", kiln: "", platform: "linux", arch: "", dataDir: "", commit: null, mock: true, deck: d });
      const st = (uiMode: string | null) => ({ steamRoots: [], defaultGameId: null, allowHttpModpacks: false, savesKeep: 10, updateCheck: true, uiMode });
      return [
        chooseUi(info(deck) as never, st(null) as never), chooseUi(info(deck) as never, st("desktop") as never), chooseUi(info(plain) as never, st("deck") as never),
        chooseUi(info({ ...deck, forced: "desktop" }) as never, st("deck") as never), chooseUi(info(plain) as never, st("auto") as never), chooseUi(null, null),
      ].map((c) => `${c.ui}:${c.why}`);
    });
    expect(picks).toEqual(["deck:detected", "desktop:setting", "deck:setting", "desktop:flag", "desktop:default", "desktop:default"]);
    // the desktop Settings has the card to go back
    await page.goto("/?deck=steamdeck#/settings");
    await page.locator(".k-splash").waitFor({ state: "detached" });
    const card = page.locator(".k-card", { hasText: "Steam Deck" });
    await card.getByRole("radio", { name: "Deck mode" }).click();
    await expect(card.getByRole("radio", { name: "Deck mode" })).toHaveAttribute("aria-checked", "true");
    await card.getByRole("radio", { name: "Auto" }).click();
    await page.getByRole("button", { name: "Open Deck mode" }).click();
    await expect(page.locator(".deck")).toBeVisible();
    // and Deck Settings' first row goes to the desktop UI now (the screen remembers its last focus: "Start in")
    for (let i = 0; i < 6; i++) await pad(page, "rb");
    await expect.poll(() => focused(page)).toBe("set-start");
    await pad(page, "up");
    expect(await focused(page)).toBe("set-desktop");
    await pad(page, "a");
    await expect(page.locator(".k-app")).toBeVisible();
  });
});

test.describe("controls", () => {
  test("focus moves predictably with the D-pad and stops at the edges", async ({ page }) => {
    await openDeck(page);
    // Home has no control left of the mouth now (no profile switch in the main flow): focus stays at the edge
    await page.keyboard.press("ArrowLeft");
    expect(await focused(page)).toBe("mouth");
    await page.keyboard.press("ArrowUp");
    expect(await focused(page)).toBe("mouth");
    await page.keyboard.press("ArrowRight");
    expect(await focused(page)).toBe("mouth");
    // a list moves row by row, both ways, with the pad's D-pad too (every kind, by name)
    await pad(page, "rb");
    await expect.poll(() => focused(page)).toBe("mod-emf-beastmaster");
    const order: (string | null)[] = [];
    for (let i = 0; i < 4; i++) { await pad(page, "down"); order.push(await focused(page)); }
    expect(order).toEqual(["mod-shroudforge-better-chests", "mod-kiln-big-stacks", "mod-shroudlink-chestsort", "mod-flame-creative-build"]);
    await pad(page, "up");
    expect(await focused(page)).toBe("mod-shroudlink-chestsort");
    // the focused row is scrolled into view at the bottom of a long list
    for (let i = 0; i < 12; i++) await pad(page, "down");
    const last = await focused(page);
    const box = await page.locator(`[data-deck-id="${last}"]`).boundingBox();
    expect(box!.y + box!.height).toBeLessThanOrEqual(736.5); // sub-pixel: the list scrolled to its end
  });

  test("every screen is reachable with L1/R1 and the menu, and B goes back to Home", async ({ page }) => {
    await openDeck(page);
    const names = ["home", "mods", "add", "catalog", "kiln", "help", "settings"];
    for (const n of names.slice(1)) {
      await pad(page, "rb");
      await expect.poll(() => screen(page)).toBe(n);
      // every screen gives something focus
      await expect.poll(() => focused(page)).not.toBeNull();
    }
    // no wrap past the last tab
    await pad(page, "rb");
    expect(await screen(page)).toBe("settings");
    for (const n of names.slice(1)) {
      await page.goto(`/?deck=steamdeck#/deck/${n === "add" ? "add" : n}`);
      await expect.poll(() => screen(page)).toBe(n);
      await pad(page, "b");
      await expect.poll(() => screen(page)).toBe("home");
      await expect.poll(() => focused(page)).toBe("mouth");
    }
    // Start: the menu lists every screen; B closes it; picking one opens it
    await pad(page, "start");
    await expect(page.locator('[data-deck-dialog="menu"]')).toBeVisible();
    await pad(page, "b");
    await expect(page.locator('[data-deck-dialog="menu"]')).toHaveCount(0);
    await pad(page, "start");
    for (let i = 0; i < 5; i++) await pad(page, "down");
    await pad(page, "a");
    await expect.poll(() => screen(page)).toBe("help");
    // View opens Help from anywhere
    await pad(page, "b");
    await pad(page, "select");
    await expect.poll(() => screen(page)).toBe("help");
  });

  test("keyboard (Steam Input templates): Q/E switch tabs, Escape is B, Enter is A", async ({ page }) => {
    await openDeck(page);
    await page.keyboard.press("e");
    await expect.poll(() => screen(page)).toBe("mods");
    await page.keyboard.press("PageDown");
    await expect.poll(() => screen(page)).toBe("add");
    await page.keyboard.press("q");
    await expect.poll(() => screen(page)).toBe("mods");
    await page.keyboard.press("Escape");
    await expect.poll(() => screen(page)).toBe("home");
  });

  test("the browser Gamepad API drives it when there is no native reader", async ({ page }) => {
    await page.addInitScript(() => {
      const btn = () => ({ pressed: false, touched: false, value: 0 });
      const p = { id: "Xbox 360 Controller (STANDARD GAMEPAD)", index: 0, connected: true, mapping: "standard", timestamp: 0, axes: [0, 0, 0, 0], buttons: Array.from({ length: 17 }, btn) };
      (window as unknown as { __pad: typeof p }).__pad = p;
      Object.defineProperty(navigator, "getGamepads", { value: () => [p] });
    });
    await openDeck(page);
    const hold = async (i: number, ms = 120) => {
      await page.evaluate((k) => { (window as unknown as { __pad: { buttons: { pressed: boolean; value: number }[] } }).__pad.buttons[k] = { pressed: true, value: 1 }; }, i);
      await page.waitForTimeout(ms);
      await page.evaluate((k) => { (window as unknown as { __pad: { buttons: { pressed: boolean; value: number }[] } }).__pad.buttons[k] = { pressed: false, value: 0 }; }, i);
      await page.waitForTimeout(80);
    };
    await page.evaluate(() => window.dispatchEvent(new Event("gamepadconnected")));
    await hold(5); // RB
    await expect.poll(() => screen(page)).toBe("mods");
    // the stick held down repeats: several rows
    await page.evaluate(() => { (window as unknown as { __pad: { axes: number[] } }).__pad.axes[1] = 0.95; });
    await page.waitForTimeout(700);
    await page.evaluate(() => { (window as unknown as { __pad: { axes: number[] } }).__pad.axes[1] = 0; });
    const at = await focused(page);
    expect(["mod-kiln-big-stacks", "mod-shroudlink-chestsort", "mod-flame-creative-build"]).toContain(at);
    await hold(1); // B
    await expect.poll(() => screen(page)).toBe("home");
  });

  test("touch works without the controller", async ({ browser }) => {
    const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 }, hasTouch: true });
    const page = await ctx.newPage();
    await openDeck(page);
    await page.getByRole("button", { name: "Mods", exact: true }).tap();
    await expect.poll(() => screen(page)).toBe("mods");
    const row = page.locator('[data-deck-id="mod-kiln-big-stacks"]');
    await expect(row).toContainText("Off");
    await row.tap();
    await expect(row).toContainText("On");
    // the hint bar's buttons are tappable too: B
    await page.locator('.deck-hint[data-hint="b"]').tap();
    await expect.poll(() => screen(page)).toBe("home");
    await ctx.close();
  });
});

test.describe("what a player does", () => {
  test("Play starts the game", async ({ page }) => {
    await openDeck(page);
    await expect(page.getByTestId("deck-last-boot")).toContainText("Booted");
    // the count covers every kind
    await expect(page.getByTestId("deck-mods")).toContainText("12 mods on, 15 off");
    await pad(page, "a");
    await expect(page.getByTestId("deck-state")).toHaveText(/Starting|Running/);
    await expect(page.locator(".deck-toast")).toContainText("Starting");
    await expect(page.getByTestId("deck-state")).toHaveText(/Running/, { timeout: 8000 });
    await page.evaluate(() => (window as unknown as { __kilnMock: { stopGame: () => void } }).__kilnMock.stopGame());
  });

  test("add a mod from Downloads with A; it is on in the Mods list", async ({ page }) => {
    await openDeck(page);
    await pad(page, "rb");
    await pad(page, "rb");
    await expect.poll(() => screen(page)).toBe("add");
    await expect(page.locator(".deck-title")).toHaveText("Downloads");
    await expect.poll(() => focused(page)).toBe("file-lantern-glow-1.2.0.zip");
    // junk (photos, a png) is not listed
    await expect(page.locator('[data-deck-id="file-holiday-photos.zip"]')).toHaveCount(0);
    await pad(page, "a");
    await expect(page.locator(".deck-toast")).toContainText("Added Lantern Glow");
    await expect(page.locator('[data-deck-id="file-lantern-glow-1.2.0.zip"]')).toContainText("On");
    await pad(page, "lb");
    await expect.poll(() => screen(page)).toBe("mods");
    await expect(page.locator('[data-deck-id="mod-kiln-lantern-glow"]')).toContainText("On");
  });

  test("browse other folders: Places, a folder, B goes back up", async ({ page }) => {
    await openDeck(page);
    await page.goto("/?deck=steamdeck#/deck/add");
    await expect(page.locator(".deck-title")).toHaveText("Downloads");
    await pad(page, "y");
    await expect(page.locator(".deck-places")).toBeVisible();
    await page.locator('[data-deck-id="place-card-SD card"]').focus();
    await pad(page, "a");
    await expect(page.locator(".deck-title")).toHaveText("mmcblk0p1");
    // a folder: A opens it, B comes back
    await expect.poll(() => focused(page)).not.toBeNull();
    await page.locator('[data-deck-id="dir-mods"]').focus();
    await pad(page, "a");
    await expect(page.locator(".deck-title")).toHaveText("mods");
    await expect(page.locator('[data-deck-id="file-big-loot-1.0.0.zip"]')).toBeVisible();
    await pad(page, "b");
    await expect(page.locator(".deck-title")).toHaveText("mmcblk0p1");
    await pad(page, "b");
    await expect(page.locator(".deck-title")).toHaveText("Downloads");
    await pad(page, "b");
    await expect.poll(() => screen(page)).toBe("home");
  });

  test("a modpack zip asks for consent before anything installs", async ({ page }) => {
    await openDeck(page);
    await page.goto("/?deck=steamdeck#/deck/add");
    await page.locator('[data-deck-id="file-server-ember-isles-modpack.zip"]').focus();
    await pad(page, "a");
    await expect(page.locator('[data-deck-dialog="modpack"]')).toContainText("runs code on your PC");
    await pad(page, "b");
    await expect(page.locator('[data-deck-dialog="modpack"]')).toHaveCount(0);
    expect(await focused(page)).toBe("file-server-ember-isles-modpack.zip");
  });

  test("Mods: A turns a mod off, X removes it after a confirm; the profile is switched in Settings", async ({ page }) => {
    await openDeck(page);
    await pad(page, "rb");
    await expect.poll(() => focused(page)).toBe("mod-emf-beastmaster");
    // no profile switch on the Mods screen
    await expect(page.locator('.deck-hint[data-hint="y"]')).toHaveCount(0);
    await page.locator('[data-deck-id="mod-kiln-new-items"]').focus();
    const row = page.locator('[data-deck-id="mod-kiln-new-items"]');
    await expect(row).toContainText("On");
    await pad(page, "a");
    await expect(row).toContainText("Off");
    await expect(page.locator('.deck-hint[data-hint="a"]')).toContainText("Turn on");
    await pad(page, "x");
    await expect(page.locator('[data-deck-dialog="remove"]')).toBeVisible();
    // under a dialog L1/R1 do nothing
    await pad(page, "rb");
    expect(await screen(page)).toBe("mods");
    // once the check of what else uses it is done, focus is on its primary button (Remove), so A removes
    const remove = page.locator('[data-deck-dialog="remove"]').getByRole("button", { name: "Remove", exact: true });
    await expect(remove).toBeEnabled();
    await expect(remove).toBeFocused();
    await pad(page, "a");
    await expect(row).toHaveCount(0);
    // removed: out of the profile and out of the library
    await expect.poll(() => page.evaluate(async () => (await (await import("/src/api/commands.ts")).libraryList()).some((e) => e.id === "new-items"))).toBe(false);
    // Settings has the profiles: A opens them, B closes
    await page.goto("/?deck=steamdeck#/deck/settings");
    await page.locator('[data-deck-id="set-profile"]').focus();
    await expect(page.locator('[data-deck-id="set-profile"]')).toContainText("Ember run");
    await pad(page, "a");
    await expect(page.locator('[data-deck-dialog="profiles"]')).toBeVisible();
    await pad(page, "b");
    await expect(page.locator('[data-deck-dialog="profiles"]')).toHaveCount(0);
  });

  test("Kiln: a short press does nothing, holding A uninstalls, then Install puts it back", async ({ page }) => {
    await openDeck(page);
    await page.goto("/?deck=steamdeck#/deck/kiln");
    await expect.poll(() => focused(page)).toBe("facts");
    await pad(page, "down");
    expect(await focused(page)).toBe("uninstall");
    await pad(page, "a", 300);
    await page.waitForTimeout(1200);
    await expect(page.locator('[data-deck-id="uninstall"]')).toBeVisible();
    await page.keyboard.down("Enter");
    await page.waitForTimeout(1400);
    await page.keyboard.up("Enter");
    await expect(page.locator('[data-deck-id="install"]')).toBeVisible({ timeout: 8000 });
    await page.locator('[data-deck-id="install"]').focus();
    await pad(page, "a");
    await expect(page.locator('[data-deck-dialog="install"]')).toBeVisible();
    await pad(page, "a");
    await expect(page.locator('[data-deck-id="uninstall"]')).toBeVisible({ timeout: 10000 });
  });

  test("first start without a game: Find game, then pick the folder by hand", async ({ page }) => {
    await page.goto("/?mock=empty&deck=steamdeck#/");
    await expect(page.locator(".deck-mouth")).toHaveAttribute("aria-label", /Find game/, { timeout: 8000 });
    await expect(page.getByTestId("deck-kiln-state")).toContainText("Game not found");
    await page.locator(".deck-mouth").focus();
    await pad(page, "a");
    await expect(page.locator('[data-deck-dialog="find-game"]')).toBeVisible({ timeout: 8000 });
    await page.locator('[data-deck-id="place-games-Steam games"]').focus();
    await pad(page, "a");
    await page.locator('[data-deck-id="dir-Enshrouded"]').waitFor();
    await page.locator('[data-deck-id="dir-Enshrouded"]').focus();
    await pad(page, "a");
    await expect.poll(() => focused(page)).toBe("use-folder");
    await pad(page, "a");
    await expect(page.locator('[data-deck-dialog="find-game"]')).toHaveCount(0);
    await expect(page.locator(".deck-mouth")).toHaveAttribute("aria-label", /Install/);
    // what opened the dialog (Pick folder) is gone: focus lands on the screen's first stop, the arch
    await expect.poll(() => focused(page)).toBe("mouth");
  });

  test("the Hearth fire runs in Deck mode: the mouth on Home, the pilot light elsewhere; Settings turns it off", async ({ page }) => {
    await openDeck(page);
    const gl = await page.evaluate(() => !!document.createElement("canvas").getContext("webgl"));
    test.skip(!gl, "no WebGL in this browser");
    await expect(page.locator(".deck")).toHaveAttribute("data-gl", "1");
    await expect(page.locator(".deck-fire-gl")).toBeVisible();
    // the focused Play arch shows the gold ring over the shader's mouth
    await expect(page.locator(".deck-mouth-ring")).toHaveCSS("opacity", "1");
    await page.goto("/?deck=steamdeck#/deck/settings");
    await page.locator('[data-deck-id="set-fire"]').focus();
    await expect(page.locator('[data-deck-id="set-fire"]')).toContainText("On");
    await pad(page, "a");
    await expect(page.locator('[data-deck-id="set-fire"]')).toContainText("Off");
    await expect(page.locator(".deck")).not.toHaveAttribute("data-gl", "1");
    await expect(page.locator(".deck-fire-gl")).toBeHidden();
    // the CSS fire still burns in the arch
    await pad(page, "b");
    await expect(page.locator(".deck-mouth-fire")).toBeVisible();
  });

  test("Help shows the exact launch option and the steps", async ({ page }) => {
    await openDeck(page);
    await page.goto("/?deck=steamdeck#/deck/help");
    await expect(page.getByTestId("deck-launch-option")).toHaveText('WINEDLLOVERRIDES="dinput8=n,b" %command%');
    await expect(page.locator(".deck-help")).toContainText("Launch Options");
    await expect(page.locator(".deck-help")).toContainText("Add a Non-Steam Game");
    await expect.poll(() => focused(page)).toBe("help-steam");
    await pad(page, "down");
    expect(await focused(page)).toBe("help-noload");
    await expect(page.locator(".deck-help")).toContainText("kiln/logs/proxy.log");
    await pad(page, "down");
    expect(await focused(page)).toBe("help-add");
  });
});

test("screenshots of every Deck screen (KILN_DECK_SHOTS)", async ({ page }) => {
  const dir = process.env.KILN_DECK_SHOTS;
  test.skip(!dir, "set KILN_DECK_SHOTS=<dir>");
  mkdirSync(dir!, { recursive: true });
  const shot = async (name: string) => { await page.waitForTimeout(450); await page.screenshot({ path: `${dir}/${name}.png` }); };
  await openDeck(page);
  await shot("01-home");
  await page.goto("/?deck=steamdeck#/deck/settings");
  await page.locator('[data-deck-id="set-profile"]').focus();
  await pad(page, "a");
  await shot("02-settings-profile");
  await pad(page, "b");
  for (const [n, file] of [["mods", "03-mods"], ["add", "04-add-mods"], ["kiln", "06-kiln"], ["help", "07-help"], ["settings", "08-settings"]] as const) {
    await page.goto(`/?deck=steamdeck#/deck/${n}`);
    await expect.poll(() => screen(page)).toBe(n);
    await shot(file);
    if (n === "add") {
      await pad(page, "y");
      await shot("05-add-mods-places");
    }
  }
  await pad(page, "start");
  await shot("09-menu");
  await page.goto("/?mock=empty&deck=steamdeck#/");
  await expect(page.locator(".deck-mouth")).toHaveAttribute("aria-label", /Find game/, { timeout: 8000 });
  await shot("10-home-no-game");
  await page.goto("/?mock=fresh&deck=steamdeck#/");
  await expect(page.locator(".deck-mouth")).toHaveAttribute("aria-label", /Install/, { timeout: 8000 });
  await shot("11-home-install-kiln");
  await page.goto("/?deck=steamdeck#/deck/kiln");
  await page.locator('[data-deck-id="uninstall"]').focus();
  await page.keyboard.down("Enter");
  await page.waitForTimeout(700);
  await page.screenshot({ path: `${dir}/12-kiln-holding.png` });
  await page.keyboard.up("Enter");
});

test.describe("notices", () => {
  const KEPT = { retained: [{ path: "mods/XHL-Vein-Mining", reason: "files Kiln placed were changed or removed, or code was added (native-plugin.ini). It is off in the profile, but its own loader may still load it from there.", mayRun: true }] };
  const plan = (page: Page, r: unknown = KEPT) => page.evaluate((r) => (window as unknown as { __kilnMock: { planActivation: (r: unknown) => void } }).__kilnMock.planActivation(r), r);

  test("adding a mod or a modpack from Downloads says what writing the profile kept", async ({ page }) => {
    await openDeck(page);
    await page.goto("/?deck=steamdeck#/deck/add");
    await expect.poll(() => focused(page)).toBe("file-lantern-glow-1.2.0.zip");
    await plan(page);
    await pad(page, "a");
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText("mods/XHL-Vein-Mining");
    await pad(page, "a");
    await expect(notice).toHaveCount(0);
    await page.locator('[data-deck-id="file-server-ember-isles-modpack.zip"]').focus();
    await pad(page, "a");
    const install = page.locator('[data-deck-dialog="modpack"]').getByRole("button", { name: "Install", exact: true });
    await expect(install).toBeEnabled();
    // the same folder again is not raised again (each sentence once per game: shell/notices.ts); another one is
    await plan(page, { retained: [{ ...KEPT.retained[0], path: "mods/XHL-Auto-Loot" }, ...KEPT.retained] });
    await install.click();
    await expect(notice).toContainText("mods/XHL-Auto-Loot", { timeout: 8000 });
    await expect(notice).not.toContainText("mods/XHL-Vein-Mining");
  });

  test("adding a mod from Downloads says what else it changed in the profile", async ({ page }) => {
    await openDeck(page);
    await page.goto("/?deck=steamdeck#/deck/add");
    await expect.poll(() => focused(page)).toBe("file-lantern-glow-1.2.0.zip");
    const line = "XHL-Core 1.4.0 stays in the profile: the XHL-Core that came with XHL-Auto-Loot is older.";
    await page.evaluate((n) => (window as unknown as { __kilnMock: { planAddNotices: (l: string[]) => void } }).__kilnMock.planAddNotices([n]), line);
    await pad(page, "a");
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText("Changes in your mod list");
    await expect(notice).toContainText(line);
    await pad(page, "a");
    await expect(notice).toHaveCount(0);
  });

  test("a long notice scrolls with the right stick, comes once, and focus goes back after several", async ({ page }) => {
    await openDeck(page);
    const many = Array.from({ length: 12 }, (_, i) => ({ path: `/home/player/.local/share/Steam/steamapps/common/Enshrouded/mods/Mod-${i}`, reason: "files Kiln placed were changed or removed, or code was added (native-plugin.ini). It is off in the profile, but its own loader may still load it from there.", mayRun: true }));
    expect(await focused(page)).toBe("mouth");
    await page.evaluate(async (retained) => {
      const { notifyActivation } = await import("/src/shell/notices.ts");
      const a = { gameId: "client-steam", profileId: "ember-run", generation: 9, pathMode: "z", profileJson: "/x", copied: 0, fingerprint: "kms1:00", activated: "2026-11-11T10:00:00Z", retained };
      await notifyActivation(a as never);
      // the next activation says the same while that notice is open: no second copy
      await notifyActivation(a as never);
      await notifyActivation({ ...a, retained: [], warnings: ["a second notice"] } as never);
    }, many);
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText("Some mod folders were kept");
    await expect(notice).not.toContainText("(2 times)");
    const body = notice.locator("[data-deck-notice]");
    const fits = await body.evaluate((el) => el.scrollHeight <= el.clientHeight);
    expect(fits, "a dozen folders do not fit at 1280x800").toBe(false);
    const send = (value: number) => page.evaluate((v) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
      .emit("kiln://gamepad", { kind: "axis", axis: "ry", value: v, device: "event9" }), value);
    for (let i = 0; i < 20; i++) await send(1);
    await send(0);
    await expect.poll(() => body.evaluate((el) => el.scrollTop)).toBeGreaterThan(0);
    await pad(page, "a");
    await expect(notice).toContainText("Notes on your mods");
    await pad(page, "a");
    await expect(notice).toHaveCount(0);
    await expect.poll(() => focused(page)).toBe("mouth");
  });

  test("an error folded into the earlier notices goes after six seconds; the folded notices stay", async ({ page }) => {
    await openDeck(page);
    await page.clock.install();
    await page.evaluate(async () => {
      const { toast } = await import("/src/ui/Toast.tsx");
      toast.error({ code: "io", message: "an old error" });
      for (let i = 0; i < 9; i++) toast.info(`Kept folder ${i}`, `mods/Mod-${i}: may still run`, { ms: 0 });
    });
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText("earlier notices");
    await page.clock.fastForward(7000);
    await expect(notice).toContainText("Kept folder 0");
    await expect(notice).not.toContainText("an old error");
  });

  test("a notice counts as seen when the player closes its dialog, not when it is displayed", async ({ page }) => {
    // two notices; the Deck shows one dialog at a time; the player closes the first, the second is displayed, the
    // launcher closes: the second comes again, the first does not
    await openDeck(page);
    const act = { gameId: "client-steam", profileId: "ember-run", generation: 9, pathMode: "z", profileJson: "/x", copied: 0, fingerprint: "kms1:00",
      activated: "2026-11-11T10:00:00Z", ...KEPT, warnings: ["shroudforge:a: the second notice"] };
    const notify = () => page.evaluate(async (a) => { await (await import("/src/shell/notices.ts")).notifyActivation(a as never); }, act);
    await notify();
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText("Some mod folders were kept");
    await expect(notice).not.toContainText("the second notice");
    await pad(page, "a");
    await expect(notice).toContainText("the second notice");
    // the launcher closes with the second one displayed, not closed
    await page.evaluate(async () => { (await import("/src/ui/Toast.tsx")).useToasts.setState({ items: [] }); });
    await expect(notice).toHaveCount(0);
    // the next activation says both again: the closed folder notice does not come, the displayed one does
    await notify();
    await expect(notice).toContainText("Notes on your mods");
    await expect(notice).toContainText("the second notice");
    await expect(notice).not.toContainText("mods/XHL-Vein-Mining");
    // closed with B now: not again
    await pad(page, "b");
    await expect(notice).toHaveCount(0);
    await notify();
    await page.waitForTimeout(300);
    await expect(notice).toHaveCount(0);
  });

  test("a long error opens as a notice: all its text, on the screen, scrolled and closed with the pad", async ({ page }) => {
    await openDeck(page);
    await page.clock.install();
    const long = Array.from({ length: 12 }, (_, i) => `part ${i} of a long error from the backend that names /home/player/.local/share/kiln/library/x/${i}.`).join(" ");
    await page.evaluate(async (m) => { (await import("/src/ui/Toast.tsx")).toast.error({ code: "io", message: m }, "Could not add the mod"); }, long);
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText("Could not add the mod");
    const body = notice.locator("[data-deck-notice]");
    await expect(body).toContainText("part 0 of a long error");
    await expect(body).toContainText("part 11");
    // not in the bar any more, and nothing of the dialog is outside the 1280x800 screen
    await expect(page.locator(".deck-toast.err")).toHaveCount(0);
    const box = (await notice.locator(".deck-dialog").boundingBox())!;
    expect(box.y, "the dialog's top is on the screen").toBeGreaterThanOrEqual(0);
    expect(box.y + box.height, "the dialog's bottom is on the screen").toBeLessThanOrEqual(800);
    expect(box.x >= 0 && box.x + box.width <= 1280, "the dialog's sides are on the screen").toBe(true);
    // it does not go after six seconds: the player closes it
    await page.clock.fastForward(7000);
    await expect(notice).toContainText("part 11");
    // what does not fit scrolls with the right stick
    const fits = await body.evaluate((el) => el.scrollHeight <= el.clientHeight);
    if (!fits) {
      const send = (value: number) => page.evaluate((v) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
        .emit("kiln://gamepad", { kind: "axis", axis: "ry", value: v, device: "event9" }), value);
      for (let i = 0; i < 20; i++) await send(1);
      await send(0);
      await expect.poll(() => body.evaluate((el) => el.scrollTop)).toBeGreaterThan(0);
    }
    await pad(page, "b");
    await expect(notice).toHaveCount(0);
    await expect.poll(() => focused(page)).toBe("mouth");
  });

  test("a short error stays in the bar with all its text and goes after six seconds", async ({ page }) => {
    await openDeck(page);
    await page.clock.install();
    await page.evaluate(async () => { (await import("/src/ui/Toast.tsx")).toast.error({ code: "io", message: "The archive is not a mod Kiln knows." }, "Could not add the mod"); });
    const toast = page.locator(".deck-toast.err");
    const detail = toast.locator(".deck-toast-detail");
    await expect(detail).toContainText("The archive is not a mod Kiln knows.");
    expect(await detail.evaluate((el) => el.scrollHeight <= el.clientHeight + 1), "a short error's detail is cut").toBe(true);
    await expect(page.locator('[data-deck-dialog="notice"]')).toHaveCount(0);
    const box = (await toast.boundingBox())!;
    expect(box.y + box.height).toBeLessThanOrEqual(800);
    await page.clock.fastForward(7000);
    await expect(toast).toHaveCount(0);
  });

  test("an error of four lines still fits the bar; one line more makes it a notice", async ({ page }) => {
    await openDeck(page);
    await page.clock.install();
    const push = (m: string) => page.evaluate(async (text) => { (await import("/src/ui/Toast.tsx")).toast.error({ code: "io", message: text }, "Could not add the mod"); }, m);
    // grow the text until the bar no longer holds it: the last text that fit was never cut, the first that did not is a dialog
    let text = "word";
    let fitted = 0;
    for (let i = 0; i < 400; i++) {
      await push(text);
      await page.waitForTimeout(30);
      if (await page.locator('[data-deck-dialog="notice"]').count()) break;
      const detail = page.locator(".deck-toast.err .deck-toast-detail").last();
      expect(await detail.evaluate((el) => el.scrollHeight <= el.clientHeight + 1), `cut in the bar at ${text.length} characters`).toBe(true);
      fitted = text.length;
      await page.clock.fastForward(7000);
      text += " another word of the error";
    }
    expect(fitted, "some error fits the bar").toBeGreaterThan(100);
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toHaveCount(1);
    await expect(notice.locator("[data-deck-notice]")).toContainText(text);
    await pad(page, "a");
    await expect(notice).toHaveCount(0);
  });

  test("Settings reads the notes about mod folders again and turns them off", async ({ page }) => {
    await openDeck(page);
    const OWN = "shroudforge:better-chests: the game uses your own folder /games/Enshrouded/mods/BetterChests for this mod; Kiln leaves it as it is and places no copy of its own.";
    await page.evaluate(async (own) => {
      const { notifyActivation } = await import("/src/shell/notices.ts");
      await notifyActivation({ gameId: "client-steam", profileId: "ember-run", generation: 9, pathMode: "z", profileJson: "/x", copied: 0, fingerprint: "kms1:00",
        activated: "2099-01-01T00:00:00Z", warnings: [own] } as never);
    }, OWN);
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText(OWN);
    await pad(page, "a");
    await expect(notice).toHaveCount(0);
    await page.evaluate(() => { location.hash = "#/deck/settings"; });
    const read = page.locator('[data-deck-id="set-notes"]');
    await expect(read).toContainText("1");
    const sw = page.locator('[data-deck-id="set-notes-switch"]');
    await expect(sw).toContainText("On");
    await sw.focus();
    await pad(page, "a");
    await expect(sw).toContainText("Off");
    await expect.poll(() => page.evaluate(async () => (await (await import("/src/api/commands.ts")).appSettingsGet()).modFolderNotes)).toBe(false);
    await page.evaluate(async (own) => {
      const { notifyActivation } = await import("/src/shell/notices.ts");
      await notifyActivation({ gameId: "client-steam", profileId: "ember-run", generation: 10, pathMode: "z", profileJson: "/x", copied: 0, fingerprint: "kms1:00",
        activated: "2099-01-01T00:00:01Z", warnings: [own, "shroudforge:x: another note"] } as never);
    }, OWN);
    await expect(read).toContainText("2");
    await expect(notice).toHaveCount(0);
    await read.focus();
    await pad(page, "a");
    const dialog = page.locator('[data-deck-dialog="folder-notes"]');
    await expect(dialog).toContainText(OWN);
    await expect(dialog).toContainText("another note");
    await pad(page, "b");
    await expect(dialog).toHaveCount(0);
  });

  test("the retained-folder warning shows its paths and reasons and a pad closes it", async ({ page }) => {
    await openDeck(page);
    await page.evaluate(() => (window as unknown as { __kilnMock: { planActivation: (r: unknown) => void } }).__kilnMock.planActivation({
      retained: [{ path: "mods/XHL-Vein-Mining", reason: "files Kiln placed were changed or removed, or code was added (native-plugin.ini). It is off in the profile, but its own loader may still load it from there.", mayRun: true }],
      keptFiles: ["xhl:xhl-vein-mining: kept runtime settings (config.txt) in kiln/state/placed-settings/xhl/xhl-vein-mining"],
    }));
    // profiles are in Settings
    await page.goto("/?deck=steamdeck#/deck/settings");
    await page.locator('[data-deck-id="set-profile"]').focus();
    expect(await focused(page)).toBe("set-profile");
    await pad(page, "a");
    await expect(page.locator('[data-deck-dialog="profiles"]')).toBeVisible();
    await page.locator('[data-deck-id="profile-vanilla-plus"]').click();
    const notice = page.locator('[data-deck-dialog="notice"]');
    await expect(notice).toContainText("Some mod folders were kept");
    await expect(notice).toContainText("mods/XHL-Vein-Mining");
    await expect(notice).toContainText("native-plugin.ini");
    await expect(notice).toContainText("may still load it");
    // A on its OK closes it; the next notice follows, B closes that one
    await pad(page, "a");
    await expect(notice).toContainText("Mod settings kept");
    await expect(notice).toContainText("placed-settings");
    await pad(page, "b");
    await expect(notice).toHaveCount(0);
  });
});
