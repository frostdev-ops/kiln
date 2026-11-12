import { expect, test } from "bun:test";
import type { DataFolder, DataFolderPlan } from "../src/api/commands";
import { ENV_WHY, listWords, noticeView, planView, relativeTo, rowView, sizeView, stoppedView } from "../src/shell/dataFolderText";
import { splitPath } from "../src/ui/format";

const folder = (o: Partial<DataFolder> = {}): DataFolder => ({
  path: "/home/player/.local/share/kiln", defaultPath: "/home/player/.local/share/kiln", source: "default", problem: null, notice: null, ...o,
});
const plan = (o: Partial<DataFolderPlan> = {}): DataFolderPlan => ({
  picked: "/mnt/games", target: "/mnt/games", action: "move", reason: null, bytes: 3_650_000_000, files: 12_408, free: 412_000_000_000,
  sameVolume: false, copyMode: [], stays: [], ...o,
});

test("a plain move: where to, what moves, the free space; no extra line", () => {
  const v = planView(plan());
  expect(v.title).toBe("Move the data folder?");
  expect(v.facts).toEqual([
    { label: "To", value: "/mnt/games", path: true },
    { label: "Moves", value: "3.4 GB, 12,408 files" },
    { label: "Free there", value: "384 GB" },
  ]);
  expect(v.lines).toEqual([]);
  expect([v.dismiss, v.confirm]).toEqual(["Cancel", "Move"]);
});

test("a move says only what changes the decision: a new folder inside, copy mode, entries that stay", () => {
  expect(planView(plan({ target: "/mnt/storage/Kiln", picked: "/mnt/storage" })).lines).toEqual([
    "That folder has other files in it, so the data goes into a new folder inside it.",
  ]);
  expect(planView(plan({ copyMode: ["Enshrouded"] })).lines).toEqual(["From there, mods are copied into the game folder of Enshrouded."]);
  expect(planView(plan({ copyMode: ["Enshrouded", "Ember Isles", "Lab"] })).lines[0]).toContain("of Enshrouded, Ember Isles and Lab.");
  expect(planView(plan({ stays: ["my-notes.txt", "old-mods"] })).lines).toEqual(["Also kept in the old folder: my-notes.txt, old-mods."]);
  // the same volume changes nothing for the player
  expect(planView(plan({ sameVolume: true })).lines).toEqual([]);
});

test("unknown free space is left out, one file is singular", () => {
  const v = planView(plan({ free: null, files: 1, bytes: 512 }));
  expect(v.facts.map((f) => f.label)).toEqual(["To", "Moves"]);
  expect(v.facts[1].value).toBe("512 B, 1 file");
});

test("use: the folder, one line, Use it", () => {
  const v = planView(plan({ action: "use", picked: "/mnt/old-kiln", target: "/mnt/old-kiln", bytes: 0, files: 0 }));
  expect(v.title).toBe("Use this data folder?");
  expect(v.facts).toEqual([{ label: "Folder", value: "/mnt/old-kiln", path: true }]);
  expect(v.lines).toEqual(["It already holds Kiln data. The current folder stays where it is."]);
  expect([v.dismiss, v.confirm]).toEqual(["Cancel", "Use it"]);
});

test("refuse: the backend's sentence, Choose another and Close", () => {
  const v = planView(plan({ action: "refuse", reason: "Kiln cannot write to this folder.", picked: "/mnt/readonly" }));
  expect(v.title).toBe("Kiln cannot use this folder");
  expect(v.facts).toEqual([{ label: "Folder", value: "/mnt/readonly", path: true }]);
  expect(v.lines).toEqual(["Kiln cannot write to this folder."]);
  expect([v.dismiss, v.confirm]).toEqual(["Close", "Choose another"]);
  expect(planView(plan({ action: "refuse", reason: null })).lines).toEqual(["This folder cannot hold the data folder."]);
});

test("the row: the folder in use; KILN_DATA_DIR turns Change off with the reason", () => {
  expect(rowView(folder(), null, "/x")).toEqual({ path: "/home/player/.local/share/kiln", canChange: true, why: null });
  expect(rowView(folder({ source: "pointer", path: "/mnt/games/Kiln" }), null, "/x")).toEqual({ path: "/mnt/games/Kiln", canChange: true, why: null });
  expect(rowView(folder({ source: "env", path: "/srv/kiln-data" }), null, "/x")).toEqual({ path: "/srv/kiln-data", canChange: false, why: ENV_WHY });
});

