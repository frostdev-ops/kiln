// ShroudForge's failed startup pass in the launcher (crates/kiln-manage/src/sf_startup.rs,
// docs/kiln/shroudforge-compat.md 5.7): ShroudForge's own line of the desktop's engine list, and the Deck's Home with a
// dialog that a pad alone can read and close. The player who reported it plays on a Steam Deck.
// Browser mock. The engine status is planned per test (`window.__kilnMockPlans`, one plan per call), so
// src/api/mock.ts stays as it is.
import { expect, test, type Page } from "@playwright/test";

const GAME = "/home/player/.local/share/Steam/steamapps/common/Enshrouded";
const WHAT = "ShroudForge 1.7.9's own loader could not write its mods' changes into the game's data files at its last start "
  + "(2026-11-10T11:00:14Z): pregame phase failed: Access denied. (os error 5). It put the game data back and the game started without them, "
  + "so the mod that needs that step did not load (bone-caller)";
const CURE = "Under Wine and Proton a ShroudForge before 1.8.0 fails this way at every start (it replaces a game file it still has open); "
  + "the ShroudForge that comes with Kiln does not. To use that one, take dbghelp and winmm out of WINEDLLOVERRIDES in the game's Steam launch "
  + "options and keep dinput8=n,b: Kiln then runs the ShroudForge mods of the active profile itself";
const FAILED = {
  error: "pregame phase failed: Access denied. (os error 5)", at: "2026-11-10T11:00:14Z", ownLoader: true, version: "1.7.9",
  mods: ["bone-caller"], cure: CURE, text: `${WHAT}. ${CURE}`,
};

/** The EML row's text while ShroudForge runs the EML mods (sf_startup.rs EngineNotes::of). */
const POINTER = "ShroudForge's startup pass failed at its last start, so the changes of the EML mods it runs are not in the game data either "
  + "(ShroudForge's line says why)";

/** What `engine_status` answers: ShroudForge's own loader ran (no own-loader note on either line), with or without a
 *  failed pass: the whole text on ShroudForge's row, a pointer on EML's, as the backend sends them. */
function engines(failed: typeof FAILED | null) {
  const st = (kind: string, state: unknown) => ({ kind, plugin: `kiln-${kind}`, state, file: `${GAME}/kiln/state/${kind}.json` });
  const mark = failed ? { startupFailed: failed } : {};
  return [
    { ...st("eml", { phase: "aside", reason: "dbghelp.dll is loaded: ShroudForge's loader runs the EML packages itself", mods: [] }),
      ...(failed ? { startupFailed: { ...failed, text: POINTER } } : {}) },
    { ...st("shroudforge", { mode: "self-hosted", reason: "ShroudForge's own bootstrap runs (winmm.dll)", side: "client" }), ...mark,
      effects: { file: `${GAME}/shroudforge/state.json`, state: "stopped", updatedAt: 1794308400, reason: "ShroudForge runtime is stopped; previous mod effects are not current", mods: [] } },
    st("set", null),
  ];
}

/** Every `engine_status` call of this page answers `rows` (a plan is used up by one call; polls ask again). */
async function plan(page: Page, rows: unknown[]) {
  await page.addInitScript((r) => {
    (window as unknown as { __kilnMockPlans: Record<string, { result: unknown }[]> }).__kilnMockPlans =
      { engine_status: Array.from({ length: 200 }, () => ({ result: r })) };
  }, rows);
}

/** A press and release on the native reader's event (as tests/deck.spec.ts). */
async function pad(page: Page, button: string) {
  const send = (pressed: boolean) => page.evaluate(([b, p]) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
    .emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "event9" }), [button, pressed] as const);
  await send(true);
  await send(false);
  await page.waitForTimeout(60);
}
const focused = (page: Page) => page.evaluate(() => (document.activeElement as HTMLElement | null)?.dataset?.deckId ?? null);

test.describe("the desktop's engine list", () => {
  test("ShroudForge's line says that its startup pass failed, with no own-loader note to carry it", async ({ page }) => {
    await plan(page, engines(FAILED));
    await page.goto("/#/mods/engines");
    await page.locator(".k-splash").waitFor({ state: "detached" });
    const sf = page.locator('[data-engine="shroudforge"]');
    await expect(sf).toBeVisible();
    const warn = sf.locator("[data-startup-failed]");
    await expect(warn).toContainText("could not write its mods' changes into the game's data files");
    await expect(warn).toContainText("take dbghelp and winmm out of WINEDLLOVERRIDES");
    await expect(warn).toHaveClass(/k-tone-warn/);
    // the EML line points there: ShroudForge runs the EML mods in the same pass
    const eml = page.locator('[data-engine="eml"] [data-startup-failed]');
    await expect(eml).toContainText("ShroudForge's startup pass failed at its last start");
    await expect(eml).not.toContainText("WINEDLLOVERRIDES");
  });

  test("no such line while the pass is fine", async ({ page }) => {
    await plan(page, engines(null));
    await page.goto("/#/mods/engines");
    await page.locator(".k-splash").waitFor({ state: "detached" });
    await expect(page.locator('[data-engine="shroudforge"]')).toBeVisible();
    await expect(page.locator("[data-startup-failed]")).toHaveCount(0);
  });
});

