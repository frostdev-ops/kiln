#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Proxy test harness: runs the Kiln dinput8 proxy in proxyhost.exe under plain Wine, without the game.

    KILN_OUT=<build dir> native/build.sh kiln-proxy proxyhost
    KILN_OUT=<build dir> python3 native/spikes/proxyhost/run.py [scenario...]

Each scenario builds a scratch "game folder" under $KILN_OUT/proxyhost/run/<scenario>/ (proxyhost.exe copied as
enshrouded.exe or enshrouded_server.exe, the proxy as dinput8.dll, the fake core as kiln/kiln-core.dll, test DLLs,
chain.ini, dinput8-chain.ini, play-once.ini), runs it once and checks the harness log: chain order, statuses and
refusal reasons, dedupe, last-in-first-out entry patches, play-once modes / expiry / deletion, KILN_DISABLE,
GetdfDIJoystick and KilnProxyInfo. Wine prefix: $KILN_OUT/proxyhost/pfx (created on first use). Exit code 0 = all pass.

PROXYHOST_WINPATHS=1: Windows-style paths. The game folders live inside the prefix's drive_c, under names with spaces
and non-ASCII characters (C:\\Spiele Ü\\Kiln Läufe\\<scenario>), and every absolute path the harness writes (chain.ini,
dinput8-chain.ini, profile.json, play-once.ini) is a C:\\ path as on a Windows PC, not a Z:\\ path.
"""
import json
import os
import shutil
import stat
import subprocess
import sys
import time
from datetime import datetime, timedelta, timezone

OUT = os.path.abspath(os.environ.get("KILN_OUT", os.path.join(os.path.dirname(__file__), "../../build")))
BIN = os.path.join(OUT, "cmake", "bin")
PH = os.path.join(BIN, "proxyhost")
WORK = os.path.join(OUT, "proxyhost")
PFX = os.path.join(WORK, "pfx")
WINE = os.environ.get("PROXYHOST_WINE", "wine")
WINPATHS = os.environ.get("PROXYHOST_WINPATHS") == "1"
DRIVE_C = os.path.join(PFX, "drive_c")
RUN = os.path.join(DRIVE_C, "Spiele Ü", "Kiln Läufe") if WINPATHS else os.path.join(WORK, "run")


def winpath(p):
    a = os.path.abspath(p)
    if WINPATHS and (a + os.sep).startswith(DRIVE_C + os.sep):
        return "C:" + (a[len(DRIVE_C):] or os.sep).replace("/", "\\")
    return "Z:" + a.replace("/", "\\")


def env():
    e = dict(os.environ)
    e.update(WINEPREFIX=PFX, WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree=;mshtml=;dinput8=n,b")
    e.pop("DISPLAY", None)
    e.pop("WAYLAND_DISPLAY", None)
    e.pop("KILN_DISABLE", None)
    e.pop("KILN_CORE", None)
    return e


def ensure_prefix():
    if os.path.isfile(os.path.join(PFX, "system.reg")):
        return
    os.makedirs(WORK, exist_ok=True)
    print(f"proxyhost: creating Wine prefix {PFX} (once)")
    subprocess.run([WINE, "wineboot", "-i"], env=env(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                   timeout=300)


class Game:
    def __init__(self, name, exe="enshrouded.exe"):
        self.name = name
        self.dir = os.path.join(RUN, name)
        if os.path.exists(self.dir):
            for root, dirs, _ in os.walk(self.dir):
                for d in dirs:
                    os.chmod(os.path.join(root, d), 0o755)
            shutil.rmtree(self.dir)
        self.kiln = os.path.join(self.dir, "kiln")
        os.makedirs(os.path.join(self.kiln, "chain"))
        os.makedirs(os.path.join(self.kiln, "logs"))
        os.makedirs(os.path.join(self.dir, "mods", "sub"))
        self.exe = exe
        shutil.copy(os.path.join(PH, "proxyhost.exe"), os.path.join(self.dir, exe))
        shutil.copy(os.path.join(BIN, "dinput8.dll"), os.path.join(self.dir, "dinput8.dll"))
        shutil.copy(os.path.join(PH, "kiln-core.dll"), os.path.join(self.kiln, "kiln-core.dll"))
        self.log = os.path.join(self.dir, "harness.log")
        self.lines = []

    def dll(self, name, dest):
        path = os.path.join(self.dir, dest)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        shutil.copy(os.path.join(PH, name + ".dll"), path)
        return path

    def write(self, rel, text):
        path = os.path.join(self.dir, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", newline="\r\n") as f:
            f.write(text)
        return path

    def play_once(self, mode, created=None, nonce="n-123", extra=""):
        created = int(time.time()) if created is None else created
        return self.write("kiln/play-once.ini", f"mode={mode}\ncreated={created}\nnonce={nonce}\n{extra}")

    def run(self, extra_env=None, args=None):
        e = env()
        e["PROXYHOST_LOG"] = winpath(self.log)
        e.update(extra_env or {})
        r = subprocess.run([WINE, os.path.join(self.dir, self.exe)] + (args or []), cwd=self.dir, env=e,
                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
        self.rc = r.returncode
        self.lines = open(self.log, encoding="utf-8").read().splitlines() if os.path.exists(self.log) else []
        plog = os.path.join(self.kiln, "logs", "proxy.log")
        self.proxy_log = open(plog, encoding="utf-8").read().splitlines() if os.path.exists(plog) else []
        return self

    def chain(self):
        """core chain lines -> list of dicts."""
        out = []
        for line in self.lines:
            if not line.startswith("core chain "):
                continue
            parts = line.split(" path=", 1)
            fields = dict(kv.split("=", 1) for kv in parts[0].split()[3:])
            fields["path"] = parts[1] if len(parts) > 1 else ""
            out.append(fields)
        return out


failures = []
passes = 0


def check(game, ok, what):
    global passes
    if ok:
        passes += 1
        print(f"  ok   {what}")
    else:
        failures.append(f"{game.name}: {what}")
        print(f"  FAIL {what}")


def index_of(lines, prefix):
    for i, line in enumerate(lines):
        if line.startswith(prefix):
            return i
    return -1


def has(lines, prefix):
    return index_of(lines, prefix) >= 0


def ordered(lines, prefixes):
    idx = [index_of(lines, p) for p in prefixes]
    return all(i >= 0 for i in idx) and idx == sorted(idx)


def dump(g):
    print("    --- harness.log ---")
    for line in g.lines:
        print("    " + line)
    print("    --- kiln/logs/proxy.log ---")
    for line in g.proxy_log:
        print("    " + line)


# ---------------------------------------------------------------------------------------------------- scenarios ----

def s_chain():
    """chain.ini [chain]/[profile] + dinput8-chain.ini: order, refusals, dedupe, LIFO entry patches, exports."""
    g = Game("chain")
    for n in ("chain-a", "entrypatch-1", "entrypatch-2", "fake-sf", "failing", "chain-b"):
        g.dll(n, f"kiln/chain/{n}.dll")
    shutil.copy(os.path.join(BIN, "dinput8.dll"), os.path.join(g.kiln, "chain", "kiln-old.dll"))
    g.write("kiln/chain/notadll.dll", "this is not a DLL\n")
    g.dll("emf-x", "mods/emf-x.dll")
    g.dll("emf-y", "mods/sub/emf-y.dll")
    library = os.path.join(RUN, "chain-library")
    os.makedirs(library, exist_ok=True)
    shutil.copy(os.path.join(PH, "chain-c.dll"), os.path.join(library, "chain-c.dll"))
    g.write("kiln/chain.ini", f"""; written by the harness
