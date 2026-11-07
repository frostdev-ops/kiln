import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { KIND_WORD } from "../src/deck/status";
import type { ReplacedFile } from "../src/api/commands";

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

test("Deck has a label for every detected kind and adopted receipts are accepted", () => {
  for (const name of values("DetectKind")) expect(KIND_WORD[name as keyof typeof KIND_WORD]?.length).toBeGreaterThan(0);
  const adopted: ReplacedFile["action"] = "adopted";
  expect(adopted).toBe("adopted");
});
