// Smoke (browser mock): the data folder. Settings' "Data folder" row (the path at once, the size when measured), a move
// from its confirmation through the task's progress to the restart, Cancel, a failed move, every refusal with its
// sentence, "Use it", the dialog over the whole app for a pointer to a folder that cannot be used (Choose folder,
// Start fresh, Quit) and while another launcher moves the folder, the notice after a move (nothing left, entries left
// on purpose and kept, the removal running or unfinished, an unfinished move undone, an unclear record),
// KILN_DATA_DIR, and the Deck's row.
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
  await expect(moving.getByRole("status", { name: "Move the data folder" })).toContainText("copying");
  await expect(moving.getByRole("button", { name: "Cancel", exact: true })).toBeEnabled();
  await cannotDismiss(page, "Moving the data folder");
  await expect(moving.locator(".k-task-msg")).toHaveText(/^checking /, { timeout: 6000 });
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
  await expect(moving.locator(".k-task-msg")).toHaveText("copying runtime/kiln-runtime-0.2.0-dev.zip", { timeout: 4000 });
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
  // eleven dialogs in a row: on a busy PC more than the default minute
  test.setTimeout(150_000);
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

test("the notice with entries left in the old folder: what Kiln left on purpose and what it could not remove, Open old folder", async ({ page }) => {
  await open(page, "/?df=moved-left#/");
  const n = page.getByRole("dialog", { name: "Data folder moved" });
  await expect(n.locator("dd")).toHaveText(["/mnt/games/Kiln", DATA]);
  await expect(n.getByRole("list", { name: "Kiln did not remove these, it does not know them:" }).getByRole("listitem"))
    .toHaveText(["my-notes.txt (12 KB)", "old-mods (1.2 GB)"]);
  await expect(n.getByRole("list", { name: "Kiln could not remove this:" }).getByRole("listitem"))
    .toHaveText(["profiles/ember-run/config/kiln.json (2.0 KB): it changed after the move"]);
  await n.getByRole("button", { name: "Open old folder" }).click();
  await expect.poll(() => calls(page)).toContain("app_open_path");
  await expect(n).toBeVisible();
  expect(await calls(page)).not.toContain("data_folder_notice_ack");
  await n.getByRole("button", { name: "OK" }).click();
  await expect.poll(() => calls(page)).toContain("data_folder_notice_ack");
});

test("the notice while the old copy is removed, and when the rest goes at the next start", async ({ page }) => {
  await open(page, "/?df=moved-removing#/");
  const n = page.getByRole("dialog", { name: "Data folder moved" });
  await expect(n).toContainText("Kiln is removing the old copy.");
  await expect(n.getByRole("button", { name: "Open old folder" })).toBeVisible();
  await open(page, "/?df=moved-unfinished#/");
  await expect(page.getByRole("dialog", { name: "Data folder moved" })).toContainText("Part of the old copy is still there: Kiln removes the rest at the next start.");
});

test("an unfinished move undone at start, and a move's record that matches neither folder", async ({ page }) => {
  await open(page, "/?df=undone#/");
  const u = page.getByRole("dialog", { name: "Move not finished" });
  await expect(u).toContainText(`The last move did not finish. Kiln uses ${DATA} as before.`, { timeout: 10_000 });
  await expect(u.getByRole("button")).toHaveText(["OK"]);
  await u.getByRole("button", { name: "OK" }).click();
  await expect.poll(() => calls(page)).toContain("data_folder_notice_ack");
  await open(page, "/?df=unclear#/");
  const c = page.getByRole("dialog", { name: "Kiln did not finish a move" });
  await expect(c).toContainText("nothing is removed.");
  await expect(c.getByRole("button")).toHaveText(["Open old folder", "OK"]);
});

test("a removal refused at start says why, not \"the rest goes at the next start\"; an undone move that left files names them", async ({ page }) => {
  await open(page, "/?df=moved-refused#/");
  const n = page.getByRole("dialog", { name: "Data folder moved" });
  await expect(n).toContainText(`Enshrouded still uses ${DATA}: activate that profile again, then the old copy goes at the next start.`, { timeout: 10_000 });
  await expect(n).not.toContainText("Kiln removes the rest at the next start");
  await open(page, "/?df=undone-left#/");
  const u = page.getByRole("dialog", { name: "Move not finished" });
  await expect(u.getByRole("list", { name: "Kiln could not remove these from the unfinished copy:" }).getByRole("listitem"))
    .toHaveText(["backups (2.3 KB): it could not be removed: Permission denied (os error 13)"], { timeout: 10_000 });
  await expect(u).toContainText("the folder stays marked as an unfinished move.");
  await u.getByRole("button", { name: "OK" }).click();
  await expect.poll(() => calls(page)).toContain("data_folder_notice_ack");
});

test("another launcher is moving the data folder: only its dialog and Quit", async ({ page }) => {
  await page.goto("/?df=elsewhere#/settings");
  const d = page.getByRole("dialog", { name: "Kiln is moving its data in another window" });
  await expect(d).toBeVisible();
  await expect(d.getByRole("button")).toHaveText(["Quit"]);
  await cannotDismiss(page, "Kiln is moving its data in another window");
  await page.waitForTimeout(1000);
  await expect(page.locator(".k-app, .deck")).toHaveCount(0);
  expect(new Set(await calls(page))).toEqual(new Set(["data_folder_get"]));
  await d.getByRole("button", { name: "Quit" }).click();
  await expect.poll(() => calls(page)).toContain("deck_quit");
});

