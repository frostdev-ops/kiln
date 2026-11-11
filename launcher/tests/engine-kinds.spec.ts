// Other engines' mods in the launcher (docs/kiln/engines.md): Library kinds and labels, a profile's Engines tab
// (switch, add, ShroudForge settings), the engine status on Logs, EML's "Restore vanilla game data", the Deck's Mods.
// Browser mock only (src/api/mock.ts seeds one mod per imported kind; the "Ember run" profile holds four of them).
import { expect, test, type Page } from "@playwright/test";

async function desktop(page: Page, route = "/") {
  await page.goto(`/#${route}`);
  await page.locator(".k-splash").waitFor({ state: "detached" });
}
const api = <T,>(page: Page, fn: string, ...args: unknown[]) =>
  page.evaluate(async ([f, a]) => {
    const m = (await import("/src/api/commands.ts")) as unknown as Record<string, (...x: unknown[]) => Promise<unknown>>;
    return m[f as string](...(a as unknown[]));
  }, [fn, args] as const) as Promise<T>;

test("the library lists every engine kind with its label, and filters by engine", async ({ page }) => {
  await desktop(page, "/library");
  const table = page.getByRole("table", { name: "Library" }).or(page.locator('[aria-label="Library"]')).first();
  for (const [name, badge] of [["Loot Tables X", "EML"], ["Better Chests", "ShroudForge"], ["FOV Tweak", "SET"], ["Zoom Out", "Greeneye"],
    ["Creative Build", "Flame"], ["Fly Mode", "Shroudtopia"]] as const) {
    const row = table.locator('[role="row"], tr').filter({ hasText: name });
    await expect(row).toContainText(badge);
  }
  await page.getByRole("radio", { name: "Other engines" }).or(page.getByRole("button", { name: "Other engines" })).or(page.getByRole("tab", { name: "Other engines" })).first().click();
  await expect(table).toContainText("Better Chests");
  await expect(table).not.toContainText("New Items");
  await page.getByLabel("Engine").selectOption("shroudforge");
  await expect(table).toContainText("Better Chests");
  await expect(table).not.toContainText("Fly Mode");
});

test("a profile's Engines tab switches an engine mod and adds one from the library", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/engines");
  const sf = page.locator('[data-kind="shroudforge"]');
  await expect(sf).toContainText("Better Chests");
  await sf.getByRole("button", { name: "Light Better Chests" }).click();
  await expect.poll(async () => (await api<{ mods: Record<string, { id: string; enabled: boolean }[]> }>(page, "profileGet", "ember-run"))
    .mods.shroudforge.find((m) => m.id === "better-chests")?.enabled).toBe(false);
  await expect(sf.getByRole("button", { name: "Light Better Chests" })).toHaveAttribute("aria-pressed", "false");

  // Flame: in the library, not in the profile yet
  const flame = page.locator('[data-kind="flame"]');
  await flame.getByRole("button", { name: "Add from the library" }).click();
  const dialog = page.getByRole("dialog", { name: "Add from the library" });
  await dialog.getByText("Creative Build").click();
  await dialog.getByRole("button", { name: /^Add 1/ }).click();
  await expect(flame).toContainText("Creative Build");
  await expect.poll(async () => (await api<{ mods: Record<string, { id: string }[]> }>(page, "profileGet", "ember-run")).mods.flame.map((m) => m.id)).toEqual(["creative-build"]);
  await expect(page.getByRole("link", { name: /Engines/ })).toContainText("5");
  // the picker again: the library's one Flame mod is in the profile now
  await flame.getByRole("button", { name: "Add", exact: true }).click();
  await expect(dialog.getByText("Everything is in this profile already.")).toBeVisible();
});

