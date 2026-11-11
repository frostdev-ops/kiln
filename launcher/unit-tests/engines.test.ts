import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { KIND_WORD } from "../src/deck/status";
import type { EngineStatus, ReplacedFile } from "../src/api/commands";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { EngineRow } from "../src/components/EngineStatus";

const source = (path: string) => readFileSync(new URL(path, import.meta.url), "utf8");
const commands = source("../src/api/commands.ts").replace(/\/\/[^\n]*/g, "");
const values = (name: string) => [...commands.match(new RegExp(`export type ${name} =([\\s\\S]*?);`))![1].matchAll(/"([^"]+)"/g)].map((m) => m[1]);

function rustNames(path: string, name: string, kebab: boolean) {
  const body = source(path).split(`pub enum ${name} {`)[1].split("\n}")[0];
  let explicit: string | undefined;
  const names: string[] = [];
  for (const line of body.split("\n")) {
    const rename = line.match(/#\[serde\(rename = "([^"]+)"\)\]/);
    if (rename) explicit = rename[1];
    const variant = line.match(/^    (\w+),$/)?.[1];
    if (variant) {
      names.push(explicit ?? (kebab ? variant.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase() : variant.toLowerCase()));
      explicit = undefined;
    }
  }
  return names.sort();
}

test("launcher unions match every Rust serde engine and detection name", () => {
  expect(values("ProxyKind").sort()).toEqual(rustNames("../../crates/kiln-manage/src/pe.rs", "ProxyKind", false));
  expect(values("DetectKind").sort()).toEqual(rustNames("../../crates/kiln-manage/src/library.rs", "DetectKind", true));
  expect(values("EngineId").sort()).toEqual(rustNames("../../crates/kiln-manage/src/engines.rs", "Engine", true));
});

const sfRow = (): EngineStatus => ({
  kind: "shroudforge", plugin: "kiln-shroudforge", state: { mode: "hosted" }, file: "kiln/state/shroudforge.json",
  effects: { file: "shroudforge/state.json", state: "current", updatedAt: 123, reason: null,
    mods: [{ id: "flight-fixture", state: "waiting", detail: "Upward tilt requires pitch-speed input controls. Target-angle controls are not supported" }] },
});
const render = (s: EngineStatus) => renderToStaticMarkup(createElement(EngineRow, { s }));

test("SF per-mod no-target and write failures reach the engine row, not just hosted", () => {
  const s = sfRow();
  const html = render(s);
  expect(html).toContain("flight-fixture");
  expect(html).toContain("waiting");
  expect(html).toContain("Target-angle controls are not supported");
  s.effects!.mods[0] = { id: "flight-fixture", state: "no-target", detail: "No live player entity is available" };
  expect(render(s)).toContain("no-target");
  expect(render(s)).toContain("No live player entity is available");
  s.effects!.mods[0] = { id: "flight-fixture", state: "write-failed", detail: "Resource write gate unavailable" };
  expect(render(s)).toContain("Resource write gate unavailable");
  expect(render(s)).toContain("write-failed");
});

test("old, stopped, missing and invalid SF reports cannot look like current flight success", () => {
  for (const state of ["stale", "stopped", "unavailable", "invalid"] as const) {
    const s = sfRow();
    s.effects!.state = state;
    s.effects!.reason = `SF report is ${state}`;
    s.effects!.mods = [{ id: "flight-fixture", state: "write-confirmed", detail: "old success" }];
    const html = render(s);
    expect(html).toContain(`SF report is ${state}`);
    expect(html).not.toContain("old success");
    expect(html).not.toContain("write-confirmed");
  }
  const missing = sfRow();
  delete missing.effects;
  expect(render(missing)).not.toContain("flight-fixture");
  const standalone = sfRow();
  standalone.state = null;
  expect(render(standalone)).toContain("Target-angle controls are not supported");
});

test("SF memory write confirmation is not presented as gameplay verification", () => {
  const s = sfRow();
  s.effects!.mods[0] = { id: "flight-fixture", state: "write-confirmed", detail: "Typed write completed" };
  expect(render(s)).toContain("not proof of the gameplay effect");
});

// Every effect state SF writes (its runtime-mod.schema.json: report_effect's five, its native DLL states and
// restart-required), kiln-manage's "error" for SF's runtime errors, and a word a newer SF might add.
const SF_EFFECT_STATES = ["waiting", "no-target", "no-change", "write-confirmed", "write-failed", "loading", "loaded", "error", "restart-required"];

test("every current SF effect row says a mod's report is not proof of the gameplay effect", () => {
  for (const state of [...SF_EFFECT_STATES, "unsupported"]) {
    const s = sfRow();
    s.effects!.mods = [{ id: "flight-fixture", state, detail: "fixture detail" }];
    expect(`${state}: ${render(s)}`).toContain("not proof of the gameplay effect");
  }
  const none = sfRow();
  none.effects!.mods = [];
  expect(render(none)).toContain("No per-mod effects reported yet.");
});

test("an SF effect state the launcher does not know is not shown like a confirmed write", () => {
  const word = (state: string) => {
    const s = sfRow();
    s.effects!.mods = [{ id: "flight-fixture", state, detail: "fixture detail" }];
    return render(s).match(new RegExp(`<span class="([^"]*)">${state}</span>`))?.[1];
  };
  expect(word("unsupported")).toBe("k-tone-warn");
  expect(word("write-failed")).toBe("k-tone-err");
  expect(word("error")).toBe("k-tone-err");
  for (const pending of ["waiting", "no-target", "restart-required", "loading"]) expect(`${pending} ${word(pending)}`).toBe(`${pending} k-tone-warn`);
});

test("Deck has a label for every detected kind and adopted receipts are accepted", () => {
  for (const name of values("DetectKind")) expect(KIND_WORD[name as keyof typeof KIND_WORD]?.length).toBeGreaterThan(0);
  const adopted: ReplacedFile["action"] = "adopted";
  expect(adopted).toBe("adopted");
});
