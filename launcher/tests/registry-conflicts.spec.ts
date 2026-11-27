import { expect, test, type Page } from "@playwright/test";
import { emptyRegistryFixture, registryFixture } from "./fixtures/registryReport";

async function report(page: Page, value: unknown, error = false) {
  await page.route("**/src/registry/mock.ts*", route => route.fulfill({
    contentType: "application/javascript",
    body: `export async function readMockRegistryReport() { ${error ? 'throw new Error("fixture: report unreadable")' : `return ${JSON.stringify(value === null ? null : typeof value === "string" ? value : JSON.stringify(value))}`}; }
export async function mockRegistryRollback(o) { return { plan: null, previous: null, rollback: null, pending: null, target: null, changes: [], changesUnknown: null, blocked: "fixture: no plans", dryRun: o.dryRun, wrote: false, cleared: o.clear ? false : null }; }
const optIns = {};
export async function mockRegistryReplayOptIns() { return { ...optIns }; }
export async function mockRegistryReplayOpt(who, on) { optIns[who] = on; return { ...optIns }; }`,
  }));
  await page.goto("/#/conflicts");
  await expect(page.getByRole("heading", { name: "Registry conflicts", exact: true })).toBeVisible();
}

test("missing report is explicit and reachable from launcher navigation", async ({ page }) => {
  await report(page, null);
  await expect(page.getByText("No registry report yet", { exact: true })).toBeVisible();
  await page.getByRole("link", { name: "Conflicts", exact: true }).click();
  await expect(page).toHaveURL(/#\/conflicts$/);
});

test("shows base, ordered owners and operations, winner, certainty and refusal reasons", async ({ page }) => {
  await report(page, registryFixture());
  await expect(page.getByRole("heading", { name: "Refused edits" })).toBeVisible();
  // the RestartRequired row is in the refused list, and in the restart card above it too
  await expect(page.locator(".registry-line").filter({ hasText: /RestartRequired: balance.levelCap/ })).toHaveCount(2);
  await expect(page.getByText("Unproven: items.maxStack (no verified mapping for this build)", { exact: true })).toBeVisible();
  await expect(page.getByText("Best-effort drift", { exact: true }).first()).toBeVisible();
  await expect(page.getByText("Certain", { exact: true }).first()).toBeVisible();
  const field = page.locator(".registry-field").first();
  await field.locator("summary").click();
  await expect(field).toContainText("Base origin: archive");
  await expect(field).toContainText('"mul":2');
  await expect(field).toContainText("Winner: stacker");
  const owners = field.locator("ol li");
  await expect(owners.nth(0)).toContainText("earlier");
  await expect(owners.nth(1)).toContainText("stacker");
  await page.getByRole("button", { name: "Refresh report" }).click();
  await expect(page.getByRole("heading", { name: "Refused edits" })).toBeVisible();
});

test("edits that wait for a restart come first, with their count, mod and field", async ({ page }) => {
  await report(page, { ...registryFixture(), counts: { ...registryFixture().counts, restartRequired: 3 } });
  await expect(page.getByRole("heading", { name: "Waits for a restart" })).toBeVisible();
  await expect(page.getByText("3 registry edits wait for a restart", { exact: true })).toBeVisible();
  await expect(page.getByText("2 more are not shown here: state/registry.json lists every one.", { exact: true })).toBeVisible();
  const row = page.getByTestId("registry-restart-row");
  await expect(row).toHaveCount(1);
  await expect(row).toContainText("late");
  await expect(row).toContainText("balance · levelCap");
  await expect(row).toContainText("RestartRequired: balance.levelCap is read at game start");
});

test("without edits that wait for a restart there is no restart card", async ({ page }) => {
  await report(page, { ...registryFixture(), refused: [registryFixture().refused[1]], counts: { ...registryFixture().counts, refused: 1, restartRequired: 0 } });
  await expect(page.getByRole("heading", { name: "Refused edits" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Waits for a restart" })).toHaveCount(0);
});

test("empty snapshot is different from a missing report", async ({ page }) => {
  await report(page, emptyRegistryFixture());
  await expect(page.getByText("No registry edits in this snapshot", { exact: true })).toBeVisible();
  await expect(page.getByText("Base origin: live", { exact: true })).toBeVisible();
});

test("previous snapshot remains readable but is clearly stale", async ({ page }) => {
  await report(page, { ...registryFixture(), written: "2020-01-01T00:00:00Z" });
  await expect(page.getByText("Stale snapshot", { exact: true })).toBeVisible();
  await expect(page.locator(".registry-field")).toHaveCount(1);
});

test("read errors have an explicit retryable state", async ({ page }) => {
  await report(page, null, true);
  await expect(page.getByText("Registry report unavailable", { exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Refresh report" })).toBeEnabled();
});

test("malformed reports cannot crash the page", async ({ page }) => {
  await report(page, '{"format":1,"fields":[null]}');
  await expect(page.getByText("Registry report unavailable", { exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: "Play", exact: true })).toBeVisible();
});

test("hostile text is escaped and raw paths and addresses stay hidden", async ({ page }) => {
  const r = registryFixture();
  r.refused[0].reason = '<img src=x onerror="window.registryInjected=true">';
  await report(page, { ...r, fields: [{ ...r.fields[0], raw: true, field: "SecretSetup.Offset" }], regmap: { ...r.regmap, record: "0xDEADBEEF" } });
  await expect(page.getByText(r.refused[0].reason, { exact: true })).toBeVisible();
  expect(await page.evaluate(() => (window as Window & { registryInjected?: boolean }).registryInjected)).toBeUndefined();
  await expect(page.locator(".registry-conflicts img")).toHaveCount(0);
  await expect(page.locator(".registry-conflicts")).not.toContainText("SecretSetup");
  await expect(page.locator(".registry-conflicts")).not.toContainText("DEADBEEF");
});

test("huge report is rejected without hanging navigation", async ({ page }) => {
  await report(page, " ".repeat(8 * 1024 * 1024 + 1));
  await expect(page.getByText("Registry report unavailable", { exact: true })).toBeVisible();
  await expect(page.getByText(/8 MiB/)).toBeVisible();
  await page.getByRole("link", { name: "Play", exact: true }).click();
  await expect(page).toHaveURL(/#\/$/);
});

test("a list-element writer shows the element it changed", async ({ page }) => {
  const r = registryFixture();
  const row = { ...r.fields[0], registry: "drops", id: "game:fixture_drops", field: "entries",
    writers: [{ kind: "registry", mod: "loot", stage: "alter", n: 0, element: "entries[3].chance", op: { set: 0.5 }, value: 0.5, certainty: "certain" }] };
  await report(page, { ...r, fields: [row] });
  const field = page.locator(".registry-field").first();
  await field.locator("summary").click();
  await expect(field).toContainText("Element: entries[3].chance");
});

test("writer-only truncation still marks the report partial", async ({ page }) => {
  const r = registryFixture();
  await report(page, { ...r, fields: [{ ...r.fields[0], writersLeftOut: 3 }], truncated: { ...r.truncated, writers: 3 } });
  await expect(page.getByText("Partial report", { exact: true })).toBeVisible();
  await expect(page.getByText(/3 earlier writers were left out of the writer chains/)).toBeVisible();
  await expect(page.locator(".registry-field summary")).toContainText("3 earlier writers omitted");
});

test("long field lists paginate, filter and disclose native omissions", async ({ page }) => {
  const r = registryFixture();
  await report(page, { ...r, fields: Array.from({ length: 30 }, (_, i) => ({ ...r.fields[0], id: `game:fixture_${i}` })), truncated: { ...r.truncated, fields: 20 } });
  await expect(page.getByText("Partial report", { exact: true })).toBeVisible();
  await expect(page.locator(".registry-field")).toHaveCount(25);
  await page.getByRole("button", { name: "Next", exact: true }).click();
  await expect(page.locator(".registry-field")).toHaveCount(5);
  await page.getByRole("textbox", { name: "Filter fields or owners" }).fill("fixture_29");
  await expect(page.locator(".registry-field")).toHaveCount(1);
  await expect(page.locator(".registry-field")).toContainText("game:fixture_29");
  await page.getByRole("textbox", { name: "Filter fields or owners" }).fill("not-present");
  await expect(page.getByText("No displayed fields match this filter.", { exact: true })).toBeVisible();
});

test("names archive-writer packages from kiln-eml's journal and labels drift best effort", async ({ page }) => {
  const r = registryFixture();
  const row = { ...r.fields[0], writers: [
    { kind: "archiveWriter", who: "shroudforge:betterstacks", value: 10, certainty: "named" },
    { kind: "drift", who: "outside Kiln (best effort; suspects: xhl-core.dll)", op: 12, value: 12, certainty: "bestEffort" },
    { kind: "registry", mod: "stacker", stage: "alter", n: 0, op: { mul: 2 }, value: 24, certainty: "certain" },
  ], result: 24 };
  const conflicts = [{ registry: "items", id: "game:fixture_stack", field: "items[game:fixture_stack].maxStack", kind: "composedOn", winner: "stacker",
    loser: "shroudforge:betterstacks", certainty: "named",
    text: "items[game:fixture_stack].maxStack: betterstacks (ShroudForge package) wrote this entry in the game's archive before Kiln; stacker's edit wins (D1): 10 -> 20" }];
  await report(page, { ...r, base: { origin: "live", writer: "kiln-eml", journal: { state: "read", packages: 1, entries: 1, truncated: false } },
    fields: [row], conflicts, counts: { ...r.counts, conflicts: 1 } });
  await expect(page.getByText("Archive-writer packages named from kiln-eml's change journal: 1 packages, 1 entries (named on the fields they changed, on recipes and loot by their element; an older journal names ShroudForge packages per entry, on items, drops and balance only).", { exact: true })).toBeVisible();
  await expect(page.getByText(/betterstacks \(ShroudForge package\) wrote this entry in the game's archive before Kiln; stacker's edit wins \(D1\)/)).toBeVisible();
  await expect(page.getByText("Named from journal", { exact: true }).first()).toBeVisible();
  const field = page.locator(".registry-field").first();
  await field.locator("summary").click();
  const owners = field.locator("ol li");
  await expect(owners.nth(0)).toContainText("betterstacks (ShroudForge package)");
  await expect(owners.nth(0)).toContainText("Wrote this entry before Kiln (archive)");
  await expect(owners.nth(0)).toContainText("Named from journal");
  await expect(owners.nth(0)).toContainText("Operation: Not known (kiln-eml's journal names the entry this package wrote, not this field)");
  await expect(owners.nth(1)).toContainText("outside Kiln (best effort; suspects: xhl-core.dll)");
  await expect(owners.nth(1)).toContainText("Changed outside Kiln");
  await expect(owners.nth(1)).toContainText("Best-effort drift");
  await expect(owners.nth(2)).toContainText("stacker");
});

test("an EML mod named from its pass's journal on the field it wrote (0.2.0-dev.31.18)", async ({ page }) => {
  const r = registryFixture();
  const row = { ...r.fields[0], writers: [
    { kind: "archiveWriter", who: "eml:betterstacks", value: 10, certainty: "named", named: "field" },
    { kind: "registry", mod: "stacker", stage: "alter", n: 0, op: { mul: 2 }, value: 20, certainty: "certain" },
  ], result: 20 };
  const conflicts = [{ registry: "items", id: "game:fixture_stack", field: "items[game:fixture_stack].maxStack", kind: "composedOn", winner: "stacker",
    loser: "eml:betterstacks", certainty: "named",
    text: "items[game:fixture_stack].maxStack: betterstacks (EML package) wrote this field in the game's archive before Kiln; stacker's edit wins (D1): 10 -> 20" }];
  await report(page, { ...r, base: { origin: "live", writer: "none", journal: { state: "read", packages: 1, entries: 1, truncated: false } },
    fields: [row], conflicts, counts: { ...r.counts, conflicts: 1 } });
  await expect(page.getByText(/betterstacks \(EML package\) wrote this field in the game's archive before Kiln; stacker's edit wins \(D1\)/)).toBeVisible();
  const field = page.locator(".registry-field").first();
  await field.locator("summary").click();
  const owners = field.locator("ol li");
  await expect(owners.nth(0)).toContainText("betterstacks (EML package)");
  await expect(owners.nth(0)).toContainText("Wrote this field before Kiln (archive)");
  await expect(owners.nth(0)).toContainText("Operation: Not known (kiln-eml's journal names this field as written by this package, not the value)");
});

test("a start whose archive writer keeps no journal says its packages are not named", async ({ page }) => {
  await report(page, { ...registryFixture(), base: { origin: "live", writer: "shroudforge-hosted",
    journal: { state: "none", packages: 0, entries: 0, truncated: false, text: "hosted ShroudForge keeps no change journal" } } });
  await expect(page.getByText("Archive-writer packages are not named in this start: hosted ShroudForge keeps no change journal", { exact: true })).toBeVisible();
});

test("hosted ShroudForge's journal, a replayed package's stand-in writer and the effect replay lines", async ({ page }) => {
  const r = registryFixture();
  const row = { ...r.fields[0], writers: [
    { kind: "archiveWriter", who: "shroudforge:betterstacks", value: 10, certainty: "named", named: "field" },
    { kind: "registry", mod: "replay.sf.betterstacks", stage: "alter", n: 0, standIn: true, op: 33, value: 33, certainty: "certain" },
    { kind: "registry", mod: "stacker", stage: "alter", n: 0, op: { mul: 2 }, value: 66, certainty: "certain" },
  ], result: 66, winner: "stacker" };
  await report(page, { ...r, base: { origin: "live", writer: "shroudforge-hosted", journal: { state: "read", packages: 1, entries: 1, truncated: false, writer: "shroudforge" } },
    fields: [row], replay: [
      { who: "shroudforge:betterstacks", state: "replayed", text: "its Lua was not applied on this game build (boom); its effect recorded on build 0123abcd is replayed through the registry: 1 field(s)", fields: 1, recordedOn: "0123abcd" },
    ] });
  await expect(page.getByText("Archive-writer packages named from hosted ShroudForge's change journal: 1 packages, 1 entries (named on the fields they changed, on recipes and loot by their element).", { exact: true })).toBeVisible();
  await expect(page.getByTestId("registry-replay")).toHaveText("Effect replay, betterstacks (ShroudForge package): replayed. its Lua was not applied on this game build (boom); its effect recorded on build 0123abcd is replayed through the registry: 1 field(s)");
  const field = page.locator(".registry-field").first();
  await field.locator("summary").click();
  await expect(field.getByText("betterstacks (ShroudForge package, replayed from its recorded effect)", { exact: true })).toBeVisible();
});

// review-j3: the effect replay card's toggle per package writes the opt-in file (the browser mock here; the Tauri command
// is kiln_manage::registry_plan::set_replay_opt_in, the one kilnctl registry replay --opt-in|--opt-out calls)
test("the effect replay card opts a package in and out", async ({ page }) => {
  const r = registryFixture();
  await report(page, { ...r, replay: [
    { who: "shroudforge:betterstacks", state: "recorded", text: "its Lua ran this start: its effect is recorded (1 field(s)), replayable after a game update", fields: 1, optIn: false, mod: "replay.sf.betterstacks" },
  ] });
  const card = page.getByTestId("registry-replay-card");
  await expect(card.getByText("Effect replay", { exact: true })).toBeVisible();
  const toggle = card.getByRole("switch", { name: "Replay shroudforge:betterstacks after a game update" });
  await expect(toggle).not.toBeChecked();
  // the splash gone and the file's opt-ins read (the switch is enabled then), as chain-order.spec.ts waits; the switch's track
  // lies over its input, so it is clicked as community-posts.spec.ts clicks one
  await page.locator(".k-splash").waitFor({ state: "detached" });
  await expect(toggle).toBeEnabled();
  await toggle.click({ force: true });
  await expect(toggle).toBeChecked();
  await expect(card.getByTestId("registry-replay-saved")).toHaveText("Saved. The next start reads it.");
  await expect(toggle).toBeEnabled();
  await toggle.click({ force: true });
  await expect(toggle).not.toBeChecked();
});
