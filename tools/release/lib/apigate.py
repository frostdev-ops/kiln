#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The release's API gate: the public API record (managed/**/PublicAPI.Shipped.txt + PublicAPI.Unshipped.txt, kept by
Microsoft.CodeAnalysis.PublicApiAnalyzers, see managed/Directory.Build.props) and the mod API version
(Kiln.Mods.ApiVersion in managed/Kiln.Api/Mods.cs). Called by tools/release/api-gate.sh; release.sh calls that.

  apigate.py [--repo DIR] removed OLD [NEW|--worktree]      exit 1 when a shipped line is gone (each one named)
  apigate.py [--repo DIR] pending [REV|--worktree]          the Unshipped.txt files that hold lines, with counts
  apigate.py [--repo DIR] version-rule OLD [NEW|--worktree] exit 1 when new API is pending and the API version is
                                                            still the one at OLD
  apigate.py [--repo DIR] promote [--if-same-as REV]        moves Unshipped.txt lines into Shipped.txt (working tree)
  apigate.py [--repo DIR] api-version [REV|--worktree]      prints the API version in Mods.cs
  apigate.py [--repo DIR] bump-api [VERSION] [--major]      raises the API version in its three places (working tree)

REV/OLD/NEW are git revisions (default NEW and REV: HEAD); --worktree reads the working tree instead (tracked and
untracked files that git does not ignore, as release.sh --worktree exports them). --repo defaults to the repository
this file is in.

What counts as shipped at a revision: the lines of its Shipped.txt, plus the additions in its Unshipped.txt, minus its
`*REMOVED*<line>` entries. A release is tagged at the commit it was built from and promote's moved lines are committed
after it, so at a tag the lines that release added are still in Unshipped.txt: they shipped all the same.

`removed`: every line shipped at OLD that is not shipped at NEW, for every project that has a Shipped.txt at OLD. Not a
removal: `#nullable enable`, a line with an experimental prefix (`[KILNX001]...`: not part of the promise), and a
project with no Shipped.txt at OLD (not gated yet, whatever is there now: the first release with the gate). A line
that is gone because its member became experimental (the same text now shipped with a `[KILNX...]` prefix) is listed as
allowed. A Shipped.txt that existed at OLD and is deleted at NEW is a removal of all its lines.

`version-rule`: new API means additions in an Unshipped.txt at NEW that are not experimental. Then the API version at
NEW must be higher than at OLD (`bump-api` raises it). An OLD whose Mods.cs cannot be read: a warning, no refusal.

`promote`: per project, additions go into Shipped.txt in ordinal order (UTF-16 code units, as the analyzer's code fix
sorts; `#nullable enable` stays first), `*REMOVED*<line>` deletes <line> from Shipped.txt when `removed` would allow it
(the line is experimental, or it is shipped again with an experimental prefix) and refuses otherwise, and Unshipped.txt
keeps `#nullable enable` only. Nothing is written unless every project passes; a second run changes nothing.
--if-same-as REV: only when every PublicAPI.*.txt of the working tree is the one at REV (else exit 3, nothing written).

