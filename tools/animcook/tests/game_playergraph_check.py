#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks the player-state merge (anicook/playergraph.py) on the real player graph. Reads the game, writes nothing but two small test mods
(player-states.json and kiln.mod.json, no game data) into a temp folder under --work.

    python3 -I tools/animcook/tests/game_playergraph_check.py [--graph FILE.bin] [--game DIR] [--work DIR] [--json FILE]

The graph: --graph, else the installed game's (--game, default Steam's Enshrouded), else the file $KILN_PLAYER_GRAPH. Neither: prints SKIP
and exits 0. The clips' facts come from the installed game; without one, from the graph's own clip nodes (said in the output). The
reflection: the game's exe when there is a game (cached under $KILN_OUT/types), else the bundled one.

Checks: no state gives "untouched" and the graph encodes back to its bytes; one state beside every supported neighbour, each with the
neighbour's own clip, merges and its clip node equals the neighbour's; two mods in both orders give the same bytes; 30 states beside one
hammer state nest and leave every game state, its id and its entry as they were; an unknown, an ambiguous and an unsupported `like` and a
clip of another skeleton are refused while the other states of the file load. Exit 1 when a check fails.
"""
import argparse
import hashlib
import json
import os
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path[:0] = [str(HERE.parent), str(HERE.parent.parent / "kfc")]

from anicook import playergraph as PG, playerstates as PS  # noqa: E402
from kfc.archive import Kfc  # noqa: E402
from kfc.blob import Decoder  # noqa: E402
from kfc.blobwrite import Encoder  # noqa: E402
from kfc.refl import TypeDb  # noqa: E402

GAME = Path.home() / ".local/share/Steam/steamapps/common/Enshrouded"
BUNDLED = HERE.parent / "types" / "anim-types.json"
FAILS = []


def check(ok, what):
    print(("ok   " if ok else "FAIL ") + what)
    if not ok:
        FAILS.append(what)
    return ok


def graph_facts(value):
    """Facts from the graph's own clip nodes, for a run without the game: 60 fps, duration * 60 frames, one skeleton."""
    out = {}
    for n in value["nodeDefinitions"]:
        v = n["value"]
        if v["type"] == "SampleAnimationClip":
            out[v["animation"]] = {"hierarchyHash": 1, "jointCount": 1, "fps": 60, "frameCount": round(v["animationDuration"] * 60), "loop": v["loop"],
                                   "rootMotion": v["rootMotionAnimation"] != PG.ZERO, "sequences": v["hasSequences"]}
    return out


def other_skeleton_clip(k, types, facts, player_hash):
    """A JointAnimation of the game made for another skeleton."""
    ja = types.get(PG.JOINT_ANIM)
    for key in k.resources_of_type(ja["hash"])[:400]:
        f = facts(key.guid)
        if f and f["hierarchyHash"] != player_hash:
            return key.guid
    return None


def write_mod(work, mod, states):
    d = Path(tempfile.mkdtemp(dir=work, prefix=mod + "."))
    (d / "assets").mkdir()
    (d / "kiln.mod.json").write_text(json.dumps({"id": mod, "name": mod, "version": "0.0.1", "side": "both", "clients": "mod"}))
    (d / "assets" / PS.FILE_NAME).write_text(json.dumps({"format": 1, "states": states}, indent=1))
    return PS.load(d / "assets" / PS.FILE_NAME)


