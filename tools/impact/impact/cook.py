# SPDX-License-Identifier: MIT
"""impact/cook.json: a mod's Impact programs, made on the player's machine (the convention of
docs/kiln/modding/models.md: mods ship sources and a recipe, never game data).

    {
      "format": 1,
      "tool": "impact",
      "out": "../assets/cooked/impact",          // relative to this file; gitignored
      "manifest": "../assets/assets.json",        // the kiln.assets manifest that serves the outputs
      "natives": ["natives.json"],                // Kiln natives the programs call (the mod declares them)
      "game": { "side": "server", "madeOn": "1285601" },   // informational
      "inputs": {                                  // vanilla programs, read from the installed game
        "fireball_explosion": { "guid": "80a2448f-945c-45f6-8fce-9b09ed61620c", "type": "keen::ImpactProgram" }
      },
      "steps": [
        { "op": "assemble", "src": "heal_over_time.kimp", "stem": "heal_over_time" },
        { "op": "clone", "base": "fireball_explosion", "guid": "<new fixed GUID>", "stem": "chain_explosion",
          "edits": [ { "set": "$NoiseRange::value", "value": "40.0" }, ... ] }
      ]
    }

Every output is linted; a program with lint errors is not written. assets.json entries:
    {"kind": "resource", "guid": G, "type": "keen::ImpactProgram", "part": 0, "mode": "new", "file": "cooked/impact/x.bin"}
(mode "override" when a step's guid is a vanilla program: a boot-time replacement served by kiln.assets).
The report (<out>/cook-report.json) records the recipe's sha256, the game's version and exe, each input's sha256
and layout, each output's sha256, its lint result and the natives it needs; cook again when any of them changes.
"""
import hashlib
import json
from pathlib import Path

from . import asm, catalog, cookpaths as CP, lint as L, patch, program as P


class RecipeError(Exception):
    pass


OPS = ("assemble", "clone")


def sha256(b):
    return hashlib.sha256(b).hexdigest()


def out_dir(recipe_path, doc, default="../assets/cooked/impact"):
    """Where a recipe's cook writes: its "out", relative to the recipe. It is the mod's assets/cooked folder or a folder in it, by its spelling and
    with no link on the way (cookpaths, the same rule in every cook tool), or the recipe is refused. The cook asks this, not load."""
    try:
        return CP.out_dir(recipe_path, doc.get("out", default), default)
    except CP.Refused as e:
        raise RecipeError(f"{recipe_path}: {e}") from e


def load(path):
    p = Path(path)
    doc = json.loads(p.read_text())
    if doc.get("format") != 1:
        raise RecipeError(f"{p}: format {doc.get('format')!r}, expected 1")
    if doc.get("tool") != "impact":
        raise RecipeError(f"{p}: tool {doc.get('tool')!r} is not impact")
    for i, st in enumerate(doc.get("steps", [])):
        if st.get("op") not in OPS:
            raise RecipeError(f"step {i}: op {st.get('op')!r} (known: {', '.join(OPS)})")
        if not st.get("stem"):
            raise RecipeError(f"step {i}: stem missing")
    return doc


def _paths(p, doc):
    base = p.parent
    out = out_dir(p, doc)  # refused unless it is in the mod's assets/cooked: needs_cook and run both come through here
    try:
        man = CP.manifest(p, doc.get("manifest", "../assets/assets.json"))  # the mod's assets/assets.json and no other file
    except CP.Refused as e:
        raise RecipeError(f"{p}: {e}") from e
    return base, out, man


def _sources(base, doc):
    """Every file the recipe reads from the mod (sources, patch files, natives): their hashes go in the report."""
    files = [base / f for f in doc.get("natives", [])]
    for st in doc.get("steps", []):
        if st["op"] == "assemble":
            files.append(base / st["src"])
        for e in st.get("edits", []):
            for k in ("insert", "code"):
                v = e.get(k)
                if isinstance(v, dict) and "file" in v:
                    files.append(base / v["file"])
    return files


def needs_cook(path):
    p = Path(path)
    doc = load(p)
    base, out, _ = _paths(p, doc)
    rep = out / "cook-report.json"
    if not rep.exists():
        return True
    r = json.loads(rep.read_text())
    if not r.get("ok") or r.get("recipe_sha256") != sha256(p.read_bytes()):
        return True
    for f in _sources(base, doc):
        if r.get("sources", {}).get(str(f.relative_to(base))) != (sha256(f.read_bytes()) if f.exists() else None):
            return True
    return any(not (out / o["file"]).exists() or sha256((out / o["file"]).read_bytes()) != o["sha256"]
               for o in r.get("outputs", []))