test("a move's plan names what stays in the old folder with its size, and the crate's warnings", async ({ page }) => {
  await open(page);
  await pick(page, "/mnt/stays");
  await change(page).click();
  const d = page.getByRole("dialog", { name: "Move the data folder?" });
  await expect(d).toContainText("These stay in the old folder too, Kiln copies them and does not remove them: my-notes.txt (12 KB), old-mods (1.2 GB).");
  await d.getByRole("button", { name: "Cancel" }).click();
  await pick(page, "/media/player/USB stick");
  await change(page).click();
  await expect(d).toContainText("From there, mods are copied into the game folder of Enshrouded.");
  await expect(d).toContainText("/media/player/USB stick does not keep file permissions");
});

test("the move's last phase says it removes the old copy; a removal stopped there: moved, the rest goes next start", async ({ page }) => {
  await open(page);
  await df(page, "interruptRemoval", true);
  await df(page, "stepMs", 2500);
  await pick(page, "/mnt/games");
  await change(page).click();
  await page.getByRole("dialog", { name: "Move the data folder?" }).getByRole("button", { name: "Move", exact: true }).click();
  const moving = page.getByRole("dialog", { name: "Moving the data folder" });
  await expect(moving.getByTestId("data-folder-removing")).toHaveText("Removing the old copy. Cancel leaves the rest for the next start.", { timeout: 20_000 });
  await expect(moving.getByRole("button", { name: "Cancel", exact: true })).toBeEnabled();
  const done = page.getByRole("dialog", { name: "Data folder moved" });
  await expect(done).toContainText("The data has moved. The rest of the old copy goes at the next start.", { timeout: 6000 });
  expect(await calls(page)).not.toContain("data_folder_restart");
  await done.getByRole("button", { name: "Restart" }).click();
  await expect.poll(() => calls(page)).toContain("data_folder_restart");
});

test("a game's profile that could not be written for the new place: the moved dialog says what to do, not \"next start\"", async ({ page }) => {
  await open(page);
  await df(page, "reactivationFails", "Enshrouded");
  await pick(page, "/mnt/games");
  await change(page).click();
  await page.getByRole("dialog", { name: "Move the data folder?" }).getByRole("button", { name: "Move", exact: true }).click();
  const done = page.getByRole("dialog", { name: "Data folder moved" });
  await expect(done).toContainText("The profile of Enshrouded could not be written for the new place.", { timeout: 15_000 });
  await expect(done).toContainText(`Enshrouded still uses ${DATA}: activate that profile again, then the old copy goes at the next start.`);
  await expect(done).not.toContainText("The rest of the old copy goes at the next start.");
  expect(await calls(page)).not.toContain("data_folder_restart");
  await done.getByRole("button", { name: "Restart" }).click();
  await expect.poll(() => calls(page)).toContain("data_folder_restart");
});

test("another data folder's profile in the game: said on Play and in Settings, never shown as loaded", async ({ page }) => {
  await open(page, "/#/");
  await page.evaluate(() => (window as unknown as { __kilnMock: Mock }).__kilnMock.setOtherFolderProfile({ id: "my-mods", root: "/home/player/.local/share/kiln" }));
  const line = page.locator(".k-hero").getByRole("link", { name: "Another data folder's profile is loaded in this game" });
  await expect(line).toBeVisible({ timeout: 8000 });
  await expect(line).toHaveAttribute("title", /of the data folder \/home\/player\/\.local\/share\/kiln/);
  await page.evaluate(() => { location.hash = "#/settings"; });
  await expect(page.getByTestId("other-folder-profile")).toHaveText("Another data folder's profile is loaded in this game");
  await expect(page.getByRole("region", { name: "Profiles" })).not.toContainText("is loaded: the main screen shows it");
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
    await expect(moving.getByTestId("deck-data-task")).toContainText("copying");
    await expect(moving.getByRole("button", { name: "Cancel" })).toBeEnabled();
    await expect(page.getByRole("dialog", { name: "Restarting" })).toBeVisible({ timeout: 8000 });
    expect(await calls(page)).toContain("data_folder_restart");
  });

  test("a refusal in the Deck's dialog, and Choose another goes back to the folders", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    await page.evaluate(() => (window as unknown as { __kilnDataFolderMock: Mock }).__kilnDataFolderMock.busy("game_running"));
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

  test("another data folder's profile: the Profile row says so", async ({ page }) => {
    await page.goto("/?deck=steamdeck#/deck/settings");
    await page.evaluate(() => (window as unknown as { __kilnMock: Mock }).__kilnMock.setOtherFolderProfile({ id: "my-mods" }));
    const r = page.locator('[data-deck-id="set-profile"]');
    await expect(r).toContainText("Another data folder's profile is loaded in this game", { timeout: 8000 });
    await expect(r).toContainText("Other folder");
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
