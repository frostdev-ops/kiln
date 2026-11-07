#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Check rules signatures and failure cases against explicit, read-only game executable copies.

    python native/services/rules/rules_sigdb_test.py --server <server.exe> --client <client.exe>

Requires Capstone. No executable is launched or modified; failure cases change an in-memory image only.
"""
import argparse
import copy
from pathlib import Path
import sys

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools" / "sigport"))

from capstone import Cs, CS_ARCH_X86, CS_MODE_64  # noqa: E402
from defs import Tree  # noqa: E402
from peimg import Image  # noqa: E402
from sigdb_check import Resolver, WIDTH, parse  # noqa: E402


def check(ok, message):
    if not ok:
        raise AssertionError(message)
    print("PASS " + message, flush=True)


def matches(image, symbols, spec):
    lo, hi = image.text_va, image.text_va + len(image.text)
    if "scope" in spec:
        scope = symbols[spec["scope"]["symbol"]]
        check(scope["ok"], "mutation scope resolves: " + spec["scope"]["symbol"])
        lo = scope["rva"]
        hi = min(hi, lo + spec["scope"]["size"])
    rx, holes, _ = parse(spec["pattern"])
    hits = Resolver(image, {}).scan(rx, lo, hi)
    check(len(hits) == 1, "pattern has one match in its scope")
    return hits[0], holes


def changed(image, edits):
    raw = bytearray(image.img)
    for offset, data in edits:
        raw[offset:offset + len(data)] = data
    out = copy.copy(image)
    out.img = bytes(raw)
    return out


def guard_document(doc):
    # Guard patterns have no dependencies on the other services. Keep failure checks focused and fast.
    return {
        "symbols": {k: v for k, v in doc["symbols"].items() if k.startswith(("rules/client.", "@rules/client."))},
        "constants": {k: v for k, v in doc["constants"].items() if k.startswith("rules/client.")},
        "features": {k: v for k, v in doc["features"].items() if k in ("rules.healthbar_guard", "rules.camera_check")},
    }


def check_boundaries(side, image, doc, symbols):
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    names = ["rules/FlyGate", "rules/ZoneFlag", "rules/ShroudDrain", "rules/GlideGravity", "rules/GlideStamina"]
    if side == "client":
        names += ["rules/client.HealthbarRay", "rules/client.CameraInput"]
    for name in names:
        site = symbols[name]["rva"]
        found = None
        for spec in doc["symbols"][name]["find"]:
            start, _ = matches(image, symbols, spec)
            if start + spec.get("offset", 0) != site:
                continue
            found = next((i for i in decoder.disasm(image.img[start:site + 16], start) if i.address == site), None)
            break
        check(found is not None, f"{side} {name}: mid hook is on an instruction boundary ({site:#x})")
        print(f"     {found.mnemonic} {found.op_str}")
        if name == "rules/ShroudDrain":
            check(found.mnemonic == "movzx" and found.op_str == "ecx, bl", f"{side} Shroud hook precedes the fog dispatch")
        if name == "rules/client.HealthbarRay":
            check(found.mnemonic == "call", "health-bar ray hook precedes the line-of-sight call")


def check_guard_failures(image, doc, symbols, constants):
    guards = guard_document(doc)
    for field, feature, unaffected in [
        ("rules/client.hb.player_z", "rules.healthbar_guard", "rules.camera_check"),
        ("rules/client.cam.pivot", "rules.camera_check", "rules.healthbar_guard"),
    ]:
        edits = []
        for definition in guards["symbols"].values():
            for spec in definition["find"]:
                if not any(h[0] == field for h in parse(spec["pattern"])[1]):
                    continue
                at, holes = matches(image, symbols, spec)
                for name, kind, offset in holes:
                    if name == field:
                        edits.append((at + offset, (constants[field]["value"] + 4).to_bytes(WIDTH[kind], "little")))
        check(bool(edits), field + " has captured layout fields to mutate")
        syms, consts, features = Resolver(changed(image, edits), guards).run()
        check(all(syms[n]["ok"] for n in guards["features"][feature]["symbols"]), field + " mismatch preserves pattern matches")
        check(not consts[field]["ok"] and not features[feature], field + " fingerprint mismatch disables only its guard")
        check(features[unaffected], unaffected + " still resolves after the other guard's layout changed")

    for name, feature, unaffected in [
        ("rules/client.HealthbarRay", "rules.healthbar_guard", "rules.camera_check"),
        ("@rules/client.LineOfSight.check", "rules.healthbar_guard", "rules.camera_check"),
        ("rules/client.CameraInput", "rules.camera_check", "rules.healthbar_guard"),
    ]:
        spec = guards["symbols"][name]["find"][0]
        at, _ = matches(image, symbols, spec)
        syms, _, features = Resolver(changed(image, [(at, b"\x90")]), guards).run()
        check(not syms[name]["ok"] and not features[feature], name + " missing pattern disables its guard")
        check(features[unaffected], unaffected + " remains available with the other guard's pattern missing")


def check_glide_stick(side, image, doc, symbols, constants, baseline):
    # The boosted glider's pitch stick: PlayerInput.fromClient.movementStick, pinned by two patterns that must agree.
    check(constants["rules/input.stick"]["ok"] and constants["rules/input.stick"]["value"] == 0x2C0,
          side + " movement stick resolves to PlayerInput+0x2c0 from both captures")
    spec = doc["symbols"]["@rules/Input.stick"]["find"][0]
    at, holes = matches(image, symbols, spec)
    for fingerprint in doc["symbols"]["@rules/Input.stick"]["find"][1:]:
        matches(image, symbols, fingerprint)
    loco_spec = doc["symbols"]["@rules/Loco.input"]["find"][0]
    loco_at, loco_holes = matches(image, symbols, loco_spec)
    for site, captures in [(at, holes), (loco_at, loco_holes)]:
        offset = next(offset for name, kind, offset in captures if name == "rules/input.stick" and kind == "d32")
        check(int.from_bytes(image.img[site + offset:site + offset + 4], "little") == 0x2C0,
              side + " each independent movement-stick capture is PlayerInput+0x2c0")

    # Do not trust a wildcarded second movss: reading another field into local +z would no longer prove stick.y
    # is forward. A changed forward field must disable this optional feature while preserving every other feature.
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    size = parse(spec["pattern"])[2]
    forward = next(i for i in decoder.disasm(image.img[at:at + size], at) if i.bytes[:4] == b"\xf3\x0f\x10\x93")
    forward_offset = forward.address + 4
    check(int.from_bytes(image.img[forward_offset:forward_offset + 4], "little") == 0x2C4,
          side + " local forward input comes from movementStick.y at +0x2c4")
    _, _, features = Resolver(changed(image, [(forward_offset, (0x2C8).to_bytes(4, "little"))]), doc).run()
    check(not features["rules.glide_stick"] and all(features[n] == ok for n, ok in baseline.items() if n != "rules.glide_stick"),
          side + " a changed forward-axis load disables only rules.glide_stick")
    edits = []
    for site, captures in [(at, holes), (loco_at, loco_holes)]:
        offset = next(offset for name, kind, offset in captures if name == "rules/input.stick")
        edits.append((site + offset, (0x2C8).to_bytes(4, "little")))
    _, consts, features = Resolver(changed(image, edits), doc).run()
    check(consts["rules/input.stick"]["ok"] and not features["rules.glide_stick"] and
          all(features[n] == ok for n, ok in baseline.items() if n != "rules.glide_stick"),
          side + " agreeing base captures cannot drift away from the pinned forward-axis field")
    syms, consts, features = Resolver(changed(image, [(at, b"\x90")]), doc).run()
    check(not features["rules.glide_stick"] and features["rules.glide"] and features["rules.glide_view"],
          side + " without the stick pattern only rules.glide_stick is off (the camera pitch stands)")
    hole = next((offset, kind) for name, kind, offset in holes if name == "rules/input.stick")
    syms, consts, features = Resolver(changed(image, [(at + hole[0], (0x2C8).to_bytes(WIDTH[hole[1]], "little"))]), doc).run()
    check(not consts["rules/input.stick"]["ok"] and not features["rules.glide_stick"] and features["rules.glide"] and
          features["rules.glide_view"],
          side + " a disagreeing stick offset turns only rules.glide_stick off")


def check_storage(side, image, doc, symbols, constants):
    # Storage's dependencies are isolated so a failed optional hook cannot suppress building/crafting elsewhere.
    name = "rules/UpgradeStoragePaid"
    subset = {
        "symbols": {k: doc["symbols"][k] for k in (name, "rules/ApplyMaterials")},
        "constants": {"items/tx.operator": {"value": constants["items/tx.operator"]["value"]}},
        "features": {"rules.storage": doc["features"]["rules.storage"],
                     "rules.costs": {"symbols": ["rules/ApplyMaterials"]}},
    }
    spec = doc["symbols"][name]["find"][0]
    at, _ = matches(image, symbols, spec)
    ret = symbols[name]["rva"]
    call = next(Cs(CS_ARCH_X86, CS_MODE_64).disasm(image.img[ret - 5:ret], ret - 5))
    check(call.mnemonic == "call" and int(call.op_str, 16) == symbols["rules/InventoryPay"]["rva"],
          side + " storage return address follows the resolved inventory payment call")
    _, _, base = Resolver(image, subset).run()
    check(base["rules.storage"] and base["rules.costs"], side + " isolated storage baseline resolves")
    _, _, missing = Resolver(changed(image, [(at, b"\x90")]), subset).run()
    check(not missing["rules.storage"] and missing["rules.costs"], side + " missing storage pattern disables only storage")
    size = parse(spec["pattern"])[2]
    duplicate = image.text_va + len(image.text) - size
    _, _, ambiguous = Resolver(changed(image, [(duplicate, image.img[at:at + size])]), subset).run()
    check(not ambiguous["rules.storage"] and ambiguous["rules.costs"], side + " ambiguous storage pattern disables only storage")
    no_operator = copy.deepcopy(subset)
    no_operator["constants"]["items/tx.operator"] = {"min_votes": 1}
    _, _, absent = Resolver(image, no_operator).run()
    check(not absent["rules.storage"] and absent["rules.costs"], side + " missing transaction operator disables only storage")


def check_time_scale(side, image, doc, symbols):
    time = {
        "symbols": {k: v for k, v in doc["symbols"].items() if k.startswith(("time/", "@time."))},
        "constants": {k: v for k, v in doc["constants"].items() if k.startswith("time/")},
        "features": {k: v for k, v in doc["features"].items() if k.startswith("time.")},
    }
    name = "time/SetTimeScale"
    spec = time["symbols"][name]["find"][0]
    at, _ = matches(image, symbols, spec)
    _, _, base = Resolver(image, time).run()
    check(base["time.scale"] and base["time.read"] and base["time.set"], side + " clock read/set/scale baseline resolves")
    size = parse(spec["pattern"])[2]
    for edits, reason in [([(at, b"\x90")], "missing"),
                          ([(image.text_va + len(image.text) - size, image.img[at:at + size])], "ambiguous")]:
        _, _, features = Resolver(changed(image, edits), time).run()
        check(not features["time.scale"] and features["time.read"] and features["time.set"],
              side + " " + reason + " scale hook preserves the existing time API")


def check_creative(side, image, doc, symbols):
    names = ["rules.storage", "rules.craft", "rules.consume", "items.pide_options"]
    if side == "client":
        names += ["rules.map_reveal", "rules.craft_ui"]
    selected = set()
    for name in names:
        selected.update(doc["features"][name].get("symbols", []))
    # Follow scoped definitions; captured-only targets (InventoryPay, RecipePayAlt, level indices) have no definition.
    pending = list(selected)
    while pending:
        for spec in doc["symbols"].get(pending.pop(), {}).get("find", []):
            dep = spec.get("scope", {}).get("symbol")
            if dep and dep not in selected:
                selected.add(dep)
                pending.append(dep)
    subset = {"symbols": {n: doc["symbols"][n] for n in selected if n in doc["symbols"]},
              "constants": {n: doc["constants"][n] for name in names
                            for n in doc["features"][name].get("constants", [])},
              "features": {n: doc["features"][n] for n in names}}
    _, _, baseline = Resolver(image, subset).run()
    check(all(baseline.values()), side + " creative hooks and gear options resolve independently")

    calls = [("rules/RecipePaid", "rules/InventoryPay", 0),
             ("rules/RecipePaid", "rules/RecipePayAlt", 7),
             ("rules/AmmoConsumed", "items/InventoryRemoveItems", 0),
             ("rules/CostConsumed", "items/InventoryRemoveItems", 0)]
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    for name, target, delta in calls:
        ret = symbols[name]["rva"] + delta
        instruction = next(decoder.disasm(image.img[ret - 5:ret], ret - 5))
        check(instruction.mnemonic == "call" and int(instruction.op_str, 16) == symbols[target]["rva"],
              side + " " + name + f"+{delta}: direct call returns from the expected target")
    spawn = symbols["items/PideSpawn"]["rva"]
    start = symbols["items/CreatePide"]["rva"]
    check(any(i.address == spawn for i in decoder.disasm(image.img[start:spawn + 16], start)),
          side + " gear options mid hook is on an instruction boundary inside createPide")
    current = int.from_bytes(image.img[symbols["items/PideLevelIndex"]["rva"]:][:4], "little")
    maximum = int.from_bytes(image.img[symbols["items/PideLevelMaxIndex"]["rva"]:][:4], "little")
    check({current, maximum} == {0, 1}, side + " the Level override has distinct in-bounds current and max indices")

    cases = [("rules/UpgradeStoragePaid", "rules.storage"), ("rules/RecipePaid", "rules.craft"),
             ("rules/AmmoConsumed", "rules.consume"), ("rules/CostConsumed", "rules.consume"),
             ("items/PideSpawn", "items.pide_options"), ("@items/CreatePide.fields", "items.pide_options")]
    if side == "client":
        cases += [("@rules/client.FogDiscover.body", "rules.map_reveal"),
                  ("@rules/client.FogDiscover.caller", "rules.map_reveal"),
                  ("rules/client.CraftableAmount", "rules.craft_ui")]
    for name, feature in cases:
        spec = subset["symbols"][name]["find"][0]
        at, _ = matches(image, symbols, spec)
        size = parse(spec["pattern"])[2]
        duplicate = image.text_va + len(image.text) - size
        if "scope" in spec:
            duplicate = symbols[spec["scope"]["symbol"]]["rva"] + spec["scope"]["size"] - size
        check(duplicate >= at + size or duplicate + size <= at, name + " mutation does not overlap its original pattern")
        for edits, reason in [([(at, b"\x90")], "missing"),
                              ([(duplicate, image.img[at:at + size])], "ambiguous")]:
            _, _, features = Resolver(changed(image, edits), subset).run()
            check(not features[feature] and all(ok for n, ok in features.items() if n != feature),
                  side + " " + reason + " " + name + " disables only " + feature)


def run(side, path):
    tree = Tree(ROOT / "sigdb" / "symbols" / (side + ".json"))
    check(not tree.problems, side + " definition tree has no duplicate entries")
    image = Image(path)
    symbols, constants, features = Resolver(image, tree.doc).run()
    rules = {name: ok for name, ok in features.items() if name.startswith("rules.")}
    required = {"rules." + name for name in ("fly", "zone", "cursor", "shroud", "glide", "glide_view", "glide_stick", "costs", "dig")}
    if side == "client":
        required |= {"rules.healthbar_guard", "rules.camera_check"}
    check(required.issubset(rules), side + " defines every expected rules feature")
    for name, ok in rules.items():
        check(ok, side + " " + name + " resolves")
    check(symbols["@rules/ShroudDrain.delta"]["ok"] and symbols["rules/StepDelta"]["ok"],
          side + " Shroud duration capture agrees with the glider's StepDelta")
    check_storage(side, image, tree.doc, symbols, constants)
    check_time_scale(side, image, tree.doc, symbols)
    check_creative(side, image, tree.doc, symbols)
    check_boundaries(side, image, tree.doc, symbols)
    check_glide_stick(side, image, tree.doc, symbols, constants, features)
    if side == "client":
        check_guard_failures(image, tree.doc, symbols, constants)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", type=Path, required=True)
    parser.add_argument("--client", type=Path, required=True)
    args = parser.parse_args()
    run("server", args.server)
    run("client", args.client)
    print("rules signatures: all checks passed")


if __name__ == "__main__":
    main()
