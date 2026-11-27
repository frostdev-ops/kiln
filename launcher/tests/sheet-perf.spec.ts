// The sheet stays cheap while the fire burns (inbox 94: every page but Play lagged, a page switch took about a second
// and typing 250 to 750 ms). The flicker (--fire, shell/Stage.tsx) and the torch (--mx, --my, shell/AppShell.tsx) write
// only onto leaves: an element with children under it would restyle and repaint the whole page on every write. With
// the fire off (Settings' Animated fire, the launcher's own animation switch; drawing without the GPU) or reduced motion
// there are no writes at all, and turned off while the launcher runs, the fire's frames and the torch's pointer listener
// go too. Without the GPU a lit flame stands still.
// The last test types into a sheet page in WebKit (the engine of the Linux launcher) while the pointer moves; it runs
// where Playwright's WebKit starts (KILN_WEBKIT names another build; not on distributions it does not support).
import { expect, test, webkit, type Page } from "@playwright/test";

type Write = { name: string; cls: string; kids: number };

/** Records every write of the moving properties with the element it went onto (and how many element children it has),
 *  the pointer listeners on .k-app (the torch's), the reduced-motion listeners and the animation frames asked for. */
async function countWrites(page: Page) {
  await page.addInitScript(() => {
    const w = window as unknown as { __writes: Write[]; __moves: number; __motion: number; __frames: number };
    w.__writes = [];
    w.__moves = 0;
    w.__motion = 0;
    w.__frames = 0;
    const count = (t: EventTarget, type: string, d: number) => {
      if (type === "mousemove" && t instanceof HTMLElement && t.classList.contains("k-app")) w.__moves += d;
      if (type === "change" && t instanceof MediaQueryList && t.media.includes("reduced-motion")) w.__motion += d;
    };
    const add = EventTarget.prototype.addEventListener, remove = EventTarget.prototype.removeEventListener;
    EventTarget.prototype.addEventListener = function (type: string, fn: EventListenerOrEventListenerObject | null, o?: boolean | AddEventListenerOptions) {
      if (fn) count(this, type, 1);
      return add.call(this, type, fn, o);
    };
    EventTarget.prototype.removeEventListener = function (type: string, fn: EventListenerOrEventListenerObject | null, o?: boolean | EventListenerOptions) {
      if (fn) count(this, type, -1);
      return remove.call(this, type, fn, o);
    };
    const frame = window.requestAnimationFrame.bind(window);
    window.requestAnimationFrame = (cb: FrameRequestCallback) => { w.__frames += 1; return frame(cb); };
    const desc = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "style")!;
    const owner = new WeakMap<CSSStyleDeclaration, HTMLElement>();
    Object.defineProperty(HTMLElement.prototype, "style", {
      configurable: true,
      get(this: HTMLElement) { const s = desc.get!.call(this) as CSSStyleDeclaration; owner.set(s, this); return s; },
      set: desc.set,
    });
    const set = CSSStyleDeclaration.prototype.setProperty;
    CSSStyleDeclaration.prototype.setProperty = function (name: string, value: string | null, prio?: string) {
      if (/^--(fire|mx|my|k)$/.test(name)) {
        const el = owner.get(this);
        w.__writes.push({ name, cls: el ? String(el.className) : "?", kids: el ? el.childElementCount : -1 });
      }
      return set.call(this, name, value, prio);
    };
  });
}

const writes = (page: Page) => page.evaluate(() => (window as unknown as { __writes: Write[] }).__writes.splice(0));
type Counts = { __moves: number; __motion: number; __frames: number };
/** The torch's pointer listeners on .k-app, the reduced-motion listeners, the animation frames asked for so far. */
const counts = (page: Page) => page.evaluate(() => { const w = window as unknown as Counts; return { moves: w.__moves, motion: w.__motion, frames: w.__frames }; });
const rimFire = (page: Page) => page.locator(".k-sheet-rim").evaluate((el) => (el as HTMLElement).style.getPropertyValue("--fire"));

