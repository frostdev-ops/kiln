#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
# Lab only (task t3, PlayerChecks.cs): writes waiting instance-data records of the two simulated characters of
# fixtures/player-visits.log into a stopped lab's newest committed mod-data generation, as Kiln.Saves would have written
# them for a player who left: character A with two equal swords in flat slots 10 and 11, character B with one in slot 3,
# each with its witness. The generation stays the world file's own (same revision and time), so the store reads it as
# Exact. Usage: python3 -I seed-character.py <lab dir>
import hashlib
import json
import os
import sys

ACCOUNT = 0x1111222233334444
A ="aaaaaaaa-0000-4000-8000-0000000000a1".replace("-", "")
B = "bbbbbbbb-0000-4000-8000-0000000000b2".replace("-", "")
SWORD = 0x43108313  # Weapon_T1_1H_SwordEpic_01
REC = [SWORD, 1, 10, 100, 100, 0, 0, 0, 3, 0, 0, 0]  # item, count, level, dur, durMax, fuel, fuelMax, dye, perks, unlocked, gem, gemLevel
MOD = "instances-test"


def bundle(char, slots, rev):
    key = f"{ACCOUNT:016x}.{char}"
    witness = [[s] + REC for s, _, _ in slots]
    records = [{"id": f"{char[:8]}{s:08x}", "slot": s, "rec": REC, "seen": rev, "state": "waiting",
                "why": "the player left with it", "since": rev, "keys": {"t3.mark": {"Label": label, "N": n}}}
               for s, label, n in slots]
    holder = {"holder": {"kind": "character", "character": key}, "last": rev,
              "witness": {str(rev): {"settled": True, "items": witness}}, "records": records}
    return f"character-instances/{key}/{MOD}", {"format": 1, "scope": f"character-instances/{key}/{MOD}", "holders": [holder]}


def main():
    lab = sys.argv[1]
    worlds = os.path.join(lab, "kiln", "data", "worlds")
    (world,) = os.listdir(worlds)
    mdir = os.path.join(worlds, world, "manifests")
    committed = sorted(f for f in os.listdir(mdir) if not f.endswith(".pending.json"))
    if not committed:
        sys.exit("no committed generation: boot the lab once first")
    path = os.path.join(mdir, committed[-1])
    man = json.load(open(path))
    rev = man["revision"]
    for char, slots in ((A, [(10, "A-10", 10), (11, "A-11", 11)]), (B, [(3, "B-3", 3)])):
        scope, doc = bundle(char, slots, rev)
        data = json.dumps(doc, separators=(",", ":")).encode()
        sha = hashlib.sha256(data).hexdigest()
        bdir = os.path.join(worlds, world, "blobs", sha[:2])
        os.makedirs(bdir, exist_ok=True)
        with open(os.path.join(bdir, sha + ".json"), "wb") as f:
            f.write(data)
        man["bundles"][scope] = {"sha256": sha, "size": len(data)}
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(man, f, separators=(",", ":"))
    os.replace(tmp, path)
    print(f"seeded 3 records of 2 characters into {committed[-1]} (revision {rev})")


if __name__ == "__main__":
    main()
