// The main list's multi-select (src/shell/selection.ts): a click toggles and sets the range's start, Shift+click adds
// the run between, Ctrl+A takes every row shown, and rows the filter hides leave the selection (and say how many).
import { expect, test } from "bun:test";
import { NONE, all, keepShown, pick, range, toggle } from "../src/shell/selection";

const order = ["a", "b", "c", "d", "e"];

test("a click toggles a row and is where a range starts", () => {
  const s = toggle(NONE, "b");
  expect(s).toEqual({ keys: ["b"], anchor: "b" });
  expect(toggle(s, "b")).toEqual({ keys: [], anchor: "b" });
});

test("Shift+click selects the run from the anchor in either direction, keeping what was selected", () => {
  const s = toggle(toggle(NONE, "e"), "b");
  expect(range(s, "d", order).keys.sort()).toEqual(["b", "c", "d", "e"]);
  expect(range(toggle(NONE, "d"), "a", order).keys.sort()).toEqual(["a", "b", "c", "d"]);
  expect(range(toggle(NONE, "d"), "a", order).anchor).toBe("d");
});

test("Shift+click without an anchor the list still shows is a plain toggle", () => {
  expect(range(NONE, "c", order)).toEqual({ keys: ["c"], anchor: "c" });
  expect(range({ keys: ["x"], anchor: "x" }, "c", order)).toEqual({ keys: ["x", "c"], anchor: "c" });
});

test("Ctrl+A takes every row shown", () => {
  expect(all(order)).toEqual({ keys: order, anchor: "a" });
  expect(all([])).toEqual({ keys: [], anchor: null });
});

test("a filter that hides selected rows lets go of them and says how many; one that hides none changes nothing", () => {
  const s = { keys: ["a", "c", "e"], anchor: "c" };
  expect(keepShown(s, ["a", "b", "c"])).toEqual({ sel: { keys: ["a", "c"], anchor: "c" }, dropped: 1 });
  expect(keepShown(s, ["b", "d"])).toEqual({ sel: { keys: [], anchor: null }, dropped: 3 });
  expect(keepShown(s, order).sel).toBe(s);
});

test("the selected rows come in the order shown", () => {
  const rows = order.map((key) => ({ key }));
  expect(pick({ keys: ["d", "a"], anchor: "d" }, rows).map((r) => r.key)).toEqual(["a", "d"]);
});