async function open(page: Page, route: string, query = "") {
  await page.goto(`/${query}#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

/** The pointer wanders over the sheet for about a second, then a field gets a few letters. */
async function wander(page: Page) {
  for (let i = 0; i < 40; i++) await page.mouse.move(300 + ((i * 37) % 700), 220 + ((i * 53) % 420));
  await page.waitForTimeout(700);
}

const hasGl = (page: Page) => page.evaluate(() => !!document.createElement("canvas").getContext("webgl"));

// both fires: every day's, and Thanksgiving weekend's (shell/thanksgiving.ts), whose greeting over the mouth flickers too
for (const turkey of [false, true]) test(`the flicker and the torch write only onto leaves, never onto an element with the page under it${turkey ? " (Thanksgiving)" : ""}`, async ({ page }) => {
  await countWrites(page);
  await open(page, "/settings/game", turkey ? "?turkey=1" : "?turkey=0");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire, the flicker and the torch never start");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await wander(page);
  await page.locator("#st-keep").fill("12");
  // Play (the title flickers) and back (the sheet's edge does, the mouth springs)
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await page.waitForTimeout(800);
  await wander(page);
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  await page.waitForTimeout(800);
  await wander(page);
  const all = await writes(page);
  const by = (n: string) => all.filter((w) => w.name === n);
  // they did run
  expect(by("--fire").length).toBeGreaterThan(10);
  expect(by("--mx").length).toBeGreaterThan(5);
  expect(by("--my").length).toBe(by("--mx").length);
  expect(by("--k").length).toBeGreaterThan(0);
  // and only onto their leaves
  const onParents = all.filter((w) => w.name !== "--k" && w.kids !== 0);
  expect(onParents).toEqual([]);
  expect([...new Set(by("--fire").map((w) => w.cls.split(" ")[0]))].sort()).toEqual(turkey ? ["k-sheet-rim", "k-thanks", "k-title"] : ["k-sheet-rim", "k-title"]);
  expect([...new Set([...by("--mx"), ...by("--my")].map((w) => w.cls))]).toEqual(["k-torch"]);
  // the mouth's spring fades the label, not the whole window
  expect([...new Set(by("--k").map((w) => w.cls))]).toEqual(["k-door-lab"]);
  // nothing under the sheet reads them: they do not inherit
  const inherited = await page.evaluate(() => {
    const el = document.querySelector(".k-sheet-in") as HTMLElement;
    const s = getComputedStyle(el);
    return [s.getPropertyValue("--fire").trim(), s.getPropertyValue("--mx").trim()];
  });
  expect(inherited).toEqual(["0.5", "0px"]);
});

test("the sheet looks as it did: the lit top edge and the torch's light are where the sheet drew them", async ({ page }) => {
  await open(page, "/settings");
  const look = await page.evaluate(() => {
    const sheet = document.querySelector(".k-sheet") as HTMLElement;
    const rim = document.querySelector(".k-sheet-rim") as HTMLElement;
    const torch = document.querySelector(".k-torch") as HTMLElement;
    const sb = sheet.getBoundingClientRect(), rb = rim.getBoundingClientRect(), tb = torch.getBoundingClientRect();
    return {
      sheetShadow: getComputedStyle(sheet).boxShadow, rimShadow: getComputedStyle(rim).boxShadow, rimRadius: getComputedStyle(rim).borderRadius,
      rim: [rb.left - sb.left, rb.top - sb.top, rb.width - sb.width], rimKids: rim.childElementCount, torchKids: torch.childElementCount,
      // the light's centre: 50% across and 30% down the sheet, less the sheet's offset (base.css before inbox 94)
      torchCentre: [Math.round(tb.left + tb.width / 2 - sb.left - (sb.width * 0.5 - 24)), Math.round(tb.top + tb.height / 2 - sb.top - (sb.height * 0.3 - 76))],
      before: getComputedStyle(sheet, "::before").content,
    };
  });
  expect(look.sheetShadow).toBe("rgba(0, 0, 0, 0.6) 0px -24px 70px 0px");
  expect(look.rimShadow).toMatch(/^rgba\(255, 190, 140, 0\.\d+\) 0px 1px 0px 0px inset$/);
  expect(look.rimRadius).toBe("20px 20px 0px 0px");
  expect(look.rim).toEqual([0, 0, 0]);
  expect([look.rimKids, look.torchKids]).toEqual([0, 0]);
  expect(look.torchCentre).toEqual([0, 0]);
  expect(look.before).toBe("none");
  // the light follows the pointer once it moves (where the fire burns): centred on it
  if (!(await hasGl(page))) return;
  await page.mouse.move(640, 420);
  await page.mouse.move(650, 430);
  await expect.poll(() => page.locator(".k-torch").evaluate((el) => {
    const b = el.getBoundingClientRect();
    return [Math.round(b.left + b.width / 2), Math.round(b.top + b.height / 2)];
  })).toEqual([650, 430]);
});

test("with reduced motion nothing is written while the pointer moves and the page stands", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await countWrites(page);
  await open(page, "/settings/game");
  await page.waitForTimeout(600);
  await writes(page); // the mouth placed once on the way in
  await wander(page);
  await page.locator("#st-keep").fill("12");
  expect((await writes(page)).filter((w) => w.name !== "--k")).toEqual([]);
});

test("Animated fire turned off while the launcher runs: no flicker, no torch, no frames, no pointer listener; on again, both come back", async ({ page }) => {
  await countWrites(page);
  await open(page, "/settings");
  const gl = await hasGl(page);
  if (gl) {
    await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
    await expect.poll(async () => (await counts(page)).moves).toBe(1);
    expect((await counts(page)).motion).toBe(2);
  }
  const fire = page.getByRole("switch", { name: /^Animated fire/ });
  await expect(fire).toBeChecked();
  await page.getByText("Animated fire", { exact: true }).click();
  await expect(fire).not.toBeChecked();
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-gl");
  // the torch's listener and the fire's reduced-motion listener are gone (the shell's own stays)
  await expect.poll(async () => (await counts(page)).moves).toBe(0);
  expect((await counts(page)).motion).toBe(1);
  expect(await rimFire(page)).toBe("");
  // the save backups box is in Game & Kiln (the fire in General)
  await page.getByRole("navigation", { name: "Settings sections" }).getByRole("link", { name: "Game & Kiln", exact: true }).click();
  await expect(page.locator("#st-keep")).toBeVisible();
  await page.waitForTimeout(300);
  await writes(page);
  const before = (await counts(page)).frames;
  await wander(page);
  await page.locator("#st-keep").fill("12");
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await wander(page);
  expect(await writes(page)).toEqual([]);
  // the loop's frames stopped (a handful for the page's own work at most)
  expect((await counts(page)).frames - before).toBeLessThan(10);
  // on again: the flicker, the torch and its listener come back where there is WebGL
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  await page.getByRole("navigation", { name: "Settings sections" }).getByRole("link", { name: "General", exact: true }).click();
  await page.getByText("Animated fire", { exact: true }).click();
  await expect(fire).toBeChecked();
  if (!gl) return;
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await expect.poll(async () => (await counts(page)).moves).toBe(1);
  await wander(page);
  const back = await writes(page);
  expect(back.filter((w) => w.name === "--fire").length).toBeGreaterThan(3);
  expect(back.filter((w) => w.name === "--mx").length).toBeGreaterThan(3);
});

test("with Animated fire off nothing is written whatever the system says about motion", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await countWrites(page);
  await open(page, "/settings");
  await page.getByText("Animated fire", { exact: true }).click();
  await expect(page.getByRole("switch", { name: /^Animated fire/ })).not.toBeChecked();
  // another page, the pointer over it: still nothing
  await page.evaluate(() => { location.hash = "#/mods/order"; });
  await page.waitForTimeout(300);
  await writes(page);
  await wander(page);
  expect(await writes(page)).toEqual([]);
  expect((await counts(page)).moves).toBe(0);
});

test("reduced motion turned on while the launcher runs: the flicker and the torch stop at once, the still fire asks for no frames", async ({ page }) => {
  await countWrites(page);
  await open(page, "/settings/game");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire, the flicker and the torch never start");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await wander(page);
  let all = await writes(page);
  expect(all.filter((w) => w.name === "--fire").length).toBeGreaterThan(3);
  expect(all.filter((w) => w.name === "--mx").length).toBeGreaterThan(3);
  expect((await counts(page)).moves).toBe(1);
  expect(await rimFire(page)).not.toBe("");
  await page.emulateMedia({ reducedMotion: "reduce" });
  await expect.poll(async () => (await counts(page)).moves).toBe(0);
  await expect.poll(() => rimFire(page)).toBe("");
  await page.waitForTimeout(300);
  await writes(page);
  const before = (await counts(page)).frames;
  await wander(page);
  await page.locator("#st-keep").fill("12");
  await page.waitForTimeout(500);
  expect((await writes(page)).filter((w) => w.name !== "--k")).toEqual([]);
  // the still fire drew its frame and stopped: no frame asked for while nothing changes
  expect((await counts(page)).frames - before).toBeLessThan(10);
  // a page switch still moves the mouth to the pilot light and back (drawn once each)
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await expect.poll(() => page.locator(".k-door").evaluate((el) => parseFloat((el as HTMLElement).style.width))).toBeGreaterThan(100);
  // motion again: both come back
  await page.emulateMedia({ reducedMotion: "no-preference" });
  await expect.poll(async () => (await counts(page)).moves).toBe(1);
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  await wander(page);
  all = await writes(page);
  expect(all.filter((w) => w.name === "--fire").length).toBeGreaterThan(3);
  expect(all.filter((w) => w.name === "--mx").length).toBeGreaterThan(3);
});

test("drawing without the GPU: no fire loop, no flicker, no torch, and lit flames stand still", async ({ page }) => {
  await countWrites(page);
  await page.goto("/?render=software#/settings/game");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-gl");
  await wander(page);
  await page.locator("#st-keep").fill("12");
  expect(await writes(page)).toEqual([]);
  // a lit flame stands still: its flicker repainted it and its glow every frame on the CPU
  await page.evaluate(() => { location.hash = "#/mods/order"; });
  const flame = page.locator(".k-flame.on svg").first();
  await expect(flame).toBeVisible();
  await expect(flame).toHaveCSS("animation-name", "none");
});

test("with the GPU a lit flame still flickers", async ({ page }) => {
  await open(page, "/mods/order");
  const flame = page.locator(".k-flame.on svg").first();
  await expect(flame).toBeVisible();
  await expect(flame).toHaveCSS("animation-name", "k-lick");
});

test("behind a sheet the fire draws only the header band (the page under the sheet is never repainted for it); on Play the whole window again", async ({ page }) => {
  await open(page, "/settings");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  const canvas = page.locator(".k-stage-gl");
  const box = () => canvas.evaluate((el) => {
    const c = el as HTMLCanvasElement;
    return { top: c.getBoundingClientRect().top, css: Math.round(c.getBoundingClientRect().height), buffer: c.height, window: innerHeight };
  });
  // once the mouth has sprung to the pilot light: the header (76 px) and the sheet's lit top edge (24 px), no more
  await expect.poll(async () => (await box()).css).toBeLessThanOrEqual(101);
  const band = await box();
  expect(band.top).toBe(0);
  expect(band.css).toBeGreaterThanOrEqual(100);
  expect(band.buffer).toBeLessThan(100);
  // the flicker still burns on the sheet's edge, written on the frames the fire draws
  await expect.poll(() => rimFire(page)).not.toBe("");
  // Play: the whole window at once (the mouth springs back over the whole wall)
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await expect.poll(async () => { const b = await box(); return b.css === b.window; }).toBe(true);
  // and on a sheet again, the band
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  await expect.poll(async () => (await box()).css).toBeLessThanOrEqual(101);
});

test("with reduced motion the still fire behind a sheet is the header band too", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await open(page, "/settings");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await expect.poll(() => page.locator(".k-stage-gl").evaluate((el) => Math.round(el.getBoundingClientRect().height))).toBeLessThanOrEqual(101);
});

/** WebKit without compositing (WebKitGTK with hardware acceleration off: WEBKIT_DISABLE_DMABUF_RENDERER=1, which the
 *  Linux launcher sets only where the DMA-BUF renderer failed since 0.2.0-dev.29.19, or WEBKIT_DISABLE_COMPOSITING_MODE=1)
 *  answers (-webkit-transform-3d) with false; Chromium says true. */
async function flatWebKit(page: Page) {
  await page.addInitScript(() => {
    const mm = window.matchMedia.bind(window);
    window.matchMedia = (q: string) => (q.includes("-webkit-transform-3d") ? { ...mm("(min-width: 1000000px)"), media: q, matches: false } as MediaQueryList : mm(q));
  });
}

/** Counts the fire's frames (its draw calls). */
async function countDraws(page: Page) {
  await page.addInitScript(() => {
    const w = window as unknown as { __draws: number };
    w.__draws = 0;
    const draw = WebGLRenderingContext.prototype.drawArrays;
    WebGLRenderingContext.prototype.drawArrays = function (...a: Parameters<typeof draw>) { w.__draws += 1; return draw.apply(this, a); };
  });
}
const draws = (page: Page) => page.evaluate(() => (window as unknown as { __draws: number }).__draws);

test("WebKit drawing without compositing: the sheet has no blurred shadow (each scroll step painted it again)", async ({ page }) => {
  await flatWebKit(page);
  await open(page, "/settings");
  await expect(page.locator(".k-app")).toHaveAttribute("data-render", "flat");
  await expect(page.locator(".k-sheet")).toHaveCSS("box-shadow", "none");
});

test("with compositing the sheet keeps its shadow", async ({ page }) => {
  await open(page, "/settings");
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-render", /.+/);
  await expect(page.locator(".k-sheet")).not.toHaveCSS("box-shadow", "none");
});

test("WebKit drawing without compositing: lit flames and the torch stand still, the fire on Play draws fewer frames, the effect stays", async ({ page }) => {
  await flatWebKit(page);
  await countWrites(page);
  await countDraws(page);
  await open(page, "/mods/order");
  await expect(page.locator(".k-app")).toHaveAttribute("data-render", "flat");
  const flame = page.locator(".k-flame.on svg").first();
  await expect(flame).toBeVisible();
  await expect(flame).toHaveCSS("animation-name", "none");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  // the fire burns (and flickers on the sheet's edge), the torch stands still: no pointer listener, no --mx writes
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await page.locator(".k-head-link", { hasText: "Settings" }).click();
  await expect.poll(() => page.locator(".k-stage-gl").evaluate((el) => Math.round(el.getBoundingClientRect().height))).toBeLessThanOrEqual(101);
  await wander(page);
  expect((await counts(page)).moves).toBe(0);
  const all = await writes(page);
  expect(all.filter((w) => w.name === "--mx")).toEqual([]);
  expect(all.filter((w) => w.name === "--fire").length).toBeGreaterThan(3);
  // Play: the fire still moves, at a lower rate than with compositing (each frame repaints the whole window there)
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await page.waitForTimeout(1500);
  const d0 = await draws(page);
  await page.waitForTimeout(2000);
  const n = (await draws(page)) - d0;
  expect(n).toBeGreaterThan(4);
  // about 15 a second (FLAT_PLAY_MS), not 30
  expect(n).toBeLessThanOrEqual(Math.ceil(2000 / 66) + 3);
});

test("with compositing (Chromium, so WebView2; WebKit with the GPU) nothing is taken for drawing without it: the torch follows the pointer", async ({ page }) => {
  await countWrites(page);
  await open(page, "/settings");
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-render", /./);
  expect(await page.evaluate(() => matchMedia("(-webkit-transform-3d)").matches)).toBe(true);
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire and the torch never start");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await expect.poll(async () => (await counts(page)).moves).toBe(1);
});

test("WebKit: typing into a sheet page stays quick while the fire burns and the pointer moves", async ({ baseURL }) => {
  let browser;
  try {
    browser = await webkit.launch({ executablePath: process.env.KILN_WEBKIT || undefined });
  } catch (e) {
    test.skip(true, `Playwright's WebKit does not start here: ${String(e).split("\n")[0]}`);
    return;
  }
  try {
    const page = await browser.newPage({ viewport: { width: 1360, height: 860 } });
    await page.goto(`${baseURL}/#/settings/game`);
    await page.locator(".k-splash").waitFor({ state: "detached" });
    await page.waitForTimeout(800);
    await page.locator("#st-keep").click();
    await page.evaluate(() => {
      const w = window as unknown as { __lat: number[] };
      w.__lat = [];
      // a key's time until the frame that shows it has been drawn
      document.addEventListener("keydown", (e) => { const t = e.timeStamp; requestAnimationFrame(() => setTimeout(() => w.__lat.push(performance.now() - t), 0)); }, true);
    });
    for (let i = 0; i < 24; i++) {
      for (let j = 0; j < 4; j++) await page.mouse.move(300 + ((i * 4 + j) * 29) % 700, 240 + ((i * 4 + j) * 47) % 400);
      await page.keyboard.press(String(i % 10));
    }
    await page.waitForTimeout(300);
    const lat = (await page.evaluate(() => (window as unknown as { __lat: number[] }).__lat)).sort((a, b) => a - b);
    expect(lat.length).toBe(24);
    const median = lat[Math.floor(lat.length / 2)];
    const gl = await page.locator(".k-app").getAttribute("data-gl");
    console.log(`WebKit typing: median ${median.toFixed(1)} ms, slowest ${lat.at(-1)!.toFixed(1)} ms (the fire ${gl ? "burned" : "was off: no WebGL"})`);
    expect(median).toBeLessThan(100);
  } finally {
    await browser.close();
  }
});
