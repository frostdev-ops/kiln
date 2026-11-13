// What a start is doing (launch_begin, launch_progress; src/shell/StartPanel.tsx): the browser mock plays the owner's
// start of 13 Nov 2026 compressed (src/api/mockProgress.ts, `__kilnMock.scriptStart`). The first start names its steps
// and says it is being timed; the second counts down; a slow step says it takes longer than usual; a 0.2.0-dev.26
// runtime (no progress.json) shows what the launcher showed before; safe idle is said as what it is. The Play page, the
// top bar's pill, the mouth and the Deck's Home. Screenshots: START_SHOTS (default .out/start-progress/shots).
import { expect, test, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const SHOTS = process.env.START_SHOTS ?? join(import.meta.dirname, "..", "..", ".out", "start-progress", "shots");
const shots = process.env.SHOTS !== "0";
if (shots) mkdirSync(SHOTS, { recursive: true });
const shot = async (page: Page, name: string) => { if (shots) await page.screenshot({ path: join(SHOTS, `${name}.png`) }); };

type Mock = { __kilnMock: { stopGame: () => void; scriptStart: (o: unknown) => void; clearStartHistory: () => void } };
const mock = (page: Page) => ({
  script: (o: unknown) => page.evaluate((x) => (window as unknown as Mock).__kilnMock.scriptStart(x), o),
  stop: () => page.evaluate(() => (window as unknown as Mock).__kilnMock.stopGame()),
  forget: () => page.evaluate(() => (window as unknown as Mock).__kilnMock.clearStartHistory()),
});

async function open(page: Page, route = "/", query = "") {
  await page.goto(`/${query}#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

const bar = (page: Page) => page.getByRole("progressbar", { name: "Starting the game" });
const left = (page: Page) => page.getByTestId("start-left");
const mouth = (page: Page) => page.locator(".k-door");

/** Seconds in "About 1 min 25 s left." */
function seconds(text: string): number {
  const m = /(?:(\d+) min)?\s*(?:(\d+) s)?/.exec(text.replace(/^.*?about /i, "")) ?? [];
  return Number(m[1] ?? 0) * 60 + Number(m[2] ?? 0);
}

/** Plays one scripted start on Play to its end and stops the game. */
async function wholeStart(page: Page) {
  await mouth(page).click();
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");
}

test.describe.configure({ mode: "serial" });

test("first start: the steps are named and Kiln says it is timing them; then the second start counts down", async ({ page }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  await open(page);
  await mock(page).script({ scale: 0.12 });
  await mouth(page).click();
  // the cook, mod by mod, with the tools' line
  await expect(page.locator(".k-start-line[role=status]").filter({ hasText: /^Cooking Kiln Equipment \(1 of 3\)/ })).toBeVisible();
  await expect(left(page)).toHaveText("First start with these mods on this PC: Kiln is timing it. Step 1 of 14.");
  await expect(bar(page)).toHaveAttribute("aria-valuetext", /^Step 1 of 14: Cooking Kiln Equipment/);
  const steps = page.getByRole("list", { name: "Steps of the start" });
  await expect(steps.getByRole("listitem")).toHaveCount(14);
  await expect(steps.locator('[aria-current="step"]')).toContainText("Cooking Kiln Equipment");
  await page.waitForTimeout(1500);
  await shot(page, "first-start-play-1280x800");
  // Kiln's services: the step's line with what it works on and the count; still "Starting", never "Running" yet
  await expect(page.locator(".k-start-line[role=status]").filter({ hasText: /^Starting Kiln's services: kiln-vfx, building 85 materials \(13 of 31\)$/ })).toBeVisible({ timeout: 20_000 });
  await expect(mouth(page)).toHaveAccessibleName(/^Starting: Starting Kiln's services/);
  await expect(steps.locator(".is-done").first()).toContainText(/Cooking mods\s*\d/);
  // boot.json is written, the game goes on loading: the bar stays until the main menu
  await expect(page.locator(".k-start-line[role=status]").filter({ hasText: /^Loading the mods/ })).toBeVisible({ timeout: 10_000 });
  await expect(page.locator(".k-head-state")).not.toHaveText(/^Running/);
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 15_000 });
  await expect(bar(page)).toHaveCount(0);
  await mock(page).stop();
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");

  // the second start: a number that counts down, from the last start
  await mouth(page).click();
  await expect(left(page)).toHaveText(/^About (\d+ min )?\d+ s left\. From your last start with these mods\.$/);
  await expect(left(page)).toHaveAttribute("title", "From your last start with these mods");
  const a = seconds(await left(page).innerText());
  await page.waitForTimeout(1600);
  await shot(page, "second-start-play-1280x800");
  const b = seconds(await left(page).innerText());
  expect(a, "about 147 s at Play").toBeGreaterThan(120);
  expect(b, "it counts down").toBeLessThan(a);
  await expect(bar(page)).toHaveAttribute("aria-valuetext", /^\d+ percent, about .* left: Cooking/);
  // the pill on another page: the step and the time left
  await page.evaluate(() => { location.hash = "#/settings"; });
  await expect(page.locator(".k-head-state")).toHaveText(/^about .* left · Cooking Kiln Equipment/);
  await page.waitForTimeout(900); // the sheet rises
  await shot(page, "second-start-pill-1280x800");
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();
});

test("a step that takes twice as long says so, and a 0.2.0-dev.26 runtime shows what it did before", async ({ page }) => {
  test.setTimeout(180_000);
  await page.setViewportSize({ width: 1280, height: 800 });
  await open(page);
  await mock(page).forget();
  await mock(page).script({ scale: 0.1 });
  await wholeStart(page);
  await mock(page).script({ scale: 0.25, slow: { id: "services", factor: 2 } });
  await mouth(page).click();
  await expect(left(page)).toHaveText(/^Starting Kiln's services: taking longer than usual \(usually 19 s\)\. Then about \d+ s\./, { timeout: 60_000 });
  await expect(left(page)).toHaveClass(/k-tone-warn/);
  await shot(page, "slow-step-play-1280x800");
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 60_000 });
  await mock(page).stop();
  await expect(page.locator(".k-head-state")).toHaveText("Stopped");

  // dev.26 in the game (no progress.json): the launcher's own steps show, then "Waiting for Kiln to boot" until its
  // boot.json, and from there "Running" as before (no bar, nothing kept)
  await mock(page).script({ scale: 0.05, dev26: true });
  await mouth(page).click();
  await expect(bar(page)).toBeVisible();
  await expect(page.locator(".k-start-line")).toHaveText("Waiting for Kiln to boot", { timeout: 15_000 });
  await shot(page, "dev26-runtime-play-1280x800");
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 15_000 });
  await expect(bar(page)).toHaveCount(0);
  await expect(page.locator(".k-hero-say").first()).toHaveText(/^Running with Kiln/);
  await shot(page, "dev26-runtime-running-1280x800");
  await mock(page).stop();
});

test("safe idle is said as what it is, not as progress", async ({ page }) => {
  test.setTimeout(60_000);
  await open(page);
  await mock(page).script({ scale: 0.05, idle: { at: "signatures", why: "the game's code is not the build Kiln knows (26430625): mods stay off" } });
  await mouth(page).click();
  await expect(page.locator(".k-head-state")).toHaveText(/^Safe idle/, { timeout: 20_000 });
  await expect(bar(page)).toHaveCount(0);
  await expect(page.locator(".k-hero").getByText(/Kiln is in safe idle: the game's code is not the build Kiln knows/).first()).toBeVisible();
  await mock(page).stop();
});

test("the start fits 1024×640 and the Deck's 1280×800", async ({ page }) => {
  test.setTimeout(120_000);
  const fits = async (what: string) => {
    const over = await page.evaluate(() => ({ x: document.documentElement.scrollWidth > innerWidth + 1, y: document.documentElement.scrollHeight > innerHeight + 1 }));
    expect(over, what).toEqual({ x: false, y: false });
  };
  await page.setViewportSize({ width: 1024, height: 640 });
  await open(page);
  await mock(page).forget();
  await mock(page).script({ scale: 0.12 });
  await mouth(page).click();
  await expect(bar(page)).toBeVisible();
  // short: the bar and one line, the line clear of the mouth
  await expect(page.getByRole("list", { name: "Steps of the start" })).toBeHidden();
  await fits("first start 1024x640");
  const say = await page.locator(".k-start-line").boundingBox();
  const door = await mouth(page).boundingBox();
  expect(say && door && say.x + say.width <= door.x + 1).toBeTruthy();
  await page.waitForTimeout(1200);
  await shot(page, "first-start-play-1024x640");
  await page.evaluate(() => { location.hash = "#/settings"; });
  await page.waitForTimeout(900); // the sheet rises
  await shot(page, "first-start-pill-1024x640");
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();
  await mouth(page).click();
  await expect(left(page)).toHaveText(/^About .* left\./);
  await fits("second start 1024x640");
  await page.waitForTimeout(1200);
  await shot(page, "second-start-play-1024x640");
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();

  // the Deck's Home at 1280x800: the mouth says the time left, the text column the step and the bar; A does nothing
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/?deck=steamdeck#/");
  await expect(page.locator('[data-deck-screen="home"]')).toBeVisible();
  await mock(page).forget();
  await mock(page).script({ scale: 0.12 });
  await page.locator(".deck-mouth").click();
  const deck = page.getByTestId("deck-start");
  await expect(deck.getByRole("progressbar", { name: "Starting the game" })).toBeVisible();
  await expect(deck).toContainText("First start with these mods on this PC: Kiln is timing it.");
  await expect(page.getByTestId("deck-state")).toHaveText(/^Starting · \d+%$/);
  await expect(page.locator(".deck-mouth")).toHaveAccessibleName(/^Starting: Step \d+ of 14$/);
  await fits("deck first start");
  await page.waitForTimeout(1200);
  await shot(page, "first-start-deck-1280x800");
  await expect(page.getByTestId("deck-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();
  await page.locator(".deck-mouth").click();
  await expect(deck).toContainText(/About .* left\./);
  await expect(page.getByTestId("deck-state")).toHaveText(/^Starting · about .* left$/);
  await fits("deck second start");
  await page.waitForTimeout(1200);
  await shot(page, "second-start-deck-1280x800");
  await expect(page.getByTestId("deck-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();
});

// ---- the review's window findings (each fails without its fix)

type Calls = { __kilnMock: { calls: () => string[]; setNoProxy: (on: boolean) => void; planCommand: (cmd: string, plan: unknown) => void } };
const progressCalls = (page: Page) => page.evaluate(() => (window as unknown as Calls).__kilnMock.calls().filter((c) => c === "launch_progress").length);

test("No Kiln wins over the timeline: the proxy never loads, the start says so and gives Play's page back", async ({ page }) => {
  test.setTimeout(60_000);
  await open(page);
  await mock(page).script({ scale: 0.02, dev26: true });
  await page.evaluate(() => (window as unknown as Calls).__kilnMock.setNoProxy(true));
  await page.getByRole("button", { name: "Without mods" }).click();
  await expect(page.locator(".k-head-state")).toHaveText(/^No Kiln/, { timeout: 15_000 });
  await expect(bar(page)).toHaveCount(0);
  await expect(page.locator(".k-mods")).toBeVisible();
  await expect(page.getByText("Running without Kiln: Steam did not load it this start.")).toBeVisible();
  await page.evaluate(() => (window as unknown as Calls).__kilnMock.setNoProxy(false));
  await mock(page).stop();
});

test("Play refused before Steam was asked: the start is over and the page is as before Play", async ({ page }) => {
  await open(page);
  await mock(page).script({ scale: 0.02 });
  await page.evaluate(() => (window as unknown as Calls).__kilnMock.planCommand("launch_play", { delay: 300, error: { code: "refused", message: "The profile could not be checked." } }));
  await mouth(page).click();
  await expect(page.locator(".k-toast").filter({ hasText: "The profile could not be checked." })).toBeVisible();
  await expect(bar(page)).toHaveCount(0);
  await expect(mouth(page)).toHaveAccessibleName(/^Play /);
  await expect(page.locator(".k-mods")).toBeVisible();
});

test("the start keeps being followed while the window is hidden (the game has the display)", async ({ page }) => {
  test.setTimeout(60_000);
  await open(page);
  await mock(page).script({ scale: 0.1 });
  await mouth(page).click();
  await expect(bar(page)).toBeVisible();
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "hidden" });
    Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  const before = await progressCalls(page);
  await page.waitForTimeout(3500);
  expect(await progressCalls(page) - before, "polled while hidden").toBeGreaterThanOrEqual(2);
  await page.evaluate(() => {
    Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "visible" });
    Object.defineProperty(document, "hidden", { configurable: true, get: () => false });
    document.dispatchEvent(new Event("visibilitychange"));
  });
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();
});

test("no word from Kiln for 60 s is its own state: no bar, no countdown, the mods list back", async ({ page }) => {
  test.setTimeout(60_000);
  await open(page);
  await mock(page).script({ scale: 0.02, quietAt: "services" });
  await mouth(page).click();
  await expect(page.locator(".k-head-state")).toHaveText(/^No word from Kiln/, { timeout: 20_000 });
  await expect(bar(page)).toHaveCount(0);
  await expect(left(page)).toHaveCount(0);
  await expect(page.locator(".k-mods")).toBeVisible();
  await expect(page.locator(".k-hero-say").first()).toHaveText(/^Kiln has said nothing for 61 s \(last: Starting Kiln's services\)/);
  await shot(page, "quiet-play-1280x800");
  await mock(page).stop();
});

test("nothing polls every second on an idle launcher, also after Steam took a start and no game came", async ({ page }) => {
  test.setTimeout(60_000);
  await page.addInitScript(() => { (window as unknown as { __kilnSteamWaitMs: number }).__kilnSteamWaitMs = 4000; });
  await open(page);
  let n = await progressCalls(page);
  await page.waitForTimeout(3000);
  expect(await progressCalls(page) - n, "idle: no poll").toBe(0);
  await mock(page).script({ scale: 0.02, noProcess: true });
  await mouth(page).click();
  await expect(bar(page)).toBeVisible();
  // Steam was asked, no game came: after the wait the start is over on both sides and Play is back
  await expect(mouth(page)).toHaveAccessibleName(/^Play /, { timeout: 15_000 });
  await expect(bar(page)).toHaveCount(0);
  await page.waitForTimeout(1500);
  n = await progressCalls(page);
  await page.waitForTimeout(4000);
  expect(await progressCalls(page) - n, "no poll once the start is given up").toBe(0);
});

test("a long detail stays inside its line at 1024×640 and on the Deck, and the Deck keeps a focus through the start", async ({ page }) => {
  test.setTimeout(90_000);
  const long = `kiln-vfx: building the materials and VFX of kiln-equipment: material ${"thunderhead.copper_wire_".repeat(12)} (57 of 120)`;
  const inside = async (sel: string) => page.locator(sel).evaluate((e) => e.scrollWidth <= e.clientWidth + 1 && e.getBoundingClientRect().right <= innerWidth);
  const fits = async () => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1 && document.documentElement.scrollHeight <= innerHeight + 1);
  await page.setViewportSize({ width: 1024, height: 640 });
  await open(page);
  await mock(page).script({ scale: 0.06, servicesDetail: long, quietAt: undefined });
  await mouth(page).click();
  await expect(page.locator(".k-start-line")).toContainText("kiln-vfx, building the materials", { timeout: 20_000 });
  expect(await inside(".k-start-line")).toBe(true);
  expect(await fits()).toBe(true);
  const say = await page.locator(".k-start-line").boundingBox();
  const door = await mouth(page).boundingBox();
  expect(say && door && say.x + say.width <= door.x + 1).toBeTruthy();
  await shot(page, "long-detail-play-1024x640");
  await expect(page.locator(".k-head-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await mock(page).stop();

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/?deck=steamdeck#/");
  await expect(page.locator('[data-deck-screen="home"]')).toBeVisible();
  const focused = () => page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset?.deckId ?? null);
  await expect.poll(focused).toBe("mouth");
  await mock(page).script({ scale: 0.06, servicesDetail: long });
  await page.keyboard.press("Enter");
  const deck = page.getByTestId("deck-start");
  await expect(deck).toContainText("kiln-vfx, building the materials", { timeout: 20_000 });
  expect(await inside(".deck-start-line")).toBe(true);
  expect(await fits()).toBe(true);
  // the mouth is not a button while the game starts; the focus stays on it (A does nothing) and the D-pad still moves
  expect(await focused(), "the focus during the start").toBe("mouth");
  await shot(page, "long-detail-deck-1280x800");
  await expect(page.getByTestId("deck-state")).toHaveText(/^Running/, { timeout: 30_000 });
  await expect.poll(focused).toBe("mouth");
  await mock(page).stop();
});