[chain]
chain/chain-a.dll
emf = chain\\entrypatch-1.dll
chain/ENTRYPATCH-2.DLL
chain/fake-sf.dll
chain/failing.dll
chain/notadll.dll
chain/kiln-old.dll
..\\dinput8.dll
C:\\windows\\system32\\dinput8.dll
chain/missing.dll
; chain/chain-b.dll
[other]
chain/chain-b.dll
[profile]
{winpath(os.path.join(library, "chain-c.dll"))}
chain/./sub/../chain-a.dll
""")
    g.write("dinput8-chain.ini", """mods\\emf-x.dll
[Chain]
mods/sub/emf-y.dll
; mods\\chain-b.dll
# kiln\\chain\\chain-b.dll
creative = mods\\EMF-X.dll
C:\\abs\\x.dll
..\\x.dll
settings = 3
[Other]
kiln\\chain\\chain-b.dll
""")
    g.run()
    print(f"[chain] rc {g.rc}")
    c = g.chain()
    expect = [  # (status, source, detail, error, path suffix)
        ("0", "0", "0", "0", "\\kiln\\chain\\chain-a.dll"),
        ("0", "0", "0", "0", "\\kiln\\chain\\entrypatch-1.dll"),
        ("0", "0", "0", "0", "\\kiln\\chain\\entrypatch-2.dll"),
        ("3", "0", "4", "0", "\\kiln\\chain\\fake-sf.dll"),
        ("1", "0", "0", "1114", "\\kiln\\chain\\failing.dll"),
        ("3", "0", "5", "0", "\\kiln\\chain\\notadll.dll"),
        ("3", "0", "3", "0", "\\kiln\\chain\\kiln-old.dll"),
        ("3", "0", "1", "0", "\\dinput8.dll"),
        ("3", "0", "2", "0", "\\system32\\dinput8.dll"),
        ("2", "0", "0", "0", "\\kiln\\chain\\missing.dll"),
        ("0", "2", "0", "0", "\\mods\\emf-x.dll"),
        ("0", "2", "0", "0", "\\mods\\sub\\emf-y.dll"),
        ("4", "2", "0", "0", "\\mods\\emf-x.dll"),
        ("3", "2", "6", "0", "\\abs\\x.dll"),
        ("3", "2", "6", "0", "\\x.dll"),
        ("0", "1", "0", "0", "\\chain-library\\chain-c.dll"),
        ("4", "1", "0", "0", "\\kiln\\chain\\chain-a.dll"),
    ]
    check(g, len(c) == len(expect), f"{len(expect)} chain entries in KilnBootInfo (got {len(c)})")
    for i, (st, src, det, err, suffix) in enumerate(expect):
        if i >= len(c):
            break
        e = c[i]
        ok = e["status"] == st and e["source"] == src and e["detail"] == det and e["error"] == err and \
            e["path"].lower().endswith(suffix.lower())
        loaded = e["module"] == ("1" if st in ("0", "4") else "0")
        check(g, ok and loaded, f"entry {i}: status {st} source {src} detail {det} error {err} {suffix} "
                                f"(got {e['status']}/{e['source']}/{e['detail']}/{e['error']} module {e['module']} {e['path']})")
    check(g, all(e["path"][1:3] == ":\\" for e in c), "paths are canonical Windows paths (drive letter)")
    check(g, ordered(g.lines, ["dll chain-a attach", "dll entrypatch-1 attach", "patch entrypatch-1: entry patched",
                               "dll entrypatch-2 attach", "patch entrypatch-2: entry patched", "dll emf-x attach",
                               "dll emf-y attach", "dll chain-c attach", "core boot", "gate entrypatch-2",
                               "gate entrypatch-1", "main reached"]),
          "load order [chain] -> dinput8-chain.ini -> [profile]; core; gates unwind LIFO (2, 1); main")
    check(g, sum(1 for line in g.lines if line.startswith("dll chain-a attach")) == 1, "chain-a attached once (dedupe)")
    check(g, sum(1 for line in g.lines if line.startswith("dll emf-x attach")) == 1, "emf-x attached once (dedupe)")
    check(g, not has(g.lines, "dll chain-b"), "commented-out and other-section lines are not loaded")
    check(g, not has(g.lines, "dll fake-sf"), "ShroudForge-marked DLL never loaded")
    check(g, has(g.lines, "core entry bytes ff 25"), "at kiln_boot the entry still holds a jump (entrypatch-2's)")
    check(g, has(g.lines, "main GetdfDIJoystick size=32 objsize=24 flags=1 datasize=80 objects=44"),
          "GetdfDIJoystick: c_dfDIJoystick (fallback under Wine)")
    check(g, has(g.lines, "main KilnProxyInfo abi=2"), "KilnProxyInfo export answers abi 2")
    check(g, has(g.lines, "main DirectInput8Create hr=0x00000000"), "DirectInput8Create forwards to the system DLL")
    check(g, has(g.lines, "core play_once=0 nonce= profile= chain=17"), "play_once NONE without play-once.ini")
    # rc -9: Wine's teardown sometimes SIGKILLs the launcher after the program ended (seen with no proxy at all)
    check(g, has(g.lines, "main done") and g.rc in (0, -9), f"main() ran to the end (exit code {g.rc})")
    check(g, any("entry gate installed" in line for line in g.proxy_log), "proxy.log written")
    return g


def s_vanilla():
    g = Game("vanilla")
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n")
    f = g.play_once("vanilla")
    g.run()
    print(f"[vanilla] rc {g.rc}")
    check(g, not os.path.exists(f), "play-once.ini deleted")
    check(g, not has(g.lines, "dll "), "no chained DLL loaded")
    check(g, not has(g.lines, "core boot"), "no gate, no core")
    check(g, has(g.lines, "main reached"), "the game runs")
    check(g, any("play-once vanilla" in line for line in g.proxy_log), "proxy.log says vanilla")
    return g


def s_nomods():
    g = Game("nomods")
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    g.dll("emf-x", "mods/emf-x.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n[profile]\nchain/chain-a.dll\n")
    g.write("dinput8-chain.ini", "[Chain]\nmods\\emf-x.dll\n")
    created = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")  # RFC 3339 form
    f = g.play_once("no-mods", created=created, nonce="abc-def")
    g.run()
    print(f"[nomods] rc {g.rc}")
    c = g.chain()
    check(g, not os.path.exists(f), "play-once.ini deleted")
    check(g, has(g.lines, "core play_once=2 nonce=abc-def"), "core sees NO_MODS and the nonce (RFC 3339 created)")
    check(g, len(c) == 3 and all(e["status"] == "5" and e["module"] == "0" for e in c), "all 3 entries SKIPPED")
    check(g, not has(g.lines, "dll "), "nothing chained")
    check(g, has(g.lines, "main reached"), "the game runs")
    return g


def s_profile():
    """mode=profile: chain\\once.ini replaces chain.ini [profile]; chain.ini [chain] (displaced proxies) still loads."""
    g = Game("profile")
    for n in ("chain-a", "chain-b", "chain-c"):
        g.dll(n, f"kiln/chain/{n}.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n[profile]\nchain/chain-c.dll\n")
    g.write("kiln/chain/once.ini", "chain/chain-b.dll\n[chain]\nchain/chain-c.dll\n")
    created = (datetime.now(timezone(timedelta(hours=1)))).strftime("%Y-%m-%dT%H:%M:%S.123+01:00")
    f = g.play_once("profile", created=created, nonce="once-1")
    g.run()
    print(f"[profile] rc {g.rc}")
    check(g, not os.path.exists(f), "play-once.ini deleted")
    check(g, has(g.lines, "core play_once=3 nonce=once-1 profile=" + winpath(g.kiln)[:2]) and
          any(line.endswith("\\kiln\\profile.once.json chain=2") for line in g.lines),
          "core sees PROFILE with kiln\\profile.once.json (offset +01:00 accepted)")
    check(g, ordered(g.lines, ["dll chain-a attach", "dll chain-b attach"]) and not has(g.lines, "dll chain-c"),
          "chain.ini [chain] + chain\\once.ini (lines before a section = [profile]; its [chain] ignored)")
    return g


def s_expired():
    g = Game("expired")
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n")
    f = g.play_once("vanilla", created=int(time.time()) - 700)
    g.run()
    print(f"[expired] rc {g.rc}")
    check(g, not os.path.exists(f), "play-once.ini deleted")
    check(g, has(g.lines, "core play_once=16"), "IGNORED_EXPIRED (700 s old)")
    check(g, has(g.lines, "dll chain-a attach"), "normal start: chain loads")
    return g


def s_future():
    g = Game("future")
    f = g.play_once("vanilla", created=int(time.time()) + 600)
    g.run()
    check(g, not os.path.exists(f) and has(g.lines, "core play_once=16"), "created in the future: IGNORED_EXPIRED")
    return g


def s_invalid():
    g = Game("invalid")
    f = g.play_once("bogus")
    g.run()
    check(g, not os.path.exists(f) and has(g.lines, "core play_once=19"), "unknown mode: IGNORED_INVALID, deleted")
    g2 = Game("invalid-time")
    f2 = g2.play_once("vanilla", created="yesterday")
    g2.run()
    check(g2, not os.path.exists(f2) and has(g2.lines, "core play_once=19"), "bad created: IGNORED_INVALID")
    return g


def s_server():
    g = Game("server", exe="enshrouded_server.exe")
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n")
    f = g.play_once("vanilla")
    g.run()
    print(f"[server] rc {g.rc}")
    check(g, not os.path.exists(f), "play-once.ini deleted on a server")
    check(g, has(g.lines, "core play_once=18"), "IGNORED_SERVER")
    check(g, has(g.lines, "dll chain-a attach"), "server start loads the chain")
    return g


def s_undeletable():
    g = Game("undeletable")
    f = g.play_once("vanilla")
    os.chmod(g.kiln, stat.S_IRUSR | stat.S_IXUSR)  # unlink in kiln/ fails
    try:
        g.run()
    finally:
        os.chmod(g.kiln, 0o755)
    print(f"[undeletable] rc {g.rc}")
    check(g, os.path.exists(f), "play-once.ini still there (could not be deleted)")
    check(g, has(g.lines, "core play_once=17"), "IGNORED_UNDELETABLE: a normal start")
    return g


def s_disabled():
    g = Game("disabled")
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n")
    f = g.play_once("no-mods")
    g.run({"KILN_DISABLE": "1"})
    print(f"[disabled] rc {g.rc}")
    check(g, os.path.exists(f), "KILN_DISABLE=1 leaves play-once.ini alone")
    check(g, not has(g.lines, "dll ") and not has(g.lines, "core boot"), "KILN_DISABLE=1: no chain, no core")
    check(g, has(g.lines, "main reached"), "the game runs")
    return g


def crash_reporter_game(name, exe, args):
    """A game folder as a crashed session leaves it, and a start of the reporter over it."""
    g = Game(name, exe=exe)
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n")
    g.write("kiln/logs/kiln.log", "the crashed session's log\n")
    before = g.write("kiln/logs/proxy.log", "the crashed session's proxy log\n")
    once = g.play_once("no-mods")
    g.run(args=args)
    return g, before, once


def s_crash_reporter():
    # exactly how the game starts it: <exe> --upload-dump "<dir>" --process_duration <t>
    for name, exe in (("reporter", "enshrouded.exe"), ("reporter-server", "enshrouded_server.exe")):
        g, proxy_log, once = crash_reporter_game(name, exe, ["--upload-dump", "S:/crash dumps/enshrouded_1", "--process_duration", "12.5"])
        print(f"[{name}] rc {g.rc}")
        check(g, has(g.lines, "main reached"), "the reporter runs")
        check(g, not has(g.lines, "core boot"), "no core in the reporter")
        check(g, not has(g.lines, "dll chain-a"), "no chained DLL in the reporter")
        check(g, os.path.exists(once), "play-once.ini left for the next real start")
        check(g, open(proxy_log).read().strip() == "the crashed session's proxy log", "proxy.log of the crashed session untouched")
        check(g, open(os.path.join(g.kiln, "logs", "kiln.log")).read().strip() == "the crashed session's log", "kiln.log untouched")
        check(g, has(g.lines, "main GetdfDIJoystick") and has(g.lines, "main DirectInput8Create hr=0x00000000"), "dinput8 still forwards")
    # any case, as an argument of its own
    g, _, _ = crash_reporter_game("reporter-case", "enshrouded.exe", ["--UPLOAD-DUMP", "x"])
    check(g, not has(g.lines, "core boot"), "the flag in another case")
    # a normal start must still boot: the words inside another argument or as the exe's own name do not count
    for name, args in (("reporter-not-1", ["--savegame", "--upload-dump-x"]), ("reporter-not-2", ["--savegame", "C:/a --upload-dump b/c"]),
                       ("reporter-not-3", ["-x--upload-dump"]), ("reporter-not-4", [])):
        g, _, once = crash_reporter_game(name, "enshrouded.exe", args)
        print(f"[{name}] rc {g.rc}")
        check(g, has(g.lines, "core boot"), f"a normal start boots the core: {args}")
        check(g, has(g.lines, "core play_once=2"), f"play-once counts: {args}")
        check(g, not os.path.exists(once), f"play-once.ini consumed: {args}")
    return None


# ------------------------------------------------------------------------- parity with schema/fixtures/ini ----

FIXTURES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "../../../schema/fixtures/ini")
PLAY_ONCE_CODES = {"vanilla": 1, "no-mods": 2, "profile": 3, "ignored-expired": 16, "ignored-undeletable": 17,
                   "ignored-server": 18, "ignored-invalid": 19}


def norm(p):
    import ntpath
    return ntpath.normpath(p)


def s_fixtures():
    """Every schema/fixtures/ini case through the real proxy: the same statuses, paths and nonces as kini.py."""
    import json
    import ntpath
    for spec_file in sorted(glob_fixtures("chain")):
        spec = json.load(open(spec_file))
        case = os.path.basename(spec_file)[:-5]
        g = Game(f"fx-chain-{case}")
        data = open(spec_file[:-5] + ".ini", "rb").read()
        once = spec.get("file") == "once"
        if once:
            open(os.path.join(g.kiln, "chain", "once.ini"), "wb").write(data)
            g.play_once("profile")
        else:
            open(os.path.join(g.kiln, "chain.ini"), "wb").write(data)
        g.run()
        kiln = winpath(g.kiln)
        want = []
        for section, source in (("chain", "0"), ("profile", "1")):
            for e in spec["expect"].get(section, []):
                path = e["path"] if not e["relative"] else kiln + "\\" + e["path"]
                want.append((source, norm(path).lower(), e["status"], e.get("detail")))
        got = [(e["source"], norm(e["path"]).lower(), e["status"], e["detail"]) for e in g.chain()]
        ok = len(got) == len(want)
        for (ws, wp, wst, wd), (gs, gp, gst, gd) in zip(want, got):
            if ws != gs or wp != gp:
                ok = False
            if wst == "refused" and not (gst == "3" and gd == "7" and wd == "too-many"):
                ok = False
            if wst == "ok" and gst == "3" and gd == "7":
                ok = False
        check(g, ok, f"chain fixture {case}: {len(want)} entries, same paths/sources/statuses")
        if not ok:
            for w in want:
                print("      want", w)
            for x in got:
                print("      got ", x)
    for spec_file in sorted(glob_fixtures("play-once")):
        spec = json.load(open(spec_file))
        case = os.path.basename(spec_file)[:-5]
        g = Game(f"fx-po-{case}", exe="enshrouded_server.exe" if spec.get("server") else "enshrouded.exe")
        f = os.path.join(g.kiln, "play-once.ini")
        open(f, "wb").write(open(spec_file[:-5] + ".ini", "rb").read())
        if spec.get("deletable", True) is False:
            os.chmod(g.kiln, stat.S_IRUSR | stat.S_IXUSR)
        try:
            g.run({"KILN_PROXY_TEST_NOW": str(spec["now"])})
        finally:
            os.chmod(g.kiln, 0o755)
        exp = spec["expect"]
        code = PLAY_ONCE_CODES[exp["status"]]
        if code == 1:
            ok = not has(g.lines, "core boot") and has(g.lines, "main reached")
        else:
            ok = has(g.lines, f"core play_once={code} ")
            if ok and exp.get("nonce") and code in (2, 3, 16):
                ok = has(g.lines, f"core play_once={code} nonce={exp['nonce']} ")
            if ok and code == 3:
                pr = exp["profile"]
                want = norm(pr["path"] if not pr["relative"] else winpath(g.kiln) + "\\" + pr["path"]).lower()
                line = next(x for x in g.lines if x.startswith("core play_once="))
                got = norm(line.split(" profile=", 1)[1].rsplit(" chain=", 1)[0]).lower()
                ok = got == want
        deleted = not os.path.exists(f)
        ok = ok and deleted == (spec.get("deletable", True) is not False)
        check(g, ok, f"play-once fixture {case}: {exp['status']}")
        if not ok:
            dump(g)
    return None


def glob_fixtures(kind):
    import glob
    return glob.glob(os.path.join(FIXTURES, kind, "*.json"))


def s_corrupt():
    """A corrupt profile.json loads no mods: the proxy skips chain.ini [profile]; [chain] (displaced proxies) loads."""
    g = Game("corrupt")
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    g.dll("chain-c", "kiln/chain/chain-c.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n[profile]\nchain/chain-c.dll\n")
    g.write("kiln/profile.json", '{"format": 1, "id": "x", "generation": 1, "mods": {"kiln": [{"id": "a", "path": "Z:\\\\tr')
    g.run()
    c = g.chain()
    check(g, has(g.lines, "dll chain-a attach") and not has(g.lines, "dll chain-c"), "[chain] loads, [profile] does not")
    check(g, len(c) == 2 and c[1]["status"] == "5" and c[1]["source"] == "1", "the [profile] entry is SKIPPED")
    check(g, any("not well-formed JSON" in line for line in g.proxy_log), "proxy.log says why")
    g2 = Game("wellformed")
    g2.dll("chain-c", "kiln/chain/chain-c.dll")
    g2.write("kiln/chain.ini", "[profile]\nchain/chain-c.dll\n")
    g2.write("kiln/profile.json", '{"format": 1, "id": "x", "generation": 1, "mods": {"kiln": [{"id": "a", "path": "Z:\\\\t\\u00e9"}]}, "n": -1.5e3, "b": [true, false, null]}')
    g2.run()
    check(g2, has(g2.lines, "dll chain-c attach"), "a well-formed profile.json: [profile] loads")
    return g


def s_semver():
    """The core's C++ semver port (native_mods_semver.h) against schema/fixtures/semver."""
    exe = os.path.join(PH, "proxyhost-semver.exe")
    fixtures = winpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "../../../schema/fixtures/semver"))
    r = subprocess.run([WINE, exe, fixtures], env=env(), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=120,
                       text=True)
    out = r.stdout.strip().splitlines()
    print("  " + (out[-1] if out else "(no output)"))
    for line in out:
        if line.startswith("FAIL"):
            print("    " + line)

    class G:
        name = "semver"
    check(G, r.returncode == 0 and out and out[-1].endswith(" 0 failed"), "semver parity with SemVer.cs fixtures")
    return None