test("the row before data_folder_get answers, and on a backend without it: app_info's folder, no Change", () => {
  expect(rowView(undefined, null, "/home/player/.local/share/kiln")).toEqual({ path: "/home/player/.local/share/kiln", canChange: false, why: null });
  const old = rowView(undefined, { code: "not_implemented", message: "no" }, "/home/player/.local/share/kiln");
  expect(old.canChange).toBe(false);
  expect(old.why).toBe("This launcher cannot move its data folder.");
  expect(rowView(undefined, null, undefined).path).toBeNull();
});

test("the size: bytes beside the path, the file count as its title, nothing while measuring", () => {
  expect(sizeView({ bytes: 3_650_000_000, files: 12_408 })).toEqual({ text: "3.4 GB", title: "12,408 files" });
  expect(sizeView({ bytes: 0, files: 1 })?.title).toBe("1 file");
  expect(sizeView(undefined)).toBeNull();
});

test("a move that did not finish says nothing changed; a failure says why first", () => {
  expect(stoppedView("canceled", null)).toEqual({ title: "Move canceled", lines: ["Nothing changed. The data stays in"] });
  expect(stoppedView("failed", "Disk full.")).toEqual({ title: "Move failed", lines: ["Disk full.", "Nothing changed. The data stays in"] });
  // a canceled move has no error to show even when one came with it
  expect(stoppedView("canceled", "Canceled.").lines).toHaveLength(1);
});

test("the notice: what stayed, relative to the old folder", () => {
  const from = "/home/player/.local/share/kiln";
  expect(noticeView({ from, to: "/mnt/games/Kiln", left: [] })).toEqual({ title: "Data folder moved", left: [], leftLine: null });
  const v = noticeView({ from, to: "/mnt/games/Kiln", left: [`${from}/my-notes.txt`, `${from}/old-mods`, "/elsewhere/x"] });
  expect(v.left).toEqual(["my-notes.txt", "old-mods", "/elsewhere/x"]);
  expect(v.leftLine).toBe("These stayed in the old folder:");
  expect(noticeView({ from, to: "/t", left: [`${from}/a`] }).leftLine).toBe("This stayed in the old folder:");
});

test("relativeTo needs a separator at the edge (kiln-old is not inside kiln)", () => {
  expect(relativeTo("/a/kiln", "/a/kiln/x")).toBe("x");
  expect(relativeTo("/a/kiln/", "/a/kiln/x/y")).toBe("x/y");
  expect(relativeTo("/a/kiln", "/a/kiln-old/x")).toBe("/a/kiln-old/x");
  expect(relativeTo("C:\\Users\\Player\\AppData\\Local\\Kiln", "C:\\Users\\Player\\AppData\\Local\\Kiln\\state")).toBe("state");
});

test("splitPath keeps the last name for the middle shortening", () => {
  expect(splitPath("/home/player/.local/share/kiln")).toEqual(["/home/player/.local/share", "/kiln"]);
  expect(splitPath("/mnt/games/")).toEqual(["/mnt", "/games"]);
  expect(splitPath("/mnt")).toEqual(["", "/mnt"]);
  expect(splitPath("/")).toEqual(["", "/"]);
  expect(splitPath("C:\\Users\\Player\\AppData\\Local\\Kiln")).toEqual(["C:\\Users\\Player\\AppData\\Local", "\\Kiln"]);
  expect(splitPath("D:\\Kiln")).toEqual(["D:", "\\Kiln"]);
  expect(splitPath("D:\\")).toEqual(["", "D:\\"]);
  // nothing is lost: the two parts are the path
  for (const p of ["/run/media/mmcblk0p1/Kiln", "E:\\Spiele\\Kiln Daten", "relative"]) expect(splitPath(p).join("")).toBe(p);
});

test("listWords", () => {
  expect(listWords([])).toBe("");
  expect(listWords(["A"])).toBe("A");
  expect(listWords(["A", "B"])).toBe("A and B");
});
