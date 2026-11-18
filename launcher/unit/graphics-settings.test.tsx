import { expect, test } from "bun:test";
import { renderToStaticMarkup } from "react-dom/server";
import type { SchemaSnapshot } from "../src/api/commands";
import { graphicsAsideText, graphicsLabels, graphicsSchema, isGraphicsPath } from "../src/components/GraphicsSettings";
import snapshot from "../src/data/kiln-json.snapshot.json";
import { SchemaForm } from "../src/ui/SchemaForm";

const schema = snapshot as unknown as SchemaSnapshot;

test("the Graphics card edits the graphics.* entries of kiln.json, the kiln.json card the rest", () => {
  const g = graphicsSchema(schema);
  expect(g.sections).toEqual([]);
  expect(g.entries.map((e) => e.path)).toEqual([
    "graphics.fpsCap", "graphics.dlssPreset", "graphics.dlssScale", "graphics.dlssRenderScale", "graphics.dlssQualityScale",
    "graphics.dlssBalancedScale", "graphics.dlssPerformanceScale", "graphics.dlssUltraPerformanceScale", "graphics.readout",
  ]);
  expect(schema.entries.filter((e) => !isGraphicsPath(e.path)).some((e) => e.path.startsWith("graphics"))).toBe(false);
  expect(isGraphicsPath("graphics")).toBe(false);
  expect(isGraphicsPath("graphicsX.a")).toBe(false);
});

test("the snapshot's graphics entries are the ones the game reads", () => {
  const by = Object.fromEntries(schema.entries.map((e) => [e.path, e]));
  expect(schema.sections?.some((s) => s.path === "graphics")).toBe(true);
  expect(by["graphics.fpsCap"]).toMatchObject({ type: "int", default: 0, min: 0, max: 500 });
  expect(by["graphics.dlssPreset"]).toMatchObject({ type: "enum", default: "default", options: ["default", "J", "K", "L", "M"] });
  expect(by["graphics.dlssScale"]).toMatchObject({ type: "enum", default: "game", options: ["game", "perMode", "custom"] });
  for (const [k, d] of [["dlssRenderScale", 0.67], ["dlssQualityScale", 0.667], ["dlssBalancedScale", 0.58], ["dlssPerformanceScale", 0.5],
    ["dlssUltraPerformanceScale", 0.333]] as const)
    expect(by[`graphics.${k}`]).toMatchObject({ type: "float", default: d, min: 0.33, max: 1 });
  expect(by["graphics.readout"]).toMatchObject({ type: "bool", default: false });
  for (const e of schema.entries.filter((e) => isGraphicsPath(e.path))) {
    expect(e.requiresRestart).toBe(false);
    expect(e.comment?.length ?? 0).toBeGreaterThan(10);
  }
});

test("a step-aside line names the tool's file and what steps aside", () => {
  expect(graphicsAsideText({ kind: "optiscaler", file: "dxgi.dll", frameCap: true })).toBe(
    "OptiScaler is installed (dxgi.dll): Kiln's frame cap and DLSS settings step aside; the readout still works.");
  expect(graphicsAsideText({ kind: "dlsstweaks", file: "dlsstweaks.ini", frameCap: false })).toBe(
    "DLSSTweaks is installed (dlsstweaks.ini): Kiln's DLSS settings step aside; the frame cap and the readout still work.");
  expect(graphicsAsideText({ kind: "streamline", file: "Streamline/sl.interposer.dll", frameCap: true })).toContain("Streamline/sl.interposer.dll");
  expect(graphicsAsideText({ kind: "vkfg", file: "EnshroudedVKFG.ini", frameCap: true })).toContain("frame generation");
  expect(graphicsAsideText({ kind: "nvngx", file: "_nvngx.dll", frameCap: false })).toContain("Another DLSS loader");
  // a kind a later backend adds still reads as a sentence
  expect(graphicsAsideText({ kind: "future", file: "x.dll", frameCap: false })).toStartWith("Another graphics tool is in the game folder (x.dll)");
});

test("the Graphics card's fields have labels in words, not their key names", () => {
  const labels = graphicsLabels();
  const paths = graphicsSchema(schema).entries.map((e) => e.path);
  // one label per entry the card shows, and no label for an entry it does not
  expect(Object.keys(labels).sort()).toEqual([...paths].sort());
  expect(labels["graphics.fpsCap"]).toBe("Frame cap");
  expect(labels["graphics.dlssPreset"]).toBe("DLSS render preset");
  expect(labels["graphics.dlssScale"]).toBe("DLSS render size");
  expect(labels["graphics.dlssUltraPerformanceScale"]).toBe("DLSS render scale, Ultra Performance");
  for (const p of paths) expect(labels[p]).not.toMatch(/^(Fps|Dlss) /);
  // the form shows them (and names Reset by them); the key stays under each, as on every card
  const noop = () => {};
  const html = renderToStaticMarkup(<SchemaForm schema={graphicsSchema(schema)} values={{ graphics: { fpsCap: 60 } }} onChange={noop} onReset={noop}
    labels={labels} />);
  for (const p of paths) expect(html).toContain(`>${labels[p]}</label>`);
  expect(html).not.toContain("Fps Cap");
  expect(html).not.toContain("Dlss Preset");
  expect(html).toContain('aria-label="Reset Frame cap to its default"');
  expect(html).toContain(">graphics.fpsCap<");
});

test("a SchemaForm without labels still names its fields from their keys", () => {
  const noop = () => {};
  const one = { ...schema, sections: [], entries: schema.entries.filter((e) => e.path === "ui.chatHistory" || e.path === "graphics.fpsCap") };
  const html = renderToStaticMarkup(<SchemaForm schema={one} values={{}} onChange={noop} />);
  expect(html).toContain(">Chat History</label>");
  expect(html).toContain(">Fps Cap</label>");
  // a labels map that leaves an entry out: that one is named from its key
  const some = renderToStaticMarkup(<SchemaForm schema={one} values={{}} onChange={noop} labels={{ "graphics.fpsCap": "Frame cap" }} />);
  expect(some).toContain(">Frame cap</label>");
  expect(some).toContain(">Chat History</label>");
});