test("ShroudForge settings edit a value through the SchemaForm; kinds without settings say so", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/engines");
  await page.locator('[data-kind="shroudforge"]').getByRole("button", { name: "More for Better Chests" }).click();
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
  const more = page.locator('[data-kind="shroudtopia"]').getByRole("button", { name: "More for Fly Mode" });
  await more.scrollIntoViewIfNeeded();
  await more.click();
  await page.getByRole("menuitem", { name: "Settings" }).click();
  const st = page.getByRole("dialog", { name: "Fly Mode settings" });
  await expect(st).toContainText("shroudtopia.json");
  await expect(st.getByRole("switch", { name: "active" })).toBeChecked();
  await st.getByRole("button", { name: "Done" }).click();

  // EML and the others: no Settings item; the backend's note for a direct call
  await expect(st).toHaveCount(0);
  const emlMore = page.locator('[data-kind="eml"]').getByRole("button", { name: "More for Loot Tables X" });
  await emlMore.scrollIntoViewIfNeeded();
  await emlMore.click();
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

test("no removal guidance while the active profile runs an EML mod", async ({ page }) => {
  await desktop(page, "/profiles/ember-run/engines");
  const notice = page.locator(".k-banner", { hasText: "Shared archive backup differs" });
  await expect(notice).toBeVisible();
  await page.locator('[data-kind="eml"]').getByRole("button", { name: "Light Loot Tables X" }).click();
  await expect(notice).toHaveCount(0);
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
  await desktop(page, "/profiles/ember-run/engines");
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

test("the Engines page reports library failures instead of claiming the library is empty", async ({ page }) => {
  await page.addInitScript(() => {
    const error = { code: "io", message: "engine library denied" };
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { library_list: Array.from({ length: 12 }, () => ({ error })) };
  });
  await desktop(page, "/profiles/vanilla-plus/engines");
  await expect(page.getByText("The library is not readable")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByText("engine library denied")).toBeVisible();
  await expect(page.getByText("No other engines' mods.")).toHaveCount(0);
});

test("the library picker says which kind it lists when the library has none of it", async ({ page }) => {
  // a player with mods of other kinds only read "The library is empty" here and took the Library for broken
  await page.addInitScript(() => {
    (window as unknown as { __kilnMockPlans: unknown }).__kilnMockPlans = { library_list: Array.from({ length: 40 }, () => ({ result: [] })) };
  });
  await desktop(page, "/profiles/ember-run/mods");
  await page.getByRole("button", { name: "Add mod", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Add from the library" });
  await expect(dialog.getByText("No Kiln mods in the library.")).toBeVisible({ timeout: 20_000 });
  await expect(dialog.getByText("Mods of another kind are added on that kind's tab of the profile.", { exact: false })).toBeVisible();
  await expect(dialog.getByText("The library is empty")).toHaveCount(0);
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
  await desktop(page, "/profiles/ember-run/engines");
  await page.locator('[data-kind="shroudforge"]').getByRole("button", { name: "More for Better Chests" }).click();
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

test("Enshwick, shroudlink and Ember mods: library labels, Engines cards, and Ember says its mods don't run yet", async ({ page }) => {
  await desktop(page, "/library");
  const table = page.getByRole("table", { name: "Library" }).or(page.locator('[aria-label="Library"]')).first();
  for (const [name, badge] of [["Lanterns", "Enshwick"], ["chestsort.dll", "shroudlink"], ["private-chests", "Ember"]] as const) {
    // badges are styled in title case
    await expect(table.locator('[role="row"], tr').filter({ hasText: name })).toContainText(new RegExp(badge, "i"));
  }
  const emberRow = table.locator('[role="row"], tr').filter({ hasText: "private-chests" });
  await expect(emberRow.locator("[data-not-running]")).toContainText(/not yet/i);
  await expect(table.locator('[role="row"], tr').filter({ hasText: "Lanterns" }).locator("[data-not-running]")).toHaveCount(0);
  await page.getByRole("radio", { name: "Other engines" }).or(page.getByRole("button", { name: "Other engines" })).or(page.getByRole("tab", { name: "Other engines" })).first().click();
  await page.getByLabel("Engine").selectOption("enshwick");
  await expect(table).toContainText("Lanterns");
  await expect(table).not.toContainText("private-chests");

  await desktop(page, "/profiles/ember-run/engines");
  for (const kind of ["enshwick", "shroudlink", "ember"]) await expect(page.locator(`[data-kind="${kind}"]`)).toContainText(`kiln-${kind}`);
  await expect(page.locator('[data-kind="enshwick"]')).not.toContainText(/run yet/i);
  await expect(page.locator('[data-kind="enshwick"]')).toContainText("Server-only packs do not need a client copy");
  const ember = page.locator('[data-kind="ember"]');
  await expect(ember).toContainText(/doesn't run yet/i);
  await expect(ember.getByRole("status").filter({ hasText: "These mods don't run yet" })).toContainText("Ember has not published its mod ABI");
  await expect(ember).toContainText("compatible loader update");
  for (const [kind, name, id] of [["ember", "private-chests", "private-chests"], ["enshwick", "Lanterns", "you.lanterns"], ["shroudlink", "chestsort.dll", "chestsort"]] as const) {
    const card = page.locator(`[data-kind="${kind}"]`);
    await card.scrollIntoViewIfNeeded();
    await card.getByRole("button", { name: "Add from the library" }).click();
    const dialog = page.getByRole("dialog", { name: "Add from the library" });
    await dialog.getByText(name, { exact: true }).first().click();
    await dialog.getByRole("button", { name: /^Add 1/ }).click();
    await expect(card).toContainText(name);
    await expect.poll(async () => (await api<{ mods: Record<string, { id: string }[]> }>(page, "profileGet", "ember-run")).mods[kind].map((m) => m.id)).toEqual([id]);
  }
  // no Settings item for these kinds; the backend's note says why
  const more = page.locator('[data-kind="enshwick"]').getByRole("button", { name: "More for Lanterns" });
  await more.click();
  await expect(page.getByRole("menuitem", { name: "Settings" })).toHaveCount(0);
  await page.keyboard.press("Escape");
  const note = await api<{ format: string; note: string }>(page, "engineSettingsGet", "enshwick", "you.lanterns", "ember-run", null);
  expect([note.format, note.note.includes("per world")]).toEqual(["none", true]);
  await expect(page.getByRole("link", { name: /Engines/ })).toContainText("7");

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
  await desktop(page, "/library");
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

test("the empty Engines page says Ember mods are stored without running", async ({ page }) => {
  await page.goto("/?mock=empty#/profiles");
  await page.locator(".k-splash").waitFor({ state: "detached" });
  const profile = await api<{ id: string }>(page, "profileCreate", "Empty engines");
  await page.goto(`/?mock=empty#/profiles/${profile.id}/engines`);
  await expect(page.getByText("No other engines' mods.", { exact: true })).toBeVisible();
  await expect(page.getByText(/Ember mods are stored but don't run yet/)).toBeVisible();
});