test.describe("the Deck", () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  async function openDeck(page: Page) {
    await page.goto("/?deck=steamdeck#/");
    await expect(page.locator('[data-deck-screen="home"]')).toBeVisible();
    await expect.poll(() => focused(page)).toBe("mouth");
  }

  test("Home says that ShroudForge's mods did not load, and a dialog says why and what to change", async ({ page }) => {
    await plan(page, engines(FAILED));
    await openDeck(page);
    // the line under the last boot, and the button to the whole text
    await expect(page.getByTestId("deck-sf-startup")).toContainText("ShroudForge mods did not load");
    const button = page.locator('[data-deck-id="sf-startup"]');
    await expect(button).toBeVisible();
    // the pad reaches it from the Play arch, and A opens the dialog
    await page.keyboard.press("ArrowLeft");
    await expect.poll(() => focused(page)).toBe("sf-startup");
    await pad(page, "a");
    const dialog = page.locator('[data-deck-dialog="sf-startup"]');
    await expect(dialog).toContainText("could not write its mods' changes into the game's data files");
    await expect(dialog).toContainText("(bone-caller)");
    await expect(dialog).toContainText("take dbghelp and winmm out of WINEDLLOVERRIDES");
    // nothing of the text is out of reach: it is all inside the dialog's body, which the right stick scrolls when
    // it does not fit the screen
    const body = dialog.locator(".deck-scroll");
    await expect(body).toBeVisible();
    const box = await dialog.locator(".deck-dialog").boundingBox();
    expect(box && box.y >= 0 && box.y + box.height <= 800, "the dialog is on the screen").toBe(true);
    const fits = await body.evaluate((el) => el.scrollHeight <= el.clientHeight + 1);
    if (!fits) {
      const stick = (value: number) => page.evaluate((v) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
        .emit("kiln://gamepad", { kind: "axis", axis: "ry", value: v, device: "event9" }), value);
      for (let i = 0; i < 20; i++) await stick(1);
      await stick(0);
      await expect.poll(() => body.evaluate((el) => el.scrollTop)).toBeGreaterThan(0);
    }
    // B closes it, and focus is back on the button
    await pad(page, "b");
    await expect(dialog).toHaveCount(0);
    await expect.poll(() => focused(page)).toBe("sf-startup");
    // A on OK closes it too
    await pad(page, "a");
    await expect(dialog).toBeVisible();
    await pad(page, "a");
    await expect(dialog).toHaveCount(0);
  });

  test("a long text scrolls with the right stick", async ({ page }) => {
    const many = Array.from({ length: 60 }, (_, i) => `startup-mod-${i}`);
    const long = { ...FAILED, mods: many, text: `${WHAT.replace("(bone-caller)", `(${many.join(", ")})`)}. ${CURE}` };
    await plan(page, engines(long));
    await openDeck(page);
    await page.locator('[data-deck-id="sf-startup"]').focus();
    await pad(page, "a");
    const dialog = page.locator('[data-deck-dialog="sf-startup"]');
    const body = dialog.locator(".deck-scroll");
    await expect(body).toContainText("startup-mod-59");
    const box = await dialog.locator(".deck-dialog").boundingBox();
    expect(box && box.y >= 0 && box.y + box.height <= 800, "the dialog is on the screen").toBe(true);
    expect(await body.evaluate((el) => el.scrollHeight <= el.clientHeight + 1), "sixty mod names do not fit at 1280x800").toBe(false);
    const stick = (value: number) => page.evaluate((v) => (window as unknown as { __kilnMock: { emit: (n: string, v: unknown) => void } }).__kilnMock
      .emit("kiln://gamepad", { kind: "axis", axis: "ry", value: v, device: "event9" }), value);
    for (let i = 0; i < 20; i++) await stick(1);
    await stick(0);
    await expect.poll(() => body.evaluate((el) => el.scrollTop)).toBeGreaterThan(0);
    await expect(dialog.getByRole("button", { name: "OK" })).toBeVisible();
  });

  test("Home stays as it was while ShroudForge's pass is fine", async ({ page }) => {
    await plan(page, engines(null));
    await openDeck(page);
    await expect(page.getByTestId("deck-last-boot")).toBeVisible();
    await expect(page.getByTestId("deck-sf-startup")).toHaveCount(0);
    await expect(page.locator('[data-deck-id="sf-startup"]')).toHaveCount(0);
  });
});
