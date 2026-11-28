#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Doc drift checks: the skill and the repository's docs against the code they describe.

  python3 -I tools/docs-site/drift.py [names|versions|facts|map|stale|all ...] [--warn <check>,...] [--allow FILE] [--tree DIR]

Five checks, run together by default (tools/check.sh, stage `drift`; no build needed):

  names     every `Type.Member` in backticks (and in a C# code fence) of agent/skills/kiln-modding/** and docs/kiln/** whose
            Type is a Kiln public type must be a public member of it: a member, a nested type, an enum value, a member it
            inherits (the base types come from the sources' class/struct/interface declarations), or a C# 14 `extension(...)`
            member (or a classic `this` extension method) of it or of a base. `Type.Prop.Member` is followed through a nested
            type or a property whose value is a Kiln type. The PublicAPI files are the surface (no build). `Ecs`, `Natives`
            and the other types whose members the build generates are left out.
  versions  a "since 0.2.0-dev.N", "dev.N+" or "mod API 0.2.N" mark before a backticked name claims the name exists from
            that build on: the claim is wrong when managed/api-history.tsv says its first release is later (a name still in
            PublicAPI.Unshipped.txt is later than every release the ledger knows). `<=` ledger rows (first tag with a record)
            prove nothing and are left out.
  facts     SKILL.md, references/setup.md and references/api*.md must not pin a build or mod API older than the current one
            (Kiln.Mods.ApiVersion): `Kiln.Sdk/<version>`, a version on a "this release" or "current" line, "(0.2.N on dev.X)".
            references/versions.md must have a row for the current mod API and for every mod API the ledger knows.
  map       references/docs-map.md: every repository page it names exists at docs/kiln/<path>, every sample it names is in
            mods/samples, every wiki page it names exists in the wiki folder (frostdev.io's src/content/kiln-wiki, or
            $KILN_WIKI_DIR; skipped when the folder is not there); the skill's relative links and #anchors resolve, and so do
            the `docs/kiln/...` pages and `Sample X` names in it.

  stale     tools/docs-site/drift-stale.txt lists what the pages must not say any more, a fact the code now contradicts (a fake
            that exists, a call shape that changed): a regular expression (Python, case-sensitive; (?i) for case), a file
            glob and what is true instead, tab-separated. A line of a checked page that matches is a finding, with that text.
            A line without the text, or a pattern that does not compile, is an error.

tools/docs-site/drift-allow.txt lists the known false positives (sample names, csproj names, deliberate "do not do this"
examples), one per line: check, name, file glob, reason, separated by tabs; a line without a reason, with an unknown check
or one that matches nothing any more (a stale entry) is an error. --warn makes the named checks report without failing.
Exit codes: 0 clean (or only warnings), 1 findings, 2 bad input. Tests: python3 -I tools/docs-site/test_drift.py
"""
import argparse
import collections
import fnmatch
import importlib.util
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SKILL = "agent/skills/kiln-modding"
CHECKS = ("names", "versions", "facts", "map", "stale")
ALLOW = "tools/docs-site/drift-allow.txt"
STALE = "tools/docs-site/drift-stale.txt"
# a type whose members the build generates or whose name only looks like a type of the API
SKIP_TYPES = {"Ecs", "Natives", "GameItems", "GameBuffs", "Programs", "Sigs", "Kiln", "GameTypes", "GameEvents"}
OBJECT_MEMBERS = {"ToString", "Equals", "GetHashCode", "GetType", "ReferenceEquals", "MemberwiseClone", "Finalize"}
BCL_BASES = {  # a non-Kiln base class: the members it adds
    "Exception": {"Message", "InnerException", "StackTrace", "Data", "Source", "HResult", "HelpLink", "TargetSite",
                  "GetBaseException", "GetObjectData"},
    "Attribute": {"TypeId", "Match", "IsDefaultAttribute"},
    "EventArgs": set(), "ValueType": set(), "Enum": {"HasFlag", "CompareTo"}, "Delegate": {"Invoke", "BeginInvoke", "EndInvoke"},
    "MulticastDelegate": {"Invoke", "BeginInvoke", "EndInvoke"},
}
DELEGATE_MEMBERS = {"Invoke", "BeginInvoke", "EndInvoke", "Method", "Target"}


class Finding:
    def __init__(self, check, path, line, name, text):
        self.check, self.path, self.line, self.name, self.text = check, path, line, name, text

    def __str__(self):
        return f"{self.path}:{self.line}: [{self.check}] {self.text}"


def load_api_index():
    """api_index.py, the module that already reads the PublicAPI files, as a library."""
    spec = importlib.util.spec_from_file_location("api_index", HERE / "api_index.py")
    mod = importlib.util.module_from_spec(spec)
    sys.modules.setdefault("api_index", mod)
    spec.loader.exec_module(mod)
    return mod


ai = load_api_index()


def strip_arity(t):
    return re.sub(r"`\d+", "", t)


def tkey(t):
    """Kiln.Ui.Tooltip -> Ui.Tooltip (the namespace Kiln left out, generic arity left out)."""
    return re.sub(r"^Kiln\.(?=[A-Z])", "", strip_arity(t))


def simple_of(key):
    return key.rsplit(".", 1)[-1]


def simple_receiver(text):
    """Kiln.Act! -> Act (the receiver of an extension member); '' for a receiver outside Kiln."""
    t = text.strip().split(" ")[0].replace("!", "").replace("?", "")
    t = re.sub(r"<.*$", "", t)
    return t.rsplit(".", 1)[-1] if t.startswith("Kiln.") else ""


# ---------------------------------------------------------------- the API model


class Model:
    """What the PublicAPI record says about names: types, their members, nested types, value types of properties,
    extension members by receiver, and (from the sources) base types."""

    def __init__(self, root):
        self.root = Path(root)
        self.tree = ai.Tree(self.root)
        self.types = set()                                   # type keys: Ui.Tooltip, Items
        self.simple = collections.defaultdict(set)           # simple name -> type keys
        self.members = collections.defaultdict(set)          # type key -> member names (nested types too)
        self.value = collections.defaultdict(set)            # (type key, member) -> simple name of its value type
        self.ext = collections.defaultdict(set)              # receiver simple name -> member names
        self.bases = collections.defaultdict(set)            # type simple name -> base simple names
        self.all_members = set()
        self.entries = []                                    # (state, project, line, Entry)
        self.api_version = self.tree.api_version()
        for asm, folder in self.tree.projects():
            for line, state in self.tree.record(folder):
                e = ai.parse_api_line(line)
                self.entries.append((state, asm, line, e))
        for _, _, _, e in self.entries:
            if e.kind == "type":
                self.add_type(tkey(e.type))
        for key in sorted(self.types):
            parent = self.parent(key)
            if parent:
                self.members[parent].add(simple_of(key))     # a nested type is a member of the type it sits in
        for _, _, _, e in self.entries:
            if e.kind in ("marker", "forward", "type") or not e.name:
                continue
            self.add_member(e)
        self.read_bases()

    def add_type(self, key):
        if key in self.types:
            return
        self.types.add(key)
        self.simple[simple_of(key)].add(key)

    def parent(self, key):
        """The type a nested type sits in: its longest proper prefix that is a type."""
        parts = key.split(".")
        for n in range(len(parts) - 1, 0, -1):
            p = ".".join(parts[:n])
            if p in self.types:
                return p
        return None

    def add_member(self, e):
        key = tkey(e.type)
        name = re.sub(r"<.*$", "", e.name)
        if name.startswith("<") or name.startswith("operator") or name.startswith("this["):
            return
        if e.kind == "ctor" or name in ("Deconstruct",):
            self.members[key].add(name)
            return
        if e.ext is not None:
            recv = simple_receiver(e.ext)
            if recv:
                self.ext[recv].add(name)
            self.members[key].add(name)
            return
        self.members[key].add(name)
        self.all_members.add(name)
        if e.kind in ("prop", "field") and e.ret:
            v = re.sub(r"[!?]|\[\]", "", e.ret)
            if v.startswith("Kiln.") and "<" not in v:
                self.value[(key, name)].add(simple_of(tkey(v)))
        # a classic extension method: the first parameter says `this Kiln.X`
        if e.kind == "method" and e.plist.startswith("this "):
            recv = simple_receiver(e.plist[5:])
            if recv:
                self.ext[recv].add(name)

    def nested(self, key, name):
        k = f"{key}.{name}"
        return k if k in self.types else None

    def read_bases(self):
        """class/struct/interface/record declarations in the gated projects' sources: base simple names by type name."""
        wanted = set(self.simple)
        for asm, folder in self.tree.projects():
            for path in sorted(folder.rglob("*.cs")):
                if {"bin", "obj"} & set(path.relative_to(folder).parts):
                    continue
                text = path.read_text(encoding="utf-8-sig", errors="replace")
                if ":" not in text:
                    continue
                for name, bases in parse_bases(text):
                    if name in wanted:
                        self.bases[name].update(bases)

    def chain(self, key):
        """The type keys whose members `key` has: itself and its bases (the Kiln ones), and whether a base is open (not
        known to this model: any member may be inherited from it)."""
        seen, todo, open_ = set(), [key], False
        extra = set(OBJECT_MEMBERS)
        while todo:
            k = todo.pop()
            if k in seen:
                continue
            seen.add(k)
            for b in self.bases.get(simple_of(k), ()):
                if b in self.simple:
                    todo.extend(self.simple[b])
                elif re.match(r"^I[A-Z]", b):
                    continue                                 # an interface outside Kiln: its members are not the API's
                elif b in BCL_BASES:
                    extra |= BCL_BASES[b]
                elif b in ("object", "Object"):
                    continue
                else:
                    open_ = True
        return seen, extra, open_

    def has_member(self, key, name):
        keys, extra, open_ = self.chain(key)
        if open_ or name in extra:
            return True
        if any(name in self.members.get(k, ()) or name in self.ext.get(simple_of(k), ()) for k in keys):
            return True
        return name in DELEGATE_MEMBERS and any(m == "Invoke" for m in self.members.get(key, ()))

    def known_name(self, name):
        return name in self.all_members or name in self.simple or any(name in s for s in self.ext.values())


def matching(text, i, open_, close):
    depth = 0
    for j in range(i, len(text)):
        if text[j] == open_:
            depth += 1
        elif text[j] == close:
            depth -= 1
            if depth == 0:
                return j
    return len(text) - 1


DECL_RE = re.compile(r"(?m)^[ \t]*(?:\[[^\]\n]*\][ \t]*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|"
                     r"unsafe|readonly|ref|file|new)[ \t]+)*(?:class|struct|interface|record[ \t]+struct|record[ \t]+class|record)"
                     r"[ \t]+([A-Za-z_]\w*)")


def parse_bases(text):
    """[(type name, [base simple names])] of the type declarations in a C# source that have a base list."""
    out = []
    for m in DECL_RE.finditer(text):
        i = m.end()
        while i < len(text) and text[i] in " \t\r\n":
            i += 1
        if text[i:i + 1] == "<":
            i = matching(text, i, "<", ">") + 1
        while i < len(text) and text[i] in " \t\r\n":
            i += 1
        if text[i:i + 1] == "(":
            i = matching(text, i, "(", ")") + 1
        while i < len(text) and text[i] in " \t\r\n":
            i += 1
        if text[i:i + 1] != ":":
            continue
        j, depth, buf = i + 1, 0, []
        while j < len(text):
            c = text[j]
            if c in "<(":
                depth += 1
            elif c in ">)":
                depth -= 1
            elif depth == 0 and (c in "{;" or (text.startswith("where", j) and not text[j - 1].isalnum()
                                                and not text[j + 5:j + 6].isalnum())):
                break
            buf.append(c)
            j += 1
        bases = []
        for b in ai.split_top("".join(buf)):
            b = re.sub(r"[<(].*$", "", b.strip(), flags=re.S).strip()
            if b:
                bases.append(b.rsplit(".", 1)[-1])
        out.append((m.group(1), bases))
    return out


# ---------------------------------------------------------------- the files


def doc_files(root):
    """Markdown files whose `Type.Member` names are checked: the skill (not its generated API index) and docs/kiln (not
    the release notes, the generated pages, the schema pages and the design plans, `*-plan.md`, which name API that
    was planned and may never have been built)."""
    root = Path(root)
    out = []
    for p in sorted((root / SKILL).rglob("*.md")):
        if "api-index" in p.parts:
            continue
        out.append(p)
    docs = root / "docs" / "kiln"
    for p in sorted(docs.rglob("*.md")):
        rel = p.relative_to(docs).parts
        if rel[0] == "release-notes" or "schemas" in rel[:-1] or p.name.endswith((".generated.md", "-plan.md")):
            continue
        out.append(p)
    return out


def rel(root, p):
    return str(Path(p).resolve().relative_to(Path(root).resolve())) if str(p).startswith(str(root)) else str(p)


CHAIN_RE = re.compile(r"(?<![\w.])([A-Z]\w*(?:\.[A-Z]\w*)+)")
FENCE_RE = re.compile(r"^\s*(```+|~~~+)\s*([\w#+-]*)")


def code_spans(text):
    """[(line number, code)]: the backticked spans of prose, and every line of a C# (or untagged) code fence."""
    out, fence = [], None
    for n, line in enumerate(text.splitlines(), 1):
        m = FENCE_RE.match(line)
        if m:
            if fence is None:
                fence = m.group(2).lower() or "-"
            else:
                fence = None
            continue
        if fence is not None:
            if fence in ("cs", "csharp", "c#", "-"):
                out.append((n, line))
            continue
        for s in re.finditer(r"`([^`\n]+)`", line):
            out.append((n, s.group(1)))
    return out


def chains(code):
    """The dotted `Type.Member[.Member]` names of a code span, generic arguments removed."""
    code = re.sub(r"<[^<>()]*>", "", code)
    return [m.group(1).split(".") for m in CHAIN_RE.finditer(code)]


# ---------------------------------------------------------------- names


def check_chain(model, parts):
    """None when the name is fine (or cannot be checked), else (the part that fails, 'nowhere' | 'not on this type')."""
    a = parts[0]
    if a in SKIP_TYPES or a not in model.simple:
        return None
    cur = set(model.simple[a])
    for i in range(1, len(parts)):
        name = parts[i]
        ok = {k for k in cur if model.has_member(k, name)}
        if not ok:
            where = "not on this type" if model.known_name(name) else "nowhere"
            return (".".join(parts[:i + 1]), where)
        if i == len(parts) - 1:
            return None
        nxt = set()
        for k in ok:
            n = model.nested(k, name)
            if n:
                nxt.add(n)
            for v in model.value.get((k, name), ()):
                nxt |= model.simple.get(v, set())
        if not nxt:
            return None
        cur = nxt
    return None


def check_names(root, model, files):
    out = []
    for f in files:
        for n, code in code_spans(f.read_text(encoding="utf-8", errors="replace")):
            for parts in chains(code):
                r = check_chain(model, parts)
                if r:
                    name, where = r
                    out.append(Finding("names", rel(root, f), n, ".".join(parts),
                                       f"`{name}` is {'no public member of any type' if where == 'nowhere' else 'no public member of ' + parts[0]}"
                                       f" (PublicAPI)"))
    return out


# ---------------------------------------------------------------- versions


def release_tuple(text):
    """0.2.0-dev.30.3 -> (30, 3, 0); None for a release this cannot order."""
    m = re.search(r"-dev\.(\d+(?:\.\d+)*)$", text)
    if not m:
        return None
    nums = [int(x) for x in m.group(1).split(".")][:3]
    return tuple(nums + [0] * (3 - len(nums)))


def api_tuple(text):
    m = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", text.strip())
    return tuple(int(x) for x in m.groups()) if m else None


class Ledger:
    """managed/api-history.tsv, by (type key, member) -> (first release tuple, mod API tuple); names of the record that
    the ledger has no row for are the upcoming release's."""

    def __init__(self, root, model):
        self.first = {}
        self.exact = {}
        self.releases = []                                   # (release tuple, mod API tuple, release text)
        path = Path(root) / ai.LEDGER
        rows = {}
        if not path.is_file():
            raise ai.Problem(f"no {ai.LEDGER}: the since ledger (tools/release/api-gate.sh history --seed)")
        for n, l in enumerate(path.read_text(encoding="utf-8").split("\n")):
            c = l.split("\t")
            if n == 0 or len(c) != 5:
                continue
            kind, project, entry, release, mod_api = c
            if kind != "managed":
                continue
            rows[(project, entry)] = (release, mod_api)
            r = release_tuple(release.lstrip("<="))
            a = api_tuple(mod_api.lstrip("<="))
            if r and a and not release.startswith("<="):
                self.releases.append((r, a, release))
            e = ai.parse_api_line(entry)
            if e.kind in ("marker", "forward") or (e.kind != "type" and not e.name):
                continue
            key = self.key(e)
            if key is None or not r or not a:
                continue
            inexact = release.startswith("<=")
            cur = self.first.get(key)
            # the earliest row of any overload wins; when that is a `<=` row the name may be older than it says, so the
            # claim cannot be judged (a newer overload of an old name must not make it look new)
            if cur is None or (r, a) < cur[:2] or ((r, a) == cur[:2] and cur[2] and not inexact):
                self.first[key] = (r, a, inexact)
        self.last_release = max((r for r, _, _ in self.releases), default=(0, 0, 0))
        self.upcoming = api_tuple(model.api_version)
        self.shipped_lines = set(rows)
        for state, asm, line, e in model.entries:
            if state != "unshipped" or e.kind in ("marker", "forward") or (e.kind != "type" and not e.name):
                continue
            key = self.key(e)
            if key is not None and key not in self.first:
                self.first[key] = ((10 ** 6, 0, 0), self.upcoming, False)   # after every release the ledger knows

    @staticmethod
    def key(e):
        if e.kind == "type":
            k = tkey(e.type)
            parent, _, name = k.rpartition(".")
            return (parent, name) if parent else (k, "")
        name = re.sub(r"<.*$", "", e.name)
        if not name or name.startswith("<") or name.startswith("operator") or name.startswith("this["):
            return None
        return (tkey(e.type), name)

    def since(self, model, type_simple, member):
        """The (release, mod API, inexact) of a `Type.Member` claim: the earliest over the types of that simple name, so
        an ambiguous name never fails a claim that is true for one of them."""
        best = None
        for k in model.simple.get(type_simple, ()):
            v = self.first.get((k, member))
            if v is not None and (best is None or v[:2] < best[:2]):
                best = v
        return best


def fmt(t):
    """(30, 3, 0) -> 30.3, (29, 0, 0) -> 29."""
    nums = list(t)
    while len(nums) > 1 and nums[-1] == 0:
        nums.pop()
    return ".".join(str(x) for x in nums)


MARK_RE = re.compile(r"(?:since\s+)?0\.2\.0-dev\.(\d+)(?:\.(\d+))?|\bdev\.(\d+)(?:\.(\d+))?\+|\bmod API (0\.2\.\d+)")


def marks(line):
    """[(position, ('build', tuple) | ('api', tuple))] of the version claims of one line."""
    out = []
    for m in MARK_RE.finditer(line):
        if m.group(5):
            out.append((m.start(), ("api", api_tuple(m.group(5)))))
        else:
            n = int(m.group(1) or m.group(3))
            k = int(m.group(2) or m.group(4) or 0)
            out.append((m.start(), ("build", (n, k, 0))))
    return out


def segments(line):
    """The parts of a line a version mark can reach: the cells of a table row, else the whole line."""
    return line.split("|") if line.lstrip().startswith("|") else [line]


def check_versions(root, model, files):
    ledger = Ledger(root, model)
    out = []
    for f in files:
        for n, full in enumerate(f.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            if not marks(full):
                continue
            for line in segments(full):
                ms = marks(line)
                if not ms:
                    continue
                for s in re.finditer(r"`([^`\n]+)`", line):
                    # a mark names the builds of the backticked names after it ("dev.30+ `A`") or before it ("`A` (dev.30+)"):
                    # the claim is wrong only when every mark that could be meant is wrong
                    before = [m for m in ms if m[0] < s.start()][-1:]
                    after = [m for m in ms if m[0] >= s.end()][:1]
                    for parts in chains(s.group(1)):
                        if parts[0] in SKIP_TYPES or parts[0] not in model.simple:
                            continue
                        v = ledger.since(model, parts[0], parts[1])
                        if v is None or v[2]:
                            continue
                        first_release, first_api, _ = v
                        upcoming = first_release[0] >= 10 ** 6
                        wrong = []
                        for _, (kind, claim) in before + after:
                            if kind == "build":
                                bad = claim <= ledger.last_release if upcoming else claim < first_release
                                label = f"dev.{fmt(claim)}"
                            else:
                                bad = claim < first_api
                                label = "mod API " + ".".join(str(x) for x in claim)
                            wrong.append(label if bad else None)
                        if wrong and all(wrong):
                            first = (f"a release after 0.2.0-dev.{fmt(ledger.last_release)}" if upcoming
                                     else f"0.2.0-dev.{fmt(first_release)} (mod API {'.'.join(str(x) for x in first_api)})")
                            name = ".".join(parts[:2])
                            out.append(Finding("versions", rel(root, f), n, name,
                                               f"`{name}` is claimed from {' / '.join(wrong)}, but first appears in {first}"))
    return out


# ---------------------------------------------------------------- facts


def table_rows(text):
    for n, line in enumerate(text.splitlines(), 1):
        if line.startswith("|") and not re.match(r"^\|[\s:|-]+\|?\s*$", line):
            yield n, [c.strip() for c in line.strip().strip("|").split("|")]


def versions_rows(text):
    """[(line, build cell, mod API, MDK cell)] of references/versions.md's table."""
    out = []
    for n, c in table_rows(text):
        if len(c) >= 3 and re.fullmatch(r"0\.2\.\d+", c[1]):
            out.append((n, c[0], c[1], c[2]))
    return out


FACT_FILES = ("SKILL.md", "references/setup.md")


def fact_files(root):
    skill = Path(root) / SKILL
    files = [skill / n for n in FACT_FILES] + sorted((skill / "references").glob("api*.md"))
    return [f for f in files if f.is_file()]


def check_facts(root, model):
    root = Path(root)
    skill = root / SKILL
    out = []
    cur_api = model.api_version
    vp = skill / "references" / "versions.md"
    vtext = vp.read_text(encoding="utf-8") if vp.is_file() else ""
    rows = versions_rows(vtext)
    vrel = rel(root, vp)
    cur_rows = [r for r in rows if r[2] == cur_api]
    if not cur_rows:
        out.append(Finding("facts", vrel, 1, cur_api, f"versions.md has no row for the current mod API {cur_api} "
                           f"(Kiln.Mods.ApiVersion): add the build that carries it"))
        mdk, builds = None, set()
    else:
        builds = {b for r in cur_rows for b in re.findall(r"dev\.\d+(?:\.\d+)*", r[1])}
        m = re.search(r"dev\.\d+(?:\.\d+)*", cur_rows[-1][3])
        mdk = m.group(0) if m else None
    have = {r[2] for r in rows}
    ledger = root / ai.LEDGER
    if ledger.is_file() and rows:
        low = min(api_tuple(r[2]) for r in rows)             # the table starts at dev.27: older APIs have no rows
        apis = set()
        for l in ledger.read_text(encoding="utf-8").split("\n")[1:]:
            c = l.split("\t")
            if len(c) == 5 and c[0] == "managed" and not c[4].startswith("<=") and api_tuple(c[4]) and api_tuple(c[4]) >= low:
                apis.add(c[4])
        for a in sorted(apis - have, key=api_tuple):
            out.append(Finding("facts", vrel, 1, a, f"versions.md has no row for mod API {a}, which managed/api-history.tsv "
                               f"records as shipped (the table has a row for each mod API from {'.'.join(str(x) for x in low)} on)"))
    for f in fact_files(root):
        frel = rel(root, f)
        for n, line in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
            for m in re.finditer(r"Kiln\.(?:Sdk|Api|Api\.Ref|TestKit|Templates)/(0\.2\.0-dev\.[\d.]+)", line):
                if mdk and m.group(1) != "0.2.0-" + mdk:
                    out.append(Finding("facts", frel, n, m.group(1), f"pins the SDK {m.group(1)}, the current one is 0.2.0-{mdk}"))
            if re.search(r"this release|current (?:build|release|MDK)|latest", line, re.I):
                for m in re.finditer(r"0\.2\.0-dev\.[\d.]*\d", line):
                    if mdk and m.group(0) != "0.2.0-" + mdk:
                        out.append(Finding("facts", frel, n, m.group(0), f"names {m.group(0)} as the current build, it is 0.2.0-{mdk}"))
                for m in re.finditer(r"mod API (0\.2\.\d+)", line):
                    if m.group(1) != cur_api:
                        out.append(Finding("facts", frel, n, m.group(1), f"names mod API {m.group(1)} as the current one, it is {cur_api}"))
            for m in re.finditer(r"\((0\.2\.\d+) (?:on|for) (dev\.\d+(?:\.\d+)*)\)", line):
                if m.group(1) != cur_api or (builds and m.group(2) not in builds):
                    out.append(Finding("facts", frel, n, m.group(0), f"{m.group(0)}: the current mod API is {cur_api}"
                                       f"{' (' + ', '.join(sorted(builds)) + ')' if builds else ''}"))
    return out


# ---------------------------------------------------------------- map


def slug(h):
    h = h.strip().lower()
    h = re.sub(r"[`*_~]", "", h)
    h = re.sub(r"[^\w\- ]", "", h)
    return h.replace(" ", "-")


def anchors(path):
    return {slug(m.group(1)) for m in re.finditer(r"^#+\s+(.*)$", path.read_text(encoding="utf-8", errors="replace"), re.M)}


def wiki_dir(arg):
    p = Path(arg or os.environ.get("KILN_WIKI_DIR") or Path.home() / "Work" / "frostdev.io" / "src" / "content" / "kiln-wiki")
    return p if p.is_dir() else None


def check_map(root, wiki):
    root = Path(root)
    skill = root / SKILL
    out = []
    pages = {p.stem for p in wiki.glob("*.md")} if wiki else None
    for f in sorted(skill.rglob("*.md")):
        if "api-index" in f.parts:
            continue
        frel = rel(root, f)
        in_fence = False
        for n, ln in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
            if ln.lstrip().startswith("```"):
                in_fence = not in_fence
            if in_fence:
                continue
            for m in re.finditer(r"\]\(([^)\s]+)\)", ln):
                u = m.group(1)
                if u.startswith("http"):
                    mm = re.match(r"https://frostdev\.io/kiln/wiki/([\w-]+)", u)
                    if mm and pages is not None and mm.group(1) not in pages:
                        out.append(Finding("map", frel, n, mm.group(1), f"wiki page {mm.group(1)!r} does not exist"))
                    continue
                if u.startswith("mailto:"):
                    continue
                path, _, anc = u.partition("#")
                tgt = (f.parent / path) if path else f
                if not tgt.exists():
                    out.append(Finding("map", frel, n, u, f"link target {u} is missing"))
                elif anc and tgt.suffix == ".md" and anc not in anchors(tgt):
                    out.append(Finding("map", frel, n, u, f"anchor #{anc} is not in {tgt.name}"))
            for m in re.finditer(r"`(docs/kiln/[\w./-]+\.md)`", ln):
                if not (root / m.group(1)).exists():
                    out.append(Finding("map", frel, n, m.group(1), f"{m.group(1)} does not exist"))
    samples = {p.name for p in (root / "mods" / "samples").iterdir()} if (root / "mods" / "samples").is_dir() else None
    dm = skill / "references" / "docs-map.md"
    if not dm.is_file():
        return out + [Finding("map", rel(root, dm), 1, "docs-map.md", "references/docs-map.md is missing")]
    drel = rel(root, dm)
    repo_mode = False
    for n, ln in enumerate(dm.read_text(encoding="utf-8").splitlines(), 1):
        if "pages in the repository" in ln:
            repo_mode = True
        if ln.startswith("|") and not re.match(r"^\|[\s:|-]+\|?\s*$", ln) and not ln.startswith("| Question"):
            cell = ln.split("|")[-2]
            for m in re.finditer(r"`([^`]+)`", cell):
                tok = m.group(1).split(" ")[0]
                wiki_name = cell[:m.start()].rstrip().lower().endswith("wiki")      # "...; wiki `debugging`"
                if (repo_mode or tok.endswith(".md")) and not wiki_name:
                    if not (root / "docs" / "kiln" / tok).is_file():
                        elsewhere = [str(c.relative_to(root)) for c in (root / "docs" / "kiln" / "services" / tok,
                                                                          root / "docs" / "kiln" / "modding" / tok) if c.is_file()]
                        out.append(Finding("map", drel, n, tok, f"repository page {tok} is not at docs/kiln/{tok}"
                                           + (f" (it is at {elsewhere[0]})" if elsewhere else "")))
                elif pages is not None and tok not in pages:
                    out.append(Finding("map", drel, n, tok, f"wiki page {tok!r} does not exist"))
        if samples is not None and re.search(r"samples?:", ln):
            body = ln.split(":", 1)[1]
            for s in re.split(r",\s*|\s+and\s+", body):
                s = re.sub(r"\s*\(advanced\)$", "", re.sub(r"^(dev\.[\d.]+\+\s*)", "", s.strip().rstrip("."))).strip()
                if s and " " not in s and not s.startswith(("dev.", "[")) and s not in samples:
                    out.append(Finding("map", drel, n, s, f"sample {s!r} is not in mods/samples"))
    if samples is not None:
        for f in sorted(skill.rglob("*.md")):
            if "api-index" in f.parts:
                continue
            for n, ln in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
                for m in re.finditer(r"[Ss]ample `?([A-Z][\w-]+)`?", ln):
                    if m.group(1) not in samples and m.group(1) != "Kiln":
                        out.append(Finding("map", rel(root, f), n, m.group(1), f"sample {m.group(1)!r} is not in mods/samples"))
    return out


# ---------------------------------------------------------------- the allow-list


# ---------------------------------------------------------------- stale


def stale_rules(root):
    """([(line, compiled pattern, pattern text, glob, what is true)], [errors]) of tools/docs-site/drift-stale.txt."""
    rules, errors = [], []
    p = Path(root) / STALE
    if not p.is_file():
        return rules, errors
    for n, line in enumerate(p.read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        c = [x.strip() for x in line.split("\t")]
        if len(c) != 3 or not all(c):
            errors.append(f"{STALE}:{n}: three tab-separated cells expected: pattern, file glob, what is true instead")
            continue
        try:
            rules.append((n, re.compile(c[0]), c[0], c[1], c[2]))
        except re.error as e:
            errors.append(f"{STALE}:{n}: the pattern does not compile ({e})")
    return rules, errors


def check_stale(root, files):
    rules, errors = stale_rules(root)
    out = [Finding("stale", STALE, 0, "", e) for e in errors]
    for f in files:
        path = rel(root, f)
        for n, line in enumerate(f.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            for _, rx, text, glob, true in rules:
                if fnmatch.fnmatch(path, glob):
                    m = rx.search(line)
                    if m:
                        out.append(Finding("stale", path, n, text, f"says `{m.group(0)}`: {true}"))
    return out


class Allow:
    def __init__(self, path):
        self.rules, self.errors, self.used = [], [], set()
        self.path = path
        if not path or not Path(path).is_file():
            return
        for n, line in enumerate(Path(path).read_text(encoding="utf-8").splitlines(), 1):
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            c = [x.strip() for x in line.split("\t")]
            if len(c) != 4 or not all(c):
                self.errors.append(f"{path}:{n}: four tab-separated cells expected: check, name, file glob, reason (a reason is required)")
            elif c[0] not in CHECKS:
                self.errors.append(f"{path}:{n}: unknown check {c[0]!r} (one of {', '.join(CHECKS)})")
            else:
                self.rules.append((n, *c))

    def allowed(self, f):
        hit = False
        for n, check, name, glob, _ in self.rules:
            if check == f.check and name == f.name and fnmatch.fnmatch(f.path, glob):
                self.used.add(n)
                hit = True
        return hit

    def stale(self, checks):
        return [f"{self.path}:{n}: {check} {name} {glob}: matches nothing any more (remove the entry)"
                for n, check, name, glob, _ in self.rules if n not in self.used and check in checks]


# ---------------------------------------------------------------- main


def run(root, checks, warn=(), allow_file=None, wiki=None, out=None):
    """-> (exit code, {check: findings})."""
    out = out or sys.stdout
    root = Path(root).resolve()
    allow = Allow(allow_file if allow_file is not None else root / ALLOW)
    model = Model(root)
    findings = {}
    files = doc_files(root)
    for c in checks:
        if c == "names":
            findings[c] = check_names(root, model, files)
        elif c == "versions":
            findings[c] = check_versions(root, model, files)
        elif c == "facts":
            findings[c] = check_facts(root, model)
        elif c == "map":
            findings[c] = check_map(root, wiki_dir(wiki))
        elif c == "stale":
            findings[c] = check_stale(root, files)
    failed = False
    for e in allow.errors:
        print(f"drift: {e}", file=out)
        failed = True
    for c in checks:
        keep = [f for f in findings[c] if not allow.allowed(f)]
        findings[c] = keep
    for line in allow.stale(set(checks)):
        print(f"drift: {line}", file=out)
        failed = True
    for c in checks:
        keep = findings[c]
        state = "OK" if not keep else ("WARN" if c in warn else "FAILED")
        if c == "map" and wiki_dir(wiki) is None:
            state += " (no wiki folder: wiki page names not checked)"
        print(f"drift: {c:<8} {state}  {len(keep)} finding{'s' if len(keep) != 1 else ''}", file=out)
        for f in keep:
            print(f"  {f}", file=out)
        if keep and c not in warn:
            failed = True
    return (1 if failed else 0), findings


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("checks", nargs="*", help="names, versions, facts, map, stale or all (default: all)")
    ap.add_argument("--tree", default=str(HERE.parent.parent), help="the repository (default: this script's)")
    ap.add_argument("--warn", default="", help="checks that report without failing, comma separated")
    ap.add_argument("--allow", help=f"the allow-list (default: <tree>/{ALLOW})")
    ap.add_argument("--wiki", help="the wiki folder (default: $KILN_WIKI_DIR or ~/Work/frostdev.io/src/content/kiln-wiki)")
    a = ap.parse_args(argv)
    checks = [c for c in (a.checks or ["all"])]
    if "all" in checks:
        checks = list(CHECKS)
    bad = [c for c in checks + [w for w in a.warn.split(",") if w] if c not in CHECKS]
    if bad:
        print(f"drift: unknown check {bad[0]!r} (one of {', '.join(CHECKS)}, all)", file=sys.stderr)
        return 2
    try:
        code, _ = run(a.tree, checks, set(w for w in a.warn.split(",") if w), a.allow, a.wiki)
    except (ai.Problem, OSError) as e:
        print(f"drift: {e}", file=sys.stderr)
        return 2
    return code


if __name__ == "__main__":
    sys.exit(main())
