#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Writes tools/docs-site/services.json, the facts behind the API reference's service pages, from the repository.

  python3 -I tools/docs-site/gen-services.py [--check] [<repository>]      (default: the repository this file is in)

One entry per assembly of the reference (Kiln.Api, Kiln.Game, Kiln.Testing, Kiln.TestKit and every managed/services/*):
  assembly      Kiln.Build
  name          its KilnServices name (`<KilnServices>Build</KilnServices>` in a mod's project), null for Kiln.Api and Kiln.TestKit
  summary       one sentence, from the first comment of the assembly's .csproj; kept as it stands in services.json, so it can be
                reworded by hand (a new assembly gets the one from its project file)
  start         optional, written by hand and kept: the type names a modder starts with (services.py lists them first on the
                service page, "Start with"); not written for an assembly without one
  guides        repository paths of its guide pages: docs/kiln/services/<name>.md and docs/kiln/modding/<name>.md when they exist,
                and the ones GUIDES below names
  capabilities  [{name, members}]: the capabilities its members need ([RequiresCapability] in its sources, a constant resolved
                to its string), with how many members name each
  samples       the folders of mods/samples whose project uses the service (<KilnServices>, or a <ProjectReference> to it)
tools/docs-site/services.py reads the file when it builds the site's service pages. --check prints what differs from the checked-in
file and exits 1 (test_services.py runs it, so a new service or sample cannot go in without regenerating).
Only git-tracked files count (stage-guide.py's list; KILN_DOCS_TRACKED names the files of an export). Standard library only.
"""
import argparse
import importlib.util
import json
from pathlib import Path
import re
import sys

HERE = Path(__file__).resolve().parent
CORE = [("Kiln.Api", None, "managed/Kiln.Api"), ("Kiln.Game", "Game", "managed/Kiln.Game"),
        ("Kiln.Testing", "Testing", "managed/Kiln.Testing"), ("Kiln.TestKit", None, "managed/Kiln.TestKit")]
# guide pages that are not named like the service: assembly -> repository paths
GUIDES = {
    "Kiln.Api": ["docs/kiln/modding/contract.md", "docs/kiln/modding/lifecycle.md", "docs/kiln/modding/README.md"],
    "Kiln.Blocks": ["docs/kiln/modding/blocks.md", "docs/kiln/modding/npc-dialogue.md", "docs/kiln/services/npc-speech.md"],
    "Kiln.Client": ["docs/kiln/modding/client-hooks.md", "docs/kiln/services/client-building-input.md",
                    "docs/kiln/services/client-crosshair.md"],
    "Kiln.Content": ["docs/kiln/modding/gear-rules.md"],
    "Kiln.CreatureControl": ["docs/kiln/services/creature-control.md"],
    "Kiln.Eml": ["docs/kiln/services/eml-archive.md", "docs/kiln/services/eml-startup.md", "docs/kiln/services/eml-values.md"],
    "Kiln.Game": ["docs/kiln/typegen.md"],
    "Kiln.Items": ["docs/kiln/modding/item-costs.md", "docs/kiln/services/item-costs.md"],
    "Kiln.Network": ["docs/kiln/services/net-steam-messages.md"],
    "Kiln.PlayerAnim": ["docs/kiln/services/playeranim.md"],
    "Kiln.Sigs": ["docs/kiln/services/hooks.md"],
    "Kiln.Terrain": ["docs/kiln/modding/terrain-tiles.md"],
    "Kiln.Testing": ["docs/kiln/modding/testing.md"],
    "Kiln.TestKit": ["docs/kiln/modding/unit-tests.md"],
}
ATTRIBUTE = re.compile(r"\bRequiresCapability\s*\(\s*([^()]*?)\s*\)")
CONSTANT = re.compile(r"\bconst\s+string\s+(\w+)\s*=\s*(\"(?:\\.|[^\"\\])*\")")
COMMENT = re.compile(r"<!--(.*?)-->", re.S)


def stage_guide():
    spec = importlib.util.spec_from_file_location("stage_guide", HERE / "stage-guide.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def sentence(text):
    """The first sentence of a project file comment, the way a service page opens: no repository paths up front."""
    text = " ".join(text.split())
    m = re.match(r"^(.*?[.!?])(?:\s|$)", text)
    s = (m.group(1) if m else text).strip()
    if s.endswith("."):
        s = s[:-1]
    return s


# the ways a project file's comment starts: "Managed face of native/services/build (kiln.build): voxels ...", "kiln.items: the
# managed face of native/services/items (give / take ...).", "Kiln.Queries: read-only questions ..."
OPENINGS = (
    re.compile(r"^(?:the\s+)?managed\s+face\s+of\s+(?:the\s+)?(?:in-core\s+)?(?:service\s+)?\S+(?:\s+\([^)]*\))?:\s*", re.I),
    re.compile(r"^kiln\.\w+(?:'s managed face)?:\s+(?:the\s+managed\s+face\s+of\s+\S+\s+\(([^)]*)\)\.?$)?", re.I),
    re.compile(r"^kiln\.\w+,\s+managed\s+face:\s*", re.I),
)


def summary_of(project_text):
    m = COMMENT.search(project_text)
    if not m:
        return ""
    s = sentence(m.group(1))
    for rx in OPENINGS:
        r = rx.match(s)
        if r:
            s = (r.group(1) if r.groups() and r.group(1) else s[r.end():]).strip() or s
            break
    return s[:1].upper() + s[1:]


def assemblies(repo, tracked):
    """[(assembly, KilnServices name, source folder)] of the reference."""
    out = list(CORE)
    for p in sorted(tracked):
        rel = p.relative_to(repo).parts
        if len(rel) == 4 and rel[:2] == ("managed", "services") and rel[3].endswith(".csproj") and rel[3][:-7] == rel[2]:
            out.append((rel[2], rel[2][len("Kiln."):], "/".join(rel[:3])))
    return [a for a in out if any(q.is_relative_to(repo / a[2]) for q in tracked)]


def capabilities(repo, tracked, folder, constants):
    rows = {}
    for p in sorted(tracked):
        if p.suffix == ".cs" and p.is_relative_to(repo / folder) and not {"obj", "bin", "tests"} & set(p.parts):
            text = p.read_text(encoding="utf-8", errors="replace")
            for m in ATTRIBUTE.finditer(text):
                arg = m.group(1).strip()
                if arg.startswith('"') and arg.endswith('"'):
                    name = json.loads(arg)
                else:
                    name = constants.get(arg.rsplit(".", 1)[-1])
                if name:
                    rows[name] = rows.get(name, 0) + 1
    return [{"name": n, "members": c} for n, c in sorted(rows.items())]


def generate(repo, previous=None):
    repo = Path(repo).resolve()
    sg = stage_guide()
    tracked = sorted(sg.tracked_files(repo))
    names = {str(p.relative_to(repo)) for p in tracked}
    from_samples = load_samples().sample_services(repo)
    constants = {}
    for p in tracked:
        if p.suffix == ".cs" and p.is_relative_to(repo / "managed") and not {"obj", "bin", "tests"} & set(p.parts):
            for m in CONSTANT.finditer(p.read_text(encoding="utf-8", errors="replace")):
                try:
                    constants.setdefault(m.group(1), json.loads(m.group(2)))
                except ValueError:
                    pass
    known = {s["assembly"]: s for s in (previous or {}).get("services", [])}
    services = []
    for assembly, name, folder in assemblies(repo, tracked):
        project = next((p for p in tracked if p.parent == repo / folder and p.suffix == ".csproj"), None)
        text = project.read_text(encoding="utf-8", errors="replace") if project else ""
        guides = [g for g in ([f"docs/kiln/services/{name.lower()}.md", f"docs/kiln/modding/{name.lower()}.md"] if name else [])
                  + GUIDES.get(assembly, []) if g in names]
        samples = sorted(s for s, used in from_samples.items() if name and name in used)
        services.append({
            "assembly": assembly,
            "name": name,
            "summary": known.get(assembly, {}).get("summary") or summary_of(text),
            **({"start": known[assembly]["start"]} if known.get(assembly, {}).get("start") else {}),
            "guides": list(dict.fromkeys(guides)),
            "capabilities": capabilities(repo, tracked, folder, constants),
            "samples": samples,
        })
    return {"format": 1, "services": services}


def load_samples():
    spec = importlib.util.spec_from_file_location("samples", HERE / "samples.py")
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def render(doc):
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("repo", nargs="?", default=str(HERE.parent.parent))
    ap.add_argument("--check", action="store_true", help="exit 1 if services.json is not what the repository gives")
    a = ap.parse_args()
    target = Path(a.repo).resolve() / "tools/docs-site/services.json"
    previous = json.loads(target.read_text(encoding="utf-8")) if target.is_file() else None
    text = render(generate(a.repo, previous))
    if a.check:
        if not target.is_file() or target.read_text(encoding="utf-8") != text:
            sys.exit(f"{target} is out of date: run python3 -I tools/docs-site/gen-services.py")
        return
    target.write_text(text, encoding="utf-8")
    print(f"{target}: {len(json.loads(text)['services'])} services")


if __name__ == "__main__":
    main()
