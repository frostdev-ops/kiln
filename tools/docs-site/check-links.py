#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks the relative links in Markdown pages: every target must exist and every anchor must match a heading.

  tools/docs-site/check-links.py [--strict] [--code-paths] [path...]     (default: docs/kiln)
  tools/docs-site/check-links.py --self-test [--tmp <dir>]

Walks *.md below each path. Checks inline links and images ([t](path), [t](path#anchor), [t](#anchor), ![a](path),
[t](<path with spaces>)) and reference definitions ([id]: path), outside fenced code blocks and inline code. Links
with a scheme (http:, https:, mailto:, ...) are skipped. A path starting with / is taken from the repository root.

Kinds, one line each as `file:line: kind: link`:
  missing         the file or folder is not there
  bad-anchor      the target has no heading (GitHub's slug rule) or HTML id/name for the #anchor
  private-target  a git-tracked page links to a file git does not track (ignored or excluded, like the private
                  *-plan.md and phase*-status.md pages): it resolves here and not in a clean checkout. Outside a git
                  tree such a link counts as existing.
  code-path       (only with --code-paths, a report that never fails) a path in backticks in the prose, starting with
                  docs/ or ./ or ../, that does not exist
Exit 1 when anything is missing or bad-anchor; private-target fails only with --strict. Standard library only, no
network. It reports; it changes no page.
"""
import argparse
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unicodedata
from urllib.parse import unquote

SCHEME = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")
FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
ATX = re.compile(r"^ {0,3}(#{1,6})[ \t]+(.*?)[ \t]*#*[ \t]*$")
SETEXT = re.compile(r"^ {0,3}(=+|-+)[ \t]*$")
HTML_ID = re.compile(r"""<[a-zA-Z][^>]*?\s(?:id|name)\s*=\s*["']([^"']+)["']""")
REFDEF = re.compile(r"^ {0,3}\[([^\]]+)\]:[ \t]*(<[^>]*>|\S+)")
CODE_SPAN = re.compile(r"(`+)(.+?)\1")


def slug(text):
    """GitHub's heading anchor: the rendered text, lower-cased, without punctuation, spaces as hyphens."""
    text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", text)      # links and images keep their text
    parts = re.split(r"(`+)(.+?)\1", text)                       # code spans keep their text as it is
    text = ""
    for i in range(0, len(parts), 3):
        prose = re.sub(r"<[^>]+>", "", parts[i])                 # inline HTML
        text += re.sub(r"(\*+|(?<!\w)_+|_+(?!\w))", "", prose)  # emphasis markers, not snake_case
        if i + 2 < len(parts):
            text += parts[i + 2]
    out = []
    for ch in text.lower():
        cat = unicodedata.category(ch)
        if ch in " -_" or cat[0] in "LN" or cat == "Mn":
            out.append("-" if ch == " " else ch)
    return "".join(out)


def strip_code_spans(line):
    """The line with inline code blanked (same length, so columns stay), and the code spans' texts."""
    spans = []

    def blank(m):
        spans.append(m.group(2).strip())
        return " " * len(m.group(0))
    return CODE_SPAN.sub(blank, line), spans


def find_links(line):
    """(target) for each inline link or image on a line with code spans already blanked."""
    found = []
    i = 0
    while True:
        j = line.find("](", i)
        if j < 0:
            break
        k, depth = j + 2, 1
        while k < len(line) and depth:
            depth += {"(": 1, ")": -1}.get(line[k], 0)
            k += 1
        if depth == 0:
            body = line[j + 2:k - 1].strip()
            if body.startswith("<") and ">" in body:
                found.append(body[1:body.index(">")])
            elif body:
                found.append(body.split()[0])
        i = j + 2
    return found


def parse(path):
    """links [(line, target)], code paths [(line, text)], anchors {slug} of one page."""
    links, code, anchors, counts = [], [], set(), {}
    fence = None
    prev = None
    try:
        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return links, code, anchors

    def add_anchor(text):
        s = slug(text)
        n = counts.get(s, 0)
        counts[s] = n + 1
        anchors.add(s if n == 0 else f"{s}-{n}")
    for no, line in enumerate(lines, 1):
        m = FENCE.match(line)
        if fence:
            if m and m.group(1)[0] == fence[0] and len(m.group(1)) >= len(fence) and not line.strip()[len(m.group(1)):].strip():
                fence = None
            prev = None
            continue
        if m:
            fence = m.group(1)
            prev = None
            continue
        bare, spans = strip_code_spans(line)
        for a in HTML_ID.findall(bare):
            anchors.add(a.lower())
        h = ATX.match(line)
        if h:
            add_anchor(h.group(2))
        elif SETEXT.match(line) and prev and prev.strip() and not prev.lstrip().startswith(("-", "*", "+", ">", "|")):
            add_anchor(prev.strip())
        r = REFDEF.match(bare)
        if r:
            t = r.group(2)
            links.append((no, t[1:-1] if t.startswith("<") else t))
        else:
            links += [(no, t) for t in find_links(bare)]
        code += [(no, s) for s in spans]
        prev = line
    return links, code, anchors


class Git:
    """Which files a clean checkout has; everything 'exists' outside a git tree."""

    def __init__(self, start):
        self.root = None
        self.tracked = set()
        self.dirs = set()
        try:
            top = subprocess.run(["git", "-C", str(start), "rev-parse", "--show-toplevel"], capture_output=True,
                                 text=True, check=True).stdout.strip()
            files = subprocess.run(["git", "-C", top, "ls-files", "-z"], capture_output=True, check=True).stdout
        except (OSError, subprocess.CalledProcessError):
            return
        self.root = Path(top).resolve()
        for f in files.decode("utf-8", "surrogateescape").split("\0"):
            if f:
                p = self.root / f
                self.tracked.add(p)
                for d in p.parents:
                    if d in self.dirs or d == self.root:
                        break
                    self.dirs.add(d)

    def has(self, p):
        return self.root is None or p in self.tracked or p in self.dirs or p == self.root \
            or not p.is_relative_to(self.root)

    def why(self, p):
        try:
            r = subprocess.run(["git", "-C", str(self.root), "check-ignore", "-q", "--no-index", str(p)])
            return "ignored" if r.returncode == 0 else "untracked"
        except OSError:
            return "untracked"


def check(paths, code_paths=False, out=sys.stdout):
    """Prints one line per problem and the summary; returns {kind: count}."""
    pages = []
    for p in paths:
        p = Path(p)
        pages += sorted(p.rglob("*.md")) if p.is_dir() else [p]
    if not pages:
        print("no Markdown pages under " + " ".join(map(str, paths)), file=out)
        return {"missing": 0}
    git = Git(pages[0].resolve().parent)
    cache = {}

    def page(p):
        if p not in cache:
            cache[p] = parse(p)
        return cache[p]
    counts = {"missing": 0, "bad-anchor": 0, "private-target": 0, "code-path": 0}
    nlinks = 0

    def report(src, no, kind, what):
        counts[kind] += 1
        print(f"{src}:{no}: {kind}: {what}", file=out)
    for src in pages:
        here = src.resolve()
        links, code, _ = page(here)
        for no, link in links:
            if SCHEME.match(link) or link.startswith("//"):
                continue
            nlinks += 1
            path, _, anchor = link.partition("#")
            path = unquote(path.split("?", 1)[0])
            if not path:
                target = here
            elif path.startswith("/"):
                target = Path(os.path.normpath((git.root or here.parent) / path.lstrip("/")))
            else:
                target = Path(os.path.normpath(here.parent / path))
            if not target.exists():
                report(src, no, "missing", link)
                continue
            if path and git.has(here) and not git.has(target):
                report(src, no, "private-target", f"{link} ({git.why(target)})")
            if anchor and target.is_file() and target.suffix.lower() in (".md", ".markdown"):
                if unquote(anchor).lower() not in page(target)[2]:
                    report(src, no, "bad-anchor", link)
        if code_paths:
            for no, text in code:
                t = text.split("#", 1)[0].rstrip(":,.")
                if " " in t or not t.startswith(("docs/", "./", "../")) or re.search(r"[*?<>{}$]", t):
                    continue
                base = (git.root or Path.cwd()) if t.startswith("docs/") else here.parent
                if not (base / t).exists():
                    report(src, no, "code-path", text)
    summary = (f"{nlinks} relative links in {len(pages)} pages: {counts['missing']} missing, "
               f"{counts['bad-anchor']} bad-anchor, {counts['private-target']} private-target")
    if code_paths:
        summary += f", {counts['code-path']} code-path (report only)"
    print(summary, file=out)
    return counts


def self_test(tmp):
    """Builds a small git tree, checks it, and compares every finding with what each case expects."""
    import io
    base = Path(tempfile.mkdtemp(prefix="check-links-", dir=tmp))
    docs = base / "docs" / "kiln"
    (docs / "sub").mkdir(parents=True)
    files = {
        "docs/kiln/index.md": "\n".join([
            "# Title",                                         # 1
            "[good file](other.md)",                           # 2
            "[good anchor](other.md#a-heading-with-code)",     # 3
            "[missing file](nothere.md)",                      # 4
            "[bad anchor](other.md#no-such-heading)",          # 5
            "[private](secret-plan.md)",                       # 6
            "[own anchor](#title) and [folder](sub/)",         # 7
            "```",                                             # 8
            "[in a fence](fenced-missing.md)",                 # 9
            "```",                                             # 10
            "`[inline code](code-missing.md)` [web](https://example.com/x.md)",  # 11
            "[ref]: missing-ref.md",                           # 12
            "![image](sub/pic.png)",                           # 13
            "[dup](other.md#same-1) see `docs/kiln/gone.md` [t](other.md#generic-listt)",  # 14
            "",
        ]),
        "docs/kiln/other.md": "## A heading with `code`\n\nSame\n----\n\n# Same\n\n### Generic `List<T>`\n",
        "docs/kiln/secret-plan.md": "# private\n",
        "docs/kiln/sub/pic.png": "png",
        ".gitignore": "*-plan.md\n",
    }
    for name, text in files.items():
        (base / name).write_text(text, encoding="utf-8")
    git = ["git", "-C", str(base), "-c", "user.name=t", "-c", "user.email=t@t"]
    subprocess.run(git + ["init", "-q"], check=True)
    subprocess.run(git + ["add", "."], check=True)
    subprocess.run(git + ["commit", "-qm", "t"], check=True)
    os.chdir(base)
    want = {
        "docs/kiln/index.md:4: missing: nothere.md",
        "docs/kiln/index.md:5: bad-anchor: other.md#no-such-heading",
        "docs/kiln/index.md:6: private-target: secret-plan.md (ignored)",
        "docs/kiln/index.md:12: missing: missing-ref.md",
        "docs/kiln/index.md:14: code-path: docs/kiln/gone.md",
    }
    buf = io.StringIO()
    counts = check([Path("docs/kiln")], code_paths=True, out=buf)
    got = set(buf.getvalue().splitlines()[:-1])
    ok = True
    for line in sorted(want - got):
        print("FAIL not reported: " + line)
        ok = False
    for line in sorted(got - want):
        print("FAIL reported a good link: " + line)
        ok = False
    if counts != {"missing": 2, "bad-anchor": 1, "private-target": 1, "code-path": 1}:
        print(f"FAIL counts {counts}")
        ok = False
    # outside git a private target is just a file that exists
    shutil.rmtree(base / ".git")
    buf = io.StringIO()
    check([Path("docs/kiln")], out=buf)
    if "private-target" in buf.getvalue().rsplit("\n", 2)[0]:
        print("FAIL private-target reported outside a git tree")
        ok = False
    print(("PASS" if ok else "FAIL") + f" self-test in {base}" + (" (removed)" if ok else ""))
    if ok:
        shutil.rmtree(base)
    return ok


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("paths", nargs="*", help="pages or folders (default docs/kiln)")
    ap.add_argument("--strict", action="store_true", help="private-target fails too")
    ap.add_argument("--code-paths", action="store_true", help="also list paths in backticks that do not exist")
    ap.add_argument("--self-test", action="store_true")
    ap.add_argument("--tmp", help="folder for the self-test's tree (default TMPDIR)")
    a = ap.parse_args()
    if a.self_test:
        sys.exit(0 if self_test(a.tmp) else 1)
    counts = check([Path(p) for p in a.paths or ["docs/kiln"]], code_paths=a.code_paths)
    bad = counts["missing"] + counts.get("bad-anchor", 0) + (counts.get("private-target", 0) if a.strict else 0)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
