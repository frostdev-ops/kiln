#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A server lab whose paths look like a Windows player's: spaces, parentheses, Cyrillic and CJK in the game folder and
in the launcher's Library, with one mod of every kind in a profile.

    tools/lab/winpaths.py new <root> <port>     lab.sh new + the folder layout below + a Wine drive D: for <root>
    tools/lab/winpaths.py clone <root> <to>     a reflinked copy with ASCII folder names (WINPATHS_ASCII=1), as the control
    tools/lab/winpaths.py stage <root>          fixtures, the Library, kiln/profile.json, chain.ini (after deploy-server.sh)
    tools/lab/winpaths.py start <root> [secs]   starts the server, waits for the world, lets the mods run [secs], stops it
    tools/lab/winpaths.py report <root>         what loaded (kiln.log lines and kiln/state/*.json), PASS/FAIL per kind
    tools/lab/winpaths.py rm <root>             deletes the lab

WINPATHS_VARIANT picks the run for `start` and `report`: "managed" (default: Kiln.Host and ShroudForge on; C# mods, a
content pack, native, EMF, ShroudForge) or "engines" (no CLR, ShroudForge off; native, EMF, EML, SET). kiln-eml and
kiln-set step aside while ShroudForge hosts its runtime or a managed assembly with SET in its text is mapped, so those
two kinds need the second run. WINPATHS_ENV="A=1 B=2" adds variables to the server's environment; WINPATHS_SAVE names a
savegame folder `new` copies into the lab.

Layout (<root> is a host folder; the Wine prefix maps it to D:, and C: is the prefix's own drive):
    <game>     D:\\Игры Steam (x86)\\steamapps\\common\\Enshrouded 日本語      (the dedicated server's folder)
    <library>  C:\\Users\\Иван 王\\AppData\\Local\\Kiln\\library               (what the launcher's Library is on Windows)
Every game-side path in profile.json and chain.ini is such a Windows path, as the launcher writes them on Windows. Wine's
ANSI code page is Western (1252), so a UTF-8 string handed to a narrow Win32 or C library call fails here as it does on
a Windows PC whose code page lacks those characters.

Deploy Kiln into <game> with tools/deploy-server.sh (it prints the folder; `stage` prints it too) and, for ShroudForge,
put a built runtime in <game>/kiln/plugins/shroudforge/ first (third_party/shroudforge/kiln/build.sh). KILN_OUT names
the build folder (emf_probe.dll, native-test, the Hello sample); KILN_GE the Wine bin folder (lab.sh's default).
"""
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import importlib.util

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.abspath(os.environ.get("KILN_OUT", os.path.join(REPO, "native", "build")))
BIN = os.path.join(OUT, "cmake", "bin")
GE = os.environ.get("KILN_GE", os.path.expanduser("~/.local/share/Steam/compatibilitytools.d/GE-Proton11-11-x86_64/files/bin"))
LIBRARY_BASE = "Игры Steam (x86)"
GAME_NAME = "Enshrouded 日本語"
USER_NAME = "Иван 王"
if os.environ.get("WINPATHS_ASCII") == "1":  # the control lab: the same shape (spaces, parentheses), ASCII letters only
    LIBRARY_BASE, GAME_NAME, USER_NAME = "Steam Library (x86)", "Enshrouded Game", "User Name"
LAB_NAME = "winpaths"
# Two runs cover every kind: "managed" (Kiln.Host and ShroudForge on: C# mods, a content pack, native, EMF, ShroudForge) and
# "engines" (no CLR, ShroudForge off: native, EMF, EML, SET). kiln-eml and kiln-set step aside while ShroudForge's runtime
# (which carries EML) is hosted or while a managed assembly with SET in its text (Kiln.Ui.dll) is mapped, so they cannot
# run in the first.
VARIANT = os.environ.get("WINPATHS_VARIANT", "managed")


def game_dir(root):
    return os.path.join(root, LIBRARY_BASE, "steamapps", "common", GAME_NAME)


def prefix(root):
    return os.path.join(root, LAB_NAME + ".pfx")


def library(root):
    return os.path.join(prefix(root), "drive_c", "Users", USER_NAME, "AppData", "Local", "Kiln", "library")


def win_game(root, p):
    """Host path under <root> -> its D:\\ path."""
    rel = os.path.relpath(p, root)
    return "D:\\" + rel.replace("/", "\\")


def win_c(root, p):
    """Host path under the prefix's drive_c -> its C:\\ path."""
    rel = os.path.relpath(p, os.path.join(prefix(root), "drive_c"))
    return "C:\\" + rel.replace("/", "\\")


def wine_env(root):
    e = dict(os.environ)
    # As lab.sh: no "mscoree=" override. With it Wine cannot load the IL-only Kiln.Api.dll / Kiln.Host.dll the CLR maps through
    # LoadLibrary ("mscoree.dll not found, IL-only binary ... cannot be loaded") and Kiln.Host never starts.
    e.update(WINEPREFIX=prefix(root), WINEDEBUG="-all", WINEDLLOVERRIDES="mshtml=;dinput8=n,b;winedbg.exe=d")
    if VARIANT == "engines":
        e["KILN_SHROUDFORGE"] = "0"
    for pair in os.environ.get("WINPATHS_ENV", "").split():  # extra variables for the server, e.g. COREHOST_TRACE=1
        k, _, v = pair.partition("=")
        e[k] = v
    e.pop("DISPLAY", None)
    e["WAYLAND_DISPLAY"] = "kiln-no-display"
    return e


def server_pids(root):
    """The lab's server processes: a process named enshrouded_serv whose environment has this prefix (as lab.sh)."""
    want = ("WINEPREFIX=" + prefix(root)).encode()
    out = []
    for pid in filter(str.isdigit, os.listdir("/proc")):
        try:
            if open(f"/proc/{pid}/comm").read().strip() != "enshrouded_serv":
                continue
            if want in open(f"/proc/{pid}/environ", "rb").read().split(b"\0"):
                out.append(int(pid))
        except OSError:
            pass
    return out


def cmd_new(root, port):
    root = os.path.abspath(root)
    labs = os.path.join(root, "labs")
    env = dict(os.environ, KILN_LABS=labs)
    save = os.environ.get("WINPATHS_SAVE")  # a savegame folder to copy into the lab; none: the server makes a new world
    subprocess.run([os.path.join(REPO, "tools", "lab", "lab.sh"), "new", LAB_NAME, str(port)] + (["--save", save] if save else []),
                   check=True, env=env)
    os.makedirs(os.path.dirname(game_dir(root)), exist_ok=True)
    os.rename(os.path.join(labs, LAB_NAME), game_dir(root))
    os.rename(os.path.join(labs, LAB_NAME + ".pfx"), prefix(root))
    os.rmdir(labs)
    map_drive_d(root)
    os.makedirs(library(root), exist_ok=True)
    print(f"game folder: {game_dir(root)}\n  as the server sees it: {win_game(root, game_dir(root))}")
    print(f"library:     {library(root)}\n  as the launcher writes it: {win_c(root, library(root))}")
    print(f"deploy: KILN_OUT={OUT} tools/deploy-server.sh '{game_dir(root)}' --policy any --samples")


def map_drive_d(root):
    """D: -> <root> in the lab's prefix. The base prefix also has block-device entries (d::, e::, ...): Wine's mount manager
    puts the device's mount point back as d: at the next start, so they go too (this prefix is the lab's own copy)."""
    dev = os.path.join(prefix(root), "dosdevices")
    for name in os.listdir(dev):
        if re.fullmatch(r"[d-y]::?", name):
            os.unlink(os.path.join(dev, name))
    os.symlink(root, os.path.join(dev, "d:"))


def cmd_clone(root, to):
    """A reflinked copy of a staged lab with ASCII folder names (run with WINPATHS_ASCII=1), to compare a failure with."""
    root, to = os.path.abspath(root), os.path.abspath(to)
    subprocess.run(["cp", "-a", "--reflink=auto", root, to], check=True)
    old_base, old_game, old_user = "Игры Steam (x86)", "Enshrouded 日本語", "Иван 王"
    os.rename(os.path.join(to, old_base), os.path.join(to, LIBRARY_BASE))
    common = os.path.join(to, LIBRARY_BASE, "steamapps", "common")
    os.rename(os.path.join(common, old_game), os.path.join(common, GAME_NAME))
    users = os.path.join(prefix(to), "drive_c", "Users")
    os.rename(os.path.join(users, old_user), os.path.join(users, USER_NAME))
    map_drive_d(to)
    print(f"game folder: {game_dir(to)}")


def load_native_stage():
    spec = importlib.util.spec_from_file_location("native_test_stage", os.path.join(REPO, "mods", "tests", "native-test", "stage.py"))
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def put(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def lib_dir(root, kind, mod_id, version):
    d = os.path.join(library(root), kind, mod_id, version + "+" + "0a1b2c3d")
    if os.path.exists(d):
        shutil.rmtree(d)
    os.makedirs(d)
    return d


def cmd_stage(root):
    root = os.path.abspath(root)
    game = game_dir(root)
    kiln = os.path.join(game, "kiln")
    if not os.path.isfile(os.path.join(kiln, "kiln-core.dll")):
        sys.exit("deploy Kiln first: tools/deploy-server.sh '%s' --policy any --samples" % game)
    mods = {"kiln": [], "native": [], "emf": [], "eml": [], "shroudforge": [], "set": []}

    def entry(kind, mod_id, version, d):
        mods[kind].append({"id": mod_id, "version": version, "path": win_c(root, d)})

    # kiln: a C# sample mod, and a content pack (a mod folder with content/*.json; its entry assembly is the sample's)
    hello = os.path.join(OUT, "mods", "hello")
    d = lib_dir(root, "kiln", "hello", "0.1.0")
    for f in ("Hello.dll", "kiln.mod.json"):
        shutil.copy(os.path.join(hello, f), d)
    entry("kiln", "hello", "0.1.0", d)
    d = lib_dir(root, "kiln", "wp-pack", "1.0.0")
    shutil.copy(os.path.join(hello, "Hello.dll"), d)
    manifest = json.load(open(os.path.join(hello, "kiln.mod.json"), encoding="utf-8"))
    manifest.update(id="wp-pack", name="Winpaths content pack", version="1.0.0")
    put(os.path.join(d, "kiln.mod.json"), json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
    put(os.path.join(d, "content", "winpaths.json"),
        json.dumps({"format": 1, "items": {"Material_T1_Wood_Logs": {"maxStackSize": 777}}}, indent=2) + "\n")
    entry("kiln", "wp-pack", "1.0.0", d)

    # native: the loader's own cases (phases early, boot, world and dllmain through the proxy)
    ns = load_native_stage()
    native_cases = ["native-test-early", "native-test-boot-a", "native-test-boot-b", "native-test-world", "native-test-dllmain"]
    dllmain = []
    staging = os.path.join(library(root), "native-staging")
    shutil.rmtree(staging, ignore_errors=True)
    for mid in native_cases:
        ns.stage_case(staging, mid)
        d = lib_dir(root, "native", mid, "1.0.0")
        shutil.rmtree(d)
        shutil.move(os.path.join(staging, mid), d)
        entry("native", mid, "1.0.0", d)
        if mid == "native-test-dllmain":  # phase dllmain: the proxy loads it from chain.ini [profile]
            dllmain.append(os.path.join(d, mid + ".dll"))
    shutil.rmtree(staging, ignore_errors=True)

    # emf
    d = lib_dir(root, "emf", "emf_probe", "1.0.0")
    shutil.copy(os.path.join(BIN, "emf-test", "emf_probe.dll"), d)
    shutil.copy(os.path.join(REPO, "mods", "tests", "emf-test", "probe", "mod.json"), d)
    entry("emf", "emf_probe", "1.0.0", d)

    # eml: the manifest id has a space and non-ASCII letters, as real EML mods' ids have; the profile entry's id is a
    # Kiln id (the launcher's own rule), the folder is named by it
    eml_id = "Winpaths Ёж 日本"
    d = lib_dir(root, "eml", "winpaths-eml", "1.0.0")
    put(os.path.join(d, "mod.json"), json.dumps({"id": eml_id, "name": eml_id, "version": "1.0.0", "capabilities": ["patch"]},
                                                 indent=2, ensure_ascii=False) + "\n")
    put(os.path.join(d, "src", "mod.lua"), "-- winpaths fixture: loads, changes nothing\nreturn true\n")
    entry("eml", "winpaths-eml", "1.0.0", d)

    # set: the launcher places the enabled mods in shrouded_engine_tweaks/mods/<id>/ (kiln-set reads that folder)
    src = os.path.join(REPO, "mods", "tests", "set-test", "mods", "settest")
    d = os.path.join(game, "shrouded_engine_tweaks", "mods", "settest")
    shutil.rmtree(d, ignore_errors=True)
    shutil.copytree(src, d)
    # a second SET mod whose folder name is non-ASCII, with io inside its folder and a second module
    d2 = os.path.join(game, "shrouded_engine_tweaks", "mods", "Мод 日本語")
    shutil.rmtree(d2, ignore_errors=True)
    put(os.path.join(d2, "util.lua"), "local M = {}\nfunction M.say(s) print('winpaths-set: ' .. s) end\nreturn M\n")
    put(os.path.join(d2, "init.lua"), "local u = require('util')\n"
        "local f = io.open('данные.txt', 'w')\nf:write('ok')\nf:close()\n"
        "local g = io.open('данные.txt')\nu.say('io ' .. (g and g:read('a') or 'FAILED'))\nif g then g:close() end\n")
    put(os.path.join(d2, "mod.json"), json.dumps({"name": "Мод 日本語", "version": "1.0.0"}, ensure_ascii=False) + "\n")
    mods["set"].append({"id": "settest", "version": "0.1.0", "path": win_game(root, d)})
    mods["set"].append({"id": "winpaths-set", "version": "1.0.0", "path": win_game(root, d2)})  # entry ids are Kiln ids

    # shroudforge: the launcher places a package in ShroudForge's mods folder (<game>/mods)
    src = os.path.join(REPO, "mods", "tests", "sf-probe")
    d = os.path.join(game, "mods", "kiln-sf-probe")
    shutil.rmtree(d, ignore_errors=True)
    shutil.copytree(src, d)
    mods["shroudforge"].append({"id": "kiln-sf-probe", "version": "0.1.0", "path": win_game(root, d)})
    if not os.path.isfile(os.path.join(kiln, "plugins", "shroudforge", "shroudforge-runtime.dll")):
        print("note: no ShroudForge runtime in kiln/plugins/shroudforge: that kind will report not-installed")

    profile = {
        "format": 1, "id": "winpaths", "name": "Winpaths", "generation": 1,
        "configDir": win_c(root, os.path.join(os.path.dirname(library(root)), "profiles", "winpaths", "config")),
        "dataDir": None, "dropins": False, "mods": mods,
    }
    for kind in [k for k, v in mods.items() if not v]:
        del profile["mods"][kind]
    put(os.path.join(kiln, "profile.json"), json.dumps(profile, indent=2, ensure_ascii=False) + "\n")
    chain = "[chain]\r\n[profile]\r\n" + "".join(win_c(root, p) + "\r\n" for p in dllmain)
    put(os.path.join(kiln, "chain.ini"), chain)
    print("staged: %s" % ", ".join(f"{k} {len(v)}" for k, v in mods.items()))
    print("profile:", os.path.join(kiln, "profile.json"))


def read_text(p):
    try:
        return open(p, encoding="utf-8", errors="replace").read()
    except OSError:
        return ""


def cmd_start(root, secs):
    root = os.path.abspath(root)
    game = game_dir(root)
    if server_pids(root):
        sys.exit("already running")
    for sub in ("logs",):
        os.makedirs(os.path.join(game, sub), exist_ok=True)
    kiln_json = os.path.join(game, "kiln", "kiln.json")
    cfg = json.load(open(kiln_json, encoding="utf-8"))
    cfg["clr"] = VARIANT == "managed"
    json.dump(cfg, open(kiln_json, "w", encoding="utf-8"), indent=2)
    mark = time.time()
    with open(prefix(root) + ".wine.log", "wb") as log:
        subprocess.Popen([os.path.join(GE, "wine"), "./enshrouded_server.exe"], cwd=game, env=wine_env(root), stdout=log,
                         stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, start_new_session=True)
    glog = os.path.join(game, "logs", "enshrouded_server.log")
    up = False
    for i in range(480):
        time.sleep(0.5)
        if os.path.exists(glog) and os.path.getmtime(glog) > mark and "LoadEcsScene to Run" in read_text(glog):
            up = True
            print(f"world up after {i // 2} s")
            break
        if i > 60 and not server_pids(root):
            print("the server exited during start; see", prefix(root) + ".wine.log")
            break
    if up:
        time.sleep(secs)
    cmd_stop(root)


def cmd_stop(root):
    root = os.path.abspath(root)
    pids = server_pids(root)
    for p in pids:
        os.kill(p, signal.SIGINT)
    for _ in range(180):
        if not server_pids(root):
            break
        time.sleep(0.5)
    for p in server_pids(root):
        os.kill(p, signal.SIGKILL)
    subprocess.run([os.path.join(GE, "wineserver"), "-k"], env=wine_env(root), stderr=subprocess.DEVNULL)
    print("stopped" if pids else "not running")


def state(game, name):
    try:
        return json.load(open(os.path.join(game, "kiln", "state", name), encoding="utf-8"))
    except (OSError, ValueError):
        return None


def cmd_report(root):
    root = os.path.abspath(root)
    game = game_dir(root)
    log = read_text(os.path.join(game, "kiln", "logs", "kiln.log"))
    lines = log.splitlines()
    managed_run = VARIANT == "managed"

    def grep(pattern, limit=3):
        rx = re.compile(pattern)
        return [l[:240] for l in lines if rx.search(l)][:limit]

    results = []

    def check(kind, ok, evidence):
        results.append((kind, ok))
        print(f"{'PASS' if ok else 'FAIL'} {kind}")
        for e in evidence:
            print("      " + e)

    boot = state(game, "boot.json") or {}
    prof = boot.get("profile", {})
    print(f"variant   : {VARIANT}")
    print("game dir  :", (boot.get("paths") or {}).get("game"))
    print("profile   :", prof.get("mode"), prof.get("id"), "counts", prof.get("counts"), "missing", len(prof.get("missing") or []),
          "error", prof.get("error"))
    print("configDir :", prof.get("configDir"))
    check("kiln-quests service starts (reads <configDir>\\knowledge.json)",
          bool(grep(r"service kiln-quests\.dll loaded")) and not grep(r"kiln-quests\.dll: kiln_plugin_main faulted"),
          grep(r"kiln-quests\.dll(: kiln_plugin_main faulted| loaded)", 1) + grep(r"settings: .*knowledge\.json", 1))
    nmods = {m.get("id"): m for m in (state(game, "native.json") or {}).get("mods", [])}
    for mid in ("native-test-early", "native-test-boot-a", "native-test-boot-b", "native-test-world", "native-test-dllmain"):
        m = nmods.get(mid, {})
        check(f"native mod {mid}", m.get("state") == "loaded", [f"native.json: state={m.get('state')} reason={m.get('reason')}"] +
              grep(rf"native-test {mid}: .*PASS", 1))
    em = [m for m in (state(game, "emf.json") or {}).get("mods", []) if m.get("id") == "emf_probe"]
    check("EMF mod emf_probe", bool(em) and em[0].get("status") == "loaded",
          [f"emf.json: status={em[0].get('status') if em else None} folder={em[0].get('folder') if em else None}"] +
          grep(r"emf-probe done", 1) + grep(r"emf_probe\] FAIL", 3))
    if managed_run:
        for mid in ("hello", "wp-pack"):
            check(f"kiln (C#) mod {mid}", bool(grep(rf"\[host\] mod {mid} \S+ loaded")),
                  grep(rf"\[mod:{mid}\] hello from C#", 1) + grep(rf"\[host\] mod {mid} \S+ loaded", 1))
        check("content pack (wp-pack's content/winpaths.json)", bool(grep(r"pack .*content/winpaths\.json: 1 change\(s\), 0 error")),
              grep(r"pack .*content/winpaths\.json", 1))
        sf = state(game, "shroudforge.json") or {}
        check("ShroudForge mod kiln-sf-probe", sf.get("mode") == "hosted" and bool(grep(r"\[kiln-sf-probe\] Runtime mod initialized and active")),
              [f"shroudforge.json: mode={sf.get('mode')} runtime={sf.get('runtime')} reason={sf.get('reason')}"] +
              grep(r"\[kiln-sf-probe\] Runtime mod initialized", 1))
        st = state(game, "set.json") or {}
        print(f"info  SET (not judged in this run): set.json status={st.get('status')} reason={(st.get('reason') or '')[-60:]}")
        eml = state(game, "eml.json") or {}
        print(f"info  EML (not judged in this run): eml.json phase={eml.get('phase')} aside={eml.get('aside')}")
    else:
        eml = state(game, "eml.json") or {}
        eml_mods = eml.get("mods", [])
        check("EML mod (manifest id with a space, Cyrillic and CJK letters)", any(m.get("state") == "loaded" for m in eml_mods),
              [f"eml.json: phase={eml.get('phase')} aside={eml.get('aside')} error={eml.get('error')} " +
               f"mods={[(m.get('id'), m.get('state'), m.get('reason')) for m in eml_mods]}"] + grep(r"\[eml\]", 3))
        set_state = state(game, "set.json") or {}
        sets = {m.get("name") or m.get("id"): m for m in set_state.get("mods", [])}
        for mid in ("settest", "Мод 日本語"):
            check(f"SET mod {mid}", sets.get(mid, {}).get("status") == "loaded",
                  [f"set.json: status={set_state.get('status')} {sets.get(mid)}"] + grep(r"Mod (loaded|failed|skipped): " + re.escape(mid), 1))
        check("SET mod: io.open + require inside a non-ASCII folder", bool(grep(r"winpaths-set: io ok")), grep(r"winpaths-set:", 2))
    print("kiln.log, error lines:")
    for l in grep(r"^\[E ", 8):
        print("   ", l)
    crash_dir = os.path.join(game, "kiln", "crash-reports")
    crashes = os.listdir(crash_dir) if os.path.isdir(crash_dir) else []
    print("crash reports:", crashes or "none")
    for c in crashes[:3]:
        print("   ", " | ".join(x for x in read_text(os.path.join(crash_dir, c)).splitlines()[1:4] if x))
    bad = [k for k, ok in results if not ok]
    print(f"{len(results) - len(bad)}/{len(results)} loaded" + (f"; not loaded: {', '.join(bad)}" if bad else ""))
    return 0 if not bad else 1


def cmd_rm(root):
    root = os.path.abspath(root)
    cmd_stop(root)
    shutil.rmtree(root, ignore_errors=True)
    print("removed", root)


def main(argv):
    if len(argv) < 3 or argv[1] not in ("new", "clone", "stage", "start", "stop", "report", "rm"):
        print(__doc__)
        return 2
    cmd, root = argv[1], argv[2]
    if cmd == "new":
        cmd_new(root, int(argv[3]) if len(argv) > 3 else 26530)
    elif cmd == "clone":
        cmd_clone(root, argv[3])
    elif cmd == "stage":
        cmd_stage(root)
    elif cmd == "start":
        cmd_start(root, float(argv[3]) if len(argv) > 3 else 90)
    elif cmd == "stop":
        cmd_stop(root)
    elif cmd == "report":
        return cmd_report(root)
    elif cmd == "rm":
        cmd_rm(root)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
