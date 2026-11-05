// SPDX-License-Identifier: MIT
// Minimal glob matching for schemas.json / idrefs.json `fileMatch` lists: `**` (any segments), `*`, `?`, `{a,b}`;
// a leading `!` excludes. Paths use `/`.

const cache = new Map<string, RegExp>();

export function globToRegExp(glob: string): RegExp {
  let re = cache.get(glob);
  if (re) return re;
  let s = "";
  let i = 0;
  let inBrace = 0;
  while (i < glob.length) {
    const c = glob[i];
    if (c === "*") {
      if (glob[i + 1] === "*") {
        const slash = glob[i + 2] === "/";
        s += slash ? "(?:[^/]*/)*" : ".*";
        i += slash ? 3 : 2;
        continue;
      }
      s += "[^/]*";
    } else if (c === "?") s += "[^/]";
    else if (c === "{") {
      inBrace++;
      s += "(?:";
    } else if (c === "}" && inBrace) {
      inBrace--;
      s += ")";
    } else if (c === "," && inBrace) s += "|";
    else s += c.replace(/[.+^$()|[\]\\]/g, "\\$&");
    i++;
  }
  re = new RegExp(`^${s}$`);
  cache.set(glob, re);
  return re;
}

/** True when some positive pattern matches one of the candidate paths and no `!` pattern does. */
export function matchFiles(patterns: string[], candidates: string[]): boolean {
  const paths = candidates.map((p) => p.replace(/\\/g, "/"));
  let hit = false;
  for (const p of patterns) {
    if (p.startsWith("!")) continue;
    const re = globToRegExp(p.startsWith("/") || p.startsWith("**") ? p : `**/${p}`);
    if (paths.some((c) => re.test(c))) {
      hit = true;
      break;
    }
  }
  if (!hit) return false;
  for (const p of patterns) {
    if (!p.startsWith("!")) continue;
    const g = p.slice(1);
    const re = globToRegExp(g.startsWith("/") || g.startsWith("**") ? g : `**/${g}`);
    if (paths.some((c) => re.test(c))) return false;
  }
  return true;
}
