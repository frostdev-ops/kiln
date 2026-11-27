// The festive season (shell/season.ts, shell/Festive.tsx): the mantel over the mouth on Play, the threshold's diyas, the
// garland on the sheets and the snow in the fire (Stage.tsx FRAG_FESTIVE), from the Monday after Thanksgiving weekend to
// 7 January. Off under automation; ?theme=festive / ?theme=normal force it, ?theme=auto lets the date decide (with the
// page's clock set). On by default in the season, switched off in Settings for the rest of it. Never with the turkey.
// Outside the season the launcher compiles and draws the same fire as every other day.
import { expect, test, type Page } from "@playwright/test";

// a smaller window than the suite's, the player's zone fixed (the dates are local)
test.use({ viewport: { width: 1024, height: 640 }, timezoneId: "America/New_York" });

type Rec = { __frags: string[]; __draws: number };
async function record(page: Page) {
  await page.addInitScript(() => {
    const w = window as unknown as Rec;
    w.__frags = [];
    w.__draws = 0;
    const P = WebGLRenderingContext.prototype;
    const src = P.shaderSource, draw = P.drawArrays;
    P.shaderSource = function (s: WebGLShader, text: string) {
      if (text.includes("gl_FragColor")) w.__frags.push(text);
      return src.call(this, s, text);
    };
    P.drawArrays = function (m: number, f: number, c: number) {
      w.__draws += 1;
      return draw.call(this, m, f, c);
    };
  });
}
const rec = (page: Page) => page.evaluate(() => { const w = window as unknown as Rec; return { frags: w.__frags.slice(), draws: w.__draws }; });
const hasGl = (page: Page) => page.evaluate(() => !!document.createElement("canvas").getContext("webgl"));
const shaders = (page: Page) => page.evaluate(async () => {
  const m = await import("/src/shell/Stage.tsx");
  return { plain: m.FRAG as string, festive: m.FRAG_FESTIVE as string };
});