def neighbours(g):
    """Every supported neighbour of the graph, in graph order: (Machine/State, state node, machine node, Neighbour)."""
    out = []
    for m in g.machines:
        row = PG.SUPPORTED.get(m["debugName"])
        if row is None:
            continue
        seen = set()
        for s in m["states"]:
            st = g.by_id[s["value"]]
            if s["value"] in seen or (row[1] is not None and st["debugName"] not in row[1]):
                continue
            seen.add(s["value"])
            out.append((f"{m['debugName']}/{st['debugName']}", st, m, PG.Neighbour(g, st, m, row[0])))
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--graph")
    ap.add_argument("--game", default=str(GAME))
    ap.add_argument("--work", help="folder for the test mods (default: the system's temp folder)")
    ap.add_argument("--json", help="write the counts as JSON here (outside the repository: they are read from the game)")
    a = ap.parse_args()
    game = Path(a.game).expanduser()
    k = Kfc(game) if game.is_dir() and any(game.glob("*.kfc")) else None
    exe = next((game / n for n in ("enshrouded.exe", "enshrouded_server.exe") if (game / n).exists()), None) if k else None
    types = TypeDb.from_exe(exe) if exe else TypeDb.load(BUNDLED)
    if a.graph:
        src, raw = a.graph, Path(a.graph).expanduser().read_bytes()
    elif k is not None:
        src, raw = f"the installed game ({k.version})", PG.read_game_graph(k, types)
    elif os.environ.get("KILN_PLAYER_GRAPH") and Path(os.environ["KILN_PLAYER_GRAPH"]).exists():
        src, raw = os.environ["KILN_PLAYER_GRAPH"], Path(os.environ["KILN_PLAYER_GRAPH"]).read_bytes()
    else:
        print(f"SKIP: no player graph: no game at {game} and no --graph or $KILN_PLAYER_GRAPH")
        return 0
    print(f"graph: {src}, {len(raw)} B; reflection: {'the game exe' if exe else 'bundled'}; clip facts: {'the installed game' if k else 'the graph own clip nodes (no game)'}")
    dec, enc = Decoder(types, max_array=PG.MAX_ARRAY), Encoder(types)
    t0 = time.perf_counter()
    value = dec.decode(PG.GRAPH_TYPE, raw)
    t1 = time.perf_counter()
    back = enc.encode(PG.GRAPH_TYPE, value)
    t2 = time.perf_counter()
    facts = PG.ClipFacts(types, [], k) if k else graph_facts(value).get
    work = tempfile.TemporaryDirectory(dir=a.work)
    counts = {"graph": src, "bytes": len(raw), "decodeSeconds": round(t1 - t0, 3), "encodeSeconds": round(t2 - t1, 3), "sha256": {}}

    def keep(name, out):
        """The merged graph's SHA-256 per scenario, to compare two versions of the merge."""
        counts["sha256"][name] = hashlib.sha256(out).hexdigest() if out is not None else None

    # 1. no states
    check(back == raw, f"the game's graph decodes and encodes to its own {len(raw)} bytes (decode {t1 - t0:.2f} s, encode {t2 - t1:.2f} s)")
    out, rep = PG.merge(raw, types, [], facts)
    check(out is None and rep["graph"] == "untouched", "no mod: untouched, no graph")
    out, rep = PG.merge(raw, types, [write_mod(work.name, "pa-empty", {})], facts)
    check(out is None and rep["graph"] == "untouched", "a mod with no states: untouched, no graph")

    # 2. one state beside every supported neighbour, with the neighbour's own clip
    g = PG.Graph(dec.decode(PG.GRAPH_TYPE, raw))
    nbs = neighbours(g)
    misfits = [(name, nb.error) for name, _, _, nb in nbs if nb.error]
    check(not misfits, f"{len(nbs)} supported neighbours, every one fits the rules" + (f": {misfits}" if misfits else ""))
    per = []
    for name, st, m, nb in nbs:
        if nb.error:
            continue
        per.append({"like": name, "entry": "global, list" if nb.entry and len(nb.entry_ids) > 1 else "global" if nb.entry else "local only",
                    "entryIds": len(nb.entry_ids), "layerBlend": nb.layer is not None, "outs": len(nb.outs), "ins": len(nb.ins),
                    "lists": len(nb.lists), "listSizes": sorted(len(x["values"]) for x in nb.lists), "selects": len(nb.selects),
                    "nodes": 4 + (nb.layer is not None) + len(nb.outs) + 2 * len(nb.ins)})
    counts["neighbours"] = per
    states = {f"n{i:02d}": {"clip": nb.clip["animation"], "like": name} for i, (name, _, _, nb) in enumerate(nbs) if not nb.error}
    m_all = write_mod(work.name, "pa-all", states)
    t3 = time.perf_counter()
    out, rep = PG.merge(raw, types, [m_all], facts)
    t4 = time.perf_counter()
    keep("all", out)
    live = [e for e in rep["states"] if e["status"] == "live"]
    check(rep["graph"] == "merged" and len(live) == len(states),
          f"{len(states)} states, one beside each neighbour: {rep['graph']}, {len(live)} live ({t4 - t3:.2f} s)" + (f": {rep['reason']}" if rep["reason"] else "")
          + "".join(f"\n       {e['name']} ({e['like']}): {e['reason']}" for e in rep["states"] if e["status"] != "live"))
    counts["all"] = {k2: rep[k2] for k2 in ("machines", "nodesAdded", "lists", "listEntries", "selects", "usedInputIds", "bytes")}
    counts["mergeSeconds"] = round(t4 - t3, 3)
    if out is not None:
        print(f"     touched: {rep['nodesAdded']} nodes in {len(rep['machines'])} machines, {rep['listEntries']} ids into {rep['lists']} lists, "
              f"{rep['selects']} selects, {rep['usedInputIds']} usedInputIds; {rep['bytes']['before']} -> {rep['bytes']['after']} B")
        for x in rep["machines"]:
            print(f"       {x['machine']}: {x['states']} states, global transitions {x['globalTransitions'][0]} -> {x['globalTransitions'][1]}, "
                  f"{x['nodesAdded']} nodes" + (f", {x['nested']} nested" if x["nested"] else ""))
        mv = dec.decode(PG.GRAPH_TYPE, out)
        by = {n["value"]["id"]["value"]: n["value"] for n in mv["nodeDefinitions"]}
        same = differ = 0
        for i, (name, st, m, nb) in enumerate(nbs):
            if nb.error:
                continue
            c = dict(by[PG.node_id("pa-all", f"n{i:02d}", "clip")])
            want = dict(nb.clip)
            for f in PG.GENDER_GUIDS:
                want[f] = PG.ZERO
            for f in PG.GENDER_HASHES:
                want[f] = {"value": 0}
            c["id"] = want["id"]
            if c == want:
                same += 1
            else:
                differ += 1
                print(f"       clip node of {name} differs: {sorted(f for f in c if c[f] != want[f])}")
        check(differ == 0, f"each new clip node is its neighbour's (gender alternatives cleared): {same} of {same + differ}; duration, loop, root motion and "
                           f"sequences from the clip's resources agree with the game's node")

    # 3. the same definitions from two mods, in both orders
    names = list(states)
    half = {n: states[n] for n in names[::2]}, {n: states[n] for n in names[1::2]}
    ma, mb = write_mod(work.name, "pa-left", half[0]), write_mod(work.name, "pa-right", half[1])
    o1, r1 = PG.merge(raw, types, [ma, mb], facts)
    o2, r2 = PG.merge(raw, types, [mb, ma], facts)
    keep("two mods", o1)
    check(o1 is not None and o1 == o2 and r1 == r2, f"two mods ({len(half[0])} and {len(half[1])} states) in both orders: the same {len(o1 or b'')} bytes and report")

    # 4. 30 states beside one hammer state: nested; every game state, its id and its entry as before
    hammer = next(name for name, _, m, nb in nbs if m["debugName"] == "SM_Attack_Hammer_FullBody" and not nb.error)
    hn = next(nb for name, _, _, nb in nbs if name == hammer)
    m30 = write_mod(work.name, "pa-thirty", {f"h{i:02d}": {"clip": hn.clip["animation"], "like": hammer} for i in range(30)})
    out, rep = PG.merge(raw, types, [m30], facts)
    keep("thirty", out)
    ok = rep["graph"] == "merged" and all(e["status"] == "live" for e in rep["states"])
    mrow = next((x for x in rep["machines"] if x["machine"] == "SM_Attack_Hammer_FullBody"), {})
    check(ok and mrow.get("nested") == 1, f"30 states beside {hammer}: {rep['graph']}, nested machines {mrow.get('nested')}, "
                                          f"global transitions {mrow.get('globalTransitions')}" + (f": {rep['reason']}" if rep["reason"] else ""))
    if out is not None:
        before, after = dec.decode(PG.GRAPH_TYPE, raw), dec.decode(PG.GRAPH_TYPE, out)
        b_by = {n["value"]["id"]["value"]: n["value"] for n in before["nodeDefinitions"]}
        a_by = {n["value"]["id"]["value"]: n["value"] for n in after["nodeDefinitions"]}
        states_ok = all(a_by.get(i) == v for i, v in b_by.items() if v["type"] == "StateMachineState")
        entries_ok = True
        for i, v in b_by.items():
            if v["type"] == "StateMachine":
                n = v["numGlobalTransitions"]
                w = a_by[i]
                if w["globalTransitions"][:n] != v["globalTransitions"][:n] or any(a_by[e["conditionNode"]["value"]] != b_by[e["conditionNode"]["value"]]
                                                                                   for e in v["globalTransitions"][:n] if e["conditionNode"]["value"] in b_by
                                                                                   and b_by[e["conditionNode"]["value"]]["type"] == "IdParameterEqualsConstant"):
                    entries_ok = False
            if v["type"] == "StateMachineTransition" and a_by.get(i) != v:
                entries_ok = False
        top = max(v["numGlobalTransitions"] for v in a_by.values() if v["type"] == "StateMachine")
        check(states_ok, f"every one of the game's {sum(1 for v in b_by.values() if v['type'] == 'StateMachineState')} states is there with its id, unchanged")
        check(entries_ok, "every game state's global transitions, their single-id conditions and every local transition are unchanged")
        check(top <= PG.MAX_GLOBAL, f"no machine above {PG.MAX_GLOBAL} global transitions (the most: {top})")
        counts["thirty"] = {k2: rep[k2] for k2 in ("machines", "nodesAdded", "lists", "listEntries", "selects", "bytes")}

    # 5. refusals, the rest of the file loads
    player = facts(hn.clip["animation"])
    other = other_skeleton_clip(k, types, facts, player["hierarchyHash"]) if k else None
    if other is None and not k:
        other = "0f0f0f0f-0000-4000-8000-000000000001"
        fake = dict(player, hierarchyHash=player["hierarchyHash"] + 1)
        base = facts
        facts = lambda gd: fake if gd == other else base(gd)  # noqa: E731
    bad = {"unknown": {"clip": hn.clip["animation"], "like": "No_Such_State_Anywhere"},
           "ambiguous": {"clip": hn.clip["animation"], "like": "Attack_Heavy"},
           "idle-block": {"clip": hn.clip["animation"], "like": "Idle_Block"},
           "skeleton": {"clip": other or PG.ZERO, "like": hammer},
           "good-1": {"clip": hn.clip["animation"], "like": hammer},
           "good-2": {"clip": hn.clip["animation"], "like": "SM_Main/Parry_FullBody"}}
    out, rep = PG.merge(raw, types, [write_mod(work.name, "pa-bad", bad)], facts)
    keep("refusals", out)
    by_name = {e["name"]: e for e in rep["states"]}
    for name, word in (("unknown", "no state of the player's graph"), ("ambiguous", "several states have that name"),
                       ("idle-block", "Block_FullBody"), ("skeleton", "another skeleton")):
        e = by_name[name]
        check(e["status"] == "refused" and word in (e["reason"] or "") and "mod pa-bad" in e["reason"], f"{name}: {e['reason']}")
    check(rep["graph"] == "merged" and by_name["good-1"]["status"] == "live" and by_name["good-2"]["status"] == "live",
          "the file's other two states load")
    if a.json:
        Path(a.json).write_text(json.dumps(counts, indent=1) + "\n")
    work.cleanup()
    print(f"{'FAILED: ' + str(len(FAILS)) if FAILS else 'all passed'}")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())