def run(path, g, update_manifest=False):
    p = Path(path).resolve()
    doc = load(p)
    base, out, man = _paths(p, doc)
    cat = catalog.Catalog.default()
    for f in doc.get("natives", []):
        cat.add_natives_file(base / f, origin=f"mod:{f}")
    if cat.problems:
        raise RecipeError("native declarations: " + "; ".join(cat.problems))
    from kfc.archive import ResourceKey
    inputs, in_rep = {}, {}
    for iid, spec in doc.get("inputs", {}).items():
        if spec.get("type", P.TYPE_NAME) != P.TYPE_NAME:
            raise RecipeError(f"input {iid}: only keen::ImpactProgram inputs are supported")
        key = ResourceKey(spec["guid"], P.TYPE_HASH, spec.get("part", 0))
        if not g.kfc.has(key):
            raise RecipeError(f"input {iid}: {key} is not in {g.kfc.path} (game update or wrong side?)")
        bundle = g.kfc.bundles.get(P.TYPE_HASH, {})
        if bundle.get("layout") != P.LAYOUT_HASH:
            raise RecipeError(f"input {iid}: ImpactProgram layout {bundle.get('layout', 0):#x} != {P.LAYOUT_HASH:#x}")
        raw = g.kfc.read(key)
        inputs[iid] = P.decode(raw)
        in_rep[iid] = {"guid": key.guid, "type": P.TYPE_NAME, "sha256": sha256(raw), "layout": f"0x{P.LAYOUT_HASH:08x}"}

    steps, files, entries = [], {}, []
    for i, st in enumerate(doc.get("steps", [])):
        s = {"step": i, "op": st["op"], "stem": st["stem"], "ok": False}
        try:
            if st["op"] == "assemble":
                src = base / st["src"]
                prog = asm.assemble(src.read_text(), cat, src=str(src.relative_to(base)))
                if st.get("guid") and P.guid_text(P.guid_bytes(st["guid"])) != prog.guid:
                    raise RecipeError(f"step guid {st['guid']} != the source's program {prog.guid}")
            else:
                if st.get("base") not in inputs:
                    raise RecipeError(f"base {st.get('base')!r} is not an input")
                if not st.get("guid"):
                    raise RecipeError("clone needs a new fixed guid")
                prog = patch.clone(inputs[st["base"]], cat, st["guid"], st.get("edits", []),
                                   keep_sites=bool(st.get("keepSites")), base_dir=base)
            mode = st.get("mode") or ("override" if g.has_program(prog.guid) else "new")
            if mode == "new" and g.has_program(prog.guid):
                raise RecipeError(f"mode new: {prog.guid} is a vanilla program")
            if mode == "override" and not g.has_program(prog.guid):
                raise RecipeError(f"mode override: {prog.guid} is not a vanilla program")
            rep = L.lint(prog, cat)
            s.update({"guid": prog.guid, "id": f"0x{prog.id:08x}", "mode": mode, "lint": rep.to_json()})
            if not rep.ok:
                raise RecipeError(f"lint: {len(rep.errors)} error(s): " + "; ".join(str(f) for f in rep.errors[:4]))
            blob = P.encode(prog)
            if P.encode(P.decode(blob)) != blob:
                raise RecipeError("internal: the blob does not re-encode byte for byte")
            rel = f"{st['stem']}.bin"
            files[rel] = blob
            s.update({"file": rel, "size": len(blob), "sha256": sha256(blob), "ok": True})
            entries.append({"kind": "resource", "guid": prog.guid, "type": P.TYPE_NAME, "part": 0, "mode": mode,
                            "file": _rel(out / rel, man.parent)})
        except (RecipeError, asm.AsmError, patch.PatchError, catalog.CatalogError, OSError) as e:
            s["error"] = str(e)
        steps.append(s)

    # every file is checked before the first one is written: a stem with ".." or a "/" in it, or a link, would lead out of assets/cooked
    try:
        for rel in files:
            CP.cooked(p, out / rel, f"the output {rel}")
    except CP.Refused as e:
        raise RecipeError(f"{p}: {e}") from e
    out.mkdir(parents=True, exist_ok=True)
    for rel, b in files.items():
        (out / rel).write_bytes(b)
    m = json.loads(man.read_text()) if man.exists() else {"format": 1, "entries": []}
    have = {(e.get("kind"), e.get("file")): e for e in m.get("entries", [])}
    missing = [e for e in entries if (e["kind"], e["file"]) not in have]
    mismatched = [e for e in entries if (e["kind"], e["file"]) in have and have[(e["kind"], e["file"])] != e]
    if update_manifest and (missing or mismatched):
        names = {(e["kind"], e["file"]) for e in entries}
        m["entries"] = [x for x in m.get("entries", []) if (x.get("kind"), x.get("file")) not in names] + entries
        man.parent.mkdir(parents=True, exist_ok=True)
        man.write_text(json.dumps(m, indent=2) + "\n")
        missing, mismatched = [], []
    ok = all(s["ok"] for s in steps) and not missing and not mismatched
    report = {
        "recipe": str(p), "recipe_sha256": sha256(p.read_bytes()), "ok": ok,
        "tool": "tools/impact", "game": {"kfc": str(g.kfc.path), "version": g.version,
                                         "exe_sha256": g.types.dump.get("exe_sha256")},
        "sources": {str(f.relative_to(base)): (sha256(f.read_bytes()) if f.exists() else None) for f in _sources(base, doc)},
        "inputs": in_rep,
        "outputs": [{"file": s["file"], "guid": s["guid"], "size": s["size"], "sha256": s["sha256"],
                     "natives": s["lint"]["natives"]} for s in steps if s["ok"]],
        "manifest": {"file": str(man), "missing": missing, "mismatched": mismatched},
        "steps": steps,
    }
    (out / "cook-report.json").write_text(json.dumps(report, indent=2) + "\n")
    report["report"] = str(out / "cook-report.json")
    return report


def _rel(path, start):
    import os
    return os.path.relpath(path, start).replace("\\", "/")
