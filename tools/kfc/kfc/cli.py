# SPDX-License-Identifier: MIT
"""kfc.py: read-only command line over a KFC4 game folder.

    kfc.py [--game DIR] [--exe EXE] <command> ...

    info                               header summary
    types [--grep RX] [--show NAME] [--closure A,B --json-out F]   reflection types (offline from the exe;
                                       cached in $KILN_OUT/types; --closure: a small dump for fixtures)
    list [--type NAME|0xHASH] [--guid G] [--limit N]   resources: guid, type, part, size
    get GUID TYPE [--part N] [-o FILE]  raw resource bytes (default $KILN_OUT/res/<guid>.<type>.<part>.bin)
    decode GUID TYPE [--part N] [-o FILE]  reflected decode as JSON
    content HASH [-o FILE]             one content blob from the .dat containers (32 hex digits or size-h0-h1-h2)
    refs GUID                          every resource with this GUID (all types and parts)
    model GUID [--lod N] [--obj F] [--gltf F [--skin]] [--all-lods] [--game-space] [--bin F [--blob F]]
                                       keen::RenderModel (the game's, or a cooked file) -> OBJ/glTF
    find-items RX                      ItemInfo debug names matching RX -> their model / icon GUIDs
    find-templates RX [--components]   TemplateResource names matching RX -> GUID, family (and component types)
    map-markers [-o DIR] [--lang L,..] [--compare MARKERS.json]   the admin map's markers (POIs, world objects,
                                       place names, every language) from the game; compare with another set
    clip GUID [--gltf F] [--model GUID] one keen::JointAnimation: segments, tracks, keys; --gltf writes the skeleton with
                                       the clip as a glTF animation (--model: and that model's skinned mesh, for inspection)
    graph GUID                         one AnimationGraphResource2_0: states, clips, what enters each state

Defaults: --game ~/.local/share/Steam/steamapps/common/Enshrouded; --exe the game's exe in --game (it is only read;
pass a scratch copy if you prefer). Outputs go to $KILN_OUT (default <repo>/.out/models), never into the game.
"""
import argparse
import json
import re
import sys
from pathlib import Path

from .archive import ContentHash, Kfc, ResourceKey
from .blob import Decoder
from .refl import TypeDb
from .util import out_dir

DEFAULT_GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"


def _exe_for(game):
    for n in ("enshrouded.exe", "enshrouded_server.exe"):
        if (game / n).exists():
            return game / n
    raise FileNotFoundError(f"no exe in {game}; pass --exe")


class Ctx:
    def __init__(self, args):
        self.args = args
        self.game = Path(args.game).expanduser()
        self._kfc = None
        self._types = None

    @property
    def kfc(self):
        if self._kfc is None:
            self._kfc = Kfc(self.game)
        return self._kfc

    @property
    def types(self):
        if self._types is None:
            exe = Path(self.args.exe).expanduser() if self.args.exe else _exe_for(self.game)
            self._types = TypeDb.from_exe(exe)
        return self._types

    def type_hash(self, s):
        if s.startswith("0x"):
            return int(s, 16)
        if not s.startswith("keen::"):
            s = "keen::" + s
        t = self.types.get(s)
        if t is None:
            raise SystemExit(f"unknown type {s}")
        return t["hash"]

    def key(self, guid, type_, part):
        return ResourceKey(guid, self.type_hash(type_), part)


def cmd_info(c):
    s = c.kfc.summary()
    tn = c.types
    s["layout_check"] = {"match": 0, "mismatch": []}
    for th, bd in c.kfc.bundles.items():
        t = tn.get(th)
        if t and t["layout"] == bd["layout"]:
            s["layout_check"]["match"] += 1
        else:
            s["layout_check"]["mismatch"].append(tn.name(th))
    s["exe_sha256"] = tn.dump["exe_sha256"]
    print(json.dumps(s, indent=2))


def cmd_types(c):
    t = c.types
    if c.args.closure:
        names = [n if n.startswith("keen::") else "keen::" + n for n in c.args.closure.split(",")]
        d = t.closure(names)
        Path(c.args.json_out).write_text(json.dumps(d, indent=0))
        print(f"{c.args.json_out}: {d['count']} types")
        return
    if c.args.show:
        n = c.args.show if c.args.show.startswith("keen::") else "keen::" + c.args.show
        print(t.describe(n))
        return
    rx = re.compile(c.args.grep or ".", re.I)
    counts = {th: bd["count"] for th, bd in c.kfc.bundles.items()} if c.args.resources else {}
    for ty in t.dump["types"]:
        if rx.search(ty["name"]) and (not c.args.resources or ty["hash"] in counts):
            extra = f"  {counts[ty['hash']]} resources" if counts else ""
            print(f"0x{ty['hash']:08x} {ty['size']:6d} {ty['name']}{extra}")


