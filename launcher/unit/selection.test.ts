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

// review of 9aeffac9, P2: the selection belongs to one loaded profile of one game (its id and `created`)
test("a selection is read only by the profile it was made in; one made again under the same id has none", async () => {
  const { ownerOf, selectionOf, setSelection } = await import("../src/shell/selection");
  const a = ownerOf("ember-run", { id: "ember-run", created: "2026-11-01T10:00:00Z" });
  setSelection("g", a, { keys: ["kiln/x"], anchor: "kiln/x" });
  expect(selectionOf("g", a).keys).toEqual(["kiln/x"]);
  expect(selectionOf("g", ownerOf("vanilla-plus", { id: "vanilla-plus", created: "2026-10-01T10:00:00Z" })).keys).toEqual([]);
  expect(selectionOf("g", ownerOf("ember-run", { id: "ember-run", created: "2026-11-13T09:00:00Z" })).keys).toEqual([]);
  expect(selectionOf("g", ownerOf(null, null)).keys).toEqual([]);
  // the profile is still being read: nothing shown, and nothing written
  expect(ownerOf("ember-run", null)).toBeNull();
  expect(selectionOf("g", null).keys).toEqual([]);
  setSelection("g", null, { keys: ["kiln/y"], anchor: null });
  expect(selectionOf("g", a).keys).toEqual(["kiln/x"]);
});

test("a Load or No profile of the game ends its selection, list open or not", async () => {
  const { ownerOf, selectionOf, setSelection } = await import("../src/shell/selection");
  const { profileSwitched } = await import("../src/api/oneActivation");
  const a = ownerOf("ember-run", { id: "ember-run", created: "2026-11-01T10:00:00Z" });
  setSelection("g2", a, { keys: ["kiln/x"], anchor: null });
  setSelection("g3", a, { keys: ["kiln/x"], anchor: null });
  profileSwitched("g2");
  expect(selectionOf("g2", a).keys).toEqual([]);
  expect(selectionOf("g3", a).keys).toEqual(["kiln/x"]);
});

// pruneSelection (what both lists call when their rows change) and forgetSelected (what a removal calls)
import { dropSelection, forgetSelected, pruneSelection, selectionOf, setSelection, useSelection } from "../src/shell/selection";

test("a removed mod leaves every game's selection: added again under the same id it is not selected", () => {
  dropSelection("g"); dropSelection("h");
  setSelection("g", "p1", { keys: ["mod:a"], anchor: "mod:a" });
  setSelection("h", "p2", { keys: ["mod:a", "mod:b"], anchor: "mod:b" });
  // the list's own clean-up does not run on an empty list (the only mod was removed): the removal itself lets go
  expect(pruneSelection("g", "p1", [], false)).toBe(0);
  expect(selectionOf("g", "p1").keys).toEqual(["mod:a"]);
  forgetSelected(["mod:a"]);
  expect(selectionOf("g", "p1")).toEqual({ keys: [], anchor: null });
  expect(selectionOf("h", "p2")).toEqual({ keys: ["mod:b"], anchor: "mod:b" });
  // the same id is back in the list: nothing selects it
  expect(pruneSelection("g", "p1", ["mod:a"], true)).toBe(0);
  expect(selectionOf("g", "p1").keys).toEqual([]);
  const before = useSelection.getState();
  forgetSelected(["mod:zzz"]);
  expect(useSelection.getState()).toBe(before); // nothing selected of it: no change, no re-render
});

test("nothing is let go while the rows are not read, nor for a list without a loaded profile", () => {
  dropSelection("g");
  setSelection("g", "p1", { keys: ["mod:a", "mod:b"], anchor: "mod:a" });
  expect(pruneSelection("g", "p1", [], false)).toBe(0);
  expect(pruneSelection("g", null, [], true)).toBe(0);
  expect(selectionOf("g", "p1").keys).toEqual(["mod:a", "mod:b"]);
  // read: the row that is gone leaves, and the bar's line is only asked for by the list that shows one
  expect(pruneSelection("g", "p1", ["mod:b"], true, true)).toBe(1);
  expect(useSelection.getState().byGame.g).toEqual({ owner: "p1", sel: { keys: ["mod:b"], anchor: null }, note: "1 not shown any more left the selection" });
  setSelection("g", "p1", { keys: ["mod:b", "mod:c"], anchor: "mod:c" });
  expect(pruneSelection("g", "p1", ["mod:b"], true)).toBe(1);
  expect(useSelection.getState().byGame.g.note).toBeNull();
});