async function open(page: Page, query: string, route = "/") {
  await page.goto(`/${query}#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
/** 00:00 local in New York as an instant (EST, UTC-5, over these dates). */
const ny = (y: number, mo: number, d: number, h = 0, mi = 0) => new Date(Date.UTC(y, mo - 1, d, h + 5, mi));

test("under automation, without a word in the address: every day's launcher, no Settings row", async ({ page }) => {
  await record(page);
  await open(page);
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-theme", /.+/);
  await expect(page.locator(".k-fest-mantel")).toHaveCount(0);
  if (await hasGl(page)) {
    await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
    expect([...new Set((await rec(page)).frags)]).toEqual([(await shaders(page)).plain]);
  }
  await page.goto("/#/settings");
  await expect(page.locator('[data-setting="fire"]')).toBeVisible();
  await expect(page.locator('[data-setting="festive"]')).toHaveCount(0);
});

test("forced on: the mantel and its wish, the threshold's diyas (no clicks taken), the snowy fire, the sheets' garland", async ({ page }) => {
  await record(page);
  await open(page, "?theme=festive");
  await expect(page.locator(".k-app")).toHaveAttribute("data-theme", "festive");
  await expect(page.locator(".k-fest-mantel").first()).toBeVisible();
  await expect(page.locator(".kf-greet-a")).toHaveText("Warm wishes for the season");
  // the symbols: a hanukkiah's nine lights and a kinara's seven, a flame for each lamp
  expect(await page.locator(".k-fest-mantel:not(.k-fest-hearth) [data-fl]").count()).toBe(9 + 7 + 4);
  // the Play button still takes the click where the diyas stand in front of it
  const b = (await page.locator(".k-door").boundingBox())!;
  expect(await page.evaluate(([x, y]) => document.elementFromPoint(x, y)?.closest(".k-door") !== null, [b.x + 14, b.y + b.height - 8])).toBe(true);
  if (await hasGl(page)) {
    await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
    expect([...new Set((await rec(page)).frags)]).toEqual([(await shaders(page)).festive]);
  }
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Servers" }).click();
  await expect(page.locator(".k-sheet .k-fest-rim")).toBeVisible();
  await expect(page.locator(".k-fest-mantel").first()).toBeHidden();
});

test("behind a sheet nothing festive moves (the mantel and the threshold's diyas are hidden there); on Play they burn with the fire", async ({ page }) => {
  await page.addInitScript(() => {
    const w = window as unknown as { __festSets: number };
    w.__festSets = 0;
    const set = Element.prototype.setAttribute;
    Element.prototype.setAttribute = function (n: string, v: string) {
      if (this instanceof SVGElement && this.closest(".k-fest-mantel, .k-fest-rim")) w.__festSets += 1;
      return set.call(this, n, v);
    };
  });
  const sets = () => page.evaluate(() => (window as unknown as { __festSets: number }).__festSets);
  await open(page, "?theme=festive", "/servers");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await expect(page.locator(".k-fest-hearth")).toBeHidden();
  await page.waitForTimeout(500);
  const a = await sets();
  await page.waitForTimeout(1500);
  expect(await sets()).toBe(a);
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Play" }).click();
  await expect(page.locator(".k-fest-hearth")).toBeVisible();
  await page.waitForTimeout(600);
  const b = await sets();
  await page.waitForTimeout(800);
  expect(await sets()).toBeGreaterThan(b);
});

// The garland's thousands of needles cost the most to draw. In one SVG with the flames, every frame of the fire drew the
// whole mantel again (Chromium drawing on the CPU, as WebKit does: about 0.9 s of drawing a second on Play at 1280x800,
// against 0.02-0.1 s with every day's fire). In a still layer of its own it is drawn once.
test("the garland lies in a still layer of its own: nothing that moves is in it, nothing of it is elsewhere", async ({ page }) => {
  await open(page, "?theme=festive");
  const r = await page.evaluate(() => {
    const mantel = document.querySelector(".k-fest-mantel:not(.k-fest-hearth)")!;
    const still = mantel.querySelector(":scope > svg.kf-still");
    const moving = [...mantel.querySelectorAll("[data-fl], [data-gl], [data-bulb], [data-sway], [data-firelit]")];
    const boughs = [...mantel.querySelectorAll("g[stroke-linecap]")];
    return {
      still: !!still,
      boughs: boughs.length,
      boughsElsewhere: boughs.filter((g) => !still?.contains(g)).length,
      movingInStill: moving.filter((e) => still?.contains(e)).length,
      // each layer its own (will-change: transform)
      layers: [...mantel.querySelectorAll(":scope > svg")].map((s) => getComputedStyle(s).willChange),
    };
  });
  expect(r).toEqual({ still: true, boughs: 3, boughsElsewhere: 0, movingInStill: 0, layers: ["transform", "transform", "transform"] });
});

// WebKit without compositing (WebKitGTK where the DMA-BUF renderer failed: Stage.tsx flatDrawing) paints the whole
// window again on the CPU for each frame of the fire, and every thing over it: there the still mantel would be drawn
// 15 times a second (its needles, lamps and glows: about three times the cost of the Play page under it). So while the
// fire burns there, no mantel and no threshold diyas; the snow, the accent and the sheets' garland stay. With the fire
// off the mantel is drawn once and stays.
async function flatWebKit(page: Page) {
  await page.addInitScript(() => {
    const mm = window.matchMedia.bind(window);
    window.matchMedia = (q: string) => (q.includes("-webkit-transform-3d") ? { ...mm("(min-width: 1000000px)"), media: q, matches: false } as MediaQueryList : mm(q));
  });
}

test("WebKit without compositing: no mantel while the fire burns (each frame would paint it again), the rest of the season stays", async ({ page }) => {
  await flatWebKit(page);
  await record(page);
  await open(page, "?theme=festive");
  await expect(page.locator(".k-app")).toHaveAttribute("data-render", "flat");
  await expect(page.locator(".k-app")).toHaveAttribute("data-theme", "festive");
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  await page.waitForTimeout(300);
  await expect(page.locator(".k-fest-mantel")).toHaveCount(0);
  expect([...new Set((await rec(page)).frags)]).toEqual([(await shaders(page)).festive]);
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Servers" }).click();
  await expect(page.locator(".k-sheet .k-fest-rim")).toBeVisible();
});

test("WebKit without compositing, the fire off: the mantel drawn once", async ({ page }) => {
  await flatWebKit(page);
  await open(page, "?theme=festive&render=software");
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-gl", "1");
  await expect(page.locator(".k-fest-mantel").first()).toBeVisible();
  await expect(page.locator(".k-fest-hearth")).toBeVisible();
});

test("forced off: every day's fire, byte for byte", async ({ page }) => {
  await record(page);
  await open(page, "?theme=normal");
  await expect(page.locator(".k-fest-mantel")).toHaveCount(0);
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-theme", /.+/);
  test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
  await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  const frags = [...new Set((await rec(page)).frags)];
  expect(frags).toEqual([(await shaders(page)).plain]);
  expect(frags[0]).not.toContain("snow(");
});

// A driver that cannot build the festive fire's shader (the snow makes it bigger; ANGLE's D3D back-end compiles it only at
// the link) keeps every day's fire, as one that cannot build the turkey's does (thanksgiving.spec.ts): the fire never goes
// out for the season. The snow is in that shader, so there is none; the rest of the season stays as on any other GPU (the
// mantel and the threshold's diyas, burning with that fire, the accent, the sheets' garland).
for (const at of ["compile", "link"] as const) {
  test(`a GPU that cannot build the festive shader (${at}) keeps every day's fire, without the snow; the mantel burns with it`, async ({ page }) => {
    await record(page);
    await page.addInitScript((at: string) => {
      const P = WebGLRenderingContext.prototype;
      const texts = new WeakMap<WebGLShader, string>();
      const parts = new WeakMap<WebGLProgram, WebGLShader[]>();
      const src = P.shaderSource, attach = P.attachShader, shaderParam = P.getShaderParameter, programParam = P.getProgramParameter;
      P.shaderSource = function (s: WebGLShader, text: string) { texts.set(s, text); return src.call(this, s, text); };
      P.attachShader = function (p: WebGLProgram, s: WebGLShader) { parts.set(p, [...(parts.get(p) ?? []), s]); return attach.call(this, p, s); };
      const snowy = (s: WebGLShader) => texts.get(s)?.includes("snow(") === true;
      if (at === "compile") {
        P.getShaderParameter = function (s: WebGLShader, name: number) {
          return name === this.COMPILE_STATUS && snowy(s) ? false : shaderParam.call(this, s, name);
        };
      } else {
        P.getProgramParameter = function (p: WebGLProgram, name: number) {
          return name === this.LINK_STATUS && (parts.get(p) ?? []).some(snowy) ? false : programParam.call(this, p, name);
        };
      }
    }, at);
    await open(page, "?theme=festive");
    test.skip(!(await hasGl(page)), "no WebGL in this browser: the fire never starts");
    await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
    await expect(page.locator(".k-app")).toHaveAttribute("data-theme", "festive");
    const { plain, festive } = await shaders(page);
    const a = await rec(page);
    // the festive shader was tried first, then every day's: the one that draws
    expect([...new Set(a.frags)]).toEqual([festive, plain]);
    await expect.poll(async () => (await rec(page)).draws - a.draws).toBeGreaterThan(2);
    await expect(page.locator(".k-fest-mantel").first()).toBeVisible();
    await expect(page.locator(".k-fest-hearth")).toBeVisible();
    await expect(page.locator(".kf-greet-a")).toHaveText("Warm wishes for the season");
    // its flames move on that fire's frames
    const flame = page.locator(".k-fest-mantel:not(.k-fest-hearth) [data-fl]").first();
    const first = await flame.getAttribute("transform");
    await expect.poll(() => flame.getAttribute("transform")).not.toBe(first);
    await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Servers" }).click();
    await expect(page.locator(".k-sheet .k-fest-rim")).toBeVisible();
    await expect(page.locator(".k-app")).toHaveAttribute("data-gl", "1");
  });
}

