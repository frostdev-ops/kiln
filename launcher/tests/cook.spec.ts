// Cooking mods from the launcher (docs/kiln/launcher/app.md "Cooking mods"): Play cooks the mods that need it before the
// game starts, as a task whose lines are the tools'; a mod whose cook fails is named with the tools' own text, the
// others are cooked and the game starts all the same; "Cook again" in a mod's menu, and Y on the Deck's Mods screen.
// Browser mock only (src/api/mock.ts: `__kilnMock.setCook(id, { reason, fails })`, `setCookPace(ms)`).
import { expect, test, type Page } from "@playwright/test";

type Mock = { __kilnMock: { setCook: (id: string, cook: unknown) => void; setCookPace: (ms: number) => void; calls: () => string[]; stopGame: () => void; emit: (name: string, payload: unknown) => void } };

async function desktop(page: Page) {
  await page.goto("/#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
/** The fake game's cook state for a mod; the page reads the plan again (as after any change of the game's state). */
const setCook = (page: Page, id: string, cook: unknown) => page.evaluate(([id, cook]) => {
  const m = (window as unknown as Mock).__kilnMock;
  m.setCook(id as string, cook);
  m.emit("kiln://game", { gameId: "client-steam", kind: "status" });
}, [id, cook] as const);
const calls = (page: Page) => page.evaluate(() => (window as unknown as Mock).__kilnMock.calls());
const api = <T,>(page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(async ([f, a]) => {
    const m = (await import("/src/api/commands.ts")) as unknown as Record<string, (...x: unknown[]) => Promise<unknown>>;
    return m[f as string](...(a as unknown[]));
  }, [fn, args] as const) as Promise<T>;
const row = (page: Page, name: string) => page.locator(".k-hero").getByRole("list", { name: "Mods" }).getByRole("listitem").filter({ has: page.getByText(name, { exact: true }) });
const TOOL_TEXT = "kiln: cooking assets/cook.json (modelcook)\nmodelcook: input bronze_model: 9c38c7f0-fd0a-41a6-b91e-9a16287b8f59 is not in enshrouded.kfc (game update or wrong side?)";

test("Play cooks a mod that needs it first, and then the game starts", async ({ page }) => {
  await desktop(page);
  await setCook(page, "new-items", { reason: "notCooked" });
  expect((await api<{ id: string; reason?: string }[]>(page, "cookPlan", "client-steam")).map((j) => [j.id, j.reason])).toEqual([["new-items", "notCooked"]]);
  const play = page.locator(".k-door");
  await expect(play).toBeEnabled({ timeout: 10_000 });
  await play.click();
  // it says what it is doing, the task shows the tools' lines, and the start follows
  await expect(page.locator(".k-toast", { hasText: "Cooking New Items" })).toContainText("then the game starts");
  await expect.poll(async () => (await calls(page)).filter((c) => c === "cook_run" || c === "launch_play"), { timeout: 15_000 }).toEqual(["cook_run", "launch_play"]);
  await expect(page.locator(".k-head-state")).toHaveText(/Starting|Running/, { timeout: 10_000 });
  await expect(page.locator(".k-toast", { hasText: "was not cooked" })).toHaveCount(0);
  // cooked for this game: the next Play's cook (it always runs: it applies a changed profile first) cooks nothing
  expect((await api<{ reason?: string }[]>(page, "cookPlan", "client-steam"))[0].reason ?? null).toBeNull();
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.stopGame());
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");
  const toasts = await page.locator(".k-toast", { hasText: "Cooking" }).count();
  await play.click();
  await expect.poll(async () => (await calls(page)).filter((c) => c === "launch_play").length, { timeout: 15_000 }).toBe(2);
  expect((await calls(page)).filter((c) => c === "cook_run").length).toBe(2);
  // no new "Cooking" toast for a cook that cooks nothing (the first Play's toast may still be up at the first count and
  // gone by now, on a slow first page: so "no more than before", not "the same number")
  expect(await page.locator(".k-toast", { hasText: "Cooking" }).count()).toBeLessThanOrEqual(toasts);
});

test("the cook shows its progress as a task: the mod and then each line the tools print", async ({ page }) => {
  await desktop(page);
  // each line shows for three seconds: on a loaded machine the tray opened after a 1.5 s line had gone ("Done")
  await page.evaluate(() => (window as unknown as Mock).__kilnMock.setCookPace(3000));
  await setCook(page, "new-items", { reason: "modChanged" });
  const play = page.locator(".k-door");
  await expect(play).toBeEnabled({ timeout: 10_000 });
  await play.click();
  await page.locator(".k-tray-btn").click();
  const task = page.getByRole("dialog", { name: "Tasks" }).locator(".k-task", { hasText: "Cook mods" });
  await expect(task.locator(".k-task-msg")).toHaveText("Cooking New Items (1 of 1)", { timeout: 5_000 });
  await expect(task.locator(".k-task-msg")).toHaveText("New Items: kiln: cooking assets/cook.json (modelcook)", { timeout: 8_000 });
  await expect(task.locator(".k-task-pct")).toBeVisible();
  // and the start follows the cook, not before it ends
  expect((await calls(page)).filter((c) => c === "launch_play")).toEqual([]);
  await expect.poll(async () => (await calls(page)).filter((c) => c === "launch_play").length, { timeout: 15_000 }).toBe(1);
});

test("a mod whose cook fails is named with the tools' own text; the others are cooked and the game starts all the same", async ({ page }) => {
  await desktop(page);
  await setCook(page, "new-items", { reason: "gameChanged", fails: TOOL_TEXT });
  await setCook(page, "weather-plus", { reason: "gameChanged" });
  const play = page.locator(".k-door");
  await expect(play).toBeEnabled({ timeout: 10_000 });
  await play.click();
  await expect(page.locator(".k-toast", { hasText: "Cooking 2 mods" })).toBeVisible();
  // the start is not held back
  await expect.poll(async () => (await calls(page)).filter((c) => c === "launch_play").length, { timeout: 15_000 }).toBe(1);
  await expect(page.locator(".k-head-state")).toHaveText(/Starting|Running/, { timeout: 10_000 });
  // the notice stays, names the mod and carries the tool's lines as they are
  const notice = page.locator(".k-toast", { hasText: "New Items was not cooked" });
  await expect(notice).toBeVisible();
  const showAll = notice.getByRole("button", { name: "Show all" });
  if (await showAll.count()) await showAll.click();
  await expect(notice).toContainText("modelcook: input bronze_model: 9c38c7f0-fd0a-41a6-b91e-9a16287b8f59 is not in enshrouded.kfc (game update or wrong side?)");
  await expect(notice).toContainText("1 other mod was cooked.");
  await expect(notice).toContainText("The game starts all the same, and a mod that could not be cooked keeps running on the files it has");
  // the record: one cooked, one to try again at the next start
  const plan = await api<{ id: string; reason?: string; last?: { ok: boolean; error?: string } }[]>(page, "cookPlan", "client-steam");
  expect(plan.map((j) => [j.id, j.reason ?? null, j.last?.ok])).toEqual([["new-items", "lastFailed", false], ["weather-plus", null, true]]);
  expect(plan[0].last?.error).toBe(TOOL_TEXT);
});

test("Cook again in a mod's menu cooks that mod, and says so or says why not", async ({ page }) => {
  await desktop(page);
  await setCook(page, "new-items", { reason: null });
  await expect(row(page, "New Items")).toBeVisible({ timeout: 10_000 });
  // a mod with nothing to cook has no such entry
  await row(page, "Weather Plus").getByLabel("More for Weather Plus").click();
  await expect(page.getByRole("menuitem", { name: "Cook again" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  await row(page, "New Items").getByLabel("More for New Items").click();
  await page.getByRole("menuitem", { name: "Cook again" }).click();
  await expect(page.locator(".k-toast", { hasText: "New Items cooked" })).toBeVisible();
  expect((await calls(page)).filter((c) => c === "cook_run").length).toBe(1);
  // and when the tools refuse: the mod's name, the tools' words, and that the game still runs it without them
  await setCook(page, "new-items", { fails: TOOL_TEXT });
  await row(page, "New Items").getByLabel("More for New Items").click();
  await page.getByRole("menuitem", { name: "Cook again" }).click();
  const notice = page.locator(".k-toast", { hasText: "New Items was not cooked" });
  await expect(notice).toBeVisible();
  const showAll = notice.getByRole("button", { name: "Show all" });
  if (await showAll.count()) await showAll.click();
  await expect(notice).toContainText("is not in enshrouded.kfc (game update or wrong side?)");
});

test("nothing to cook: the cook ends at once, says nothing, and the game starts", async ({ page }) => {
  await desktop(page);
  const play = page.locator(".k-door");
  await expect(play).toBeEnabled({ timeout: 10_000 });
  await play.click();
  await expect(page.locator(".k-head-state")).toHaveText(/Starting|Running/, { timeout: 10_000 });
  await expect.poll(async () => (await calls(page)).filter((c) => c === "cook_run" || c === "launch_play"), { timeout: 15_000 }).toEqual(["cook_run", "launch_play"]);
  await expect(page.locator(".k-toast", { hasText: /Cooking|cooked/ })).toHaveCount(0);
});

// The Deck view (1280×800, the pad): Play cooks first there too, and a mod that could not be cooked comes as the notice
// that stays, with the tools' lines on the screen and closed with B.
test.describe("in the Deck view", () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test("Play cooks first, and a failed cook is a notice with the tools' text that B closes", async ({ page }) => {
    const pad = async (button: string) => {
      const send = (pressed: boolean) => page.evaluate(([b, p]) => (window as unknown as Mock).__kilnMock
        .emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
      await send(true);
      await send(false);
      await page.waitForTimeout(60);
    };
    await page.goto("/?deck=steamdeck#/");
    await expect(page.locator('[data-deck-screen="home"]')).toBeVisible();
    await expect.poll(() => page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset?.deckId ?? null)).toBe("mouth");
    await setCook(page, "new-items", { reason: "notCooked", fails: TOOL_TEXT });
    await setCook(page, "weather-plus", { reason: "notCooked" });
    await pad("a");
    await expect.poll(async () => (await calls(page)).filter((c) => c === "cook_run" || c === "launch_play"), { timeout: 15_000 }).toEqual(["cook_run", "launch_play"]);
    await expect(page.getByTestId("deck-state")).toHaveText(/Starting|Running/, { timeout: 10_000 });
    // the notice: a dialog that holds all of the text, inside the screen
    const notice = page.getByRole("dialog").filter({ hasText: "New Items was not cooked" });
    await expect(notice).toBeVisible();
    await expect(notice).toContainText("modelcook: input bronze_model: 9c38c7f0-fd0a-41a6-b91e-9a16287b8f59 is not in enshrouded.kfc (game update or wrong side?)");
    await expect(notice).toContainText("The game starts all the same");
    const box = await notice.boundingBox();
    expect(box && box.x >= 0 && box.y >= 0 && box.x + box.width <= 1280 && box.y + box.height <= 800, JSON.stringify(box)).toBeTruthy();
    await pad("b");
    await expect(notice).toHaveCount(0);
  });

  test("Y on a mod that has something to cook cooks it again, and says so or says why not", async ({ page }) => {
    const pad = async (button: string) => {
      const send = (pressed: boolean) => page.evaluate(([b, p]) => (window as unknown as Mock).__kilnMock
        .emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
      await send(true);
      await send(false);
      await page.waitForTimeout(60);
    };
    await page.goto("/?deck=steamdeck#/deck/mods");
    await setCook(page, "new-items", { reason: null });
    const mods = page.getByRole("list", { name: "Mods" });
    await expect(mods.locator('[data-deck-id="mod-kiln-new-items"]')).toBeVisible({ timeout: 10_000 });
    // a mod with nothing to cook offers no Y
    await mods.locator('[data-deck-id="mod-kiln-weather-plus"]').focus();
    await expect(page.locator('.deck-hint[data-hint="y"]')).toHaveCount(0);
    // a mod with something to cook: Y cooks it, whatever its record says
    await mods.locator('[data-deck-id="mod-kiln-new-items"]').focus();
    await expect(page.locator('.deck-hint[data-hint="y"]')).toContainText("Cook again");
    await pad("y");
    await expect(page.locator(".deck-toast, .k-toast", { hasText: "New Items cooked" }).first()).toBeVisible({ timeout: 10_000 });
    expect((await calls(page)).filter((c) => c === "cook_run").length).toBe(1);
    // the tools refuse: the notice names the mod with their words, on the screen, and B closes it
    await setCook(page, "new-items", { fails: TOOL_TEXT });
    await mods.locator('[data-deck-id="mod-kiln-new-items"]').focus();
    await pad("y");
    const notice = page.getByRole("dialog").filter({ hasText: "New Items was not cooked" });
    await expect(notice).toBeVisible({ timeout: 10_000 });
    await expect(notice).toContainText("is not in enshrouded.kfc (game update or wrong side?)");
    const box = await notice.boundingBox();
    expect(box && box.x >= 0 && box.y >= 0 && box.x + box.width <= 1280 && box.y + box.height <= 800, JSON.stringify(box)).toBeTruthy();
    await pad("b");
    await expect(notice).toHaveCount(0);
    expect((await calls(page)).filter((c) => c === "cook_run").length).toBe(2);
  });
});
