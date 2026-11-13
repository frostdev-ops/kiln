// The save editor in Deck mode (src/editor/deck/DeckEditor.jsx, docs/kiln/launcher/deck.md "Editor"), at the Steam
// Deck's 1280×800 in mock mode with the mock's synthetic saves (src/api/mockEditor.ts), driven by the controller alone
// (the native reader's kiln://gamepad events); the one exception is the search text, typed as Steam's keyboard types
// it (key events into the focused field). `KILN_DECK_SHOTS=<dir>` saves a screenshot of each step there.
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

test.use({ viewport: { width: 1280, height: 800 } });

const SHOTS = process.env.KILN_DECK_SHOTS;
async function shot(page: Page, name: string) {
  if (!SHOTS) return;
  mkdirSync(SHOTS, { recursive: true });
  await page.waitForTimeout(300);
  await page.screenshot({ path: join(SHOTS, `deck-editor-${name}.png`) });
}

async function pad(page: Page, button: string, times = 1) {
  for (let i = 0; i < times; i++) {
    const send = (pressed: boolean) => page.evaluate(([b, p]) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
      .emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
    await send(true);
    await send(false);
    await page.waitForTimeout(90);
  }
}
const focused = (page: Page) => page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset?.deckId ?? null);
/** Presses `dir` until the focused control's id matches (at most `max` presses). */
async function walk(page: Page, dir: string, id: string | RegExp, max = 12) {
  const trail: string[] = [];
  for (let i = 0; i <= max; i++) {
    const f = await focused(page);
    trail.push(f ?? await page.evaluate(() => { const a = document.activeElement as HTMLElement | null; return a ? `<${a.tagName.toLowerCase()} ${a.className}> ${(a.textContent ?? "").slice(0, 20)}` : "none"; }));
    if (f !== null && (typeof id === "string" ? f === id : id.test(f))) return;
    if (i === max) break;
    await pad(page, dir);
  }
  expect(trail.join(" > ")).toMatch(id);
}
const hint = (page: Page, button: string) => page.locator(`.deck-hints [data-hint="${button}"]`);
const insp = (page: Page) => page.locator(".deck-editor .ke-insp");
const say = (page: Page) => page.getByTestId("deck-editor-say");

/** Home, then R1 to the Editor tab: Aldric (the newest readable save) opens, focus on the first slot. */
async function openEditor(page: Page, query = "") {
  await page.goto(`/?deck=steamdeck${query}#/`);
  await expect(page.locator('[data-deck-screen="home"]')).toBeVisible();
  await expect.poll(() => focused(page)).toBe("mouth");
  // R1 through Mods, Add mods, Catalog and Kiln
  const at = () => page.evaluate(() => location.hash);
  for (let i = 0; i < 8 && (await at()) !== "#/deck/editor"; i++) { await pad(page, "rb"); await page.waitForTimeout(150); }
  await expect(page.locator('[data-deck-screen="editor"]')).toBeVisible({ timeout: 15000 });
  await expect(page.locator(".dke-name")).toHaveText("Aldric", { timeout: 15000 });
  await expect.poll(() => focused(page)).toBe("slot-main:0");
}

/** The number pad: digits typed with the D-pad and A, Y for OK. */
async function typeNumber(page: Page, digits: string) {
  const dialog = page.locator('[data-deck-dialog="editor-number"]');
  await expect(dialog).toBeVisible();
  await expect.poll(() => focused(page)).toBe("pad-5");
  const at: Record<string, [number, number]> = { 1: [0, 0], 2: [0, 1], 3: [0, 2], 4: [1, 0], 5: [1, 1], 6: [1, 2], 7: [2, 0], 8: [2, 1], 9: [2, 2], 0: [3, 1] };
  let [r, c] = at["5"];
  for (const d of digits) {
    const [tr, tc] = at[d];
    await pad(page, tr > r ? "down" : "up", Math.abs(tr - r));
    await pad(page, tc > c ? "right" : "left", Math.abs(tc - c));
    [r, c] = [tr, tc];
    expect(await focused(page)).toBe(`pad-${d}`);
    await pad(page, "a");
  }
  await expect(page.getByTestId("pad-value")).toHaveText(String(Number(digits)));
  await shot(page, "number-pad");
  await pad(page, "y");
  await expect(dialog).toHaveCount(0);
}

