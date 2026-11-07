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


def check_glide_stick(side, image, doc, symbols, constants):
    # The boosted glider's pitch stick: PlayerInput.fromClient.movementStick, pinned by two patterns that must agree.
    check(constants["rules/input.stick"]["ok"] and constants["rules/input.stick"]["value"] == 0x2C0,
          side + " movement stick resolves to PlayerInput+0x2c0 from both captures")
    spec = doc["symbols"]["@rules/Input.stick"]["find"][0]
    at, holes = matches(image, symbols, spec)
    syms, consts, features = Resolver(changed(image, [(at, b"\x90")]), doc).run()
    check(not features["rules.glide_stick"] and features["rules.glide"] and features["rules.glide_view"],
          side + " without the stick pattern only rules.glide_stick is off (the camera pitch stands)")
    hole = next((offset, kind) for name, kind, offset in holes if name == "rules/input.stick")
    syms, consts, features = Resolver(changed(image, [(at + hole[0], (0x2C8).to_bytes(WIDTH[hole[1]], "little"))]), doc).run()
    check(not consts["rules/input.stick"]["ok"] and not features["rules.glide_stick"] and features["rules.glide"],
          side + " a disagreeing stick offset turns only rules.glide_stick off")


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
    check_boundaries(side, image, tree.doc, symbols)
    check_glide_stick(side, image, tree.doc, symbols, constants)
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
