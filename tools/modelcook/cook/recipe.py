# SPDX-License-Identifier: MIT
"""cook.json: how a mod's game-derived assets are made on the player's machine (docs/kiln/modding/models.md).

A mod ships its own sources (glTF, PNG, JSON) and this recipe, never game data. The cook runs locally against the
installed game. The files it writes go to `out` (default "cooked", gitignored) next to the recipe. The mod's
committed assets/assets.json names them.

    {
      "format": 1,
      "tool": "modelcook",                  // the default cooker of the steps (absent: modelcook)
      "mod": "my-mod",                      // for ids that follow from names; default: the id in the nearest kiln.mod.json
      "out": "cooked",                      // relative to this file
      "game": { "side": "client", "madeOn": "1285601" },   // informational: the build the recipe was written on
      "inputs": {                           // vanilla inputs, by GUID + type (+ part): read from the installed game
        "bronze_model": { "guid": "9c38c7f0-…", "type": "keen::RenderModel" }
      },
      "steps": [
        { "op": "model", "gltf": "src/sword.gltf", "lods": ["src/sword_lod1.gltf"], "pixelSizes": [0, 0.02],
          "base": "bronze_model", "guid": "@bronze_model", "mode": "override", "name": "my_sword", "stem": "sword",
          "material": "@some_material", "materialMap": { "<glTF material name>": "<GUID, @input or a MaterialBlock's name>" } },
        { "op": "visual", "base": "bronze_visual", "model": "<GUID>", "guid": "<GUID>", "name": "Visuals_My_Sword",
          "stem": "sword_visual" },
        { "tool": "animcook", "op": "skeleton", "name": "golem", "gltf": "src/golem.glb" },
        { "op": "model", "gltf": "src/golem.glb", "skeleton": "golem", "material": "@some_material", "name": "golem" }
      ]
    }

"@id" means the GUID of input `id`. A step's `base` must be an input of the matching type. `material` and `materialMap` also take the name of a MaterialBlock in the
"materials" section of this mod's blocks/*.json. `"materials": "gltf"` makes a block of every glTF material the step doesn't otherwise map: blocks/<stem>.materials.json,
and any PNGs embedded in the glTF under <out>/materials (docs/kiln/modding/materials.md).

A model step with `skeleton` cooks a skinned model (docs/kiln/modding/models.md): `skeleton` names a `skeleton` step of this recipe, or is "@id" for one of the game's
skeletons. A step of this recipe is read directly from its glTF, so it makes no difference which tool runs first. `base` is optional when `material` or `materialMap`
says what to draw with; without `guid` such a model's GUID follows from <mod>:model:<name>, as the animation tools' ids do.

`"skeleton": "@player"` is the player's skeleton, with no input to declare. A model on it is worn: cook/worn.py has the "worn" step, and the steps a mod's blocks/*.json ask for by themselves.
A mod with no assets/cook.json of modelcook's still has those: looks_recipe() is the recipe they make on their own (`modelcook.py looks MODDIR`, which `kiln cook` runs), with its report in
cook-report.looks.json.

A step may name its own cooker with `"tool"` (default: the file's `tool`), so one recipe can cook models and sounds:
`{ "tool": "soundcook", "op": "sound", ... }` is run by tools/soundcook. modelcook runs only its own steps (a step's
index stays its position in `steps`) and leaves the others to their tool (`modelcook.py recipe` starts them unless
`--no-dispatch`). Each tool writes its own report: `cook-report.json` for the file's default tool,
`cook-report.<tool>.json` for the others.

The runner writes `<out>/cook-report.json`, so a launcher can decide when to cook again. It records:
- the recipe's sha256;
- the game's .kfc version and exe sha256;
- each input's sha256 and type layout hash;
- every output's sha256.

Cook again when any of them differs, or when an output is missing.

On a dedicated server's files (`enshrouded_server.kfc`) the model steps (and worn and placed looks, which are model steps) are
skipped: a server has no RenderModel, and only a game client draws one (docs/kiln/modding/models.md, "On a dedicated server").
An input of a type a server's files never hold (CLIENT_ONLY_TYPES) is then not read; any other input still has to be there.
Everything else runs as on a client: a visual template is served on both sides, and the material blocks a step makes from its
glTF are blocks every machine reads. With update_manifest, the assets.json entries of a skipped step get "side": "client", so
kiln.assets on the server skips them without a warning (assets.cpp, load_manifest).
"""
import json
import os
from pathlib import Path

