# SPDX-License-Identifier: MIT
"""A made-up game folder for the cook tools' tests: a synthetic KFC4 (tools/kfc's test builder) with a made-up base RenderModel, its
material and a visual template on a client, only the template on a dedicated server (whose files hold no RenderModel and no material:
enshrouded_server.kfc). Nothing from the real game. The reflection is the tests' closures of the game's (layouts only).

    python3 tools/modelcook/tests/fakegame.py GAME_DIR --side server|client [--mod MOD_DIR] [--kiln-out DIR]

--mod writes a mod project there (kiln.mod.json, a CMakeLists.txt calling kiln_plugin, assets/cook.json with a model step, a visual
step and an animcook skeleton + clips step, and the glTF sources). --kiln-out also puts the game's "exe" into the folder and its
reflection into DIR/types (where tools/kfc caches what it reads from an exe; point KILN_OUT at DIR): the exe is a few bytes of text,
never a program, and is never run.
"""
import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
TOOLS = HERE.parent.parent
for p in (TOOLS / "modelcook", TOOLS / "modelcook" / "samples", TOOLS / "kfc", TOOLS / "kfc" / "tests", TOOLS / "animcook", TOOLS / "animcook" / "samples"):
    if str(p) not in sys.path:
        sys.path.insert(0, str(p))

import cube_sword  # noqa: E402
from cook import gltf, rendermodel as RM  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

MATERIAL, BASE_MODEL, BASE_VISUAL = ("11111111-0000-4000-8000-000000000001", "22222222-0000-4000-8000-000000000002",
                                     "33333333-0000-4000-8000-000000000003")
NEW_MODEL, NEW_VISUAL = "44444444-0000-4000-8000-000000000004", "55555555-0000-4000-8000-000000000005"
STEM = {"server": "enshrouded_server", "client": "enshrouded"}
EXE_TEXT = b"a test's stand-in for the game's exe: its reflection is in KILN_OUT/types; not a program\n"


def types():
    """The reflection the fake game is read with: the RenderModel closure of tests/fixtures and animcook's bundled one."""
    a = json.loads((HERE / "fixtures" / "types-rendermodel.json").read_text())
    b = json.loads((TOOLS / "animcook" / "types" / "anim-types.json").read_text())
    have = {t["name"] for t in a["types"]}
    dump = dict(a, types=a["types"] + [t for t in b["types"] if t["name"] not in have])
    dump["count"] = len(dump["types"])
    dump["exe_sha256"] = hashlib.sha256(EXE_TEXT).hexdigest()
    return TypeDb(dump)


def build(game, side, t=None, kiln_out=None):
    """The game folder at `game` for `side` ("server" or "client"). -> its Kfc."""
    from test_kfc import build_kfc
    t = t or types()
    game = Path(game)
    game.mkdir(parents=True, exist_ok=True)
    enc = Encoder(t)
    tmp = game / "base.gltf"
    cube_sword.write(tmp)
    p = RM.prepare(gltf.read(tmp)["primitives"][0])
    tmp.unlink()
    p["material_slot"] = 0
    desc, base_blob = RM.build([[p]], [{"material": MATERIAL, "renderEffectId": "Pbr", "materialFlags": []}],
                               {"debugName": "", "materials": [], "meshlets": [], "meshes": [], "lods": []}, "base")
    th = {n: t.get(n)["hash"] for n in ("keen::RenderMaterialResource", "keen::RenderModel", "keen::ecs::TemplateResource")}
    visual = enc.encode("keen::ecs::TemplateResource", {"name": "Visuals_Base", "components": [
        {"$type": "keen::ecs::ModelResource", "value": {"model": BASE_MODEL}},
        {"$type": "keen::ecs::SlotAttachment", "value": {"attachmentSlot": "HandR"}}]})
    res = [(BASE_VISUAL, th["keen::ecs::TemplateResource"], 0, visual)]
    contents = [b"unused"]
    if side == "client":  # a dedicated server's files hold neither
        res += [(MATERIAL, th["keen::RenderMaterialResource"], 0, enc.encode("keen::RenderMaterialResource", {"effectId": "Pbr", "flags": [], "debugName": "m"})),
                (BASE_MODEL, th["keen::RenderModel"], 0, RM.encode(t, desc))]
        contents.append((RM.content_hash(base_blob), base_blob))
    build_kfc(game, res, contents, chunk_size=4096)
    stem = STEM[side]
    for f in sorted(game.glob("test*")):
        f.rename(game / (stem + f.name[len("test"):]))
    # the bundles carry the reflection's layout hashes (the builder writes placeholders)
    kfc = game / f"{stem}.kfc"
    k = Kfc(game)
    kb = bytearray(kfc.read_bytes())
    vo = struct.unpack_from("<I", kb, 0x80)[0] + 0x80
    for i, (h, _) in enumerate(sorted(k.bundles.items())):
        struct.pack_into("<I", kb, vo + 12 * i, t.get(h)["layout"])
    k.close()
    kfc.write_bytes(bytes(kb))
    if kiln_out is not None:
        exe = game / f"{stem}.exe"
        exe.write_bytes(EXE_TEXT)
        cache = Path(kiln_out) / "types"
        cache.mkdir(parents=True, exist_ok=True)
        (cache / f"{hashlib.sha256(EXE_TEXT).hexdigest()[:16]}.json").write_text(json.dumps(t.dump))
    return Kfc(game)


RECIPE = {
    "format": 1, "tool": "modelcook", "out": "cooked",
    "inputs": {"model": {"guid": BASE_MODEL, "type": "keen::RenderModel"},
               "visual": {"guid": BASE_VISUAL, "type": "keen::ecs::TemplateResource"}},
    "steps": [
        {"op": "model", "gltf": "src/sword.gltf", "base": "model", "guid": "@model", "mode": "override", "stem": "a"},
        {"op": "model", "gltf": "src/sword.gltf", "base": "model", "guid": NEW_MODEL, "stem": "b"},
        {"op": "visual", "base": "visual", "model": NEW_MODEL, "guid": NEW_VISUAL, "stem": "v"},
        {"tool": "animcook", "op": "skeleton", "name": "golem", "gltf": "src/golem.glb"},
        {"tool": "animcook", "op": "clips", "skeleton": "golem", "gltf": "src/golem.glb", "clips": {"idle": {"animation": "Idle", "loop": True}}}]}


def write_mod(mod, recipe=None):
    """A mod project at `mod` with `recipe` (default RECIPE) as assets/cook.json. -> the recipe's path."""
    import make_golem
    mod = Path(mod)
    (mod / "assets" / "src").mkdir(parents=True, exist_ok=True)
    (mod / "kiln.mod.json").write_text(json.dumps({"id": "fake-game-mod", "version": "1.0.0", "side": "both", "clients": "mod"}))
    (mod / "CMakeLists.txt").write_text("kiln_plugin(fake_game_mod)\n")
    cube_sword.write(mod / "assets" / "src" / "sword.gltf", "tower")
    make_golem.write(mod / "assets" / "src" / "golem.glb")
    r = mod / "assets" / "cook.json"
    r.write_text(json.dumps(recipe or RECIPE, indent=1))
    return r


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("game")
    ap.add_argument("--side", choices=("server", "client"), required=True)
    ap.add_argument("--mod")
    ap.add_argument("--kiln-out")
    a = ap.parse_args(argv)
    build(a.game, a.side, kiln_out=a.kiln_out).close()
    if a.mod:
        write_mod(a.mod)
    print(f"fake {a.side} game: {a.game}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