test.describe("the save editor in Deck mode", () => {
  test("a tab of its own: an amount on the number pad, duplicate, remove, move, then Save with X", async ({ page }) => {
    await openEditor(page);
    await shot(page, "inventory");
    await expect(hint(page, "x")).toContainText("Save");
    await expect(hint(page, "x")).toBeDisabled();
    await expect(hint(page, "lt")).toContainText("Equipment");
    await expect(hint(page, "rt")).toContainText("Skills");
    // focus picks the slot: the inspector shows it as the D-pad moves
    await expect(insp(page)).toContainText("Diamond");
    await pad(page, "right");
    await expect(insp(page)).toContainText("Metal Scraps");
    await pad(page, "left");
    // A goes into the inspector; the amount's value opens the number pad: 200 (B goes back to the slot)
    await pad(page, "a");
    await walk(page, "right", "value-Amount");
    await expect(hint(page, "b")).toContainText("Back");
    await pad(page, "a");
    await typeNumber(page, "200");
    await expect(page.locator('[data-deck-id="value-Amount"]')).toHaveText("200");
    await expect(page.locator('[data-deck-id="slot-main:0"]')).toContainText("200");
    await expect(say(page)).toContainText("Not saved");
    await expect(hint(page, "x")).toBeEnabled();
    // far above the game's stack of 250 (a mod may raise it): the pad takes up to 65535 and the inspector says so
    await pad(page, "a");
    await typeNumber(page, "65535");
    await expect(insp(page)).toContainText("more than the game's stack of 250");
    // Duplicate: a copy in the first free slot; Remove takes it away again
    await walk(page, "down", "item-remove");
    await pad(page, "left");
    expect(await focused(page)).toBe("item-duplicate");
    await pad(page, "a");
    await expect(page.locator('[data-deck-id="slot-main:10"]')).toContainText("65535");
    await walk(page, "right", "item-remove");
    await pad(page, "a");
    await expect.poll(() => focused(page)).toBe("slot-main:10");
    await expect(page.locator('[data-deck-id="slot-main:10"] img')).toHaveCount(0);
    // B in the inspector went back to the slot; on a slot, B would go Home
    await expect(hint(page, "b")).toContainText("Home");
    // Y moves: Metal Scraps from slot 2 to the empty slot 11
    await pad(page, "up");
    await walk(page, "left", "slot-main:1");
    await pad(page, "y");
    await expect(hint(page, "y")).toContainText("Cancel");
    await walk(page, "down", "slot-main:9");
    await pad(page, "right");
    expect(await focused(page)).toBe("slot-main:10");
    await pad(page, "a");
    await expect(page.locator('[data-deck-id="slot-main:10"]')).toContainText("250");
    await expect(page.locator('[data-deck-id="slot-main:1"] img')).toHaveCount(0);
    // X saves: the save is written (a Kiln backup first), nothing is left unsaved
    await pad(page, "x");
    await expect(page.locator(".deck-toast")).toContainText("Saved");
    await expect(say(page)).not.toContainText("Not saved");
    await expect(say(page)).toContainText("Level 25");
    await expect(hint(page, "x")).toBeDisabled();
    // read back from the written bytes: the 65535 stack and the moved scraps
    await expect(page.locator('[data-deck-id="slot-main:0"]')).toContainText("65535");
    await expect(page.locator('[data-deck-id="slot-main:10"]')).toContainText("250");
  });

  test("the catalog: a category, a search typed with Steam's keyboard, pick, amount, add", async ({ page }) => {
    await openEditor(page);
    // an empty slot: A opens the catalog for it, focus in the search field
    await walk(page, "right", "slot-main:7");
    await pad(page, "down");
    expect(await focused(page)).toBe("slot-main:15");
    await pad(page, "a");
    const cat = page.locator('[data-deck-dialog="editor-catalog"]');
    await expect(cat).toBeVisible();
    await expect.poll(() => focused(page)).toBe("cat-search");
    await expect(cat.locator('[data-deck-id="cat-search"]')).toHaveAttribute("placeholder", /Steam \+ X/);
    // Steam + X's keyboard types into the focused field
    await page.keyboard.type("iron bar");
    // the grid follows the field a moment later: one item is left
    await expect(cat.locator(".ke-cat-grid .ke-slot")).toHaveCount(1);
    await expect(cat.locator('.ke-cat-grid [aria-label="Iron Bars"]')).toBeVisible();
    await shot(page, "catalog");
    await pad(page, "down");
    await walk(page, "down", /^cat-\d+$/, 8);
    await walk(page, "right", "cat-159114158", 20);
    // A picks it and focus goes to Add; up is the amount
    await pad(page, "a");
    await expect.poll(() => focused(page)).toBe("cat-add");
    await expect(cat.locator(".ke-cat-pick")).toContainText("Iron Bars");
    await pad(page, "up");
    await walk(page, "left", "value-Amount", 4);
    await pad(page, "a");
    await typeNumber(page, "40");
    await expect.poll(() => focused(page)).toBe("value-Amount");
    await pad(page, "down");
    await walk(page, "right", "cat-add", 3);
    await pad(page, "a");
    await expect(cat).toHaveCount(0);
    // the dialog gives focus back to the slot it was opened from, now holding 40 Iron Bars
    await expect.poll(() => focused(page)).toBe("slot-main:15");
    await expect(page.locator('[data-deck-id="slot-main:15"]')).toContainText("40");
    await expect(insp(page)).toContainText("Iron Bars");
    // a category without typing: the rail on the left narrows the list; B closes the dialog
    await pad(page, "down");
    expect(await focused(page)).toBe("slot-main:39");
    await pad(page, "a");
    await expect(cat).toBeVisible();
    await expect.poll(() => focused(page)).toBe("cat-search");
    await walk(page, "left", /^cat-rail-/, 3);
    await walk(page, "down", "cat-rail-mat", 10);
    await pad(page, "a");
    await expect(cat.locator(".ke-cat-main > .ke-note")).toContainText("329 items");
    await pad(page, "b");
    await expect(cat).toHaveCount(0);
    await expect.poll(() => focused(page)).toBe("slot-main:39");
  });

  test("equipment, a weapon's gem and durability, rarity and upgrades", async ({ page }) => {
    await openEditor(page);
    // the Eclipsed Blade on action bar 1: its gem from a list, its durability on the number pad
    await walk(page, "down", "slot-main:24", 10);
    await expect(insp(page)).toContainText("Eclipsed Blade");
    await pad(page, "a");
    await walk(page, "down", "item-gem", 10);
    await pad(page, "a");
    const choice = page.locator('[data-deck-dialog="editor-choice"]');
    await expect(choice).toBeVisible();
    await expect(choice).toContainText("Gem of Battle Mania");
    await pad(page, "down");
    const picked = await page.evaluate(() => document.activeElement?.querySelector(".deck-row-title")?.textContent ?? "");
    await pad(page, "a");
    await expect(choice).toHaveCount(0);
    await expect(page.locator('[data-deck-id="item-gem"]')).toHaveText(picked);
    await walk(page, "down", "value-Durability", 6);
    await pad(page, "a");
    const num = page.locator('[data-deck-dialog="editor-number"]');
    await expect(num).toContainText("Full");
    await walk(page, "down", "pad-preset-300", 6);
    await pad(page, "a");
    await expect(num).toHaveCount(0);
    await expect(insp(page)).toContainText("300/300");
    // rarity (the diamonds) and upgrades: Legendary lets a fifth upgrade in; the level moves with the upgrades
    await walk(page, "up", /^(item-gem|value-Level)$/, 6);
    await expect(insp(page)).toContainText("= base 30 + 2 upgrades");
    // L2: Equipment. Focus picks a worn piece; A goes into the inspector
    await pad(page, "lt");
    await expect(page.locator('[data-deck-id="ed-tab-char"]')).toHaveClass(/on/);
    await expect.poll(() => focused(page)).toBe("worn-5");
    await expect(insp(page)).toContainText("Brazen Bull Helmet");
    await shot(page, "equipment");
    await pad(page, "a");
    const before = await page.locator('.deck-editor .ke-insp [aria-label="+1"]').count();
    expect(before).toBeGreaterThan(0);
    await pad(page, "down");
    await walk(page, "right", "value-Level", 3);
    await pad(page, "a");
    await typeNumber(page, "35");
    await expect(page.locator('[data-deck-id="value-Level"]')).toHaveText("35");
    await expect(say(page)).toContainText("Not saved");
  });

  test("the skill tree and knowledge with the pad", async ({ page }) => {
    await openEditor(page);
    await pad(page, "rt");
    await expect(page.locator('[data-deck-id="ed-tab-skills"]')).toHaveClass(/on/);
    // into the tree: the camera follows focus, the panel shows the focused node
    await walk(page, "down", /^node-\d+$/, 8);
    await shot(page, "skills");
    const tip = page.locator(".deck-editor .ke-tree-tip");
    await expect(tip).toBeVisible();
    // walk to an unlit node next to a lit one, A raises it, Y lowers it again
    const used = page.locator(".ke-tree-points b");
    const was = Number(await used.textContent());
    let raised = false;
    for (let i = 0; i < 24 && !raised; i++) {
      const a = await page.evaluate(() => (document.activeElement as HTMLElement).dataset.deckA);
      if (a === "+1 level") {
        await pad(page, "a");
        await expect.poll(async () => Number(await used.textContent())).toBeGreaterThan(was);
        raised = true;
      } else await pad(page, ["right", "down", "left", "up"][i % 4]);
    }
    expect(raised).toBe(true);
    await expect(hint(page, "y")).toContainText("−1 level");
    await pad(page, "y");
    await expect.poll(async () => Number(await used.textContent())).toBe(was);
    // R2: Knowledge. A ticks a row, Remove takes it out
    await pad(page, "rt");
    await expect(page.locator('[data-deck-id="ed-tab-know"]')).toHaveClass(/on/);
    const count = page.locator('[data-deck-id="ed-tab-know"] .dke-count');
    await expect(count).toHaveText("2000");
    await expect.poll(() => focused(page)).toBe("know-search");
    // the level filter on the number pad (no keyboard needed): above level 30 the list is shorter
    const rows = page.locator(".ke-know-body .ke-know-row");
    const all = await rows.count();
    await walk(page, "right", "know-above", 3);
    await pad(page, "a");
    await typeNumber(page, "30");
    await expect(page.locator('[data-deck-id="know-above"]')).toContainText("30");
    await expect.poll(() => rows.count()).toBeLessThan(all);
    await pad(page, "a");
    await walk(page, "down", "pad-preset-0", 6);
    await pad(page, "a");
    await expect(page.locator('[data-deck-id="know-above"]')).toContainText("all");
    await walk(page, "left", "know-search", 3);
    await walk(page, "down", /^know-\d+$/, 4);
    await pad(page, "a");
    await expect(page.locator(".ke-know-row.on")).toHaveCount(1);
    await shot(page, "knowledge");
    const remove = page.locator(".ke-know-bar button.k-btn-danger");
    await expect(remove).toContainText("Remove 1");
    await walk(page, "up", "know-search", 4);
    await walk(page, "right", "know-remove", 8);
    await pad(page, "a");
    await expect(count).toHaveText("1999");
  });

  test("Backups and restore, leaving keeps the changes, Quit names them, another character asks first, a read-only save", async ({ page }) => {
    await openEditor(page);
    // a change: Diamond 120 -> 7
    await pad(page, "a");
    await walk(page, "right", "value-Amount");
    await pad(page, "a");
    await typeNumber(page, "7");
    await pad(page, "b");
    expect(await focused(page)).toBe("slot-main:0");
    // leave the tab and come back: the change is still there
    await pad(page, "rb");
    await expect(page.locator('[data-deck-screen="help"]')).toBeVisible();
    await pad(page, "lb");
    await expect(page.locator(".dke-name")).toHaveText("Aldric");
    await expect(say(page)).toContainText("Not saved");
    await expect(page.locator('[data-deck-id="slot-main:0"]')).toContainText("7");
    // Quit names the unsaved changes
    await pad(page, "start");
    await walk(page, "down", "menu-quit", 10);
    await pad(page, "a");
    await expect(page.getByTestId("quit-unsaved")).toContainText("Aldric");
    await pad(page, "b");
    await expect(page.locator('[data-deck-dialog="quit"]')).toHaveCount(0);
    // another character while changed: asked first; Cancel keeps Aldric
    await walk(page, "up", /^ed-/, 8);
    await walk(page, "left", "ed-characters", 4);
    await pad(page, "a");
    const chars = page.locator('[data-deck-dialog="editor-characters"]');
    await expect(chars).toContainText("Brienne");
    await expect(chars).toContainText("Cedric");
    await walk(page, "down", "char-character2.ksd", 4);
    await pad(page, "a");
    const sw = page.locator('[data-deck-dialog="editor-switch"]');
    await expect(sw).toContainText("The changes to Aldric are not saved yet.");
    await pad(page, "b");
    await expect(page.locator(".dke-name")).toHaveText("Aldric");
    // Backups: the game's two copies; restoring one asks, then reads the save again (the change is dropped)
    await walk(page, "right", "ed-backups", 3);
    await pad(page, "a");
    const bk = page.locator('[data-deck-dialog="editor-backups"]');
    await expect(bk.locator(".deck-row")).toHaveCount(2);
    await expect(bk).toContainText("Game's copy");
    await shot(page, "backups");
    await pad(page, "a");
    const confirm = page.locator('[data-deck-dialog="editor-restore"]');
    await expect(confirm).toContainText("Restoring drops the changes that are not saved.");
    await walk(page, "right", "restore-confirm", 2);
    await pad(page, "a");
    await expect(page.locator(".deck-toast")).toContainText("Restored");
    await expect(say(page)).not.toContainText("Not saved");
    await expect(page.locator('[data-deck-id="slot-main:0"]')).toContainText("120");
    // the restore kept the save it replaced: three backups now
    await walk(page, "up", /^ed-/, 8);
    await walk(page, "right", "ed-backups", 3);
    await pad(page, "a");
    await expect(bk.locator(".deck-row")).toHaveCount(3);
    await expect(bk).toContainText("Kiln backup");
    await pad(page, "b");
    // Mordred holds an item no table knows: read-only, Save stays off
    await walk(page, "left", "ed-characters", 3);
    await pad(page, "a");
    await walk(page, "down", "char-character2.ksd", 4);
    await pad(page, "a");
    await expect(page.locator(".dke-name")).toHaveText("Mordred");
    await expect(page.locator(".dke-status")).toContainText("Unknown items in this save");
    await expect(say(page)).toContainText("This save can only be read");
    await expect(hint(page, "x")).toBeDisabled();
    await shot(page, "read-only");
    // Cedric's character block is not one the editor can read: said in English
    await pad(page, "a");
    await walk(page, "down", "char-character3.ksd", 4);
    await expect(chars).toContainText("The editor cannot find the equipment and inventory in this save");
  });
});
