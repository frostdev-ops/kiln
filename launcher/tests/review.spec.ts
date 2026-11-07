// Regression cases from the frontend review of 1bae3f6, eba9a5e and c3515fe. Browser mock only.
import { expect, test, type Page } from "@playwright/test";

type Plan = { delay?: number; error?: { code: string; message: string } };
type Mock = { calls: () => string[]; planCommand: (cmd: string, plan: Plan) => void; setNoProxy: (on: boolean) => void; emit: (name: string, payload: unknown) => void };
const calls = (page: Page, cmd: string) => page.evaluate((c) => (window as unknown as { __kilnMock: Mock }).__kilnMock.calls().filter((x) => x === c).length, cmd);
const plan = (page: Page, cmd: string, next: Plan) => page.evaluate(([c, p]) => (window as unknown as { __kilnMock: Mock }).__kilnMock.planCommand(c, p), [cmd, next] as const);
const focusId = (page: Page) => page.evaluate(() => (document.activeElement as HTMLElement)?.dataset.deckId);
const drop = async (page: Page, selector: string, ...files: string[]) => {
  const dt = await page.evaluateHandle((names) => { const d = new DataTransfer(); names.forEach((n) => d.items.add(new File(["x"], n))); return d; }, files);
  await page.locator(selector).dispatchEvent("drop", { dataTransfer: dt });
};
const pad = (page: Page, button: string, pressed = true) => page.evaluate(([b, p]) => (window as unknown as { __kilnMock: Mock }).__kilnMock.emit("kiln://gamepad", { kind: "button", button: b, pressed: p, repeat: false, device: "review-pad" }), [button, pressed] as const);

async function desktop(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

test("overlapping identical drops run one add, report skipped files, and Undo refreshes counts", async ({ page }) => {
  await desktop(page);
  const before = await calls(page, "library_add");
  await drop(page, ".k-hero", "night-owl-1.2.0.zip", "notes.txt");
  await drop(page, ".k-hero", "night-owl-1.2.0.zip", "notes.txt");
  const toast = page.locator(".k-toast", { hasText: "Added Night Owl" });
  await expect(toast).toBeVisible();
  await expect(toast).toContainText("1 skipped:");
  expect(await calls(page, "library_add")).toBe(before + 1);
  await toast.getByRole("button", { name: "Undo" }).click();
  await expect(page.locator(".k-toast", { hasText: "Removed Night Owl" })).toBeVisible();
  await page.evaluate(() => { location.hash = "#/profiles/ember-run/mods"; });
  await expect(page.locator(".k-order-row", { hasText: "night-owl" })).toHaveCount(0);
});

test("Undo reports when a later profile edit must be kept", async ({ page }) => {
  await desktop(page);
  await drop(page, ".k-hero", "night-owl-1.2.0.zip");
  const added = page.locator(".k-toast", { hasText: "Added Night Owl" });
  await expect(added).toBeVisible();
  await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    const p = await api.profileGet("ember-run");
    p.mods.kiln.find((m) => m.id === "night-owl")!.enabled = false;
    await api.profileSave(p);
  });
  await added.getByRole("button", { name: "Undo" }).click();
  await expect(page.locator(".k-toast", { hasText: "Undo kept later changes" })).toBeVisible();
  const enabled = await page.evaluate(async () => (await (await import("/src/api/commands.ts")).profileGet("ember-run")).mods.kiln.find((m) => m.id === "night-owl")?.enabled);
  expect(enabled).toBe(false);
});

test("a DropZone owns its drop once, and a dialog prevents background drops", async ({ page }) => {
  await desktop(page, "/library");
  const before = await calls(page, "library_add");
  await drop(page, "[data-drop]", "night-owl-1.2.0.zip");
  await expect(page.locator(".k-toast", { hasText: "Added Night Owl" })).toBeVisible();
  expect(await calls(page, "library_add")).toBe(before + 1);
  await page.evaluate(() => { location.hash = "#/"; });
  await page.getByRole("button", { name: "Another profile", exact: true }).click();
  await expect(page.getByRole("dialog", { name: "Play a profile once" })).toBeVisible();
  await drop(page, ".k-hero", "dawn-lamp-2.0.0.zip");
  await page.waitForTimeout(150);
  expect(await calls(page, "library_add")).toBe(before + 1);
});

