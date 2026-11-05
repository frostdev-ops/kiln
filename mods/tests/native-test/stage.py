#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""native-test: the native mod loader's lab cases (docs/kiln/native-mods.md). Builds nothing: run
`KILN_OUT=... native/build.sh kiln-native-test kiln-native-plain kiln-native-plugin` first.

  stage.py library <dir>                 every case as <dir>/native/<id>/ (+ <dir>/plugin/native-plugin.dll)
  stage.py dropins <kiln dir>            the cases as drop-ins: <kiln>/native/<id>/ (legacy layout, no profile)
  stage.py profile <kiln dir> <library>  write <kiln>/profile.json (Z:\\ paths into <library>), chain.ini [profile]
        [--kiln <mod folder>]...         managed mods (folders with kiln.mod.json) to list as "kiln" entries
        [--config-dir <dir>] [--data-dir <dir>] [--dropins] [--id <profile id>] [--generation N] [--once]
        (--once writes profile.once.json + chain/once.ini instead, for a play-once mode=profile start)

Expected results (kiln.log "native-test <id>: ... PASS|FAIL", state/native.json) are in CASES below.
"""
import json
import os
import shutil
import sys

OUT = os.path.abspath(os.environ.get("KILN_OUT", os.path.join(os.path.dirname(__file__), "../../../native/build")))
BIN = os.path.join(OUT, "cmake", "bin", "samples", "native-test")

# id -> (native.json fields, dll source, expected state on a server with hooks, note)
CASES = {
    "native-test-early": ({"phase": "early", "init": "kiln_native_init", "side": "both"}, "native-test", "loaded"),
    "native-test-plain": ({"phase": "early", "side": "both"}, "native-plain", "loaded"),
    "native-test-boot-a": ({"phase": "boot", "init": "kiln_native_init", "side": "both", "after": ["native-test-boot-b"]},
                           "native-test", "loaded"),
    "native-test-boot-b": ({"phase": "boot", "init": "kiln_native_init", "side": "both", "requires": ["native-test-early"]},
                           "native-test", "loaded"),
    "native-test-boot-fail": ({"phase": "boot", "init": "kiln_native_init", "side": "both"}, "native-test", "failed"),
    "native-test-boot-unsupported": ({"phase": "boot", "init": "kiln_native_init", "side": "both"}, "native-test", "unsupported"),
    "native-test-boot-fault": ({"phase": "boot", "init": "kiln_native_init", "side": "both"}, "native-test", "failed"),
    "native-test-world": ({"phase": "world", "init": "kiln_native_init", "side": "both"}, "native-test", "loaded"),
    "native-test-dllmain": ({"phase": "dllmain", "side": "both"}, "native-plain", "loaded"),
    "native-test-cycle-a": ({"phase": "boot", "side": "both", "after": ["native-test-cycle-b"]}, "native-plain", "failed"),
    "native-test-cycle-b": ({"phase": "boot", "side": "both", "after": ["native-test-cycle-a"]}, "native-plain", "failed"),
    "native-test-needs-missing": ({"phase": "boot", "side": "both", "requires": ["native-test-nowhere"]}, "native-plain", "failed"),
    "native-test-clientside": ({"phase": "early"}, "native-plain", "skipped"),  # side defaults to client
    "native-test-off": ({"phase": "early", "side": "both", "enabled": False}, "native-plain", "disabled"),
    "native-test-userdisabled": ({"phase": "early", "side": "both"}, "native-plain", "disabled"),
    "native-test-missingdll": ({"phase": "early", "side": "both", "dlls": ["nowhere.dll"]}, "native-plain", "failed"),
    "native-test-oldkiln": ({"phase": "early", "side": "both", "kiln": "<0.1"}, "native-plain", "failed"),
    "native-test-untested": ({"phase": "early", "side": "both", "builds": ["0" * 64]}, "native-plain", "loaded"),
}
USER_DISABLED = ["native-test-userdisabled"]


def winpath(p):
    return "Z:" + os.path.abspath(p).replace("/", "\\")


def stage_case(root, mid):
    fields, src, _ = CASES[mid]
    d = os.path.join(root, mid)
    if os.path.exists(d):
        shutil.rmtree(d)
    os.makedirs(d)
    dll = mid + ".dll"
    if "dlls" not in fields:
        shutil.copy(os.path.join(BIN, src + ".dll"), os.path.join(d, dll))
    manifest = {"format": 1, "id": mid, "name": mid, "version": "1.0.0", "dlls": [dll]}
    manifest.update(fields)
    text = json.dumps(manifest, indent=2)
    if mid == "native-test-early":  # JSONC: comments and a trailing comma are allowed in native.json
        text = "// native-test: comments and trailing commas are allowed\n" + text[:-2] + ",\n}\n"
    open(os.path.join(d, "native.json"), "w").write(text)
    return d


def library(lib):
    os.makedirs(os.path.join(lib, "native"), exist_ok=True)
    for mid in CASES:
        stage_case(os.path.join(lib, "native"), mid)
    os.makedirs(os.path.join(lib, "plugin"), exist_ok=True)
    shutil.copy(os.path.join(BIN, "native-plugin.dll"), os.path.join(lib, "plugin", "native-plugin.dll"))
    print(f"native-test: {len(CASES)} cases + native-plugin.dll in {lib}")


def user_state(config_dir):
    os.makedirs(config_dir, exist_ok=True)
    json.dump({"format": 1, "mods": {m: {"enabled": False} for m in USER_DISABLED}},
              open(os.path.join(config_dir, "native.json"), "w"), indent=2)


def write_chain_profile(kiln, entries, once):
    """chain.ini [profile] (keeping [chain] lines) or chain/once.ini."""
    if once:
        os.makedirs(os.path.join(kiln, "chain"), exist_ok=True)
        open(os.path.join(kiln, "chain", "once.ini"), "w", newline="\r\n").write(
            "; native-test (play-once profile)\n[profile]\n" + "".join(e + "\n" for e in entries))
        return
    chain_ini = os.path.join(kiln, "chain.ini")
    keep = []
    if os.path.exists(chain_ini):
        section = "chain"
        for line in open(chain_ini, encoding="utf-8-sig").read().splitlines():
            s = line.strip()
            if s.startswith("[") and s.endswith("]"):
                section = s[1:-1].strip().lower()
            if section != "profile":
                keep.append(line)
    text = "\n".join(keep).rstrip() + ("\n\n" if keep else "") + "[profile]\n" + "".join(e + "\n" for e in entries)
    open(chain_ini + ".tmp", "w", newline="\r\n").write(text)
    os.replace(chain_ini + ".tmp", chain_ini)


def profile(kiln, lib, args):
    kiln_mods, config_dir, data_dir, dropins, pid, gen, once = [], None, None, False, "native-test", 1, False
    i = 0
    while i < len(args):
        a = args[i]
        if a == "--kiln":
            kiln_mods.append(os.path.abspath(args[i + 1])); i += 1
        elif a == "--config-dir":
            config_dir = os.path.abspath(args[i + 1]); i += 1
        elif a == "--data-dir":
            data_dir = os.path.abspath(args[i + 1]); i += 1
        elif a == "--dropins":
            dropins = True
        elif a == "--id":
            pid = args[i + 1]; i += 1
        elif a == "--generation":
            gen = int(args[i + 1]); i += 1
        elif a == "--once":
            once = True
        else:
            sys.exit(f"unknown option {a}")
        i += 1
    config_dir = config_dir or os.path.join(lib, "profiles", pid, "config")
    user_state(config_dir)
    natives = [{"id": m, "version": "1.0.0", "path": winpath(os.path.join(lib, "native", m))} for m in CASES]
    kmods = []
    for folder in kiln_mods:
        man = json.load(open(os.path.join(folder, "kiln.mod.json")))
        kmods.append({"id": man["id"], "version": man.get("version"), "path": winpath(folder), "enabled": True})
    doc = {
        "format": 1, "id": pid, "name": f"Kiln test profile {pid}", "generation": gen, "writer": "native-test stage.py",
        "pathMode": "z", "configDir": winpath(config_dir), "dataDir": winpath(data_dir) if data_dir else None,
        "dropins": dropins,
        "mods": {"kiln": kmods, "native": natives, "plugin": [{"id": "native-plugin", "path": winpath(os.path.join(lib, "plugin", "native-plugin.dll"))}],
                 "emf": []},
    }
    name = "profile.once.json" if once else "profile.json"
    path = os.path.join(kiln, name)
    json.dump(doc, open(path + ".tmp", "w"), indent=2)
    os.replace(path + ".tmp", path)
    write_chain_profile(kiln, [winpath(os.path.join(lib, "native", "native-test-dllmain", "native-test-dllmain.dll"))], once)
    print(f"native-test: {path} ({len(kmods)} kiln, {len(natives)} native, 1 plugin; config {config_dir})")


def main(argv):
    if len(argv) >= 3 and argv[1] == "library":
        library(os.path.abspath(argv[2]))
    elif len(argv) >= 3 and argv[1] == "dropins":
        kiln = os.path.abspath(argv[2])
        for mid in CASES:
            stage_case(os.path.join(kiln, "native"), mid)
        user_state(os.path.join(kiln, "config"))
        write_chain_profile(kiln, ["native\\native-test-dllmain\\native-test-dllmain.dll"], False)
        print(f"native-test: {len(CASES)} drop-ins in {kiln}/native")
    elif len(argv) >= 4 and argv[1] == "profile":
        profile(os.path.abspath(argv[2]), os.path.abspath(argv[3]), argv[4:])
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