from kfc.archive import ResourceKey

from . import material as MB, steps as S, worn as W

OPS = ("model", "visual", "worn")
TOOL = "modelcook"
HIERARCHY = "keen::ModelHierarchyResource"
KNOWN_TOOLS = ("modelcook", "soundcook", "animcook")


SERVER_KFC = "enshrouded_server.kfc"
# Types a dedicated server's files never hold: what only a game client draws (none of them is in enshrouded_server.kfc, svn 1287369)
CLIENT_ONLY_TYPES = ("keen::RenderModel", "keen::RenderMaterialResource", "keen::RenderTextureResource", "keen::UiTextureResource")
CLIENT_ONLY_OPS = ("model",)  # a worn look and a placed thing are model steps too
SKIPPED = "skipped on a dedicated server: only a game client draws it"


class RecipeError(Exception):
    pass


def side_of(k):
    """'server' for a dedicated server's game files (enshrouded_server.kfc), else 'client'."""
    return "server" if k is not None and Path(k.path).name.lower() == SERVER_KFC else "client"


def _stem_of(st):
    """The file stem a model step writes (steps.model: stem, else name, else the glTF's)."""
    return st.get("stem") or st.get("name") or Path(st["gltf"]).stem


def _resolve(ref, inputs):
    if isinstance(ref, str) and ref.startswith("@"):
        if ref[1:] not in inputs:
            raise RecipeError(f"{ref}: no such input")
        return inputs[ref[1:]]["guid"]
    return ref


LOOKS_REPORT = "cook-report.looks.json"


def looks_recipe(mod_dir, mod=None):
    """The recipe of a mod with looks in blocks/*.json and no assets/cook.json of modelcook's: (where it would be, its document)."""
    doc = {"format": 1, "tool": TOOL, "out": "cooked", "steps": [], "looks": True}
    if mod:
        doc["mod"] = mod
    return Path(mod_dir) / "assets" / "cook.json", doc


def _recipe_bytes(path, given=None):
    """What a report's recipe_sha256 is of: the file, or the document a recipe was made from."""
    return json.dumps(given, sort_keys=True).encode() if given is not None else Path(path).read_bytes()


def load(path, given=None):
    """A recipe: the file at `path`, or `given` as if it were there."""
    p = Path(path)
    doc = json.loads(json.dumps(given)) if given is not None else json.loads(p.read_text())
    if doc.get("format") != 1:
        raise RecipeError(f"{p}: format {doc.get('format')!r}, expected 1")
    if doc.get("tool", TOOL) not in KNOWN_TOOLS:
        raise RecipeError(f"{p}: tool {doc.get('tool')!r} (known: {', '.join(KNOWN_TOOLS)})")
    for i, st in enumerate(doc.get("steps", [])):
        tool = tool_of(doc, st)
        if tool not in KNOWN_TOOLS:
            raise RecipeError(f"step {i}: tool {tool!r} (known: {', '.join(KNOWN_TOOLS)})")
        if tool == TOOL and st.get("op") not in OPS:
            raise RecipeError(f"step {i}: op {st.get('op')!r} (known: {', '.join(OPS)})")
        if tool == TOOL and st.get("op") == "worn" and "model" not in st:
            raise RecipeError(f"step {i} (worn): model missing: the glTF file, relative to the mod's folder")
        if tool == TOOL and st.get("op") == "model":
            if "gltf" not in st:
                raise RecipeError(f"step {i} (model): gltf missing")
            if not any(x in st for x in ("base", "material", "materialMap", "materials")):
                raise RecipeError(f"step {i} (model): base, material or materialMap missing, or \"materials\": \"gltf\": nothing says what to draw it with")
            if st.get("materials", "gltf") != "gltf":
                raise RecipeError(f"step {i} (model): materials {st['materials']!r}: the only value is \"gltf\"")
    # what the mod's blocks ask for; a step of the recipe that names the same file says more, and wins
    named = {st.get("model") for st in doc.get("steps", []) if st.get("op") == "worn"}
    doc["steps"] = doc.get("steps", []) + [st for st in W.steps_from_blocks(_mod_dir(p)) if st["model"] not in named]
    # the models of placed things the blocks show; a model step of the recipe for the same file says more, and wins
    from . import placed as PL
    gltfs = {(p.parent / st["gltf"]).resolve() for st in doc["steps"] if st.get("op") == "model" and "gltf" in st}
    doc["steps"] += [st for st in PL.steps_from_blocks(_mod_dir(p), p.parent) if (p.parent / st["gltf"]).resolve() not in gltfs]
    return _creature_blocks(doc, p)


