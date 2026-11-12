#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Checks the player-state merge (anicook/playergraph.py) on the real player graph. Reads the game, writes nothing but two small test mods
(player-states.json and kiln.mod.json, no game data) into a temp folder under --work.

    python3 -I tools/animcook/tests/game_playergraph_check.py [--graph FILE.bin] [--game DIR] [--work DIR] [--json FILE]

The graph: --graph, else the installed game's (--game, default Steam's Enshrouded), else the file $KILN_PLAYER_GRAPH. Neither: prints SKIP
and exits 0. The clips' facts come from the installed game; without one, from the graph's own clip nodes (said in the output). The
reflection: the game's exe when there is a game (cached under $KILN_OUT/types), else the bundled one.

Checks: no state gives "untouched" and the graph encodes back to its bytes; one state beside every supported neighbour of group 1 and of
group 2, each with the neighbour's own clip, merges and its clip node equals the neighbour's; the overlay states that cannot be neighbours
are refused with their reason; an overlay machine filled to its room takes no more; groups 1 and 2 from two mods in both orders give the
same bytes; 30 states beside one hammer state nest and leave every game state, its id and its entry as they were; an unknown, an
ambiguous and an unsupported `like` and a clip of another skeleton are refused while the other states of the file load. --json writes the
counts and each merged graph's SHA-256. Exit 1 when a check fails.
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
OVERLAY_MACHINES = [m for m, row in PG.SUPPORTED.items() if row[0] == "overlay"]
GROUP2 = set(OVERLAY_MACHINES) | {"SM_Attack_Staff_FullBody", "SM_Attack_Wand_FullBody"}
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

    # 2. one state beside every supported neighbour of group 1, with the neighbour's own clip
    g = PG.Graph(dec.decode(PG.GRAPH_TYPE, raw))
    nbs = neighbours(g)
    g1 = [x for x in nbs if x[2]["debugName"] not in GROUP2]
    g2 = [x for x in nbs if x[2]["debugName"] in GROUP2]
    misfits = [(name, nb.error) for name, _, _, nb in g1 if nb.error]
    check(not misfits, f"group 1: {len(g1)} supported neighbours, every one fits the rules" + (f": {misfits}" if misfits else ""))
    per = []
    for name, st, m, nb in nbs:
        if nb.error:
            continue
        per.append({"like": name, "kind": nb.kind, "entry": "global, list" if nb.entry and len(nb.entry_ids) > 1 else "global" if nb.entry else "local only",
                    "entryIds": len(nb.entry_ids), "layerBlend": nb.layer is not None, "outs": len(nb.outs), "autoOuts": sum(1 for t in nb.outs if t["autoTransition"]),
                    "ins": len(nb.ins), "lists": len(nb.lists), "listSizes": sorted(len(x[PG.ENTRIES[nb.kind].values]) for x in nb.lists),
                    "selects": len(nb.selects), "nodes": 4 + (nb.layer is not None) + len(nb.outs) + 2 * len(nb.ins)})
    counts["neighbours"] = per

    def merge_beside(mod, items, label):
        """One state beside each (Machine/State, neighbour) with the neighbour's own clip; prints and checks the result."""
        sts = {f"n{i:02d}": {"clip": nb.clip["animation"], "like": name} for i, (name, nb) in enumerate(items)}
        t3 = time.perf_counter()
        out, rep = PG.merge(raw, types, [write_mod(work.name, mod, sts)], facts)
        t4 = time.perf_counter()
        live = [e for e in rep["states"] if e["status"] == "live"]
        check(rep["graph"] == "merged" and len(live) == len(sts),
              f"{label}: {len(sts)} states, one beside each neighbour: {rep['graph']}, {len(live)} live ({t4 - t3:.2f} s)" + (f": {rep['reason']}" if rep["reason"] else "")
              + "".join(f"\n       {e['name']} ({e['like']}): {e['reason']}" for e in rep["states"] if e["status"] != "live"))
        if out is None:
            return out, rep, t4 - t3
        print(f"     touched: {rep['nodesAdded']} nodes in {len(rep['machines'])} machines, {rep['listEntries']} ids into {rep['lists']} lists, "
              f"{rep['selects']} selects, {rep['usedInputIds']} usedInputIds; {rep['bytes']['before']} -> {rep['bytes']['after']} B; graphHash {rep['graphHash']}")
        for x in rep["machines"]:
            print(f"       {x['machine']}: {x['states']} states, global transitions {x['globalTransitions'][0]} -> {x['globalTransitions'][1]}, "
                  f"{x['nodesAdded']} nodes" + (f", {x['nested']} nested" if x["nested"] else ""))
        by = {n["value"]["id"]["value"]: n["value"] for n in dec.decode(PG.GRAPH_TYPE, out)["nodeDefinitions"]}
        same = differ = 0
        for i, (name, nb) in enumerate(items):
            c = dict(by[PG.node_id(mod, f"n{i:02d}", "clip")])
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
        check(differ == 0, f"{label}: each new clip node is its neighbour's (gender alternatives cleared): {same} of {same + differ}; duration, loop, root "
                           f"motion and sequences from the clip's resources agree with the game's node")
        return out, rep, t4 - t3

    out, rep, secs = merge_beside("pa-all", [(name, nb) for name, _, _, nb in g1 if not nb.error], "group 1")
    keep("all", out)
    counts["all"] = {k2: rep[k2] for k2 in ("machines", "nodesAdded", "lists", "listEntries", "selects", "usedInputIds", "bytes", "graphHash")}
    counts["mergeSeconds"] = round(secs, 3)
    states = {f"n{i:02d}": {"clip": nb.clip["animation"], "like": name} for i, (name, _, _, nb) in enumerate(g1) if not nb.error}

    # 3. group 2: one state beside each supported neighbour; the states that cannot be neighbours refused with their reason
    expected = {f"{m}/DoNothing": "rest state" for m in OVERLAY_MACHINES if m != "SM_Wand_Overlay"}
    expected.update({f"SM_Ranged_Attack_Additive/{x}": "holds the nested machine" for x in ("Ground_Bow_Overlay", "Staff_Overlay", "Air_Bow_Overlay")})
    expected["SM_Wand_Overlay/Attack_Magic_Wand_01_bis"] = "no overlay condition of its own"
    refused = {name: nb.error for name, _, _, nb in g2 if nb.error}
    for name, word in sorted(expected.items()):
        check(word in (refused.get(name) or ""), f"group 2 refuses {name}: {refused.get(name)}")
    for name in sorted(set(refused) - set(expected)):
        print(f"     also refused (not on the brief's list): {name}: {refused[name]}")
        counts.setdefault("alsoRefused", {})[name] = refused[name]
    good2 = [(name, nb) for name, _, _, nb in g2 if not nb.error]
    out, rep, _ = merge_beside("pa-group2", good2, "group 2")
    keep("group 2", out)
    counts["group2"] = {k2: rep[k2] for k2 in ("machines", "nodesAdded", "lists", "listEntries", "selects", "usedInputIds", "bytes", "graphHash")}
    if out is not None:
        check(rep["usedInputIds"] == sum(1 for _, nb in good2 if nb.kind == "state"), f"usedInputIds take the {rep['usedInputIds']} state ids, no overlay id")
    first = next(name for name, _, _, nb in g2 if not nb.error)
    sts = {f"r{i:02d}": {"clip": next(nb for n, nb in good2 if n == first).clip["animation"], "like": name} for i, name in enumerate(sorted(refused))}
    sts["ok"] = {"clip": next(nb for n, nb in good2 if n == first).clip["animation"], "like": first}
    out, rep = PG.merge(raw, types, [write_mod(work.name, "pa-g2bad", sts)], facts)
    by_like = {e["like"]: e for e in rep["states"]}
    check(all(by_like[n]["status"] == "refused" and "mod pa-g2bad" in by_like[n]["reason"] for n in refused) and by_like[first]["status"] == "live",
          f"the {len(refused)} refused in one file, each with its reason, and the file's other state loads")
    # an overlay machine filled to its room, and one more
    bow = next(x for x in g2 if x[2]["debugName"] == "SM_Ground_Bow_Overlay" and not x[3].error)
    room = PG.MAX_GLOBAL - bow[2]["numGlobalTransitions"]
    out, rep = PG.merge(raw, types, [write_mod(work.name, "pa-room", {f"b{i:02d}": {"clip": bow[3].clip["animation"], "like": bow[0]} for i in range(room + 1)})], facts)
    keep("overlay room", out)
    live = [e for e in rep["states"] if e["status"] == "live"]
    last = max(rep["states"], key=lambda e: e["name"].encode())
    mrow = next((x for x in rep["machines"] if x["machine"] == "SM_Ground_Bow_Overlay"), {})
    check(rep["graph"] == "merged" and len(live) == room and last["status"] == "refused" and f"has room for {room} new states" in last["reason"]
          and mrow.get("nested") == 0 and mrow.get("globalTransitions") == [PG.MAX_GLOBAL - room, PG.MAX_GLOBAL],
          f"{room + 1} states beside {bow[0]}: {len(live)} live, the last refused: {last['reason']}")

    # 4. group 1 and group 2 from two mods, in both orders
    both = dict(states, **{f"g{i:02d}": {"clip": nb.clip["animation"], "like": name} for i, (name, nb) in enumerate(good2)})
    names = list(both)
    half = {n: both[n] for n in names[::2]}, {n: both[n] for n in names[1::2]}
    ma, mb = write_mod(work.name, "pa-left", half[0]), write_mod(work.name, "pa-right", half[1])
    o1, r1 = PG.merge(raw, types, [ma, mb], facts)
    o2, r2 = PG.merge(raw, types, [mb, ma], facts)
    keep("two mods", o1)
    check(o1 is not None and o1 == o2 and r1 == r2 and all(e["status"] == "live" for e in r1["states"]),
          f"groups 1 and 2 from two mods ({len(half[0])} and {len(half[1])} states) in both orders: the same {len(o1 or b'')} bytes and report, graphHash {r1['graphHash']}")

    # 5. 30 states beside one hammer state: nested; every game state, its id and its entry as before
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

    # 6. refusals, the rest of the file loads
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