test("failed adds surface an error and a later drop can succeed", async ({ page }) => {
  await desktop(page);
  await plan(page, "library_add", { error: { code: "io", message: "Import storage is full" } });
  await drop(page, ".k-hero", "night-owl-1.2.0.zip");
  await expect(page.getByRole("alert").filter({ hasText: "Import storage is full" })).toBeVisible();
  await drop(page, ".k-hero", "night-owl-1.2.0.zip");
  await expect(page.locator(".k-toast", { hasText: "Added Night Owl" })).toBeVisible();
});

test("every dropped modpack gets separate consent, also with profile adds disabled", async ({ page }) => {
  await desktop(page, "/settings");
  await page.getByText("Add new mods to the active profile", { exact: true }).click();
  await expect(page.getByRole("switch", { name: /^Add new mods/ })).not.toBeChecked();
  await drop(page, ".k-sheet", "first-modpack.zip", "second-modpack.zip");
  const dialog = page.getByRole("dialog", { name: "Install this modpack?" });
  await expect(dialog).toContainText("first-modpack.zip");
  await expect(dialog.getByRole("button", { name: "Install", exact: true })).toBeDisabled();
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(dialog).toContainText("second-modpack.zip");
  await expect(dialog.getByRole("button", { name: "Install", exact: true })).toBeDisabled();
  expect(await calls(page, "modpack_install")).toBe(0);
  expect(await calls(page, "library_import")).toBe(0);
});

test("a first-install profile failure is visible and a later drop recovers", async ({ page }) => {
  await page.goto("/?mock=fresh#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(page.locator(".k-door")).toHaveAccessibleName("Install Kiln");
  await plan(page, "profile_ensure_default", { error: { code: "io", message: "Profile write failed" } });
  await page.locator(".k-door").click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install", exact: true }).click();
  await expect(page.getByRole("alert").filter({ hasText: "Profile write failed" })).toBeVisible();
  await drop(page, ".k-hero", "night-owl-1.2.0.zip");
  await expect(page.locator(".k-toast", { hasText: "Added Night Owl" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Active profile: My mods. Change profile." })).toBeVisible();
});

test("desktop settings cards preserve each other's changes during a slow save", async ({ page }) => {
  await desktop(page, "/settings");
  await plan(page, "app_settings_set", { delay: 400 });
  await page.getByText("Add new mods to the active profile", { exact: true }).click();
  const appearance = page.locator(".k-card", { has: page.getByRole("heading", { name: "Appearance", exact: true }) });
  await appearance.getByText("Animated fire", { exact: true }).click();
  const deck = page.locator(".k-card", { has: page.getByRole("heading", { name: "Steam Deck", exact: true }) });
  await deck.getByRole("radio", { name: "Deck mode", exact: true }).click();
  await expect.poll(() => page.evaluate(async () => {
    const s = await (await import("/src/api/commands.ts")).appSettingsGet();
    return [s.addToActiveProfile, s.animatedFire, s.uiMode];
  })).toEqual([false, false, "deck"]);
});

test("a deferred first-run drop stays with its game when selection changes", async ({ page }) => {
  await page.goto("/?mock=fresh#/");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(page.locator(".k-door")).toHaveAccessibleName("Install Kiln");
  await drop(page, ".k-hero", "night-owl-1.2.0.zip");
  const waiting = page.locator(".k-toast", { hasText: "Install Kiln first" });
  await expect(waiting).toBeVisible();
  const other = await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    const g = await api.gameAdd("/tmp/another-enshrouded");
    (await import("/src/api/hooks.ts")).useGameSel.getState().setGameId(g.id);
    return g.id;
  });
  await waiting.getByRole("button", { name: "Install Kiln", exact: true }).click();
  await page.getByRole("dialog", { name: "Install Kiln" }).getByRole("button", { name: "Install", exact: true }).click();
  // the mock install's progress runs first (firstrun.spec.ts waits as long)
  await expect(page.locator(".k-toast", { hasText: "Added Night Owl" })).toBeVisible({ timeout: 12_000 });
  const result = await page.evaluate(async (id) => {
    const api = await import("/src/api/commands.ts");
    return [(await api.gameStatus("client-steam")).runtime.installed, (await api.gameStatus(id)).runtime.installed,
      (await api.profileActive("client-steam"))?.profileId, await api.profileActive(id)];
  }, other);
  expect(result).toEqual([true, false, "my-mods", null]);
});

test("Play is disabled while an installed runtime is being changed", async ({ page }) => {
  await desktop(page);
  await expect(page.locator(".k-door")).toBeEnabled();
  await page.evaluate(async () => {
    (await import("/src/api/hooks.ts")).useRuntimeChanges.getState().put("client-steam", {
      what: "Update", version: "0.2.0", taskId: null, state: "running", error: null, at: Date.now(),
    });
  });
  await expect(page.locator(".k-door")).toBeDisabled();
  await expect(page.getByRole("button", { name: "Without mods", exact: true })).toBeDisabled();
});

test("Nexus command boundary refuses every update operation, including direct download calls", async ({ page }) => {
  await page.goto("/?dist=nexus#/settings");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  const results = await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    return Promise.all([api.updateStatus, api.updateCheck, api.updateInstall, api.updateRestart, () => api.updateSkip(null), api.updateAck]
      .map(async (run) => { try { await run(); return "invoked"; } catch (e) { return (e as { code: string }).code; } }));
  });
  expect(results).toEqual(Array(6).fill("unsupported"));
  const invoked = await page.evaluate(() => (window as unknown as { __kilnMock: Mock }).__kilnMock.calls().filter((c) => c.startsWith("update_")));
  expect(invoked).toEqual([]);
});