def _creature_blocks(doc, p):
    """The bodies and animations of the mod's blocks/*.json (tools/animcook/anicook/blocks.py): their model steps, and the skeleton steps those bind to."""
    try:
        from anicook import blocks as AB
    except ImportError:
        return doc
    try:
        return AB.add_to(doc, p)
    except AB.BlocksError as e:
        raise RecipeError(str(e)) from e


def tool_of(doc, st):
    """The cooker of a step: its own "tool", else the file's, else modelcook."""
    return st.get("tool", doc.get("tool", TOOL))


def own_steps(doc):
    """(index, step) of the steps modelcook runs."""
    return [(i, st) for i, st in enumerate(doc.get("steps", [])) if tool_of(doc, st) == TOOL]


def other_tools(doc):
    return sorted({tool_of(doc, st) for st in doc.get("steps", [])} - {TOOL})


def _refs(v, out):
    if isinstance(v, str) and v.startswith("@"):
        out.add(v[1:])
    elif isinstance(v, list):
        for x in v:
            _refs(x, out)
    elif isinstance(v, dict):
        for x in v.values():
            _refs(x, out)
    return out


def _foreign_inputs(doc):
    """Inputs named ("@id" or "base") only by other tools' steps."""
    mine, theirs = set(), set()
    for st in doc.get("steps", []):
        s = mine if tool_of(doc, st) == TOOL else theirs
        _refs(st, s)
        if isinstance(st.get("base"), str):
            s.add(st["base"])
    return theirs - mine


def report_name(doc):
    if doc.get("looks"):
        return LOOKS_REPORT
    return "cook-report.json" if doc.get("tool", TOOL) == TOOL else f"cook-report.{TOOL}.json"


def needs_cook(path, given=None):
    """True when the outputs are missing or the report does not match this recipe (no game access needed)."""
    p = Path(path)
    doc = load(p, given)
    if not own_steps(doc):
        return False
    rep = p.parent / doc.get("out", "cooked") / report_name(doc)
    if not rep.exists():
        return True
    r = json.loads(rep.read_text())
    if r.get("recipe_sha256") != S.sha256(_recipe_bytes(p, given)) or not r.get("ok"):
        return True
    for src, h in r.get("sources", {}).items():
        f = p.parent / src
        if not f.exists() or S.sha256(f.read_bytes()) != h:
            return True
    return any(not (p.parent / o["file"]).exists() or S.sha256((p.parent / o["file"]).read_bytes()) != o["sha256"]
               for o in r.get("outputs", []))


def _skeleton_step(doc, name):
    for _, st in enumerate(doc.get("steps", [])):
        if tool_of(doc, st) == "animcook" and st.get("op") == "skeleton" and st.get("name") == name:
            return st
    return None


