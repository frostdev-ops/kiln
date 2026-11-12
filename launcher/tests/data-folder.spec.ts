// Smoke (browser mock): the data folder. Settings' "Data folder" row (the path at once, the size when measured), a move
// from its confirmation through the task's progress to the restart, Cancel, a failed move, every refusal with its
// sentence, "Use it", the dialog over the whole app for a pointer to a folder that cannot be used (Choose folder,
// Start fresh, Quit), the notice after a move with and without files left behind, KILN_DATA_DIR, and the Deck's row.
// The mock (src/api/mockDataFolder.ts) starts from `?df=` and answers the folder dialog from __kilnDataFolderMock.pick.
import { expect, test, type Page } from "@playwright/test";

const DATA = "/home/player/.local/share/kiln";
const BROKEN = "Kiln cannot open its data folder";

type Mock = Record<string, (...a: unknown[]) => unknown>;
const df = (page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(([f, a]) => (window as unknown as { __kilnDataFolderMock: Mock }).__kilnDataFolderMock[f as string](...(a as unknown[])), [fn, args] as const);
const calls = (page: Page) => page.evaluate(() => (window as unknown as { __kilnMock: { calls: () => string[] } }).__kilnMock.calls());
const pick = (page: Page, path: string | null) => df(page, "pick", path);

async function open(page: Page, url = "/#/settings") {
  await page.goto(url);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}

const row = (page: Page) => page.getByTestId("data-folder");
const change = (page: Page) => row(page).getByRole("button", { name: "Change", exact: true });

/** Esc and a click outside leave a dialog that cannot be dismissed where it is. */
async function cannotDismiss(page: Page, name: string) {
  await page.keyboard.press("Escape");
  await page.mouse.click(4, 4);
  await expect(page.getByRole("dialog", { name })).toBeVisible();
}

test("the row: the path at once, the size when it is measured, Open", async ({ page }) => {
  // the size answers late: the path must not wait for it
  await page.addInitScript(() => { (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { data_folder_size: [{ delay: 2500 }] }; });
  await open(page);
  const path = page.getByTestId("data-folder-path");
  await expect(path).toHaveText(DATA);
  await expect(path).toHaveAttribute("title", DATA);
  const size = page.getByTestId("data-folder-size");
  await expect(size).toHaveText("…");
  await expect(size).toHaveAttribute("aria-busy", "true");
  await expect(size).toHaveText("3.4 GB", { timeout: 6000 });
  await expect(size).toHaveAttribute("title", "12,408 files");
  await expect(change(page)).toBeEnabled();
  // the Library card's old "Folder" line is gone: the data folder replaces it
  await expect(page.locator("#library dt")).toHaveText(["Mods", "On disk", "Data folder"]);
  await row(page).getByRole("button", { name: "Open", exact: true }).click();
  await expect.poll(() => calls(page)).toContain("app_open_path");
});

test("a move: the confirmation, the task's progress (not dismissable), then the restart", async ({ page }) => {
  await open(page);
  await pick(page, "/mnt/storage");
  await change(page).click();
  const confirm = page.getByRole("dialog", { name: "Move the data folder?" });
  await expect(confirm).toBeVisible();
  await expect(confirm.locator("dt")).toHaveText(["To", "Moves", "Free there"]);
  await expect(confirm.locator("dd")).toHaveText(["/mnt/storage/Kiln", "3.4 GB, 12,408 files", "384 GB"]);
  await expect(confirm).toContainText("That folder has other files in it, so the data goes into a new folder inside it.");
  await expect(confirm.getByRole("button")).toHaveText(["Cancel", "Move"]);
  expect(await calls(page)).not.toContain("data_folder_move");

  await confirm.getByRole("button", { name: "Move", exact: true }).click();
  const moving = page.getByRole("dialog", { name: "Moving the data folder" });
  await expect(moving).toBeVisible();
  await expect(moving.getByRole("status", { name: "Move the data folder" })).toContainText("Copying");
  await expect(moving.getByRole("button", { name: "Cancel", exact: true })).toBeEnabled();
  await cannotDismiss(page, "Moving the data folder");
  await expect(moving.locator(".k-task-msg")).toHaveText("Checking the copies", { timeout: 6000 });
  // done: the launcher restarts (the mock records the call; a real restart ends the page)
  await expect(page.getByRole("dialog", { name: "Restarting" })).toBeVisible({ timeout: 6000 });
  await cannotDismiss(page, "Restarting");
  const c = await calls(page);
  expect(c).toContain("data_folder_restart");
  expect(c.indexOf("data_folder_move")).toBeLessThan(c.indexOf("data_folder_restart"));
  expect(c).not.toContain("task_cancel");
});

test("a move the player cancels: nothing changed, no restart", async ({ page }) => {
  await open(page);
  await pick(page, "/mnt/games");
  await change(page).click();
  const confirm = page.getByRole("dialog", { name: "Move the data folder?" });
  // an empty folder is used as it is: no line under the facts
  await expect(confirm.locator(".k-df-line")).toHaveCount(0);
  await confirm.getByRole("button", { name: "Move", exact: true }).click();
  const moving = page.getByRole("dialog", { name: "Moving the data folder" });
  await expect(moving.locator(".k-task-msg")).toHaveText("Copying runtimes and modpacks", { timeout: 4000 });
  await moving.getByRole("button", { name: "Cancel", exact: true }).click();
  const stopped = page.getByRole("dialog", { name: "Move canceled" });
  await expect(stopped).toBeVisible({ timeout: 4000 });
  await expect(stopped).toContainText(`Nothing changed. The data stays in ${DATA}`);
  await expect(stopped.getByRole("alert")).toHaveCount(0);
  expect(await calls(page)).toContain("task_cancel");
  expect(await calls(page)).not.toContain("data_folder_restart");
  await stopped.getByRole("button", { name: "Close", exact: true }).click();
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await expect(page.getByTestId("data-folder-path")).toHaveText(DATA);
});

test("a move that fails: the error, nothing changed, no restart", async ({ page }) => {
  await open(page, "/?df=fail#/settings");
  await pick(page, "/mnt/games");
  await change(page).click();
  await page.getByRole("dialog", { name: "Move the data folder?" }).getByRole("button", { name: "Move", exact: true }).click();
  const failed = page.getByRole("dialog", { name: "Move failed" });
  await expect(failed).toBeVisible({ timeout: 8000 });
  await expect(failed.getByRole("alert")).toHaveText("Could not copy library/kiln/new-items: the disk reported an input/output error.");
  await expect(failed).toContainText(`Nothing changed. The data stays in ${DATA}`);
  expect(await calls(page)).not.toContain("data_folder_restart");
});

test("every refusal shows its reason, with Choose another and Close", async ({ page }) => {
  await open(page);
  const refusals = await df(page, "refusals") as { kind: string; path: string | null; reason: string }[];
  expect(refusals.length).toBeGreaterThanOrEqual(11);
  for (const r of refusals) {
    if (r.path) await pick(page, r.path); else { await df(page, "busy", r.kind); await pick(page, "/mnt/games"); }
    await change(page).click();
    const d = page.getByRole("dialog", { name: "Kiln cannot use this folder" });
    await expect(d, r.kind).toContainText(r.reason);
    await expect(d.getByRole("button")).toHaveText(["Close", "Choose another"]);
    await d.getByRole("button", { name: "Close", exact: true }).click();
    await expect(d).toHaveCount(0);
    if (!r.path) await df(page, "busy", null);
  }
  expect(await calls(page)).not.toContain("data_folder_move");
  // Choose another opens the folder dialog again
  await pick(page, "/mnt/readonly");
  await pick(page, "/mnt/games");
  await change(page).click();
  await page.getByRole("dialog", { name: "Kiln cannot use this folder" }).getByRole("button", { name: "Choose another" }).click();
  await expect(page.getByRole("dialog", { name: "Move the data folder?" })).toContainText("/mnt/games");
});

test("a folder the player canceled in the folder dialog opens nothing", async ({ page }) => {
  await open(page);
  await pick(page, null);
  await change(page).click();
  await page.waitForTimeout(300);
  await expect(page.getByRole("dialog")).toHaveCount(0);
  expect(await calls(page)).not.toContain("data_folder_check");
});

test("a folder that already holds Kiln data: Use it, then the restart", async ({ page }) => {
  await open(page);
  await pick(page, "/mnt/old-kiln");
  await change(page).click();
  const use = page.getByRole("dialog", { name: "Use this data folder?" });
  await expect(use).toContainText("/mnt/old-kiln");
  await expect(use).toContainText("It already holds Kiln data. The current folder stays where it is.");
  await expect(use.getByRole("button")).toHaveText(["Cancel", "Use it"]);
  await use.getByRole("button", { name: "Use it" }).click();
  await expect(page.getByRole("dialog", { name: "Restarting" })).toBeVisible();
  const c = await calls(page);
  expect(c).toContain("data_folder_use");
  expect(c).toContain("data_folder_restart");
  expect(c).not.toContain("data_folder_move");
});

test.describe("a pointer to a folder that cannot be used", () => {
  async function openBroken(page: Page, url = "/?df=broken#/") {
    await page.goto(url);
    const d = page.getByRole("dialog", { name: BROKEN });
    await expect(d).toBeVisible();
    return d;
  }

  test("only its dialog: no shell, no toasts, no error page, no other command", async ({ page }) => {
    const d = await openBroken(page);
    await expect(d.getByTestId("data-folder-problem-path")).toHaveText("/mnt/usb/Kiln");
    await expect(d).toContainText("The folder is not there. If it is on a drive that is not connected, connect it and start Kiln again.");
    await expect(d.getByRole("button")).toHaveText(["Quit", "Start fresh", "Choose folder"]);
    await expect(d.getByRole("button", { name: "Choose folder" })).toBeFocused();
    await cannotDismiss(page, BROKEN);
    await page.waitForTimeout(1500);
    await expect(page.locator(".k-app, .deck")).toHaveCount(0);
    await expect(page.locator(".k-toast, .deck-toast")).toHaveCount(0);
    await expect(page.getByText("This page failed")).toHaveCount(0);
    expect(new Set(await calls(page))).toEqual(new Set(["data_folder_get"]));
    // the same in a deep link and in a Deck start
    await openBroken(page, "/?df=broken&deck=steamdeck#/settings");
    await expect(page.locator(".k-app, .deck")).toHaveCount(0);
  });

  test("Choose folder: a Kiln data folder is used and the launcher restarts; another folder says why", async ({ page }) => {
    const d = await openBroken(page);
    await pick(page, "/mnt/games");
    await d.getByRole("button", { name: "Choose folder" }).click();
    await expect(d.getByRole("alert")).toHaveText("This folder holds no Kiln data.");
    expect(await calls(page)).not.toContain("data_folder_restart");
    await pick(page, null);
    await d.getByRole("button", { name: "Choose folder" }).click();
    await expect(d.getByRole("button", { name: "Choose folder" })).toBeEnabled();
    await pick(page, "/mnt/usb/Kiln");
    await d.getByRole("button", { name: "Choose folder" }).click();
    await expect(page.getByRole("dialog", { name: "Restarting" })).toBeVisible();
    const c = await calls(page);
    expect(c.filter((x) => x === "data_folder_use")).toHaveLength(2);
    expect(c).toContain("data_folder_restart");
    expect(c).not.toContain("data_folder_reset");
  });

  test("Start fresh: the default place, then the restart", async ({ page }) => {
    const d = await openBroken(page, "/?df=unreadable#/");
    await expect(d).toContainText("The folder is there but holds no Kiln data.");
    await expect(d.getByRole("button", { name: "Start fresh" })).toHaveAttribute("title", `An empty data folder in ${DATA}`);
    await d.getByRole("button", { name: "Start fresh" }).click();
    await expect(page.getByRole("dialog", { name: "Restarting" })).toBeVisible();
    const c = await calls(page);
    expect(c).toContain("data_folder_reset");
    expect(c).toContain("data_folder_restart");
  });

  test("Quit", async ({ page }) => {
    const d = await openBroken(page);
    await d.getByRole("button", { name: "Quit" }).click();
    await expect.poll(() => calls(page)).toContain("deck_quit");
    expect(await calls(page)).not.toContain("data_folder_reset");
  });
});

test("the first start after a move: where the data is now, until OK", async ({ page }) => {
  await open(page, "/?df=moved#/");
  const n = page.getByRole("dialog", { name: "Data folder moved" });
  await expect(n).toBeVisible();
  await expect(n.locator("dt")).toHaveText(["Now in"]);
  await expect(n.locator("dd")).toHaveText(["/mnt/games/Kiln"]);
  await expect(n.getByRole("button")).toHaveText(["OK"]);
  await expect(n.getByRole("list")).toHaveCount(0);
  expect(await calls(page)).not.toContain("data_folder_notice_ack");
  await n.getByRole("button", { name: "OK" }).click();
  await expect(n).toHaveCount(0);
  await expect.poll(() => calls(page)).toContain("data_folder_notice_ack");
  // the row shows the new place
  await page.evaluate(() => { location.hash = "#/settings"; });
  await expect(page.getByTestId("data-folder-path")).toHaveText("/mnt/games/Kiln");
  await expect(page.getByRole("dialog", { name: "Data folder moved" })).toHaveCount(0);
});

test("the notice with files left in the old folder: the list and Open old folder", async ({ page }) => {
  await open(page, "/?df=moved-left#/");
  const n = page.getByRole("dialog", { name: "Data folder moved" });
  await expect(n.locator("dd")).toHaveText(["/mnt/games/Kiln", DATA]);
  await expect(n).toContainText("These stayed in the old folder:");
  await expect(n.getByRole("list", { name: "Left in the old folder" }).getByRole("listitem")).toHaveText(["my-notes.txt", "old-mods"]);
  await n.getByRole("button", { name: "Open old folder" }).click();
  await expect.poll(() => calls(page)).toContain("app_open_path");
  await expect(n).toBeVisible();
  expect(await calls(page)).not.toContain("data_folder_notice_ack");
  await n.getByRole("button", { name: "OK" }).click();
  await expect.poll(() => calls(page)).toContain("data_folder_notice_ack");
});

test("KILN_DATA_DIR set: the path and Open; Change is off with the reason", async ({ page }) => {
  await open(page, "/?df=env#/settings");
  await expect(page.getByTestId("data-folder-path")).toHaveText("/srv/kiln-data");
  await expect(change(page)).toBeDisabled();
  await expect(change(page)).toHaveAttribute("title", "Set by KILN_DATA_DIR.");
  await expect(page.getByTestId("data-folder-why")).toHaveText("Set by KILN_DATA_DIR.");
  await expect(row(page).getByRole("button", { name: "Open", exact: true })).toBeEnabled();
});

test.describe("Deck mode", () => {
  test.use({ viewport: { width: 1280, height: 800 } });

  test("the row: path and size; Change browses to a folder, Move, the progress, the restart", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    const r = page.locator('[data-deck-id="set-data"]');
    await expect(r).toContainText("Data folder");
    await expect(page.getByTestId("deck-data-folder-path")).toHaveText(DATA);
    await expect(r).toContainText("3.4 GB", { timeout: 6000 });
    await r.click();
    const browse = page.getByRole("dialog", { name: "Data folder" });
    await browse.locator('[data-deck-id="place-card-SD card"]').click();
    await expect(browse.locator(".deck-path")).toHaveText("/run/media/mmcblk0p1");
    // no game folders among the places: they are refused anyway
    await expect(browse.locator('[data-deck-id^="place-games-"]')).toHaveCount(0);
    await browse.getByRole("button", { name: "Use this folder" }).click();
    const confirm = page.getByRole("dialog", { name: "Move the data folder?" });
    await expect(confirm.locator("dd").first()).toHaveText("/run/media/mmcblk0p1/Kiln");
    await expect(confirm).toContainText("That folder has other files in it");
    await confirm.getByRole("button", { name: "Move", exact: true }).click();
    const moving = page.getByRole("dialog", { name: "Moving the data folder" });
    await expect(moving.getByTestId("deck-data-task")).toContainText("Copying");
    await expect(moving.getByRole("button", { name: "Cancel" })).toBeEnabled();
    await expect(page.getByRole("dialog", { name: "Restarting" })).toBeVisible({ timeout: 8000 });
    expect(await calls(page)).toContain("data_folder_restart");
  });

  test("a refusal in the Deck's dialog, and Choose another goes back to the folders", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    await page.evaluate(() => (window as unknown as { __kilnDataFolderMock: Mock }).__kilnDataFolderMock.busy("game"));
    await page.locator('[data-deck-id="set-data"]').click();
    await page.getByRole("dialog", { name: "Data folder" }).getByRole("button", { name: "Use this folder" }).click();
    const d = page.getByRole("dialog", { name: "Kiln cannot use this folder" });
    await expect(d).toContainText("Close Enshrouded first");
    await d.getByRole("button", { name: "Choose another" }).click();
    await expect(page.getByRole("dialog", { name: "Data folder" })).toBeVisible();
  });

  test("KILN_DATA_DIR: the row is off and says why", async ({ page }) => {
    await page.goto("/?deck=steamdeck&df=env#/deck/settings");
    const r = page.locator('[data-deck-id="set-data"]');
    await expect(r).toBeDisabled();
    await expect(r).toContainText("Set by KILN_DATA_DIR.");
  });

  test("the notice after a move, in the Deck's dialog", async ({ page }) => {
    await page.goto("/?deck=steamdeck&df=moved-left#/");
    const n = page.getByRole("dialog", { name: "Data folder moved" });
    await expect(n).toContainText("/mnt/games/Kiln");
    await expect(n).toContainText("my-notes.txt");
    await n.getByRole("button", { name: "OK" }).click();
    await expect(n).toHaveCount(0);
    await expect.poll(() => calls(page)).toContain("data_folder_notice_ack");
  });
});