`bump-api`: VERSION (X.Y.Z) default the next minor; it must be higher than the current one and keep the major unless
--major is given. It writes managed/Kiln.Api/Mods.cs (the const's text), managed/Kiln.Sdk/Sdk/Kiln.Version.props
(KilnModApiVersion: major.minor, or the full version when the patch is not 0) and crates/kiln-manage/src/profile.rs
(KILN_API), after checking that the three agree, and prints the doc lines that name the old version.

Exit codes: 0 fine, 1 refused (the reasons are printed), 2 usage or a revision/file that cannot be read, 3 promote
skipped by --if-same-as.
"""
import argparse
import os
import re
import subprocess
import sys

SHIPPED = "PublicAPI.Shipped.txt"
UNSHIPPED = "PublicAPI.Unshipped.txt"
NULLABLE = "#nullable enable"
REMOVED = "*REMOVED*"
EXPERIMENTAL = re.compile(r"^\[KILNX[0-9A-Za-z_]*\]")
MODS_CS = "managed/Kiln.Api/Mods.cs"
VERSION_PROPS = "managed/Kiln.Sdk/Sdk/Kiln.Version.props"
PROFILE_RS = "crates/kiln-manage/src/profile.rs"
# the const's text, as tools/package/stage.sh and crates/kiln-manage/tests/profiles.rs read it
API_RE = re.compile(r'ApiVersion = "([^"]+)"')
CONST_RE = re.compile(r'(public const string ApiVersion = ")([^"]*)(")')
PROPS_RE = re.compile(r"(<KilnModApiVersion>)([^<]*)(</KilnModApiVersion>)")
PROFILE_RE = re.compile(r'(pub const KILN_API: &str = ")([^"]*)(")')
DOC_HINT = re.compile(r"(?i)apiversion|kiln api|mod api|kiln\.api\b|api version|KILN_API")
SKIP_DOCS = ("docs/kiln/release-notes/",)
SHOW_MAX = 40


class Fail(Exception):
    """Exit 2: a revision or a file that cannot be read."""


def git(repo, *args, binary=False):
    r = subprocess.run(["git", "-C", repo, *args], capture_output=True)
    if r.returncode != 0:
        raise Fail(f"git {' '.join(args)}: {r.stderr.decode(errors='replace').strip()}")
    return r.stdout if binary else r.stdout.decode("utf-8")


class GitTree:
    def __init__(self, repo, rev):
        self.repo = repo
        self.rev = git(repo, "rev-parse", "--verify", "--quiet", f"{rev}^{{commit}}").strip()
        self.label = rev
        self._files = None

    def files(self):
        if self._files is None:
            out = git(self.repo, "ls-tree", "-r", "-z", "--name-only", self.rev, binary=True)
            self._files = sorted(p.decode("utf-8") for p in out.split(b"\0") if p)
            self._set = set(self._files)
        return self._files

    def read(self, path):
        self.files()
        if path not in self._set:
            return None
        return git(self.repo, "cat-file", "blob", f"{self.rev}:{path}", binary=True).decode("utf-8-sig")


class WorkTree:
    def __init__(self, repo):
        self.repo = repo
        self.label = "the working tree"
        self._files = None

    def files(self):
        if self._files is None:
            out = git(self.repo, "ls-files", "-z", "-co", "--exclude-standard", binary=True)
            self._files = sorted({p.decode("utf-8") for p in out.split(b"\0")
                                  if p and os.path.isfile(os.path.join(self.repo, p.decode("utf-8")))})
        return self._files

    def read(self, path):
        full = os.path.join(self.repo, path)
        if not os.path.isfile(full):
            return None
        with open(full, encoding="utf-8-sig", newline="") as f:
            return f.read()


def tree(repo, rev, worktree):
    if worktree:
        return WorkTree(repo)
    try:
        return GitTree(repo, rev)
    except Fail:
        raise Fail(f"not a commit in {repo}: {rev}")


def api_files(t, name):
    return [p for p in t.files() if p.startswith("managed/") and p.rsplit("/", 1)[-1] == name]


def lines(text):
    """The entries of a PublicAPI file: no blank lines, no #nullable enable."""
    return [l.rstrip("\r") for l in (text or "").split("\n") if l.strip() and l.rstrip("\r") != NULLABLE]


def project(path):
    return path.rsplit("/", 1)[0]


def shipped_at(t, proj):
    """The lines shipped at t in project proj (see the module doc), or None when it has no Shipped.txt there."""
    s = t.read(f"{proj}/{SHIPPED}")
    if s is None:
        return None
    have = set(lines(s))
    for l in lines(t.read(f"{proj}/{UNSHIPPED}")):
        if l.startswith(REMOVED):
            have.discard(l[len(REMOVED):])
        else:
            have.add(l)
    return have


def unprefixed(line):
    return EXPERIMENTAL.sub("", line, count=1)


def removals(old, new):
    """(problems, allowed, notes): problems and allowed are (file, text)."""
    problems, allowed, notes = [], [], []
    old_files = api_files(old, SHIPPED)
    if not old_files:
        notes.append(f"no {SHIPPED} at {old.label}: nothing to compare (the first release with the gate)")
    gated = {project(p) for p in old_files}
    for p in api_files(new, SHIPPED):
        if project(p) not in gated and old_files:
            notes.append(f"{project(p)}: not gated at {old.label} (no {SHIPPED} there): not compared")
    for p in old_files:
        proj = project(p)
        before = shipped_at(old, proj)
        after = shipped_at(new, proj)
        kept = sorted(l for l in before if not EXPERIMENTAL.match(l))
        if after is None:
            problems.append((p, f"deleted at {new.label}: a removal of all its {len(kept)} shipped lines"))
            problems.extend((p, f"removed: {l}") for l in kept)
            continue
        now_experimental = {unprefixed(l) for l in after if EXPERIMENTAL.match(l)}
        for l in kept:
            if l in after:
                continue
            if l in now_experimental:
                allowed.append((p, f"now experimental: {l}"))
            else:
                where = f"{proj}/{UNSHIPPED}" if f"{REMOVED}{l}" in lines(new.read(f"{proj}/{UNSHIPPED}")) else p
                problems.append((where, f"removed: {l}"))
    return problems, allowed, notes


def pending(t):
    """[(file, additions, experimental additions, removals)] for every Unshipped.txt holding lines."""
    out = []
    for p in api_files(t, UNSHIPPED):
        ls = lines(t.read(p))
        if not ls:
            continue
        rem = [l for l in ls if l.startswith(REMOVED)]
        exp = [l for l in ls if not l.startswith(REMOVED) and EXPERIMENTAL.match(l)]
        out.append((p, len(ls) - len(rem) - len(exp), len(exp), len(rem)))
    return out


def api_version(t):
    text = t.read(MODS_CS)
    m = API_RE.search(text) if text is not None else None
    return m.group(1) if m else None


def parse_version(v):
    m = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", v or "")
    return tuple(int(x) for x in m.groups()) if m else None


def ordinal(line):
    """Ordinal order of .NET strings: UTF-16 code units."""
    return line.encode("utf-16-be")


# ---------------------------------------------------------------- commands


def cmd_removed(a):
    old = tree(a.repo, a.old, False)
    new = tree(a.repo, a.new or "HEAD", a.worktree)
    problems, allowed, notes = removals(old, new)
    for n in notes:
        print(f"api-gate: {n}")
    for f, what in allowed:
        print(f"api-gate: {f}: allowed, {what}")
    if problems:
        print(f"api-gate: {len([x for x in problems if x[1].startswith('removed: ')])} shipped API line(s) removed since "
              f"{old.label} (the mod API is additive: keep the member, mark it [Obsolete]):", file=sys.stderr)
        for f, what in problems[:SHOW_MAX * 5]:
            print(f"  {f}: {what}", file=sys.stderr)
        if len(problems) > SHOW_MAX * 5:
            print(f"  ... {len(problems) - SHOW_MAX * 5} more", file=sys.stderr)
        return 1
    print(f"api-gate: no shipped API removed since {old.label} ({len(api_files(old, SHIPPED))} projects compared)")
    return 0


def cmd_pending(a):
    t = tree(a.repo, a.rev or "HEAD", a.worktree)
    rows = pending(t)
    if not rows:
        print(f"api-gate: no pending API at {t.label} (every {UNSHIPPED} holds only {NULLABLE})")
        return 0
    for p, add, exp, rem in rows:
        print(f"{p}: {add + exp + rem} lines ({add} added, {exp} experimental, {rem} *REMOVED*)")
    return 0


def cmd_api_version(a):
    t = tree(a.repo, a.rev or "HEAD", a.worktree)
    v = api_version(t)
    if v is None:
        raise Fail(f"no ApiVersion = \"...\" in {MODS_CS} at {t.label}")
    print(v)
    return 0


def cmd_version_rule(a):
    old = tree(a.repo, a.old, False)
    new = tree(a.repo, a.new or "HEAD", a.worktree)
    added = [(p, n) for p, n, _, _ in pending(new) if n]
    total = sum(n for _, n in added)
    if not added:
        print(f"api-gate: no new API pending at {new.label}: the API version may stay")
        return 0
    v_old, v_new = api_version(old), api_version(new)
    if v_new is None:
        raise Fail(f"no ApiVersion = \"...\" in {MODS_CS} at {new.label}")
    if v_old is None or parse_version(v_old) is None:
        print(f"api-gate: WARNING: cannot read the API version in {MODS_CS} at {old.label}: the API version rule is not "
              f"applied ({total} new API lines pending)", file=sys.stderr)
        return 0
    if parse_version(v_new) is None:
        raise Fail(f"the API version at {new.label} is not X.Y.Z: {v_new}")
    if parse_version(v_new) > parse_version(v_old):
        print(f"api-gate: {total} new API lines, API version {v_old} at {old.label} -> {v_new}: fine")
        return 0
    why = "is still" if v_new == v_old else "went down from " + v_old + " to"
    print(f"api-gate: {total} new API lines are pending, and the API version {why} {v_new} (at {old.label}: {v_old}):",
          file=sys.stderr)
    for p, n in added:
        print(f"  {p}: {n} added", file=sys.stderr)
    print("  Raise it so a mod can ask for the new API: tools/release/api-gate.sh bump-api <version> (default: the next\n"
          "  minor), fix the doc lines it prints, commit, and release again. If these lines were already released,\n"
          "  run tools/release/api-gate.sh promote and commit the moved lines instead.", file=sys.stderr)
    return 1


def api_record(t):
    return {p: t.read(p) for p in t.files() if p.startswith("managed/") and p.rsplit("/", 1)[-1] in (SHIPPED, UNSHIPPED)}


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def cmd_promote(a):
    wt = WorkTree(a.repo)
    if a.if_same_as:
        at = tree(a.repo, a.if_same_as, False)
        mine, theirs = api_record(wt), api_record(at)
        differ = sorted(p for p in set(mine) | set(theirs) if mine.get(p) != theirs.get(p))
        if differ:
            print(f"api-gate: promote skipped: the working tree's API files are not the ones at {a.if_same_as}:")
            for p in differ[:SHOW_MAX]:
                print(f"  {p}")
            print("  When the working tree holds the released API files, run: tools/release/api-gate.sh promote")
            return 3
    plans, problems = [], []
    for u in api_files(wt, UNSHIPPED):
        proj = project(u)
        s_path = f"{proj}/{SHIPPED}"
        u_text = wt.read(u)
        entries = lines(u_text)
        if not entries:
            continue
        s_text = wt.read(s_path)
        if s_text is None:
            problems.append(f"{u}: holds lines, but there is no {SHIPPED} next to it")
            continue
        shipped = lines(s_text)
        has = set(shipped)
        adds = [l for l in entries if not l.startswith(REMOVED)]
        after = has | set(adds)
        now_experimental = {unprefixed(l) for l in after if EXPERIMENTAL.match(l)}
        drop = set()
        for l in entries:
            if not l.startswith(REMOVED):
                continue
            x = l[len(REMOVED):]
            if x not in has:
                problems.append(f"{u}: {l}: not in {SHIPPED}")
            elif EXPERIMENTAL.match(x) or x in now_experimental:
                drop.add(x)
            else:
                problems.append(f"{u}: {l}: a shipped line may not be removed (the mod API is additive)")
        s_nullable = NULLABLE in s_text.replace("\r", "").split("\n")
        u_nullable = NULLABLE in (u_text or "").replace("\r", "").split("\n")
        if u_nullable and not s_nullable and shipped:
            problems.append(f"{s_path}: has no {NULLABLE}, {u} has: move the lines by hand")
            continue
        nl = "\r\n" if "\r\n" in s_text else "\n"
        body = sorted((has | set(adds)) - drop, key=ordinal)
        head = [NULLABLE] if (s_nullable or u_nullable) else []
        plans.append((proj, s_path, nl.join(head + body) + nl, u, NULLABLE + nl, len(set(adds) - has), len(drop)))
    if problems:
        print("api-gate: promote refused, nothing written:", file=sys.stderr)
        for p in problems:
            print(f"  {p}", file=sys.stderr)
        return 1
    if not plans:
        print("api-gate: promote: nothing pending")
        return 0
    for proj, s_path, s_new, u, u_new, n_add, n_drop in plans:
        write(os.path.join(a.repo, s_path), s_new)
        write(os.path.join(a.repo, u), u_new)
        print(f"api-gate: promote: {proj}: {n_add} added, {n_drop} removed")
    print("api-gate: promote: commit the moved lines (git add managed; git commit)")
    return 0


def doc_lines(repo, old):
    """Lines in docs/ and templates that name the API version (old X.Y.Z or X.Y as a whole token)."""
    maj_min = ".".join(old.split(".")[:2])
    rx = re.compile(r"(?<![\w.])(" + re.escape(old) + "|" + re.escape(maj_min) + r")(?![\w-]|\.\d)")
    out = []
    t = WorkTree(repo)
    for p in t.files():
        if not (p.startswith("docs/") or p.startswith("templates/") or p.startswith("managed/Kiln.Sdk/templates/")
                or p == "README.md") or p.startswith(SKIP_DOCS):
            continue
        if not p.endswith((".md", ".json", ".txt", ".csproj", ".props")):
            continue
        text = t.read(p) or ""
        for i, l in enumerate(text.split("\n"), 1):
            if rx.search(l) and DOC_HINT.search(l):
                out.append(f"{p}:{i}: {l.strip()}")
    return out


def cmd_bump_api(a):
    paths = {k: os.path.join(a.repo, k) for k in (MODS_CS, VERSION_PROPS, PROFILE_RS)}
    texts = {}
    for k, p in paths.items():
        if not os.path.isfile(p):
            raise Fail(f"bump-api: {k} is missing")
        with open(p, encoding="utf-8", newline="") as f:
            texts[k] = f.read()
    found = {}
    for k, rx in ((MODS_CS, CONST_RE), (VERSION_PROPS, PROPS_RE), (PROFILE_RS, PROFILE_RE)):
        ms = rx.findall(texts[k])
        if len(ms) != 1:
            raise Fail(f"bump-api: {k}: expected one {rx.pattern!r}, found {len(ms)}")
        found[k] = ms[0][1]
    cur = parse_version(found[MODS_CS])
    if cur is None:
        raise Fail(f"bump-api: the API version in {MODS_CS} is not X.Y.Z: {found[MODS_CS]}")

    def props_form(v):
        return f"{v[0]}.{v[1]}" if v[2] == 0 else f"{v[0]}.{v[1]}.{v[2]}"

    if found[PROFILE_RS] != found[MODS_CS] or found[VERSION_PROPS] != props_form(cur):
        print(f"api-gate: bump-api refused: the three places disagree: {MODS_CS} {found[MODS_CS]}, "
              f"{VERSION_PROPS} KilnModApiVersion {found[VERSION_PROPS]}, {PROFILE_RS} KILN_API {found[PROFILE_RS]}",
              file=sys.stderr)
        return 1
    if a.version:
        new = parse_version(a.version)
        if new is None:
            print(f"api-gate: bump-api: the API version is X.Y.Z (no pre-release part), not {a.version}", file=sys.stderr)
            return 2
    else:
        new = (cur[0] + 1, 0, 0) if a.major else (cur[0], cur[1] + 1, 0)
    text = "%d.%d.%d" % new
    if new <= cur:
        print(f"api-gate: bump-api refused: {text} is not higher than the current API version {found[MODS_CS]}",
              file=sys.stderr)
        return 1
    if new[0] != cur[0] and not a.major:
        print(f"api-gate: bump-api refused: {text} changes the major ({cur[0]} -> {new[0]}), which breaks every mod "
              f"that names API {cur[0]}.x: pass --major if that is meant", file=sys.stderr)
        return 1
    old = found[MODS_CS]
    texts[MODS_CS] = CONST_RE.sub(lambda m: m.group(1) + text + m.group(3), texts[MODS_CS])
    texts[VERSION_PROPS] = PROPS_RE.sub(lambda m: m.group(1) + props_form(new) + m.group(3), texts[VERSION_PROPS])
    texts[PROFILE_RS] = PROFILE_RE.sub(lambda m: m.group(1) + text + m.group(3), texts[PROFILE_RS])
    for k, p in paths.items():
        write(p, texts[k])
    print(f"api-gate: API version {old} -> {text}: {MODS_CS}, {VERSION_PROPS} (KilnModApiVersion {props_form(new)}), "
          f"{PROFILE_RS}")
    docs = doc_lines(a.repo, old)
    if docs:
        print(f"api-gate: lines that may name the old API version ({old}); fix the ones about Kiln's API by hand:")
        for d in docs:
            print(f"  {d}")
    return 0


def main(argv=None):
    p = argparse.ArgumentParser(prog="api-gate.sh", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--repo", default=os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "../../..")))
    sub = p.add_subparsers(dest="cmd", required=True)

    def side(s, name, default_help):
        s.add_argument(name, nargs="?", help=default_help)
        s.add_argument("--worktree", action="store_true", help="read the working tree")

    s = sub.add_parser("removed", help="shipped API lines removed since OLD")
    s.add_argument("old")
    side(s, "new", "default HEAD")
    s.set_defaults(fn=cmd_removed)
    s = sub.add_parser("pending", help="Unshipped.txt files that hold lines")
    side(s, "rev", "default HEAD")
    s.set_defaults(fn=cmd_pending)
    s = sub.add_parser("version-rule", help="new API needs a higher API version than at OLD")
    s.add_argument("old")
    side(s, "new", "default HEAD")
    s.set_defaults(fn=cmd_version_rule)
    s = sub.add_parser("promote", help="move Unshipped.txt lines into Shipped.txt in the working tree")
    s.add_argument("--if-same-as", metavar="REV")
    s.set_defaults(fn=cmd_promote)
    s = sub.add_parser("api-version", help="the API version in Mods.cs")
    side(s, "rev", "default HEAD")
    s.set_defaults(fn=cmd_api_version)
    s = sub.add_parser("bump-api", help="raise the API version (default: the next minor) in the working tree")
    s.add_argument("version", nargs="?")
    s.add_argument("--major", action="store_true", help="allow a new major")
    s.set_defaults(fn=cmd_bump_api)
    a = p.parse_args(argv)
    for name in ("new", "rev"):
        if getattr(a, "worktree", False) and getattr(a, name, None):
            p.error(f"give {name.upper()} or --worktree, not both")
    a.repo = os.path.abspath(a.repo)
    try:
        return a.fn(a)
    except Fail as e:
        print(f"api-gate: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
