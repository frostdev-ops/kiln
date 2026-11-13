// The save editor's amounts: a stacking item takes any amount the save holds, because a mod may have raised the game's
// stack sizes (GitHub issue 23: a x10 stack mod, and the editor stopped at 20 potions).
import { expect, test } from "bun:test";
// @ts-ignore: the editor is plain JavaScript
import { MAX_AMOUNT, aboveStack, amountLimit } from "../src/editor/kiln/amounts.js";
// @ts-ignore
import { items, newEntry } from "../src/editor/src/gamedata.js";
// @ts-ignore
import { definirIdioma, t } from "../src/editor/src/i18n.js";

const all = Object.entries(items as Record<string, { maxStack: number; debug: string }>);
const stacking = all.find(([, it]) => it.maxStack === 20) ?? all.find(([, it]) => it.maxStack > 1)!;
const single = all.find(([, it]) => it.maxStack === 1 && it.debug)!;

test("an item that stacks may hold up to what the save holds, whatever the game's stack size", () => {
  expect(MAX_AMOUNT).toBe(65535); // the save's amount is 16 bits (shared/character.mjs)
  expect(amountLimit(20)).toBe(65535);
  expect(amountLimit(20, 200)).toBe(65535);
  expect(amountLimit(9999)).toBe(65535);
});

test("an item that does not stack keeps the amount it has", () => {
  expect(amountLimit(1)).toBe(1);
  expect(amountLimit(1, 1)).toBe(1);
  expect(amountLimit(1, 3)).toBe(3); // a save that came with one
  expect(amountLimit(0)).toBe(1);
  expect(amountLimit(undefined as unknown as number)).toBe(1);
  expect(amountLimit(1, 0)).toBe(1);
});

test("an amount above the game's stack is said, not refused", () => {
  expect(aboveStack(20, 20)).toBe(false);
  expect(aboveStack(20, 21)).toBe(true);
  expect(aboveStack(20, 200)).toBe(true);
  expect(aboveStack(1, 3)).toBe(false); // not a stack at all
  expect(aboveStack(undefined as unknown as number, 5)).toBe(false);
});

test("a new entry of a stacking item takes an amount above its stack size, bounded by the save", () => {
  const [id, it] = stacking;
  expect(newEntry(Number(id), 0, it.maxStack).count).toBe(it.maxStack);
  expect(newEntry(Number(id), 0, it.maxStack * 10).count).toBe(it.maxStack * 10);
  expect(newEntry(Number(id), 0, 70000).count).toBe(65535);
  expect(newEntry(Number(id), 0, 0).count).toBe(1);
});

test("a new entry of an item that does not stack is one", () => {
  const [id] = single;
  expect(newEntry(Number(id), 0, 50).count).toBe(1);
});

test("the note has its English text", () => {
  definirIdioma("en");
  expect(t("acima da pilha do jogo ({n}): para mundos com um mod que aumenta as pilhas", { n: 20 })).toBe(
    "more than the game's stack of 20: for worlds with a mod that raises stack sizes",
  );
});