def realcore_game(name):
    """A game folder with the real kiln-core.dll (no sigdb, no .NET: the core boots in safe idle) and a library of
    native-test mods (native/samples/native-test)."""
    g = Game(name)
    shutil.copy(os.path.join(BIN, "kiln-core.dll"), os.path.join(g.kiln, "kiln-core.dll"))
    nt = os.path.join(BIN, "samples", "native-test")
    lib = os.path.join(g.dir, "..", name + "-library")
    if os.path.exists(lib):
        shutil.rmtree(lib)
    cases = {
        "native-test-early": {"phase": "early", "init": "kiln_native_init", "side": "both"},
        "native-test-boot": {"phase": "boot", "init": "kiln_native_init", "side": "both", "requires": ["native-test-early"]},
        "native-test-clientside": {"phase": "early"},  # side defaults to client: loads here (the exe is enshrouded.exe)
        "native-test-dllmain": {"phase": "dllmain", "side": "both"},
        "native-test-world": {"phase": "world", "init": "kiln_native_init", "side": "both"},
    }
    for mid, fields in cases.items():
        d = os.path.join(lib, "native", mid)
        os.makedirs(d)
        src = "native-test" if "init" in fields else "native-plain"
        shutil.copy(os.path.join(nt, src + ".dll"), os.path.join(d, mid + ".dll"))
        json.dump(dict({"id": mid, "version": "1.0.0", "dlls": [mid + ".dll"]}, **fields), open(os.path.join(d, "native.json"), "w"))
    os.makedirs(os.path.join(lib, "plugin"))
    shutil.copy(os.path.join(nt, "native-plugin.dll"), os.path.join(lib, "plugin", "native-plugin.dll"))
    profile = {"format": 1, "id": "harness", "name": "Harness", "generation": 4, "configDir": winpath(os.path.join(lib, "config")),
               "dataDir": winpath(os.path.join(lib, "data")), "dropins": False,
               "mods": {"native": [{"id": m, "path": winpath(os.path.join(lib, "native", m))} for m in cases] +
                                  [{"id": "native-test-gone", "path": winpath(os.path.join(lib, "native", "gone"))}],
                        "plugin": [{"path": winpath(os.path.join(lib, "plugin", "native-plugin.dll"))}], "kiln": []}}
    return g, lib, profile