def cmd_clip(c):
    import struct
    from . import anim, animgraph, mesh as kmesh
    th = c.types.get("keen::JointAnimation")["hash"]
    raw = c.kfc.read(ResourceKey(c.args.guid, th, 0))
    clip = anim.parse(raw)
    tracks = sum(len(s.orientation.animated) + len(s.position.animated) + len(s.scale.animated) for s in clip.segments)
    keys = sum(len(t.keys) for s in clip.segments for ch in (s.orientation, s.position, s.scale) for t in ch.animated)
    print(f"{len(raw)} bytes, {clip.frame_count} frames at {clip.fps} fps, {clip.joint_count} joints, "
          f"{'loop' if clip.flags == 1 else 'clamp'}, {len(clip.segments)} segments, {tracks} animated tracks, {keys} keys")
    print(f"hierarchyHash {clip.hierarchy_hash:#x}, factors position {clip.position_factor} / tangent "
          f"{clip.position_tangent}, scale {clip.scale_factor}; byte-identical rebuild: {anim.build(clip) == raw}")
    if not c.args.gltf:
        return
    # the skeleton: by the model given, else by hierarchy hash
    mh = c.types.get("keen::ModelHierarchyResource")
    dec = Decoder(c.types)
    tri = None
    if c.args.model:
        m = kmesh.load_render_model(c.kfc, c.types, c.args.model)
        hg = m.desc["hierarchy"]
        tri = kmesh.to_rh(kmesh.triangles(m, 0))
    else:
        hg = next((k.guid for k in c.kfc.resources_of_type(mh["hash"])
                   if anim.hierarchy_hash(dec.decode(mh, c.kfc.read(k))["jointNames"]) == clip.hierarchy_hash), None)
        if hg is None:
            raise SystemExit("no skeleton has this clip's hierarchy hash; pass --model")
    hraw = c.kfc.read(ResourceKey(hg, mh["hash"], 0))
    anim.write_gltf(clip, anim.parse_default_pose(hraw), dec.decode(mh, hraw), c.args.gltf, tri)
    print("wrote", c.args.gltf)


def cmd_graph(c):
    from . import animgraph
    t = c.types.get("keen::anim_graph::runtime_graph::AnimationGraphResource2_0")
    print(animgraph.describe_graph(Decoder(c.types, max_array=1 << 26).decode(t, c.kfc.read(ResourceKey(c.args.guid, t["hash"], 0)))))


def cmd_list(c):
    k = c.kfc
    keys = k.keys
    if c.args.type:
        keys = k.resources_of_type(c.type_hash(c.args.type))
    if c.args.guid:
        keys = [x for x in keys if x.guid == c.args.guid.lower()]
    for i, x in enumerate(keys):
        if c.args.limit and i >= c.args.limit:
            break
        print(f"{x.guid} {c.types.name(x[1])} {x[2]} {k.size_of(x)}")


def _out(c, default_name):
    if c.args.o:
        p = Path(c.args.o)
        p.parent.mkdir(parents=True, exist_ok=True)
        return p
    return out_dir("res") / default_name


def cmd_get(c):
    key = c.key(c.args.guid, c.args.type, c.args.part)
    data = c.kfc.read(key)
    p = _out(c, f"{key.guid}.{c.types.name(key[1]).split('::')[-1]}.{key[2]}.bin")
    p.write_bytes(data)
    print(f"{p} {len(data)} bytes")


def cmd_decode(c):
    key = c.key(c.args.guid, c.args.type, c.args.part)
    data = c.kfc.read(key)
    v = Decoder(c.types).decode(key[1], data)
    s = json.dumps(v, indent=1)
    if c.args.o:
        Path(c.args.o).write_text(s)
        print(c.args.o)
    else:
        print(s)


def cmd_content(c):
    h = ContentHash(c.args.hash)
    data = c.kfc.read_content(h)
    p = _out(c, f"{h}.bin")
    p.write_bytes(data)
    print(f"{p} {len(data)} bytes ({h.assets_text()}) {c.kfc.content_location(h)}")