// The threshold's diyas stand on the floor in front of the jambs, the band the Crucible dock's controls sit in (6 px under
// the mouth). Where the dock's row is nearly as wide as the mouth (a small window, a longer language), a diya's flame or
// spout ran under the Share button or a link; the row keeps clear of them instead, wrapping sooner. Where it had room,
// the dock stands exactly where it does every other day.
type Box = { l: number; t: number; r: number; b: number };
const dockAndDiyas = (page: Page) => page.evaluate(() => {
  const box = (e: Element): Box => { const b = e.getBoundingClientRect(); return { l: b.left, t: b.top, r: b.right, b: b.bottom }; };
  const diyas = [...document.querySelectorAll(".k-fest-hearth > g")].map((g) =>
    [...g.querySelectorAll('path[fill="url(#kf-clay)"], [data-fl]')].map(box).reduce((a, p) => ({ l: Math.min(a.l, p.l), t: Math.min(a.t, p.t), r: Math.max(a.r, p.r), b: Math.max(a.b, p.b) })));
  return { diyas, controls: [...document.querySelectorAll(".kcr-dock-row > *")].map(box) };
});
test("the threshold's diyas keep clear of the Crucible dock's controls; where there was room the dock has not moved", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await open(page, "?theme=festive");
  await expect(page.locator(".k-fest-hearth")).toBeVisible();
  await expect(page.getByTestId("crucible-share")).toBeVisible();
  for (const [width, height] of [[1024, 640], [1152, 648], [1280, 720]]) {
    await page.setViewportSize({ width, height });
    await expect.poll(async () => {
      const { diyas, controls } = await dockAndDiyas(page);
      const touching: string[] = [];
      for (const [i, d] of diyas.entries()) for (const [j, c] of controls.entries()) {
        // 2 px apart at least, across or down
        if (Math.max(c.l - d.r, d.l - c.r, c.t - d.b, d.t - c.b) < 2) touching.push(`${width}x${height}: diya ${i} and control ${j}`);
      }
      return touching;
    }).toEqual([]);
  }
  await page.setViewportSize({ width: 1920, height: 1080 });
  await page.waitForTimeout(200);
  const festive = (await dockAndDiyas(page)).controls;
  await open(page, "?theme=normal");
  await page.waitForTimeout(200);
  expect((await dockAndDiyas(page)).controls).toEqual(festive);
});