def _target(doc, st, inputs, k, t, base_dir, path):
    """What a model step binds to: a skin.Target, or None for a rigid model."""
    ref = st.get("skeleton")
    if ref is None:
        return None
    if ref == "@player" and "player" not in inputs:
        return S.game_target(k, t, S.PLAYER_SKELETON)
    if ref.startswith("@"):
        guid = _resolve(ref, inputs)
        if inputs[ref[1:]]["type"] != HIERARCHY:
            raise RecipeError(f"skeleton {ref} is a {inputs[ref[1:]]['type']}, needs {HIERARCHY}")
        return S.game_target(k, t, guid)
    sk_step = _skeleton_step(doc, ref)
    if sk_step is None:
        raise RecipeError(f"skeleton {ref!r}: no skeleton step of that name in this recipe, and not an @input")
    from anicook import recipe as anim_recipe, skeleton as K
    from anicook.gltfdoc import Doc
    from anicook.graphgen import derive
    try:
        sk = K.from_gltf(Doc(base_dir / sk_step["gltf"]), skin=sk_step.get("skin"), root=sk_step.get("root"))
    except K.SkeletonError as e:
        raise RecipeError(f"skeleton {ref}: {e}") from e
    return S.SK.Target.from_skeleton(sk, sk_step.get("guid") or derive(anim_recipe.mod_id(doc, path), "skeleton", ref))


def _model_guid(doc, st, inputs, path):
    g = _resolve(st.get("guid"), inputs)
    if g or (st.get("base") is not None and st.get("mode") != "new"):
        return g
    from anicook import recipe as anim_recipe
    from anicook.graphgen import derive
    return derive(anim_recipe.mod_id(doc, path), "model", st.get("name") or st.get("stem") or Path(st["gltf"]).stem)


def _mod_dir(path):
    """The folder of the mod a recipe belongs to: the nearest kiln.mod.json above it, or the recipe's parent's parent, as mods/<x>/assets/cook.json is."""
    p = Path(path).resolve()
    for d in p.parents:
        if (d / "kiln.mod.json").exists():
            return d
    return p.parent.parent


def own_material_guids(path):
    """The GUIDs of the MaterialBlocks of the mod a cooked file belongs to."""
    mod_dir = _mod_dir(path)
    try:
        mod = MB.mod_id({}, mod_dir)
        return {MB.block_guid(mod, b.get("was") or n) for n, (b, _) in MB.read_blocks(mod_dir).items()}
    except MB.MaterialError:
        return set()


def _names_blocks(st):
    """Whether a step may name MaterialBlocks."""
    values = [st.get("material")] + list(st.get("materialMap", {}).values())
    return any(isinstance(v, str) and not v.startswith("@") and not MB.GUID.match(v) for v in values)


def sources_of(doc, path=None):
    """The files a cook of this recipe's own steps reads, relative to the recipe: a change in one means a new cook."""
    out = set(doc.get("blocksSources", []))
    for _, st in own_steps(doc):
        if st.get("op") == "worn" and path is not None:
            mod_dir = _mod_dir(path)
            base = Path(path).resolve().parent
            for f in [st["model"], *st.get("lods", [])]:
                out.add(os.path.relpath(mod_dir / f, base))
            for f in sorted((mod_dir / "blocks").glob("*.json")):
                out.add(os.path.relpath(f, base))  # the look's fields and its materials
            continue
        if st.get("op") != "model":
            continue
        out.add(st["gltf"])
        out.update(st.get("lods", []))
        if path is not None and _names_blocks(st):
            base = Path(path).resolve().parent
            for f in sorted((_mod_dir(path) / "blocks").glob("*.json")):
                out.add(os.path.relpath(f, base))
        sk = _skeleton_step(doc, st.get("skeleton"))
        if sk:
            out.add(sk["gltf"])
    return sorted(out)