def cmd_refs(c):
    for x in c.kfc.by_guid(c.args.guid):
        print(f"{x.guid} {c.types.name(x[1])} part {x[2]} {c.kfc.size_of(x)} bytes")


def cmd_model(c):
    from . import mesh
    if c.args.bin:  # a cooked model from files (modelcook output) instead of the game's
        desc = Decoder(c.types).decode("keen::RenderModel", Path(c.args.bin).read_bytes())
        blob = Path(c.args.blob or Path(c.args.bin).with_suffix(".blob")).read_bytes()
        m = mesh.RenderModel(guid=c.args.guid, desc=desc, data=blob)
    else:
        m = mesh.load_render_model(c.kfc, c.types, c.args.guid)
    print(mesh.summary(m))
    lods = range(len(m.lods)) if c.args.all_lods else [c.args.lod]
    for lod in lods:
        tri = mesh.triangles(m, lod)
        if not c.args.game_space:
            tri = mesh.to_rh(tri)
        stem = f"{c.args.guid}.lod{lod}"
        if c.args.obj or not c.args.gltf:
            p = Path(c.args.obj) if (c.args.obj and not c.args.all_lods) else out_dir("export") / f"{stem}.obj"
            mesh.write_obj(tri, p)
            print(f"lod {lod}: {p} ({tri.vertex_count} vertices, {tri.triangle_count} triangles)")
        if c.args.gltf:
            p = Path(c.args.gltf) if not c.args.all_lods else out_dir("export") / f"{stem}.gltf"
            skel = None
            if c.args.skin and m.desc["hierarchy"] != "00000000-0000-0000-0000-000000000000":
                skel = mesh.skeleton_rh(mesh.load_hierarchy(c.kfc, c.types, m.desc["hierarchy"]))
            mesh.write_gltf(tri, p, skeleton=skel)
            print(f"lod {lod}: {p}")


def scalar(v):
    """A decoded id as an int: ids are typedefs of keen::HashKey32 / structs of one field, so depending on the build's
    reflection they decode as 123, {"value": 123} or {"id": 123}. None when it is not one of those."""
    for _ in range(4):
        if isinstance(v, bool):
            return int(v)
        if isinstance(v, int):
            return v
        if isinstance(v, dict) and len(v) == 1:
            v = next(iter(v.values()))
            continue
        return None
    return None


def hex_id(v):
    n = scalar(v)
    return f"0x{n:08x}" if n is not None else None


def item_row(guid, v):
    """find-items' JSON row of one decoded ItemInfo."""
    eq = v.get("equipment", {}) or {}
    setup = eq.get("equipmentEntityModelSetup", {}) or {}
    return {
        "guid": guid, "debugName": v.get("debugName", ""), "itemId": hex_id(v.get("itemId")),
        "visualModel": eq.get("visualModel"), "iconModel": v.get("iconModel"),
        "equipmentModel": setup.get("model"), "visualEntity": eq.get("visualEntity"),
    }


def cmd_find_items(c):
    rx = re.compile(c.args.rx, re.I)
    th = c.type_hash("keen::ItemInfo")
    dec = Decoder(c.types)
    for key in c.kfc.resources_of_type(th):
        v = dec.decode(th, c.kfc.read(key))
        if not rx.search(v.get("debugName", "") or ""):
            continue
        print(json.dumps(item_row(key.guid, v)))


def template_row(guid, v, components=False):
    """find-templates' JSON row of one decoded ecs::TemplateResource."""
    row = {"guid": guid, "name": v.get("name", ""), "family": v.get("family")}
    if components:
        row["components"] = [(x or {}).get("$type", "?").split("::")[-1] for x in v.get("components") or []]
    return row


def blob_string_at(data, off):
    """A keen::BlobString {i32 rel, u32 length} at `off` of a resource."""
    import struct
    rel, n = struct.unpack_from("<iI", data, off)
    return data[off + rel:off + rel + n].decode("utf-8", "replace") if n else ""


def cmd_find_templates(c):
    rx = re.compile(c.args.rx, re.I)
    th = c.type_hash("keen::ecs::TemplateResource")
    t = c.types.get(th)
    name_off = next(f["offset"] for f in c.types.all_fields(t) if f["name"] == "name")
    dec = Decoder(c.types)
    for key in c.kfc.resources_of_type(th):
        data = c.kfc.read(key)
        if not rx.search(blob_string_at(data, name_off)):  # the name only: decoding 12k templates fully takes minutes
            continue
        print(json.dumps(template_row(key.guid, dec.decode(th, data), c.args.components)))