test("the turkey wins: never both", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await open(page, "?turkey=1&theme=festive");
  await expect(page.locator(".k-fest-mantel")).toHaveCount(0);
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-theme", /.+/);
});

test("reduced motion: a still frame (no snow falling, no sway, no flicker), the mantel drawn", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await record(page);
  await open(page, "?theme=festive");
  await expect(page.locator(".k-fest-mantel").first()).toBeVisible();
  const sway = page.locator("[data-sway]").first(), flame = page.locator("[data-fl]").first();
  await page.waitForTimeout(700);
  const a = { sway: await sway.getAttribute("transform"), flame: await flame.getAttribute("transform"), draws: (await rec(page)).draws };
  await page.waitForTimeout(900);
  expect(await sway.getAttribute("transform")).toBe(a.sway);
  expect(await flame.getAttribute("transform")).toBe(a.flame);
  expect((await rec(page)).draws).toBe(a.draws);
});

test("the date decides (?theme=auto): on by default in December, off in Settings, still off after a restart", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.clock.install({ time: ny(2026, 12, 10, 20) });
  await open(page, "?theme=auto", "/settings");
  await expect(page.locator(".k-app")).toHaveAttribute("data-theme", "festive");
  const row = page.locator('[data-setting="festive"]');
  await expect(row).toContainText("Festive season");
  await expect(page.getByRole("switch", { name: /^Festive season/ })).toBeChecked();
  await row.locator("label.k-toggle").click();
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-theme", /.+/);
  expect(await page.evaluate(() => localStorage.getItem("kiln.festive.off"))).toBe("2026");
  await page.reload();
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(page.getByRole("switch", { name: /^Festive season/ })).not.toBeChecked();
  await page.evaluate(() => { location.hash = "#/"; });
  await expect(page.locator(".k-door")).toBeVisible();
  await expect(page.locator(".k-fest-mantel")).toHaveCount(0);
  // on again: at once, and the opt-out is gone
  await page.evaluate(() => { location.hash = "#/settings"; });
  await row.locator("label.k-toggle").click();
  await expect(page.locator(".k-app")).toHaveAttribute("data-theme", "festive");
  expect(await page.evaluate(() => localStorage.getItem("kiln.festive.off"))).toBeNull();
});

test("a launcher left open: the turkey gives way at Monday 00:00, the festive look goes at 8 January 00:00", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await page.clock.install({ time: ny(2026, 11, 29, 23, 55) });
  await open(page, "?theme=auto");
  await expect(page.locator(".k-fest-mantel")).toHaveCount(0);
  // Sunday night: the turkey (its greeting shows where the fire burns)
  if (await hasGl(page)) await expect(page.locator(".k-thanks")).toBeVisible();
  await page.clock.fastForward("10:00");
  await expect(page.locator(".k-fest-mantel").first()).toBeVisible();
  await expect(page.locator(".k-thanks")).toHaveCount(0);
  await page.clock.setSystemTime(ny(2027, 1, 7, 23, 55));
  await page.clock.fastForward("10:00");
  await expect(page.locator(".k-fest-mantel")).toHaveCount(0);
  await expect(page.locator(".k-app")).not.toHaveAttribute("data-theme", /.+/);
});

test("the wish in the player's language, kept between the lintel's joints", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await open(page, "?theme=festive", "/settings");
  await page.locator("#st-lang").selectOption("pl");
  await expect.poll(() => page.evaluate(() => document.documentElement.lang)).toBe("pl");
  await page.evaluate(() => { location.hash = "#/"; });
  const greet = page.locator(".kf-greet-a");
  await expect(greet).toHaveText("Ciepłych świąt");
  expect(await greet.evaluate((el) => (el as SVGTextElement).getComputedTextLength())).toBeLessThanOrEqual(601);
});