test.describe("Deck recovery", () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test("slow settings keep detection at the startup deadline, with no desktop flash", async ({ page }) => {
    await page.addInitScript(() => {
      (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { app_settings_get: [{ delay: 2200 }, { delay: 2200 }] };
    });
    await page.goto("/?deck=steamdeck#/");
    await expect(page.locator(".deck")).toBeVisible();
    await expect(page.locator(".k-app")).toHaveCount(0);
    await expect(page).toHaveURL(/#\/deck$/);
  });

  test("Tab stays in dialogs; B closes them and restores focus", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck");
    const opener = page.locator('[data-deck-id="switch-profile"]');
    await opener.click();
    await expect(page.getByRole("dialog", { name: "Profile", exact: true })).toBeVisible();
    for (let i = 0; i < 8; i++) await page.keyboard.press("Tab");
    expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"]'))).toBe(true);
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(opener).toBeFocused();
  });

  test("window blur cancels an uninstall hold", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/kiln");
    const hold = page.locator('[data-deck-id="uninstall"]');
    await hold.focus();
    await pad(page, "a");
    await expect(hold).toHaveClass(/holding/);
    await page.evaluate(() => window.dispatchEvent(new Event("blur")));
    await page.waitForTimeout(1350);
    expect(await calls(page, "runtime_uninstall")).toBe(0);
    await expect(hold).not.toHaveClass(/holding/);
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await pad(page, "a", false);
  });

  test("a disabled focused control recovers to an enabled stop", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    const row = page.locator('[data-deck-id="set-fire"]');
    await row.focus();
    await row.evaluate((el) => { (el as HTMLButtonElement).disabled = true; });
    await expect.poll(() => focusId(page)).toBe("set-desktop");
  });

  test("rapid settings changes survive a slow save and an earlier failed write", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    await expect(page.locator('[data-deck-id="set-start"]')).toContainText("Auto");
    await plan(page, "app_settings_set", { delay: 300, error: { code: "io", message: "Settings write failed" } });
    const row = page.locator('[data-deck-id="set-start"]');
    await row.click();
    await row.click();
    await expect(row).toContainText("Deck mode");
    await expect.poll(() => page.evaluate(async () => (await (await import("/src/api/commands.ts")).appSettingsGet()).uiMode)).toBe("deck");
  });

  test("vanilla with an unread flag does not become No Kiln", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck");
    await expect(page.locator('[data-deck-id="mouth"]')).toBeEnabled();
    await page.evaluate(() => (window as unknown as { __kilnMock: Mock }).__kilnMock.setNoProxy(true));
    await pad(page, "x");
    await page.locator('[data-deck-id="once-vanilla"]').click();
    await expect(page.getByTestId("deck-state")).toHaveText(/^Vanilla/);
    await expect(page.getByRole("button", { name: "Kiln is not loading", exact: true })).toHaveCount(0);
  });

  test("the tab bar stays clear of the running clock at 1280 by 800", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    await expect(page.getByTestId("deck-state")).toBeVisible();
    await page.getByTestId("deck-state").evaluate((el) => { el.textContent = "No Kiln 1:23:45"; });
    const boxes = await page.evaluate(() => {
      const right = Math.max(...[...document.querySelectorAll('.deck-tabs button')].map((el) => el.getBoundingClientRect().right));
      const state = document.querySelector('.deck-state')!.getBoundingClientRect();
      return { right, state: state.left, viewport: innerWidth, edge: state.right };
    });
    expect(boxes.right).toBeLessThanOrEqual(boxes.state);
    expect(boxes.edge).toBeLessThanOrEqual(boxes.viewport);
  });
});
