// Other engines' mods in the launcher (docs/kiln/engines.md): kinds and labels in Play's list of every mod (switch, add,
// ShroudForge settings from a row's menu), the engine status on Logs and on the mods' Engines view, EML's archive
// guidance, the Deck's Mods.
// Browser mock only (src/api/mock.ts seeds one mod per imported kind; the "Ember run" profile holds four of them).
import { expect, test, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import type { EngineStatus } from "../src/api/commands";
import { MOD_KINDS } from "../src/api/kinds";

async function desktop(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
/** A row of Play's list of every mod (pages/mods/MainList.tsx), by its name and, where two share one, its kind. */
const modRow = (page: Page, name: string, kind?: string) =>
  page.locator(".k-hero").getByRole("list", { name: "Mods" }).locator(kind ? `li[data-kind="${kind}"]` : "li").filter({ has: page.getByText(name, { exact: true }) });
/** Opens a row's menu. The row is brought into view first and the list's scroll event let through: a menu closes when
 *  its own list scrolls (ui/Menu.tsx), and a scroll followed at once by the click can deliver that event after it opened. */
async function openRowMenu(page: Page, name: string, kind: string) {
  const more = modRow(page, name, kind).getByRole("button", { name: `More for ${name}` });
  await more.scrollIntoViewIfNeeded();
  await page.evaluate(() => new Promise((done) => requestAnimationFrame(() => requestAnimationFrame(() => done(null)))));
  await more.click();
}
const api = <T,>(page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(async ([f, a]) => {
    const m = (await import("/src/api/commands.ts")) as unknown as Record<string, (...x: unknown[]) => Promise<unknown>>;
    return m[f as string](...(a as unknown[]));
  }, [fn, args] as const) as Promise<T>;

test("Play's list shows every engine kind with its label, and filters by kind", async ({ page }) => {
  await desktop(page, "/");
  for (const [name, badge] of [["Loot Tables X", "EML"], ["Better Chests", "ShroudForge"], ["FOV Tweak", "SET"], ["Zoom Out", "Greeneye"],
    ["Creative Build", "Flame"], ["Fly Mode", "Shroudtopia"]] as const) {
    // .first(): the game folder holds a Greeneye "Zoom Out" too (its own row)
    await expect(modRow(page, name, badge === "SET" ? "set" : badge.toLowerCase()).first(), name).toContainText(new RegExp(badge, "i"));
  }
  const list = page.locator(".k-hero").getByRole("list", { name: "Mods" });
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "ShroudForge" });
  await expect(list).toContainText("Better Chests");
  await expect(list).not.toContainText("New Items");
  await expect(list).not.toContainText("Fly Mode");
});

test("Play's list switches an engine mod off, and one the profile lacks on", async ({ page }) => {
  await desktop(page, "/");
  const sf = modRow(page, "Better Chests", "shroudforge");
  await sf.getByRole("switch", { name: "Better Chests" }).click();
  await expect.poll(async () => (await api<{ mods: Record<string, { id: string; enabled: boolean }[]> }>(page, "profileGet", "ember-run"))
    .mods.shroudforge.find((m) => m.id === "better-chests")?.enabled).toBe(false);
  await expect(sf.getByRole("switch", { name: "Better Chests" })).toHaveAttribute("aria-checked", "false");

  // Flame: in the library, not in the profile yet
  const flame = modRow(page, "Creative Build", "flame");
  await flame.getByRole("switch", { name: "Creative Build" }).click();
  await expect(flame.getByRole("switch", { name: "Creative Build" })).toHaveAttribute("aria-checked", "true");
  await expect.poll(async () => (await api<{ mods: Record<string, { id: string }[]> }>(page, "profileGet", "ember-run")).mods.flame.map((m) => m.id)).toEqual(["creative-build"]);
  // the profile's engine mods: ShroudForge, EML, Greeneye, Shroudtopia and now Flame
  const p = await api<{ mods: Record<string, unknown[]> }>(page, "profileGet", "ember-run");
  expect(["eml", "xhl", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"].reduce((n, k) => n + (p.mods[k]?.length ?? 0), 0)).toBe(5);
});

test("ShroudForge settings edit a value through the SchemaForm; kinds without settings say so", async ({ page }) => {
  await desktop(page, "/");
  await openRowMenu(page, "Better Chests", "shroudforge");
  await page.getByRole("menuitem", { name: "Settings" }).click();
  const dialog = page.getByRole("dialog", { name: "Better Chests settings" });
  await expect(dialog).toContainText("extended.mod.json");
  await expect(dialog).toContainText("Storage");
  const sort = dialog.getByRole("switch", { name: "autoSort" });
  await expect(sort).not.toBeChecked();
  await dialog.locator('label.k-toggle[for="sf-autoSort"]').click();
  await expect(sort).toBeChecked();
  await expect.poll(async () => (await api<{ values: Record<string, unknown> }>(page, "engineSettingsGet", "shroudforge", "better-chests", "ember-run", "client-steam")).values.autoSort).toBe(true);
  await dialog.getByRole("button", { name: "Done" }).click();
  await expect(dialog).toHaveCount(0);

  // Shroudtopia: values from shroudtopia.json
  await openRowMenu(page, "Fly Mode", "shroudtopia");
  await page.getByRole("menuitem", { name: "Settings" }).click();
  const st = page.getByRole("dialog", { name: "Fly Mode settings" });
  await expect(st).toContainText("shroudtopia.json");
  await expect(st.getByRole("switch", { name: "active" })).toBeChecked();
  await st.getByRole("button", { name: "Done" }).click();

  // EML and the others: no Settings item; the backend's note for a direct call
  await expect(st).toHaveCount(0);
  await openRowMenu(page, "Loot Tables X", "eml");
  await expect(page.getByRole("menuitem", { name: "Settings" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  const eml = await api<{ format: string; note: string }>(page, "engineSettingsGet", "eml", "loot-tables-x", "ember-run", null);
  expect(eml.format).toBe("none");
  expect(eml.note).toContain("no settings");
});

test("Logs > Engines shows state without overwriting a shared archive backup", async ({ page }) => {
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const list = page.getByLabel("Engine plugins");
  await expect(list.locator('[data-engine="eml"]')).toContainText("done");
  await expect(list.locator('[data-engine="eml"]')).toContainText("loot-tables-x disabled");
  await expect(list.locator('[data-engine="shroudforge"]')).toContainText("hosted");
  await expect(list.locator('[data-engine="greeneye"]')).toContainText("zoom-out started");
  await expect(list.locator('[data-engine="shroudtopia"]')).toContainText("Fly Mode active");
  // an engine's own loader in the game: the row says what it means for the profile's mods
  await expect(list.locator('[data-engine="flame"]')).toContainText("Flame's own loader (kiln/chain/flame-dinput8.dll) is installed");

  // A difference from the shared .bak does not prove EML owns this output.
  const notice = page.locator(".k-banner", { hasText: "Shared archive backup differs" });
  await expect(notice).toContainText("enshrouded.kfc");
  await expect(notice).toContainText("checks ownership");
  await expect(notice).toContainText("Other loaders' data is kept");
  await expect(page.getByRole("button", { name: "Restore vanilla game data" })).toHaveCount(0);
  const result = await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    try { await api.emlRestore("client-steam"); return "unexpected success"; }
    catch (error) { return JSON.stringify(error); }
  });
  expect(result).toContain("ownership");
  const backups = await api<{ patched: boolean }[]>(page, "emlBackup", "client-steam");
  expect(backups.some((b) => b.patched)).toBe(true);
});

test("archive guidance gives the way back that works, and the writer's reason when it could not prove ownership", async ({ page }) => {
  // without a reason: the generic guidance names the fallbacks too
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const notice = page.locator(".k-banner", { hasText: "Shared archive backup differs" });
  await expect(notice).toContainText("enable any EML mod for one start and disable it again");
  await expect(notice).toContainText("Verify integrity of game files");
  await expect(notice).not.toContainText("On the last start without EML mods");
  // kiln-eml left differing data alone (kiln/state/eml.json `reason`): the banner says what it said, and why
  const reason = "The game data differs from its vanilla backup, and Kiln cannot prove it is its own EML output (it has no " +
    "ownership receipt, and it is not the output the earlier change cache recorded), so it was left as it is";
  await page.addInitScript((why) => {
    const eml = { kind: "eml", plugin: "kiln-eml", file: "eml.json", state: { phase: "idle", reason: why, mods: [] } };
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { engine_status: Array.from({ length: 20 }, () => ({ result: [eml] })) };
  }, reason);
  await page.reload(); // the init script runs on a load, not on a hash change
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await page.getByRole("tab", { name: "Engines" }).click();
  await expect(notice).toContainText(`On the last start without EML mods: ${reason}`);
  await expect(notice).toContainText("Other loaders' data is kept");
  await expect(notice).not.toContainText("checks ownership and");
  await expect(page.locator('[data-engine="eml"]')).toContainText("cannot prove it is its own EML output");
});

test("archive guidance names a backup of an earlier game build instead of unproven data", async ({ page }) => {
  await page.addInitScript(() => {
    const eml = { kind: "eml", plugin: "kiln-eml", file: "eml.json", state: { phase: "idle", staleBackup: true, mods: [] } };
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { engine_status: Array.from({ length: 20 }, () => ({ result: [eml] })) };
  });
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const notice = page.locator(".k-banner", { hasText: "Shared archive backup differs" });
  await expect(notice).toContainText("The backup is of an earlier game build");
  await expect(notice).toContainText("Nothing needs doing");
  await expect(notice).not.toContainText("Verify integrity");
  await expect(notice).not.toContainText("On the last start without EML mods");
});

// kiln/state/eml.json `reason` of the last start (idle, done or failed) names what Kiln kept or moved aside in the game
// folder. It is shown whatever the backup comparison says and whether or not the profile runs an EML mod.
const planEml = (plan: { state: Record<string, unknown>; backups?: unknown[] }) => {
  const eml = { kind: "eml", plugin: "kiln-eml", file: "eml.json", state: plan.state };
  const plans: Record<string, unknown> = { engine_status: Array.from({ length: 20 }, () => ({ result: [eml] })) };
  if (plan.backups) plans.eml_backup = Array.from({ length: 20 }, () => ({ result: plan.backups }));
  (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = plans;
};

test("containers moved aside are named with where they are, also with no backup difference", async ({ page }) => {
  const reason = "Archive containers moved aside: enshrouded_055.dat (4096 bytes) -> kiln/eml/displaced/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0/enshrouded_055.dat. " +
    "The originals are preserved; Kiln never deletes this recovery folder";
  const plan = { state: { phase: "done", patched: 1, reason, mods: [] }, backups: [] };
  await page.addInitScript(planEml, plan);
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const note = page.locator(".k-banner", { hasText: "EML archive note" });
  await expect(note).toContainText(`On the last start: ${reason}.`);
  await expect(note).not.toContainText("without EML mods");
  await expect(page.locator(".k-banner", { hasText: "Shared archive backup differs" })).toHaveCount(0);
});

test("files kept beside vanilla archives are named when nothing differs from the backup", async ({ page }) => {
  const reason = "Extra archive containers retained: enshrouded_055.dat. They may belong to another loader or an earlier EML run; inspect them before manual cleanup. " +
    "Steam verification may leave extra files. Recovery files retained without cleanup proof: enshrouded.kfc_resources.kiln-0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0 (225443840 bytes). " +
    "Inspect them before manual cleanup; Kiln will not delete these files";
  const plan = { state: { phase: "idle", reason, mods: [] }, backups: [] };
  await page.addInitScript(planEml, plan);
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const note = page.locator(".k-banner", { hasText: "EML archive note" });
  await expect(note).toContainText(`On the last start without EML mods: ${reason}.`);
  await expect(note).toContainText("225443840 bytes");
});

test("a backup of an earlier game build together with kept files shows both", async ({ page }) => {
  const reason = "Extra archive containers retained: enshrouded_055.dat. They may belong to another loader or an earlier EML run; inspect them before manual cleanup. " +
    "Steam verification may leave extra files";
  const plan = { state: { phase: "idle", staleBackup: true, reason, mods: [] } };
  await page.addInitScript(planEml, plan);
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const notice = page.locator(".k-banner", { hasText: "Shared archive backup differs" });
  await expect(notice).toContainText("The backup is of an earlier game build");
  await expect(notice).toContainText(`On the last start without EML mods: ${reason}.`);
  await expect(notice).not.toContainText("Nothing needs doing");
  await expect(page.locator(".k-banner", { hasText: "EML archive note" })).toHaveCount(0); // said once, in the one banner
});

test("the last start's note stays while the active profile runs an EML mod", async ({ page }) => {
  const reason = "Archive recovery folder retained: kiln/eml/displaced/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0/. Inspect its move journal and any saved containers; Kiln never deletes this folder";
  const plan = { state: { phase: "done", patched: 1, reason, mods: [] } };
  await page.addInitScript(planEml, plan);
  await desktop(page, "/");
  await modRow(page, "Loot Tables X", "eml").getByRole("switch", { name: "Loot Tables X" }).click();
  await expect.poll(async () => (await api<{ mods: Record<string, { enabled: boolean }[]> }>(page, "profileGet", "ember-run")).mods.eml[0].enabled).toBe(true);
  await page.locator(".k-hero").getByRole("link", { name: "Engines" }).click();
  await expect(page).toHaveURL(/#\/mods\/engines$/);
  await expect(page.locator(".k-banner", { hasText: "Shared archive backup differs" })).toHaveCount(0);
  await expect(page.locator(".k-banner", { hasText: "EML archive note" })).toContainText("kiln/eml/displaced/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0/");
});

test("no removal guidance while the active profile runs an EML mod", async ({ page }) => {
  await desktop(page, "/mods/engines");
  const notice = page.locator(".k-banner", { hasText: "Shared archive backup differs" });
  await expect(notice).toBeVisible();
  // switched on in the Load order view of every kind: the guidance goes
  await page.getByRole("link", { name: "Load order", exact: true }).click();
  await page.getByRole("region", { name: "Off" }).getByRole("switch", { name: "Loot Tables X" }).click();
  await expect.poll(async () => (await api<{ mods: Record<string, { enabled: boolean }[]> }>(page, "profileGet", "ember-run")).mods.eml[0].enabled).toBe(true);
  await page.getByRole("link", { name: "Engines", exact: true }).click();
  await expect(page.locator(".k-banner", { hasText: "Shared archive backup differs" })).toHaveCount(0);
});

test("the Deck's Mods screen lists engine mods with their engine", async ({ page }) => {
  await page.goto("/?deck=steamdeck#/deck/mods");
  await expect(page.locator('[data-deck-screen="mods"]')).toBeVisible();
  await expect(page.locator('[data-deck-id="mod-shroudforge-better-chests"]')).toContainText("ShroudForge mod");
  await expect(page.locator('[data-deck-id="mod-shroudtopia-fly-mode"]')).toContainText("Shroudtopia mod");
  await expect(page.locator('[data-deck-id="mod-greeneye-zoom-out"]')).toContainText("Greeneye mod");
  await expect(page.locator('[data-deck-id="mod-eml-loot-tables-x"]')).toContainText("Off");
});

test("engine settings preserve numeric options and cannot mutate object prototypes", async ({ page }) => {
  await desktop(page, "/mods/engines");
  const result = await page.evaluate(async () => {
    const { settingsGroups, topLevelChange } = await import("/src/components/EngineSettings.tsx");
    const groups = settingsGroups({ kind: "shroudforge", id: "test", format: "extended.mod.json", file: null,
      schema: { type: "object", properties: { choice: { type: "integer", enum: ["1", "2"] } } }, values: { choice: 1 } });
    const nested = topLevelChange({ group: { keep: 7 } }, "group.__proto__.engineReviewPolluted", true);
    const pollution = (Object.prototype as Record<string, unknown>).engineReviewPolluted;
    // JSON text, not the object: Playwright rebuilds returned objects by assignment, which turns an own "__proto__"
    // key back into a prototype change on the Node side.
    const stored = JSON.stringify(nested.value);
    return { type: groups[0].schema.entries[0].type, default: groups[0].schema.entries[0].default,
      pollution: pollution ?? null, stored, numeric: topLevelChange({ choice: 1 }, "choice", 2) };
  });
  expect(result.type).toBe("int");
  expect(result.default).toBe(1);
  expect(result.numeric).toEqual({ key: "choice", value: 2 });
  expect(result.pollution).toBeNull();
  const stored = JSON.parse(result.stored) as Record<string, unknown>;
  expect(stored.keep).toBe(7);
  expect(Object.hasOwn(stored, "__proto__")).toBe(true);
  expect((Object.getOwnPropertyDescriptor(stored, "__proto__")?.value as Record<string, unknown>).engineReviewPolluted).toBe(true);
});

test("Play reports library failures instead of claiming the mods are gone or there are none", async ({ page }) => {
  await page.addInitScript(() => {
    const error = { code: "io", message: "engine library denied" };
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { library_list: Array.from({ length: 12 }, () => ({ error })) };
  });
  await desktop(page, "/");
  await expect(page.locator(".k-hero .k-hero-say")).toContainText("engine library denied", { timeout: 20_000 });
  await expect(page.locator(".k-hero").getByText("No mods yet.")).toHaveCount(0);
  await expect(page.locator(".k-hero").getByText("Not in the library")).toHaveCount(0);
});

test("with an empty library, Play still lists the profile's mods of every kind and never says the library is empty", async ({ page }) => {
  // a player with mods of other kinds only read "The library is empty" in a one-kind picker and took the Library for broken
  await page.addInitScript(() => {
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { library_list: Array.from({ length: 40 }, () => ({ result: [] })) };
  });
  await desktop(page, "/");
  for (const [name, kind] of [["better-chests", "shroudforge"], ["loot-tables-x", "eml"], ["new-items", "kiln"]] as const)
    await expect(modRow(page, name, kind), name).toBeVisible({ timeout: 20_000 });
  await expect(page.locator(".k-hero").getByText(/library is empty/i)).toHaveCount(0);
});

test("external host recognition is not a runtime claim and preserves ShroudForge effect reporting", async ({ page }) => {
  await page.addInitScript(() => {
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = {
      engine_status: [{ result: [
        { kind: "xhl", plugin: "", state: null, file: "", prerequisite: { available: true, text: "Only XHL-Core loads these companions; gameplay is not verified" } },
        { kind: "shroudforge", plugin: "kiln-shroudforge", state: null, file: "shroudforge.json", effects: {
          state: "current", file: "effects.json", updatedAt: 1, reason: null,
          mods: [{ id: "flight-fixture", state: "write-confirmed", detail: "fixture memory write" }],
        } },
      ] }],
    };
  });
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const xhl = page.locator('[data-engine="xhl"]');
  await expect(xhl).toContainText("Host recognized (not verified)");
  await expect(xhl).toContainText("Only XHL-Core loads these companions");
  await expect(xhl).not.toContainText("Not run yet");
  const sf = page.locator('[data-engine="shroudforge"]');
  await expect(sf).toContainText("Runtime report available");
  await expect(sf).toContainText("flight-fixture");
  await expect(sf).toContainText("Memory write confirmed; not proof of the gameplay effect.");
});

test("engine state parse errors and backup failures remain visible", async ({ page }) => {
  await page.addInitScript(() => {
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = {
      engine_status: [{ result: [{ kind: "set", plugin: "kiln-set", state: null, file: "set.json", error: "invalid state JSON" }] }],
      eml_backup: Array.from({ length: 4 }, () => ({ error: { code: "io", message: "backup denied" } })),
    };
  });
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  await expect(page.locator('[data-engine="set"]')).toContainText("Unreadable");
  await expect(page.locator('[data-engine="set"]')).toContainText("invalid state JSON");
  await expect(page.getByText("The game data backup is not readable")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByRole("button", { name: "Restore vanilla game data" })).toHaveCount(0);
});

test("settings stop while the game runs and cannot edit another profile's ShroudForge copy", async ({ page }) => {
  await desktop(page, "/");
  await openRowMenu(page, "Better Chests", "shroudforge");
  await page.getByRole("menuitem", { name: "Settings" }).click();
  const dialog = page.getByRole("dialog", { name: "Better Chests settings" });
  await expect(dialog.getByRole("switch", { name: "autoSort" })).toBeEnabled();
  await page.evaluate(() => (window as unknown as { __kilnMock: { startOutside: (ago: number) => void } }).__kilnMock.startOutside(5));
  await expect(dialog.getByRole("switch", { name: "autoSort" })).toBeDisabled();
  const code = await page.evaluate(async () => {
    try { await (await import("/src/api/commands.ts")).engineSettingsSet("shroudforge", "better-chests", "ember-run", "client-steam", "autoSort", true); }
    catch (e) { return (e as { code: string }).code; }
    return null;
  });
  expect(code).toBe("game_running");
  await page.evaluate(() => (window as unknown as { __kilnMock: { stopGame: () => void } }).__kilnMock.stopGame());
  const inactive = await page.evaluate(async () => {
    const api = await import("/src/api/commands.ts");
    const p = await api.profileCreate("Other SF", "ember-run");
    try { await api.engineSettingsSet("shroudforge", "better-chests", p.id, "client-steam", "autoSort", true); }
    catch (e) { return (e as { code: string }).code; }
    return null;
  });
  expect(inactive).toBe("conflict");
});

test("Enshwick, shroudlink and Ember mods: labels in the list, their plugins in Load order, and Ember says its mods don't run yet", async ({ page }) => {
  await desktop(page, "/");
  for (const [name, badge] of [["Lanterns", "enshwick"], ["chestsort.dll", "shroudlink"], ["private-chests", "ember"]] as const) {
    // badges are styled in title case
    await expect(modRow(page, name, badge)).toContainText(new RegExp(badge, "i"));
  }
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Enshwick" });
  const list = page.locator(".k-hero").getByRole("list", { name: "Mods" });
  await expect(list).toContainText("Lanterns");
  await expect(list).not.toContainText("private-chests");
  await page.getByRole("combobox", { name: "Kind" }).selectOption({ label: "Every kind" });
  for (const [kind, name, id] of [["ember", "private-chests", "private-chests"], ["enshwick", "Lanterns", "you.lanterns"], ["shroudlink", "chestsort.dll", "chestsort"]] as const) {
    const r = modRow(page, name, kind);
    await expect(r).not.toContainText(/run yet/i);
    await r.getByRole("switch", { name }).click();
    await expect.poll(async () => (await api<{ mods: Record<string, { id: string }[]> }>(page, "profileGet", "ember-run")).mods[kind].map((m) => m.id)).toEqual([id]);
  }
  // Ember's is on but does not run: its row and the count say so, with the reason; Enshwick's runs
  const ember = modRow(page, "private-chests", "ember");
  await expect(ember).toContainText("Does not run yet");
  await expect(ember.locator(".k-mod-note span[title]")).toHaveAttribute("title", /Ember has published no mod ABI/);
  await expect(page.locator(".k-hero .k-hero-say")).toContainText("private-chests does not run yet: Ember has published no mod ABI yet.");
  await expect(modRow(page, "Lanterns", "enshwick")).not.toContainText(/run yet/i);
  // no Settings item for these kinds; the backend's note says why
  await openRowMenu(page, "Lanterns", "enshwick");
  await expect(page.getByRole("menuitem", { name: "Settings" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  const note = await api<{ format: string; note: string }>(page, "engineSettingsGet", "enshwick", "you.lanterns", "ember-run", null);
  expect([note.format, note.note.includes("per world")]).toEqual(["none", true]);
  // Load order names the plugin that starts them; Ember's waits under "On, but not loading"
  await desktop(page, "/mods/order");
  const order = page.getByRole("list", { name: "Load order on a client" });
  await expect(order).toContainText("kiln-enshwick");
  await expect(order).toContainText("kiln-shroudlink");
  await expect(page.getByRole("region", { name: "On, but not loading" })).toContainText("private-chests");

  // the Deck's Mods and Add mods screens name the kind with the same note
  const words = await page.evaluate(async () => {
    const { KIND_WORD } = await import("/src/deck/status.ts");
    return [KIND_WORD.ember, KIND_WORD.enshwick, KIND_WORD.shroudlink];
  });
  expect(words).toEqual(["Ember mod (doesn't run yet)", "Enshwick mod", "shroudlink mod"]);
});

test("Logs > Engines reads Enshwick's and Ember's state files", async ({ page }) => {
  await desktop(page, "/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const list = page.getByLabel("Engine plugins");
  await expect(list.locator('[data-engine="enshwick"]')).toContainText("started");
  await expect(list.locator('[data-engine="enshwick"]')).toContainText("you.lanterns copy");
  await expect(list.locator('[data-engine="ember"]')).toContainText("waiting for Ember's ABI");
  await expect(list.locator('[data-engine="ember"]')).toContainText("private-chests waiting-for-abi");
});

test("engine headlines show Enshwick's result rather than its start phase", async ({ page }) => {
  await desktop(page, "/logs");
  const words = await page.evaluate(async () => {
    const { headline } = await import("/src/components/EngineStatus.tsx");
    return [
      headline({ status: "failed", phase: "early", reason: "loader faulted" }),
      headline({ status: "started", phase: "world" }),
      headline({ phase: "done" }),
      headline({ mode: "hosted" }),
      headline({ abi: null, steppedAside: true }),
      headline({ abi: null, steppedAside: false }),
    ];
  });
  expect(words).toEqual(["failed", "started", "done", "hosted", "stepped aside", "waiting for Ember's ABI"]);
});

test("the mock library matches Rust's kind and id ordering before and after import", async ({ page }) => {
  await desktop(page, "/");
  const sorted = async () => page.evaluate(async () => {
    const { libraryList } = await import("/src/api/commands.ts");
    const { MOD_KINDS } = await import("/src/api/kinds.ts");
    const entries = (await libraryList()).map(({ kind, id }) => ({ kind, id }));
    const expected = [...entries].sort((a, b) => MOD_KINDS.indexOf(a.kind) - MOD_KINDS.indexOf(b.kind) || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
    return { entries, expected };
  });
  const before = await sorted();
  expect(before.entries).toEqual(before.expected);
  const task = await api<string>(page, "libraryImport", ["middle-example-1.0.0.zip", "aaa-native-1.0.0.zip"]);
  await expect.poll(async () => (await api<{ state: string }>(page, "taskGet", task)).state).toBe("done");
  const after = await sorted();
  expect(after.entries.length).toBe(before.entries.length + 2);
  expect(after.entries).toEqual(after.expected);
});

test("the Engines view says Ember mods are kept without running and XHL companions need XHL-Core", async ({ page }) => {
  await desktop(page, "/mods/engines");
  await expect(page.getByText(/Ember mods are kept but do not run yet/)).toBeVisible();
  await expect(page.getByText(/XHL companions run only through XHL-Core, installed separately/)).toBeVisible();
});

test("the Library's XHL badge says XHL-Core loads the companion, not Kiln", async ({ page }) => {
  // an imported companion as kiln-manage lists it: Library id from the folder name, the folder name as its own id
  await page.addInitScript(() => {
    const entry = { kind: "xhl", id: "xhl-vein-mining", name: "XHL-Vein-Mining", versions: [{ version: "0.0.0", hash: `kdh1:${"b".repeat(64)}`,
      folder: "library/xhl/xhl-vein-mining/0.0.0+bbbbbbbb", imported: "2026-11-05T09:00:00Z", side: "both", clients: "mod", size: 52_000,
      source: { type: "folder", from: "XHL-Vein-Mining.zip" }, manifestId: "XHL-Vein-Mining", manifest: {} }] };
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { library_list: Array.from({ length: 40 }, () => ({ result: [entry] })) };
  });
  await desktop(page, "/");
  const row = modRow(page, "XHL-Vein-Mining", "xhl");
  const badge = row.locator(".k-badge", { hasText: /^XHL$/ });
  await expect(badge).toHaveAttribute("title", /XHL-Core/);
  await expect(badge).not.toHaveAttribute("title", /run by Kiln/);
});

test("an XHL companion switched on says it needs XHL-Core, with kiln-manage's host check on Engines", async ({ page }) => {
  await desktop(page, "/");
  const r = modRow(page, "XHL-Vein-Mining", "xhl");
  await expect(r.locator(".k-badge")).toHaveAttribute("title", "XHL (requires XHL-Core)");
  await r.getByRole("switch", { name: "XHL-Vein-Mining" }).click();
  await expect.poll(async () => (await api<{ mods: Record<string, { id: string }[]>; dropins: string[] }>(page, "profileGet", "ember-run")).mods.xhl.map((m) => m.id))
    .toEqual(["xhl-vein-mining"]);
  // no host on the game's proxy/chain paths (engine_status's xhl row): the row says so and Kiln never loads its DLLs
  await expect(r).toContainText("Needs XHL-Core");
  await expect(r.locator(".k-mod-note span[title]")).toHaveAttribute("title", /Kiln never loads their DLLs/);
  // the count's exceptions name it with the two Kiln mods that will not load either (three: "and 1 more")
  await expect(page.locator(".k-hero .k-hero-say")).toHaveText("13 mods on, 14 off. More Hotbars, Night Sky and 1 more will not run in this game.");
  // engine_status's xhl row: kiln-manage's own sentence when no host is on the game's proxy/chain paths
  await desktop(page, "/mods/engines");
  await expect(page.locator('[data-engine="xhl"]')).toContainText("no recognized host was found on this game's active proxy/chain paths");
  // with a companion in the active profile, Logs > Engines shows the host check, never a run state
  await page.goto("/#/logs");
  await page.getByRole("tab", { name: "Engines" }).click();
  const row = page.getByLabel("Engine plugins").locator('[data-engine="xhl"]');
  await expect(row).toContainText("Requires XHL-Core");
  await expect(row).toContainText("Kiln never loads companion plugin DLLs itself");
  await expect(row).not.toContainText("Not run yet");
});

test("the mock's engine_status rows have the shape kiln-manage sends: the XHL host row and ShroudForge effects", async ({ page }) => {
  await desktop(page, "/logs");
  const rows = await api<EngineStatus[]>(page, "engineStatus", "client-steam");
  // engine_mods::status: ModKind::ALL order, every kind with a state file, and XHL's host row
  expect(rows.map((r) => r.kind)).toEqual(MOD_KINDS.filter((k) => !["kiln", "native", "plugin"].includes(k)));
  const rust = readFileSync(new URL("../../crates/kiln-manage/src/engine_mods.rs", import.meta.url), "utf8");
  const xhl = rows.find((r) => r.kind === "xhl")!;
  expect(Object.keys(xhl).sort()).toEqual(["file", "kind", "plugin", "prerequisite", "state"]);
  expect(xhl).toMatchObject({ plugin: "", state: null, file: "", prerequisite: { available: false } });
  expect(rust).toContain(`"${xhl.prerequisite!.text}"`);
  const sf = rows.find((r) => r.kind === "shroudforge")!;
  expect(Object.keys(sf.effects ?? {}).sort()).toEqual(["file", "mods", "reason", "state", "updatedAt"]);
  expect(["current", "stale", "stopped", "unavailable", "invalid"]).toContain(sf.effects!.state);
  if (sf.effects!.reason) expect(rust.includes(`"${sf.effects!.reason}"`) || readFileSync(new URL("../../crates/kiln-manage/src/shroudforge.rs", import.meta.url), "utf8").includes(`"${sf.effects!.reason}"`)).toBe(true);
  for (const r of rows) {
    expect(`${r.kind} effects ${"effects" in r}`).toBe(`${r.kind} effects ${r.kind === "shroudforge"}`);
    expect(`${r.kind} prerequisite ${"prerequisite" in r}`).toBe(`${r.kind} prerequisite ${r.kind === "xhl"}`);
  }
});
