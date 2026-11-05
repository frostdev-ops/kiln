// Scopes the vendored Save Editor's stylesheet under `.kiln-editor`. Re-run after re-vendoring:
//   bun scripts/scope-editor-css.ts <upstream src/styles.css> src/editor/src/styles.css
// Rules: `:root`, `html`, `body`, `#root` become `.kiln-editor`; `*` becomes `.kiln-editor *`; every other selector gets
// the `.kiln-editor ` prefix; `.viewport { position: fixed }` becomes absolute (the editor lives inside the launcher).
// Handles plain rule blocks and comments (the upstream file has no @media/@keyframes; the script refuses them).
import { readFileSync, writeFileSync } from "node:fs";

const [src, out] = process.argv.slice(2);
if (!src || !out) throw new Error("usage: scope-editor-css.ts <in.css> <out.css>");
const css = readFileSync(src, "utf8");
if (/@(media|keyframes|supports|layer)/.test(css)) throw new Error("at-rules found: extend the scoper first");

const S = ".kiln-editor";
const scopeOne = (sel: string): string => {
  const s = sel.trim();
  if (!s) return s;
  if (s === ":root" || s === "html" || s === "body" || s === "#root") return S;
  if (s === "*") return `${S} *`;
  if (s.startsWith("body ")) return `${S} ${s.slice(5)}`;
  return `${S} ${s}`;
};

let outCss = "/* Scoped copy of the Save Editor's styles.css (scripts/scope-editor-css.ts). Do not edit by hand. */\n";
let i = 0;
while (i < css.length) {
  const ws = css.slice(i).match(/^\s*/)![0];
  if (ws) { outCss += ws; i += ws.length; continue; }
  if (css.startsWith("/*", i)) {
    const end = css.indexOf("*/", i) + 2;
    outCss += css.slice(i, end);
    i = end;
    continue;
  }
  const open = css.indexOf("{", i);
  if (open < 0) { outCss += css.slice(i); break; }
  const close = css.indexOf("}", open);
  const head = css.slice(i, open);
  const lead = head.match(/^\s*/)![0];
  const selectors = head.trim().split(",").map(scopeOne);
  const uniq = [...new Set(selectors)];
  let body = css.slice(open, close + 1);
  if (uniq.includes(`${S} .viewport`)) body = body.replace("position: fixed", "position: absolute");
  outCss += lead + uniq.join(", ") + " " + body;
  i = close + 1;
}
writeFileSync(out, outCss);
console.log(`scoped ${src} -> ${out}`);