def state(g, name):
    import json as j
    path = os.path.join(g.kiln, "state", name)
    return j.load(open(path)) if os.path.exists(path) else {}


def kiln_log(g):
    path = os.path.join(g.kiln, "logs", "kiln.log")
    return open(path, encoding="utf-8", errors="replace").read().replace("\r", "").splitlines() if os.path.exists(path) else []


def s_realcore():
    """The real core in proxyhost: play-once mode=profile (profile.once.json + chain/once.ini), then profile.json,
    then a corrupt profile, then no profile; boot.json, native.json and kiln.log each time."""
    import json as j
    g, lib, profile = realcore_game("realcore-once")
    # copy mode: one mod under kiln/profiles/<id>/native/<mod>, named by a path relative to kiln/
    copied = os.path.join(g.kiln, "profiles", "harness", "native", "native-test-copied")
    shutil.copytree(os.path.join(lib, "native", "native-test-early"), copied)
    os.rename(os.path.join(copied, "native-test-early.dll"), os.path.join(copied, "native-test-copied.dll"))
    j.dump({"id": "native-test-copied", "version": "1.0.0", "dlls": ["native-test-copied.dll"], "phase": "boot",
            "init": "kiln_native_init", "side": "both"}, open(os.path.join(copied, "native.json"), "w"))
    profile["mods"]["native"].append({"id": "native-test-copied", "path": "profiles/harness/native/native-test-copied"})
    j.dump(profile, open(os.path.join(g.kiln, "profile.once.json"), "w"))
    g.write("kiln/chain/once.ini", "[profile]\n" + winpath(os.path.join(lib, "native", "native-test-dllmain", "native-test-dllmain.dll")) + "\n")
    g.play_once("profile", nonce="once-nonce-7")
    g.run()
    b, n, log = state(g, "boot.json"), state(g, "native.json"), kiln_log(g)
    mods = {m["id"]: m for m in n.get("mods", [])}
    print(f"[realcore once] rc {g.rc}")
    check(g, b.get("profile", {}).get("mode") == "once" and b["profile"]["id"] == "harness" and b["profile"]["generation"] == 4,
          f"boot.json profile once 'harness' gen 4 ({b.get('profile', {}).get('mode')})")
    check(g, b.get("playOnce") == {"mode": "profile", "nonce": "once-nonce-7", "consumed": True}, f"boot.json playOnce {b.get('playOnce')}")
    check(g, b.get("side") == "client" and b.get("hooks") is False and "no symbol definitions" in (b.get("safeIdle") or ""),
          f"client side, safe idle (no sigdb): {b.get('side')} {b.get('safeIdle')}")
    check(g, [m["id"] for m in b.get("profile", {}).get("missing", [])] == ["native-test-gone"], "the missing entry is reported")
    check(g, b.get("paths", {}).get("config", "").lower() == winpath(os.path.join(lib, "config")).lower(), "KILN_PATH_CONFIG = configDir")
    check(g, [c["status"] for c in b.get("chain", [])] == ["loaded"], "chain/once.ini [profile] loaded by the proxy")
    want = {"native-test-early": "loaded", "native-test-boot": "loaded", "native-test-clientside": "loaded",
            "native-test-dllmain": "loaded", "native-test-world": "skipped"}
    check(g, {k: mods.get(k, {}).get("state") for k in want} == want, f"native states {[(k, mods.get(k, {}).get('state')) for k in want]}")
    check(g, "needs hooks" in (mods.get("native-test-world", {}).get("reason") or ""), "world phase skipped in safe idle")
    cp = mods.get("native-test-copied", {})
    check(g, cp.get("state") == "loaded" and cp.get("folder", "").lower() == winpath(copied).lower(),
          f"copy mode: a path relative to kiln/ resolves ({cp.get('state')}, {cp.get('folder')})")
    passes = [l for l in log if "native-test" in l and " PASS" in l]
    fails = [l for l in log if "native-test" in l and " FAIL" in l]
    check(g, len(passes) >= 10 and not fails, f"native-test lines: {len(passes)} PASS, {len(fails)} FAIL")
    check(g, any("kiln.profile PASS mode once id 'harness'" in l for l in log), "early mods read kiln.profile (mode once)")
    check(g, os.path.isdir(os.path.join(lib, "config", "native", "native-test-early")) and
          os.path.isdir(os.path.join(lib, "data", "native-test-boot")), "config_dir/data_dir under the profile's folders")
    check(g, not b.get("plugins"), "no plugins in safe idle")
    for l in fails:
        print("    " + l)

    g2, lib2, profile2 = realcore_game("realcore-active")
    j.dump(profile2, open(os.path.join(g2.kiln, "profile.json"), "w"))
    g2.run()
    b2 = state(g2, "boot.json")
    check(g2, b2.get("profile", {}).get("mode") == "active" and b2.get("playOnce", {}).get("mode") == "none",
          f"profile.json: mode active, no play-once ({b2.get('profile', {}).get('mode')})")
    check(g2, state(g2, "native.json").get("mods") and
          {m["id"]: m["state"] for m in state(g2, "native.json")["mods"]}.get("native-test-dllmain") == "skipped",
          "a dllmain mod not in chain.ini is skipped")

    g3, lib3, profile3 = realcore_game("realcore-invalid")
    open(os.path.join(g3.kiln, "profile.json"), "w").write('{"format": 1, "id": "Bad Id", "generation": 1, "mods": {}}')
    g3.run()
    b3 = state(g3, "boot.json")
    check(g3, b3.get("profile", {}).get("mode") == "invalid" and "id" in (b3["profile"].get("error") or "") and b3.get("errors"),
          f"invalid profile: mode invalid, error {b3.get('profile', {}).get('error')}")
    check(g3, not state(g3, "native.json").get("mods"), "invalid profile: no native mod at all")
    check(g3, any("NO MODS load this start" in l for l in kiln_log(g3)), "kiln.log says no mods")

    g4, _, _ = realcore_game("realcore-legacy")
    g4.run()
    b4 = state(g4, "boot.json")
    check(g4, b4.get("profile", {}).get("mode") == "legacy" and b4["profile"]["dropins"] == ["kiln", "native", "plugin", "emf"],
          "no profile: legacy, every drop-in kind")
    check(g4, b4.get("paths", {}).get("config", "").lower().endswith("\\kiln\\config"), "legacy configs in kiln\\config")
    return g


SCENARIOS = {f.__name__[2:]: f for f in (s_chain, s_vanilla, s_nomods, s_profile, s_expired, s_future, s_invalid,
                                          s_server, s_undeletable, s_disabled, s_crash_reporter, s_corrupt, s_fixtures, s_semver,
                                          s_realcore)}


def main():
    for f in ("proxyhost.exe", "kiln-core.dll", "chain-a.dll"):
        if not os.path.exists(os.path.join(PH, f)):
            sys.exit(f"proxyhost: {os.path.join(PH, f)} missing; build with native/build.sh kiln-proxy proxyhost")
    ensure_prefix()
    names = sys.argv[1:] or list(SCENARIOS)
    for name in names:
        before = len(failures)
        g = SCENARIOS[name]()
        if g is not None and (len(failures) != before or os.environ.get("PROXYHOST_VERBOSE")):
            dump(g)
    print(f"\nproxyhost: {passes} checks passed, {len(failures)} failed")
    for f in failures:
        print("  FAIL " + f)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