def cmd_map_markers(c):
    """The admin map's markers from the game (kfc/mapmarkers.py); --compare: against another markers.json."""
    from . import mapmarkers as M
    out = Path(c.args.o) if c.args.o else out_dir("map-markers")
    markers, other, icons, report = M.extract(c.kfc, c.types, langs=set(c.args.lang.split(",")) if c.args.lang else None)
    (out / "lang").mkdir(parents=True, exist_ok=True)
    (out / "markers.json").write_text(json.dumps(markers, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    for code, d in other.items():
        (out / "lang" / f"{code}.json").write_text(json.dumps(d, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    report["icons"] = icons
    report["out"] = str(out)
    print(json.dumps(report, indent=1))
    for i, cat in enumerate(markers["cats"]):
        print(f"  {cat['count']:5}  {cat['id']:16} {cat['group']:18} {cat['label']}")
    if c.args.compare:
        ew = json.loads(Path(c.args.compare).expanduser().read_text())
        print(f"\n{'category':16} {'game':>5} {'other':>5} {'median m':>8} {'game<=25m':>9} {'oth<=25m':>8}")
        for r in M.compare(markers, ew):
            print(f"{r['id']:16} {r['game']:5} {r['other']:5} {str(r['median_m']):>8} {r['game_within']:9} {r['other_within']:8}")


def main(argv=None):
    ap = argparse.ArgumentParser(prog="kfc.py", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", default=str(DEFAULT_GAME))
    ap.add_argument("--exe")
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("info")
    p = sub.add_parser("types")
    p.add_argument("--grep")
    p.add_argument("--show")
    p.add_argument("--resources", action="store_true", help="only types that have resources in the .kfc")
    p.add_argument("--closure", help="comma-separated type names: write them and every type they use to --json-out")
    p.add_argument("--json-out", default="types-closure.json")
    p = sub.add_parser("list")
    p.add_argument("--type")
    p.add_argument("--guid")
    p.add_argument("--limit", type=int, default=0)
    for n in ("get", "decode"):
        p = sub.add_parser(n)
        p.add_argument("guid")
        p.add_argument("type")
        p.add_argument("--part", type=int, default=0)
        p.add_argument("-o")
    p = sub.add_parser("content")
    p.add_argument("hash")
    p.add_argument("-o")
    p = sub.add_parser("refs")
    p.add_argument("guid")
    p = sub.add_parser("model")
    p.add_argument("guid")
    p.add_argument("--lod", type=int, default=0)
    p.add_argument("--all-lods", action="store_true")
    p.add_argument("--obj")
    p.add_argument("--gltf")
    p.add_argument("--game-space", action="store_true", help="keep the game's left-handed axes (no x mirror)")
    p.add_argument("--skin", action="store_true", help="glTF: add the model's skeleton (ModelHierarchyResource) and weights")
    p.add_argument("--bin", help="decode this RenderModel file instead of the game's (GUID is then only a label)")
    p.add_argument("--blob", help="its renderData blob (default: the .bin path with .blob)")
    p = sub.add_parser("find-items")
    p.add_argument("rx")
    p = sub.add_parser("find-templates")
    p.add_argument("rx")
    p.add_argument("--components", action="store_true", help="list each template's component types")
    p = sub.add_parser("map-markers")
    p.add_argument("-o", help="output folder (default $KILN_OUT/map-markers)")
    p.add_argument("--lang", help="comma-separated BCP 47 codes to keep (default: every language)")
    p.add_argument("--compare", help="another markers.json to compare counts and positions with")
    p = sub.add_parser("clip")
    p.add_argument("guid")
    p.add_argument("--gltf")
    p.add_argument("--model")
    p = sub.add_parser("graph")
    p.add_argument("guid")
    a = ap.parse_args(argv)
    c = Ctx(a)
    {"info": cmd_info, "types": cmd_types, "list": cmd_list, "get": cmd_get, "decode": cmd_decode,
     "content": cmd_content, "refs": cmd_refs, "model": cmd_model, "find-items": cmd_find_items,
     "find-templates": cmd_find_templates, "map-markers": cmd_map_markers, "clip": cmd_clip, "graph": cmd_graph}[a.cmd](c)


if __name__ == "__main__":
    sys.exit(main())