def _made_here(path, mod_dir, mod_files):
    """A blocks file this cook just wrote is an output, not a source."""
    try:
        return Path(path).resolve().relative_to(mod_dir).as_posix() in mod_files
    except ValueError:
        return False


def run(path, k, t, exe_sha256=None, update_manifest=False, given=None):
    p = Path(path)
    doc = load(p, given)
    base_dir = p.parent
    out = doc.get("out", "cooked")
    inputs, in_report = {}, {}
    foreign = _foreign_inputs(doc)
    server = side_of(k) == "server"
    skipped_files = set()  # what the steps skipped on a server would have written
    for iid, spec in doc.get("inputs", {}).items():
        if iid in foreign:
            continue  # used only by another tool's steps (e.g. a vanilla sound cue): that tool reads it
        tt = t.get(spec["type"])
        if tt is None:
            raise RecipeError(f"input {iid}: unknown type {spec['type']}")
        key = ResourceKey(spec["guid"], tt["hash"], spec.get("part", 0))
        if not k.has(key) and server and spec["type"] in CLIENT_ONLY_TYPES:
            # a dedicated server has none of these: only the client-only steps read it, and those are skipped here
            inputs[iid] = {"guid": key.guid, "type": spec["type"], "part": key[2]}
            in_report[iid] = dict(inputs[iid], available=False, why=f"a dedicated server's files have no {spec['type']}")
            continue
        if not k.has(key):
            raise RecipeError(f"input {iid}: {key} is not in {k.path} (game update or wrong side?)")
        bundle = k.bundles.get(tt["hash"], {})
        if bundle.get("layout") != tt["layout"]:
            raise RecipeError(f"input {iid}: {spec['type']} layout {tt['layout']:#x} != the data's {bundle.get('layout', 0):#x}")
        inputs[iid] = {"guid": key.guid, "type": spec["type"], "part": key[2]}
        in_report[iid] = dict(inputs[iid], sha256=S.sha256(k.read(key)), layout=f"0x{tt['layout']:08x}")

    files, entries, reports = {}, [], []
    mod_dir = _mod_dir(p)
    own = None
    mod_files = {}  # what goes in the mod's folder rather than beside the recipe: blocks made from a glTF, and PNGs that were embedded in it
    for i, st in own_steps(doc):
        if st["op"] == "worn":
            st = W.as_model_step(st, base_dir.resolve(), mod_dir)
        op = st["op"]
        base_id = st.get("base")
        if base_id not in inputs and not (op == "model" and base_id is None):
            raise RecipeError(f"step {i} ({op}): base {base_id!r} is not an input")
        want = {"model": "keen::RenderModel", "visual": "keen::ecs::TemplateResource"}[op]
        if base_id is not None and inputs[base_id]["type"] != want:
            raise RecipeError(f"step {i} ({op}): base {base_id} is a {inputs[base_id]['type']}, needs {want}")
        if op == "model":
            gl = [str(base_dir / st["gltf"])] + [str(base_dir / x) for x in st.get("lods", [])]
            mm = {n: _resolve(g, inputs) for n, g in st.get("materialMap", {}).items()}
            material = _resolve(st.get("material"), inputs)
            from_gltf = []
            if st.get("materials") == "gltf" or _names_blocks(st):
                if own is None:
                    try:
                        own = {"mod": MB.mod_id(doc, mod_dir), "blocks": MB.read_blocks(mod_dir)}
                    except MB.MaterialError as e:
                        raise RecipeError(f"step {i} (model): {e}") from e
            if st.get("materials") == "gltf":
                stem = st.get("stem") or st.get("name") or Path(st["gltf"]).stem
                names, blocks, made, from_gltf = S.gltf_blocks(gl[0], mod_dir, stem, Path(os.path.relpath(base_dir / out / "materials", mod_dir)).as_posix())
                for key, name in names.items():
                    mm.setdefault(key, name)
                own["blocks"] = {**own["blocks"], **{n: (b, f"blocks/{stem}.materials.json") for n, b in blocks.items()}}
                mod_files.update(made)
            if server and op in CLIENT_ONLY_OPS:
                stem = _stem_of(st)
                skipped_files.update({f"{out}/{stem}.bin", f"{out}/{stem}.blob"})
                reports.append({"op": op, "step": i, "name": st.get("name") or stem,
                                "guid": _model_guid(doc, st, inputs, p) or (inputs[base_id]["guid"] if base_id else None),
                                "ok": True, "skipped": SKIPPED, "warnings": from_gltf})
                continue
            f, e, r = S.model(k, t, gl, inputs[base_id]["guid"] if base_id else None, guid=_model_guid(doc, st, inputs, p),
                              mode=st.get("mode"), name=st.get("name"), material=material,
                              material_map=mm, pixel_sizes=st.get("pixelSizes"), stem=st.get("stem"), subdir=out,
                              target=_target(doc, st, inputs, k, t, base_dir, p), global_scale=st.get("globalScale"), own_materials=own, attach=st.get("attach"))
            r["warnings"] = r.get("warnings", []) + from_gltf
        else:
            f, e, r = S.visual(k, t, inputs[base_id]["guid"], _resolve(st["model"], inputs), _resolve(st["guid"], inputs),
                               name=st.get("name"), stem=st.get("stem"), subdir=out)
        r["step"] = i
        files.update(f)
        entries.extend(e)
        reports.append(r)

    (base_dir / out).mkdir(parents=True, exist_ok=True)
    for rel, data in files.items():
        (base_dir / rel).write_bytes(data)
    for rel, data in mod_files.items():
        (mod_dir / rel).parent.mkdir(parents=True, exist_ok=True)
        (mod_dir / rel).write_bytes(data)

    # the committed assets.json must name every output exactly as cooked
    mp = base_dir / "assets.json"
    man = json.loads(mp.read_text()) if mp.exists() else {"format": 1, "entries": []}
    have = {(e.get("kind"), e.get("file")): e for e in man.get("entries", [])}
    missing = [e for e in entries if (e["kind"], e["file"]) not in have]
    mismatched = [e for e in entries if (e["kind"], e["file"]) in have and have[(e["kind"], e["file"])] != e]
    # on a server, the entries of a skipped step stay (they are the clients'), marked so that kiln.assets skips them quietly there
    unmarked = [x for x in man.get("entries", []) if x.get("file") in skipped_files and x.get("side") != "client"
                and (x.get("kind") == "blob" or x.get("type") in CLIENT_ONLY_TYPES)]
    if update_manifest and (missing or mismatched or unmarked):
        for x in unmarked:
            x["side"] = "client"
        if missing or mismatched:
            keep = [x for x in man.get("entries", []) if (x.get("kind"), x.get("file")) not in {(e["kind"], e["file"]) for e in entries}]
            man["entries"] = keep + entries
        mp.write_text(json.dumps(man, indent=2) + "\n")
        missing, mismatched, unmarked = [], [], []

    ok = all(r.get("ok") for r in reports) and not missing and not mismatched
    report = {
        "recipe": str(p), "recipe_sha256": S.sha256(_recipe_bytes(p, given)), "ok": ok,
        "game": dict({"kfc": str(k.path), "version": k.version, "exe_sha256": exe_sha256}, **({"side": "server"} if server else {})),
        "inputs": in_report,
        "sources": {src: S.sha256((base_dir / src).read_bytes()) for src in sources_of(doc, p) if not _made_here(base_dir / src, mod_dir, mod_files)},
        "outputs": [{"file": rel, "size": len(d), "sha256": S.sha256(d)} for rel, d in files.items()],
        "manifest": dict({"missing": missing, "mismatched": mismatched}, **({"clientOnlyUnmarked": [x["file"] for x in unmarked]} if unmarked else {})),
        "steps": reports,
    }
    (base_dir / out / report_name(doc)).write_text(json.dumps(report, indent=2) + "\n")
    return report
