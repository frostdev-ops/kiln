// SPDX-License-Identifier: MIT
// Finds Kiln.Testing tests in C# source without a compiler: `[KilnTest]` / `[KilnTest("name")]` / `[KilnTest(Name = ..)]`
// on a method. The test's name is what Kiln.Testing reports: the attribute's name, else `<TypeShortName>.<Method>`
// (managed/Kiln.Testing/TestRunner.cs).

export interface FoundTest {
  /** the name in kiln.log and kiln/logs/tests/<mod>.json */
  name: string;
  className: string;
  method: string;
  /** 0-based line of the attribute, and of the method name */
  line: number;
  methodLine: number;
  methodCol: number;
  skip?: string;
}

/** Blanks comments and string contents (keeps offsets and newlines), so the scanner sees only code. */
export function blankNonCode(src: string): string {
  const out = src.split("");
  let i = 0;
  const n = src.length;
  const blank = (a: number, b: number) => {
    for (let k = a; k < b; k++) if (out[k] !== "\n") out[k] = " ";
  };
  while (i < n) {
    const c = src[i];
    const d = src[i + 1];
    if (c === "/" && d === "/") {
      const e = src.indexOf("\n", i);
      const end = e < 0 ? n : e;
      blank(i, end);
      i = end;
    } else if (c === "/" && d === "*") {
      const e = src.indexOf("*/", i + 2);
      const end = e < 0 ? n : e + 2;
      blank(i, end);
      i = end;
    } else if (c === '"' || (c === "@" && d === '"') || (c === "$" && d === '"') || (c === "$" && d === "@") || (c === "@" && d === "$")) {
      // regular, verbatim, interpolated strings (raw """ strings are treated as verbatim-ish)
      let j = i;
      while (src[j] !== '"') j++;
      const verbatim = src.slice(i, j).includes("@");
      if (src.startsWith('"""', j)) {
        const e = src.indexOf('"""', j + 3);
        const end = e < 0 ? n : e + 3;
        blank(j + 1, end - 1);
        i = end;
        continue;
      }
      j++;
      const s0 = j;
      while (j < n) {
        if (!verbatim && src[j] === "\\") {
          j += 2;
          continue;
        }
        if (src[j] === '"') {
          if (verbatim && src[j + 1] === '"') {
            j += 2;
            continue;
          }
          break;
        }
        if (!verbatim && src[j] === "\n") break;
        j++;
      }
      blank(s0, j);
      i = j + 1;
    } else if (c === "'") {
      let j = i + 1;
      while (j < n && src[j] !== "'" && src[j] !== "\n") j += src[j] === "\\" ? 2 : 1;
      blank(i + 1, j);
      i = j + 1;
    } else i++;
  }
  return out.join("");
}

function lineCol(src: string, idx: number): [number, number] {
  let line = 0;
  let last = -1;
  for (let k = src.indexOf("\n"); k >= 0 && k < idx; k = src.indexOf("\n", k + 1)) {
    line++;
    last = k;
  }
  return [line, idx - last - 1];
}

const TYPE_RX = /\b(?:class|struct|record(?:\s+(?:class|struct))?)\s+([A-Za-z_]\w*)/y;
const ATTR_RX = /\[\s*(?:[A-Za-z_][\w.]*\.)?KilnTest(?:Attribute)?\b/y;
const METHOD_RX = /\b([A-Za-z_]\w*)\s*(?:<[^>]*>)?\s*\(/g;

export function findTests(source: string): FoundTest[] {
  const code = blankNonCode(source);
  const tests: FoundTest[] = [];
  const stack: { name: string; depth: number }[] = [];
  let depth = 0;
  let pendingType: string | undefined;
  for (let i = 0; i < code.length; i++) {
    const ch = code[i];
    if (ch === "{") {
      depth++;
      if (pendingType) {
        stack.push({ name: pendingType, depth });
        pendingType = undefined;
      }
      continue;
    }
    if (ch === "}") {
      if (stack.length && stack[stack.length - 1].depth === depth) stack.pop();
      depth--;
      continue;
    }
    if (ch === ";" && pendingType) {
      pendingType = undefined; // `record Foo(int X);` has no body
      continue;
    }
    if (/[A-Za-z]/.test(ch) && (i === 0 || !/[\w.]/.test(code[i - 1]))) {
      TYPE_RX.lastIndex = i;
      const t = TYPE_RX.exec(code);
      if (t) {
        pendingType = t[1];
        i = TYPE_RX.lastIndex - 1;
        continue;
      }
    }
    if (ch === "[") {
      ATTR_RX.lastIndex = i;
      const a = ATTR_RX.exec(code);
      if (!a) continue;
      // the attribute's argument list (from the original source, strings intact)
      const close = matchBracket(code, i);
      const attrText = source.slice(i, close + 1);
      const named = /\bName\s*=\s*"((?:[^"\\]|\\.)*)"/.exec(attrText);
      const positional = /KilnTest(?:Attribute)?\s*\(\s*"((?:[^"\\]|\\.)*)"/.exec(attrText);
      const skip = /\bSkip\s*=\s*"((?:[^"\\]|\\.)*)"/.exec(attrText);
      // the method: the first `name(` after the attribute (and any further attributes) outside brackets
      let j = close + 1;
      let m: RegExpExecArray | null = null;
      for (;;) {
        while (j < code.length && /\s/.test(code[j])) j++;
        if (code[j] === "[") {
          j = matchBracket(code, j) + 1;
          continue;
        }
        METHOD_RX.lastIndex = j;
        m = METHOD_RX.exec(code);
        break;
      }
      if (!m) continue;
      const method = m[1];
      const cls = stack.length ? stack[stack.length - 1].name : "";
      const [line] = lineCol(source, i);
      const [ml, mc] = lineCol(source, m.index);
      tests.push({
        name: named?.[1] ?? positional?.[1] ?? `${cls}.${method}`,
        className: cls,
        method,
        line,
        methodLine: ml,
        methodCol: mc,
        skip: skip?.[1],
      });
      i = close;
    }
  }
  return tests;
}

function matchBracket(code: string, open: number): number {
  let d = 0;
  for (let k = open; k < code.length; k++) {
    if (code[k] === "[") d++;
    else if (code[k] === "]" && --d === 0) return k;
  }
  return code.length - 1;
}
