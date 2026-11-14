#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""The approved builds of kiln.toolwear against the program catalog (sigdb/programs/<side>/<exe sha256>.json).

wear.h approves a game build by its hash because the two debit sites read compiler-specific frames. This test keeps
that list honest: every approved build has a catalog, and in it durability_loss and actor_spawn_entity declare what
the hooks rely on:
  - durability_loss' iterator is ten slots (0x50 bytes): the context, four systems, ItemState and PlayerSkillsUi
    (other, read), Durability (other, write), HitEvent in, NotifyEvent out; so slot 5 is ItemState and slot 7 Durability;
  - actor_spawn_entity writes other entities' Durability and ItemState and reads the systems the debit needs;
  - both carry the contract hash recorded here: a game update that changes a program's lists changes its contract, and
    this test then fails until the frames were read again for that build.
Run: python3 native/services/items/tests/wear_catalog_test.py  (no game needed; exit code 0 = ok)
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[4]
CONTRACTS = {"durability_loss": "0x0dfee8d1", "actor_spawn_entity": "0x66a956b1"}
SIDES = {0: "server", 1: "client"}


def approved():
    text = (ROOT / "native/services/items/wear.h").read_text()
    return [(m[1], int(m[2])) for m in re.finditer(r'\{"([0-9a-f]{64})",\s*(\d),', text)]


def main():
    bad = []
    builds = approved()
    if len(builds) < 2:
        bad.append(f"wear.h lists {len(builds)} approved build(s): the reader of this test no longer finds them")
    for sha, side in builds:
        f = ROOT / "sigdb" / "programs" / SIDES[side] / f"{sha}.json"
        if not f.is_file():
            bad.append(f"{SIDES[side]} {sha[:12]}: approved in wear.h, but no program catalog {f.relative_to(ROOT)}")
            continue
        programs = {p["name"]: p for p in json.loads(f.read_text())["programs"]}
        for name, contract in CONTRACTS.items():
            p = programs.get(name)
            if p is None:
                bad.append(f"{SIDES[side]} {sha[:12]}: no program {name}")
            elif p["contract"] != contract:
                bad.append(f"{SIDES[side]} {sha[:12]}: {name} has contract {p['contract']}, the frames were read for {contract}")
        loss = programs.get("durability_loss", {}).get("lists", {})
        want = {"sys_read": ["BalancingRegistry", "g38_items::ItemRegistry", "g38_skilltree::SkillTree", "impact::AttributeRegistry"],
                "other_read": ["ItemState", "PlayerSkillsUi"], "other_write": ["Durability"], "events_in": ["HitEvent"], "events_out": ["NotifyEvent"]}
        if loss != want:
            bad.append(f"{SIDES[side]} {sha[:12]}: durability_loss declares {loss}, the weapon site reads slot 5 as ItemState and is handed Durability's accessor")
        if 1 + sum(len(v) for v in loss.values()) != 0x50 // 8:
            bad.append(f"{SIDES[side]} {sha[:12]}: durability_loss' iterator is not ten slots")
        spawn = programs.get("actor_spawn_entity", {}).get("lists", {})
        if not {"Durability", "ItemState"} <= set(spawn.get("other_write", [])):
            bad.append(f"{SIDES[side]} {sha[:12]}: actor_spawn_entity does not write other entities' Durability and ItemState")
        if not {"BalancingRegistry", "impact::AttributeRegistry", "g38_skilltree::SkillTree"} <= set(spawn.get("sys_read", [])):
            bad.append(f"{SIDES[side]} {sha[:12]}: actor_spawn_entity does not read the systems the debit needs")
    for b in bad:
        print("FAIL", b)
    print("items-wear-catalog-test:", "FAILED" if bad else "ok")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
