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
import struct
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
    check(g, has(g.lines, "core entry bytes ff 25") and has(g.lines, "core entry is kiln gate=0"),
          "at kiln_boot the entry holds entrypatch-2's jump again: Kiln's gate put the saved bytes back before the core")
    check(g, has(g.lines, "main GetdfDIJoystick size=32 objsize=24 flags=1 datasize=80 objects=44"),
          "GetdfDIJoystick: c_dfDIJoystick (fallback under Wine)")
    check(g, has(g.lines, "main KilnProxyInfo abi=2"), "KilnProxyInfo export answers abi 2")
    check(g, has(g.lines, "main DirectInput8Create hr=0x00000000"), "DirectInput8Create forwards to the system DLL")
    check(g, has(g.lines, "core play_once=0 nonce= profile= chain=17"), "play_once NONE without play-once.ini")
    # rc -9: Wine's teardown sometimes SIGKILLs the launcher after the program ended (seen with no proxy at all)
    check(g, has(g.lines, "main done") and g.rc in (0, -9), f"main() ran to the end (exit code {g.rc})")
    check(g, any("entry gate installed" in line for line in g.proxy_log), "proxy.log written")
    return g


def s_emf():
    """EMF 0.5: its loader (which also exports ShroudForge's chain-host name) loads as EMF; dinput8-chain.ini [EMF]
    (the DLLs EMF's installer renamed) loads with [Chain] in file order, with the same dedupe and refusals."""
    g = Game("emf")
    g.dll("fake-emf05", "kiln/chain/emf-dinput8.dll")
    g.dll("emf-x", "mods/emf-x.dll")
    g.dll("emf-y", "emf-y.dll")
    g.dll("chain-a", "chain-a.dll")
    g.dll("chain-b", "chain-b.dll")
    g.write("kiln/chain.ini", "[chain]\nchain\\emf-dinput8.dll\n")
    g.write("dinput8-chain.ini", """[Chain]
mods\\emf-x.dll
[EMF]
emf-y.dll
mods\\EMF-X.dll
C:\\abs\\renamed.dll
..\\renamed.dll
dinput8.dll
[Other]
chain-b.dll
[emf]
chain-a.dll
""")
    g.run()
    print(f"[emf] rc {g.rc}")
    c = g.chain()
    expect = [  # (status, source, detail, path suffix)
        ("0", "0", "0", "\\kiln\\chain\\emf-dinput8.dll"),
        ("0", "2", "0", "\\mods\\emf-x.dll"),
        ("0", "2", "0", "\\emf-y.dll"),
        ("4", "2", "0", "\\mods\\emf-x.dll"),
        ("3", "2", "6", "\\abs\\renamed.dll"),
        ("3", "2", "6", "\\renamed.dll"),
        ("3", "2", "1", "\\dinput8.dll"),
        ("0", "2", "0", "\\chain-a.dll"),
    ]
    check(g, len(c) == len(expect), f"{len(expect)} chain entries (got {len(c)})")
    for i, (st, src, det, suffix) in enumerate(expect):
        if i >= len(c):
            break
        e = c[i]
        ok = e["status"] == st and e["source"] == src and e["detail"] == det and e["path"].lower().endswith(suffix.lower())
        check(g, ok, f"entry {i}: status {st} source {src} detail {det} {suffix} "
                     f"(got {e['status']}/{e['source']}/{e['detail']} {e['path']})")
    check(g, has(g.lines, "dll fake-emf05 attach"), "EMF 0.5's loader is loaded, not refused as ShroudForge")
    check(g, ordered(g.lines, ["dll fake-emf05 attach", "dll emf-x attach", "dll emf-y attach", "dll chain-a attach",
                               "core boot", "main reached"]),
          "order: [chain], then dinput8-chain.ini [Chain] and [EMF] in file order; core; main")
    check(g, not has(g.lines, "dll chain-b"), "other sections of dinput8-chain.ini are not loaded")
    check(g, sum(1 for line in g.lines if line.startswith("dll emf-x attach")) == 1, "emf-x attached once (dedupe)")
    return g


def s_engine_bounds():
    g = Game("engine-bounds")
    g.dll("fake-emf-exports", "emf.dll")
    g.dll("fake-emf-wide", "emf-wide.dll")
    g.dll("fake-kiln-exports", "own.dll")
    g.dll("chain-a", "malformed.dll")
    bad = os.path.join(g.dir, "malformed.dll")
    with open(bad, "rb") as f:
        data = bytearray(f.read())
    nt = struct.unpack_from("<I", data, 0x3c)[0]
    table = nt + 24 + struct.unpack_from("<H", data, nt + 20)[0]
    struct.pack_into("<I", data, table + 20, 0xfffffff0)
    with open(bad, "wb") as f:
        f.write(data)
    # Under Wine, Unix symlinks are invisible to Windows path APIs (GetFinalPathNameByHandleW returns the path as
    # opened), so an escape through one cannot be detected there; on Windows links resolve to their final target.
    # Wine-visible escapes (absolute, drive, "..") are refused before resolving, see s_emf. Here: containment must
    # accept a legit game folder reached through a link or a mapped drive.
    g.write("dinput8-chain.ini", "[Chain]\nemf.dll\nemf-wide.dll\nown.dll\nmalformed.dll\n")
    g.run()
    c = g.chain()
    check(g, [(e["status"], e["detail"]) for e in c] == [("0", "0"), ("0", "0"), ("3", "3"), ("3", "5")],
          "export-only and UTF-16 EMF accepted; export-only Kiln and malformed PE refused")
    check(g, has(g.lines, "dll fake-emf-exports attach"), "EMF without identity marker loads before SF refusal")

    # The Steam library behind a symlink: the exe is started through the link.
    linked = Game("engine-bounds-linked")
    linked.dll("chain-b", "sub/b.dll")
    linked.write("dinput8-chain.ini", "[EMF]\nsub/b.dll\n")
    via = os.path.join(RUN, "engine-bounds-via")
    if os.path.lexists(via):
        os.remove(via)
    os.symlink(linked.dir, via, target_is_directory=True)
    real, linked.dir = linked.dir, via
    try:
        linked.run()
    finally:
        linked.dir = real
    lc = linked.chain()
    check(linked, len(lc) == 1 and lc[0]["status"] == "0" and has(linked.lines, "dll chain-b attach"),
          "game folder reached through a symlink: its DLLs are inside, not refused")

    # Proton's S: drive mapped to the game folder itself (a drive root keeps its trailing separator).
    drive = Game("engine-bounds-drive")
    drive.dll("chain-b", "b.dll")
    drive.write("dinput8-chain.ini", "[EMF]\nb.dll\n")
    letter = os.path.join(PFX, "dosdevices", "s:")
    if os.path.lexists(letter):
        os.remove(letter)
    os.symlink(drive.dir, letter)
    try:
        drive.exe = "S:\\enshrouded.exe"
        e = env()
        e["PROXYHOST_LOG"] = winpath(drive.log)
        subprocess.run([WINE, drive.exe], cwd=drive.dir, env=e, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                       timeout=120)
        drive.lines = open(drive.log, encoding="utf-8").read().splitlines() if os.path.exists(drive.log) else []
    finally:
        os.remove(letter)
    dc = drive.chain()
    check(drive, len(dc) == 1 and dc[0]["status"] == "0" and dc[0]["path"].upper().startswith("S:\\")
          and has(drive.lines, "dll chain-b attach"), "game folder at a mapped drive root (S:\\): its DLLs are inside")
    return g


def s_emf_limits():
    g = Game("emf-limits")
    g.dll("chain-a", "a.dll")
    g.write("dinput8-chain.ini", "[Chain]\n" + "a.dll\n" * 128 + "[EMF]\n" + "a.dll\n" * 10000)
    g.run()
    c = g.chain()
    check(g, len(c) == 257 and c[0]["status"] == "0" and all(e["status"] == "4" for e in c[1:256])
          and (c[-1]["status"], c[-1]["detail"]) == ("3", "7"), "256-entry limit spans both sections; dedupe loads once")
    huge = Game("emf-size")
    huge.dll("chain-b", "b.dll")
    huge.write("dinput8-chain.ini", "[EMF]\nb.dll\n;" + "x" * (256 * 1024))
    huge.run()
    check(huge, not huge.chain() and not has(huge.lines, "dll chain-b attach") and has(huge.lines, "main reached"),
          "oversized ini ignored without loading DLLs or blocking startup")
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
        # The proxy's storage is bounded: per list it reports the first too-many entry and discards the rest
        # (docs/kiln/engines.md "Proxy"). kini.py lists every overflow entry; compare up to the first.
        seen_overflow, bounded = set(), []
        for w in want:
            if w[2] == "refused" and w[3] == "too-many":
                if w[0] in seen_overflow:
                    continue
                seen_overflow.add(w[0])
            bounded.append(w)
        want = bounded
        got =[(e["source"], norm(e["path"]).lower(), e["status"], e["detail"]) for e in g.chain()]
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


# ------------------------------------------------------------------------------------- chain.ini [late] ----

def late_library(name, dlls):
    lib = os.path.join(RUN, name + "-library")
    if os.path.exists(lib):
        shutil.rmtree(lib)
    os.makedirs(lib)
    for d in dlls:
        shutil.copy(os.path.join(PH, d + ".dll"), os.path.join(lib, d + ".dll"))
    return lib


def s_late():
    """chain.ini [late] with the stand-in core: an entry of [chain], dinput8-chain.ini or [profile] whose DLL [late] names
    passes every check but is not loaded (status 6); the others load as before; a [late] line that names no entry loads
    nothing. Matching is by canonical path, so other spellings, case, '..' and the game-relative spelling count."""
    g = Game("late")
    for n in ("chain-a", "chain-b", "fake-sf"):
        g.dll(n, f"kiln/chain/{n}.dll")
    g.dll("chain-d", "kiln/chain/orphan.dll")
    g.dll("emf-x", "mods/emf-x.dll")
    g.dll("emf-y", "mods/emf-y.dll")
    lib = late_library("late", ["chain-c"])
    lib_c = winpath(os.path.join(lib, "chain-c.dll"))
    g.write("kiln/chain.ini", f"""[chain]
chain/chain-a.dll
chain/chain-b.dll
chain/fake-sf.dll
chain/missing-late.dll
[profile]
{lib_c}
[Late]
; the launcher's comment lines stay comments
// kiln rule x
chain/./CHAIN-B.dll
late = mods/emf-x.dll
chain/fake-sf.dll
chain/missing-late.dll
{winpath(os.path.join(lib, "..", os.path.basename(lib), "chain-c.dll"))}
chain/orphan.dll
readme.txt
""")
    g.write("dinput8-chain.ini", "[Chain]\nmods\\emf-x.dll\nmods\\emf-y.dll\n")
    g.run()
    print(f"[late] rc {g.rc}")
    c = g.chain()
    expect = [  # (status, source, detail, module loaded, path suffix)
        ("0", "0", "0", True, "\\kiln\\chain\\chain-a.dll"),
        ("6", "0", "0", False, "\\kiln\\chain\\chain-b.dll"),
        ("3", "0", "4", False, "\\kiln\\chain\\fake-sf.dll"),
        ("2", "0", "0", False, "\\kiln\\chain\\missing-late.dll"),
        ("6", "2", "0", False, "\\mods\\emf-x.dll"),
        ("0", "2", "0", True, "\\mods\\emf-y.dll"),
        ("6", "1", "0", False, "\\chain-library\\chain-c.dll".replace("chain-library", "late-library")),
    ]
    check(g, len(c) == len(expect), f"{len(expect)} chain entries, the orphan [late] line adds none (got {len(c)})")
    for i, (st, src, det, loaded, suffix) in enumerate(expect):
        if i >= len(c):
            break
        e = c[i]
        ok = e["status"] == st and e["source"] == src and e["detail"] == det and e["path"].lower().endswith(suffix.lower()) \
            and (e["module"] == "1") == loaded
        check(g, ok, f"entry {i}: status {st} source {src} detail {det} loaded {loaded} {suffix} "
                     f"(got {e['status']}/{e['source']}/{e['detail']} module {e['module']} {e['path']})")
    check(g, has(g.lines, "dll chain-a attach") and has(g.lines, "dll emf-y attach"), "the entries [late] does not name load as before")
    check(g, not any(x.startswith(("dll chain-b", "dll emf-x", "dll chain-c", "dll chain-d", "dll fake-sf")) for x in g.lines),
          "deferred, refused and orphan DLLs are not loaded by the proxy")
    check(g, has(g.lines, "probe chain-a kiln-core=0"), "an early chain DLL sees no core")
    check(g, ordered(g.lines, ["dll chain-a attach", "dll emf-y attach", "core boot", "main reached"]), "early DLLs, then the core, then main")
    deferred = [x for x in g.proxy_log if "deferred" in x]
    check(g, len(deferred) == 3 and all("chain.ini [late]" in x for x in deferred), f"proxy.log: 3 deferred lines ({len(deferred)})")
    check(g, any("orphan.dll" in x and "names no entry" in x for x in g.proxy_log), "proxy.log names the orphan [late] line")
    check(g, any("fake-sf.dll refused" in x for x in g.proxy_log) and any("missing-late.dll missing" in x for x in g.proxy_log),
          "a refused or missing entry named in [late] keeps its status, once")
    check(g, has(g.lines, "core play_once=0 nonce= profile= chain=7"), "KilnBootInfo carries 7 entries")
    return g


def s_late_modes():
    """[late] in every mode: play-once mode=profile (chain.ini [late] defers [chain] entries and chain/once.ini [profile]
    entries by path), play-once no-mods (everything is skipped, nothing is deferred), and chain order is load order."""
    g = Game("late-profile")
    for n in ("chain-a", "chain-b"):
        g.dll(n, f"kiln/chain/{n}.dll")
    lib = late_library("late-profile", ["chain-c"])
    lib_c = winpath(os.path.join(lib, "chain-c.dll"))
    g.write("kiln/chain.ini", f"[chain]\nchain/chain-b.dll\nchain/chain-a.dll\n[profile]\n{lib_c}\n[late]\nchain/chain-a.dll\n{lib_c}\nchain/chain-b.dll\n")
    g.write("kiln/chain/once.ini", f"[profile]\n{lib_c}\n")
    g.play_once("profile", nonce="late-once")
    g.run()
    c = g.chain()
    check(g, [(e["status"], e["source"]) for e in c] == [("6", "0"), ("6", "0"), ("6", "1")] and
          [os.path.basename(e["path"].replace("\\", "/")).lower() for e in c] == ["chain-b.dll", "chain-a.dll", "chain-c.dll"],
          f"play-once profile: [chain] and once.ini [profile] entries deferred, in chain order, not [late] order ({[(e['status'], e['source'], e['path'][-12:]) for e in c]})")
    check(g, not has(g.lines, "dll ") and has(g.lines, "core play_once=3"), "play-once profile: nothing loaded by the proxy")
    n = Game("late-nomods")
    n.dll("chain-a", "kiln/chain/chain-a.dll")
    n.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n[late]\nchain/chain-a.dll\nchain/orphan.dll\n")
    n.play_once("no-mods", nonce="late-nomods")
    n.run()
    nc = n.chain()
    check(n, len(nc) == 1 and nc[0]["status"] == "5", "play-once no-mods: the entry is SKIPPED, not deferred")
    check(n, not has(n.lines, "dll ") and not any("deferred" in x for x in n.proxy_log), "play-once no-mods: nothing loads, nothing is deferred")
    p = Game("late-corrupt")
    for name in ("chain-a", "chain-c"):
        p.dll(name, f"kiln/chain/{name}.dll")
    p.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n[profile]\nchain/chain-c.dll\n[late]\nchain/chain-a.dll\nchain/chain-c.dll\n")
    p.write("kiln/profile.json", '{"format": 1, "id": "x", "generation": 1, "mods": {"kiln": [{"id": "a", "path": "Z:\\\\tr')
    p.run()
    pc = p.chain()
    check(p, [e["status"] for e in pc] == ["6", "5"], "corrupt profile.json: [chain] entry deferred as usual, [profile] entry SKIPPED")
    return g


def s_late_realcore():
    """The real core loads the proxy's late entries after Kiln's native phases, with the same SEH guard and reports:
    state/native.json chain.late, a dllmain native mod finishing late, boot.json chain[] with the final status and
    "late": true, kiln.log lines. Safe idle (no sigdb): the player's chain DLLs still load."""
    g, lib, profile = realcore_game("late-real")
    for n in ("chain-a", "chain-b", "chain-d", "failing", "fake-sf"):
        g.dll(n, f"kiln/chain/{n}.dll")
    dllmain = winpath(os.path.join(lib, "native", "native-test-dllmain", "native-test-dllmain.dll"))
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    g.write("kiln/chain.ini", f"""[chain]
chain/chain-a.dll
chain/chain-b.dll
chain/failing.dll
chain/fake-sf.dll
chain/chain-d.dll
[profile]
{dllmain}
[late]
chain/chain-d.dll
{dllmain}
chain/failing.dll
chain/fake-sf.dll
chain/chain-b.dll
""")
    g.run()
    print(f"[late real core] rc {g.rc}")
    b, n, log = state(g, "boot.json"), state(g, "native.json"), kiln_log(g)

    def at(prefix):
        return next((i for i, line in enumerate(log) if prefix in line), -1)

    check(g, has(g.lines, "probe chain-a kiln-core=0 native-test-boot=0"), "chain-a (not late) loaded before the core")
    check(g, has(g.lines, "probe chain-b kiln-core=1 native-test-boot=1") and has(g.lines, "probe chain-d kiln-core=1 native-test-boot=1"),
          "late DLLs load with the core and the boot-phase native mods already in the process")
    check(g, ordered(g.lines, ["dll chain-a attach", "dll chain-b attach", "dll failing attach", "dll chain-d attach"]),
          "late DLLs load in chain order (chain-b, failing, chain-d), not in [late] order")
    check(g, not has(g.lines, "dll fake-sf"), "a refused entry named in [late] stays refused")
    i_boot, i_late, i_done = at("native: phase boot"), at("native: loading 4 chain DLL(s)"), at("boot complete")
    check(g, 0 <= i_boot < i_late < i_done, f"kiln.log: native boot phase ({i_boot}) < late phase ({i_late}) < boot complete ({i_done})")
    check(g, any("late: " in l and "chain-b.dll loaded at" in l for l in log), "kiln.log: late: <path> loaded at ...")
    check(g, any(l.startswith("[W ") and "late: " in l and "failing.dll failed to load (error 1114: its DllMain failed)" in l for l in log),
          "kiln.log: a late DLL that fails to load is a warning with its error")
    check(g, any("native mod native-test-dllmain" in l and "deferred to the late phase" in l for l in log), "kiln.log: the dllmain native mod finished late")
    late = n.get("chain", {}).get("late", [])
    want = [("chain-b.dll", "loaded", 0), ("failing.dll", "failed", 1114), ("chain-d.dll", "loaded", 0), ("native-test-dllmain.dll", "loaded", 0)]
    check(g, [(os.path.basename(x["path"].replace("\\", "/")), x["status"], x["error"]) for x in late] == want and
          [x["order"] for x in late] == [1, 2, 3, 4] and [x["source"] for x in late] == ["chain", "chain", "chain", "profile"],
          f"native.json chain.late: {[(os.path.basename(x['path'].replace(chr(92), '/')), x['status'], x['error']) for x in late]}")
    check(g, all(x["status"] != "loaded" or "ms" in x for x in late), "native.json chain.late: loaded entries carry ms")
    mods = {m["id"]: m for m in n.get("mods", [])}
    dm = mods.get("native-test-dllmain", {})
    check(g, dm.get("state") == "loaded" and dm.get("phase") == "dllmain" and [d["status"] for d in dm.get("dlls", [])] == ["loaded"],
          f"the dllmain native mod: loaded by the late phase ({dm.get('state')}, {dm.get('reason')})")
    check(g, dm.get("loadIndex", -1) > mods.get("native-test-boot", {}).get("loadIndex", 99), "its loadIndex is after the boot-phase mods")
    check(g, n.get("phase") in ("dllmain", "early", "boot", "world"), f"native.json phase stays in its enum ({n.get('phase')})")
    chain = b.get("chain", [])
    flagged = [(os.path.basename(x["path"].replace("\\", "/")), x["status"], x.get("late")) for x in chain if x.get("late")]
    check(g, flagged == [("chain-b.dll", "loaded", True), ("failing.dll", "failed", True), ("chain-d.dll", "loaded", True),
                         ("native-test-dllmain.dll", "loaded", True)], f"boot.json chain[]: late entries with their final status {flagged}")
    check(g, all(x["status"] in ("loaded", "failed", "missing", "refused", "duplicate", "skipped") and x["source"] in ("chain", "emf", "profile")
                 for x in chain), "boot.json chain[] stays inside the statuses and sources the launcher parses")
    check(g, [x for x in chain if x["path"].lower().endswith("fake-sf.dll")][0]["status"] == "refused", "boot.json: the refused entry")
    check(g, b.get("native", {}).get("pending") == 0, f"boot.json native.pending is 0 ({b.get('native')})")
    passes = [l for l in log if "native-test" in l and " PASS" in l]
    fails = [l for l in log if "native-test" in l and " FAIL" in l]
    check(g, len(passes) >= 8 and not fails, f"native-test lines: {len(passes)} PASS, {len(fails)} FAIL")
    return g


def s_rules_realcore():
    """compat-rules.json in the real core (safe idle): the shipped file and the player's (configDir), a player's rule
    replacing a shipped one, a bad rule dropped with a warning, `warn` logged, `late` reported as not applied, and
    `disable` of a mod the profile lists applied before the native loader reads it."""
    g, lib, profile = realcore_game("rules-real")
    g.dll("chain-a", "kiln/chain/chain-a.dll")
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\n")

    def rule(id, action, message, when=None, subject=None):
        r = {"id": id, "when": when if when is not None else [{"module": "chain-a.dll"}], "action": action, "message": message}
        if subject:
            r["subject"] = subject
        return r

    shipped = {"format": 1, "rules": [
        rule("warn-chain-a", "warn", "chain-a is old"),
        rule("off-clientside", "disable", "clientside clashes with chain-a", subject={"mod": "native:native-test-clientside"}),
        rule("late-chain-a", "late", "chain-a hooks too early", subject={"module": "CHAIN-A.dll"}),
        {"id": "broken", "when": [{"module": "chain-a.dll"}], "action": "late", "message": "no subject"},
        rule("not-here", "warn", "chain-b is not here", when=[{"module": "chain-b.dll"}]),
        rule("off-absent", "disable", "x", subject={"mod": "emf:nothing"}),
    ]}
    json.dump(shipped, open(os.path.join(g.kiln, "compat-rules.json"), "w"))
    os.makedirs(os.path.join(lib, "config"), exist_ok=True)
    json.dump({"format": 1, "rules": [rule("warn-chain-a", "warn", "chain-a is old, says the player")]},
              open(os.path.join(lib, "config", "compat-rules.json"), "w"))
    g.run()
    print(f"[rules real core] rc {g.rc}")
    n, log = state(g, "native.json"), kiln_log(g)
    mods = {m["id"]: m for m in n.get("mods", [])}
    cs = mods.get("native-test-clientside", {})
    check(g, cs.get("state") == "disabled" and "switched off by compat rule 'off-clientside'" in (cs.get("reason") or "") and
          "clientside clashes with chain-a" in (cs.get("reason") or ""), f"disable on a listed mod: native.json says {cs.get('state')} / {cs.get('reason')}")
    check(g, all(mods.get(k, {}).get("state") == "loaded" for k in ("native-test-early", "native-test-boot")), "the other native mods load")
    check(g, any("compat: rule off-clientside switched native:native-test-clientside off" in l for l in log), "kiln.log: rule switched the mod off")
    check(g, any("compat: rule warn-chain-a: chain-a is old, says the player" in l for l in log) and not any("chain-a is old" in l and "says the player" not in l for l in log),
          "warn: the player's rule replaced the shipped one")
    check(g, any("the player's rule 'warn-chain-a' replaces the shipped one" in l for l in log), "kiln.log: the replacement is announced")
    check(g, any("rule late-chain-a asks CHAIN-A.dll to load late; apply it in the launcher" in l for l in log), "late is reported as not applied (launcher)")
    check(g, any("compat-rules.json" in l and "rule #4 (broken)" in l and "needs a \"subject\"" in l and l.startswith("[W ") for l in log), "a bad rule is dropped with a warning")
    check(g, not any("chain-b is not here" in l for l in log), "a rule whose matchers do not hold stays quiet")
    check(g, not any("off-absent" in l for l in log), "disable of a mod that is not there does nothing")
    check(g, any("5 compatibility rule(s)" in l or "compatibility rule(s)" in l for l in log), "kiln.log counts the rules")
    return g


# --------------------------------------------------------------------- review fixes for chain.ini [late] ----

def plain_mod(lib, profile, mid, enabled=True, **fields):
    """A dllmain native mod made of native-plain.dll (it writes <folder>/native-plain.log when its DLL runs), listed in the
    profile. Returns the DLL's game path."""
    d = os.path.join(lib, "native", mid)
    os.makedirs(d, exist_ok=True)
    shutil.copy(os.path.join(BIN, "samples", "native-test", "native-plain.dll"), os.path.join(d, mid + ".dll"))
    json.dump(dict({"id": mid, "version": "1.0.0", "dlls": [mid + ".dll"], "phase": "dllmain", "side": "both"}, **fields),
              open(os.path.join(d, "native.json"), "w"))
    entry = {"id": mid, "path": winpath(d)}
    if not enabled:
        entry["enabled"] = False
    profile["mods"]["native"].append(entry)
    return winpath(os.path.join(d, mid + ".dll"))


def ran(lib, mid):
    return os.path.exists(os.path.join(lib, "native", mid, "native-plain.log"))


def empty_profile(profile):
    profile["mods"]["native"] = []
    profile["mods"]["plugin"] = []
    return profile


def late_rows(n):
    return {os.path.basename(x["path"].replace("\\", "/")): x for x in n.get("chain", {}).get("late", [])}


def s_late_native_realcore():
    """A [late] DLL of a native mod that is disabled, on the wrong side or switched off by a compat rule is not loaded (the
    row says skipped and why); one the proxy loaded before a `disable` rule could be read stays reported as running, and
    the rule as not applied. No kiln.log line says \"switched off\" for a DLL that runs."""
    g, lib, profile = realcore_game("late-native")
    empty_profile(profile)
    dlls = {
        "late-ok": plain_mod(lib, profile, "late-ok"),
        "late-off": plain_mod(lib, profile, "late-off", enabled=False),
        "late-server": plain_mod(lib, profile, "late-server", side="server"),
        "late-rule": plain_mod(lib, profile, "late-rule"),
        "now-rule": plain_mod(lib, profile, "now-rule"),
    }
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    late = "\n".join(dlls[m] for m in ("late-ok", "late-off", "late-server", "late-rule"))
    g.write("kiln/chain.ini", "[profile]\n" + "\n".join(dlls.values()) + "\n[late]\n" + late + "\n")
    json.dump({"format": 1, "rules": [
        {"id": "r-late", "action": "disable", "subject": {"mod": "native:late-rule"}, "message": "clash A"},
        {"id": "r-now", "action": "disable", "subject": {"mod": "native:now-rule"}, "message": "clash B"},
    ]}, open(os.path.join(g.kiln, "compat-rules.json"), "w"))
    g.run()
    print(f"[late native real core] rc {g.rc}")
    n, b, log = state(g, "native.json"), state(g, "boot.json"), kiln_log(g)
    mods = {m["id"]: m for m in n.get("mods", [])}
    rows = late_rows(n)
    check(g, ran(lib, "late-ok") and mods["late-ok"]["state"] == "loaded", "a late DLL of an enabled mod loads late")
    for mid in ("late-off", "late-server", "late-rule"):
        check(g, not ran(lib, mid), f"{mid}: the DLL is not loaded")
    check(g, [rows[m + ".dll"]["status"] for m in ("late-off", "late-server", "late-rule")] == ["skipped"] * 3 and
          rows["late-ok.dll"]["status"] == "loaded", f"native.json chain.late: {[(k, v['status']) for k, v in rows.items()]}")
    check(g, "is disabled" in rows["late-off.dll"].get("note", "") and "is skipped" in rows["late-server.dll"].get("note", "") and
          "switched off by compat rule 'r-late'" in rows["late-rule.dll"].get("note", ""), "the rows say why")
    check(g, mods["late-off"]["state"] == "disabled" and mods["late-server"]["state"] == "skipped" and
          mods["late-rule"]["state"] == "disabled" and "switched off by compat rule 'r-late'" in (mods["late-rule"].get("reason") or ""),
          f"the mods stay as they were resolved ({[(k, v['state']) for k, v in mods.items()]})")
    for mid in ("late-off", "late-server", "late-rule"):
        check(g, any(l.startswith("[W ") and f"late: " in l and f"{mid}.dll is not loaded: native mod {mid} is" in l for l in log),
              f"kiln.log: a warning says the late DLL of {mid} is not loaded")
    chain = {os.path.basename(x["path"].replace("\\", "/")): x for x in b.get("chain", [])}
    check(g, all(chain[m + ".dll"]["status"] == "skipped" and chain[m + ".dll"].get("late") for m in ("late-off", "late-server", "late-rule")) and
          chain["late-ok.dll"]["status"] == "loaded", "boot.json: the skipped late entries show skipped (and late), the loaded one loaded")
    # a rule against a mod whose DLL the proxy loaded already
    now = mods["now-rule"]
    check(g, ran(lib, "now-rule") and now["state"] == "loaded" and now.get("reason") is None and
          any("the proxy loaded its DLL before kiln-core started" in x for x in now.get("notes", [])),
          f"now-rule: running, reported loaded with a note ({now.get('state')}, {now.get('notes')})")
    check(g, any("compat: rule r-now asks native:now-rule to be disabled, but its DLL was loaded by the proxy" in l and l.startswith("[W ") for l in log),
          "kiln.log: the rule is reported as not applied (the DLL runs)")
    check(g, not any("r-now switched" in l or "now-rule" in l and "switched off" in l for l in log), "kiln.log never says a running mod was switched off")
    check(g, any("compat: rule r-late switched native:late-rule off: clash A" in l for l in log), "kiln.log: a rule that kept the DLL out says it switched the mod off")
    return g


def s_late_policy_realcore():
    """kiln.json native.policy applies to a DLL chain.ini [late] deferred (the proxy has not loaded it); what the proxy
    loaded before kiln.json was read keeps its note."""
    g, lib, profile = realcore_game("late-policy")
    empty_profile(profile)
    late = plain_mod(lib, profile, "late-pol")
    now = plain_mod(lib, profile, "now-pol")
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    g.write("kiln/chain.ini", f"[profile]\n{late}\n{now}\n[late]\n{late}\n")
    json.dump({"native": {"policy": "never"}}, open(os.path.join(g.kiln, "kiln.json"), "w"))
    g.run()
    print(f"[late policy real core] rc {g.rc}")
    n, log = state(g, "native.json"), kiln_log(g)
    mods = {m["id"]: m for m in n.get("mods", [])}
    check(g, not ran(lib, "late-pol") and ran(lib, "now-pol"), "native.policy never: the deferred DLL stays out, the proxy's had loaded")
    check(g, mods["late-pol"]["state"] == "policy" and "chain.ini [late] deferred its DLL" in (mods["late-pol"].get("reason") or "") and
          not any("before kiln.json" in x for x in mods["late-pol"].get("notes", [])), f"late-pol: policy ({mods['late-pol'].get('state')}, {mods['late-pol'].get('reason')})")
    check(g, mods["now-pol"]["state"] == "loaded" and any("before kiln.json native.policy" in x for x in mods["now-pol"].get("notes", [])),
          "now-pol: loaded by the proxy, with the note that the policy came too late")
    check(g, late_rows(n)["late-pol.dll"]["status"] == "skipped" and any("late-pol.dll is not loaded: native mod late-pol is policy" in l for l in log),
          "the late row says skipped (policy)")
    return g


def s_late_requires_realcore():
    """An early or boot native mod that `requires` a dllmain mod chain.ini [late] defers cannot be satisfied (the late phase
    runs last): it is refused up front with that reason, never with \"failed to load\". A world mod is accepted."""
    g, lib, profile = realcore_game("late-requires")
    empty_profile(profile)
    base = plain_mod(lib, profile, "base-late")
    for mid, phase in (("early-dep", "early"), ("boot-dep", "boot"), ("world-dep", "world")):
        plain_mod(lib, profile, mid, phase=phase, requires=["base-late"])
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    g.write("kiln/chain.ini", f"[profile]\n{base}\n[late]\n{base}\n")
    g.run()
    print(f"[late requires real core] rc {g.rc}")
    n, log = state(g, "native.json"), kiln_log(g)
    mods = {m["id"]: m for m in n.get("mods", [])}
    check(g, mods["base-late"]["state"] == "loaded" and ran(lib, "base-late"), "the deferred mod loads in the late phase")
    for mid, phase in (("early-dep", "early"), ("boot-dep", "boot")):
        m = mods[mid]
        why = m.get("reason") or ""
        check(g, m["state"] == "failed" and "requires base-late" in why and "chain.ini [late] loads after the plugins" in why and
              f"this mod's {phase} phase" in why and "failed to load" not in why, f"{mid}: refused up front, accurately ({m['state']}: {why})")
        check(g, not ran(lib, mid), f"{mid}: not loaded")
    w = mods["world-dep"]
    check(g, w["state"] == "skipped" and "needs hooks" in (w.get("reason") or ""),
          f"world-dep: accepted by the dependency check (safe idle skips every world mod: {w['state']}: {w.get('reason')})")
    check(g, not any("failed to load" in l for l in log), "kiln.log never says a required mod failed to load")
    return g


def s_late_already_loaded_realcore():
    """EMF's loader (chained) starts the DLLs of dinput8-chain.ini itself, so [late] cannot defer them: the late phase finds
    the DLL in the process and says so (native.json, boot.json, a kiln.log warning, and the compat rule outcomes)."""
    g, lib, profile = realcore_game("late-already")
    empty_profile(profile)
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    g.dll("emf-loader", "kiln/chain/emf-loader.dll")
    g.dll("emf-x", "mods/emf-x.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/emf-loader.dll\n[late]\nmods/emf-x.dll\n")
    g.write("dinput8-chain.ini", "[Chain]\nmods\\emf-x.dll\n")
    json.dump({"format": 1, "rules": [
        {"id": "x-late", "action": "late", "subject": {"module": "emf-x.dll"}, "message": "xx"},
        {"id": "loader-late", "action": "late", "subject": {"module": "emf-loader.dll"}, "message": "ll"},
    ]}, open(os.path.join(g.kiln, "compat-rules.json"), "w"))
    g.run()
    print(f"[late already real core] rc {g.rc}")
    n, b, log = state(g, "native.json"), state(g, "boot.json"), kiln_log(g)
    check(g, any("emf-loader.dll loaded at" in x for x in g.proxy_log) and
          any("dinput8-chain.ini" in x and "emf-x.dll deferred" in x for x in g.proxy_log),
          "the proxy: the loader loads, the dinput8-chain.ini entry is deferred")
    check(g, sum(1 for x in g.lines if x == "dll emf-x attach") == 1 and has(g.lines, "loader emf-loader started mods\\emf-x.dll: yes"),
          "emf-x runs once, started by the loader")
    row = late_rows(n).get("emf-x.dll", {})
    check(g, row.get("status") == "already-loaded" and "another loader started it" in row.get("note", "") and "ms" not in row,
          f"native.json chain.late: already-loaded with a note ({row})")
    check(g, any(l.startswith("[W ") and "late: " in l and "emf-x.dll was already loaded when the late phase ran" in l for l in log), "kiln.log: a warning, not 'loaded at'")
    check(g, not any("emf-x.dll loaded at" in l for l in log), "kiln.log never says the core loaded it late")
    chain = {os.path.basename(x["path"].replace("\\", "/")): x for x in b.get("chain", [])}
    check(g, chain["emf-x.dll"]["status"] == "loaded" and chain["emf-x.dll"].get("late") is True and chain["emf-x.dll"].get("lateIgnored") is True,
          f"boot.json: loaded, late, lateIgnored ({chain['emf-x.dll']})")
    check(g, any("compat: emf-x.dll is named in chain.ini [late], but another loader started it first" in l for l in log),
          "compat rule: the late rule on it is not applied, and says why")
    check(g, any("compat: emf-loader.dll is EMF's loader" in l and "cannot be moved late" in l for l in log), "compat rule: a late rule on EMF's loader is not applied")
    return g


def s_late_loader_and_entry():
    """EMF's own loader is never deferred, even when [late] names it (it starts EMF itself, which kiln-emf has to see when
    the plugins start). Any other DLL may patch the entry point from the late phase: the gate put the entry's bytes back
    before the core started, so the patch saves the right bytes and runs before the game's entry, last in, first out."""
    g = Game("late-emf-loader")
    for n in ("emf-loader", "emf-loader-patch", "chain-a"):
        g.dll(n, f"kiln/chain/{n}.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/emf-loader.dll\nchain/emf-loader-patch.dll\nchain/chain-a.dll\n[late]\nchain/emf-loader.dll\nchain/emf-loader-patch.dll\nchain/chain-a.dll\n")
    g.write("dinput8-chain.ini", "")
    g.dll("emf-x", "mods/emf-x.dll")
    g.run()
    print(f"[late EMF loader] rc {g.rc}")
    c = g.chain()
    check(g, [(e["status"], e["module"]) for e in c] == [("0", "1"), ("0", "1"), ("6", "0")],
          f"EMF's loaders load in their place, the other entry is deferred ({[(e['status'], e['module']) for e in c]})")
    check(g, sum(1 for x in g.proxy_log if "is EMF's loader: it starts EMF itself" in x and "does not defer it" in x) == 2, "proxy.log: one line per loader")
    check(g, ordered(g.lines, ["dll emf-loader-patch attach", "core boot", "gate emf-loader-patch", "main reached"]),
          "a loader that holds the start keeps its entry patch: Kiln's gate, then its gate, then the game")
    check(g, not any("entry point" in x and "patched" in x for x in g.proxy_log), "no entry-patch line for a patch made before the gate")
    check(g, has(g.lines, "core entry bytes ff 25") and has(g.lines, "core entry is kiln gate=0"), "at kiln_boot the entry holds the loader's jump again, not Kiln's gate")

    # a DLL deferred to the late phase that patches the entry point: it saves the game's own bytes and its gate runs
    r, lib, profile = realcore_game("late-entry-patch")
    empty_profile(profile)
    json.dump(profile, open(os.path.join(r.kiln, "profile.json"), "w"))
    r.dll("entrypatch-1", "kiln/chain/entrypatch-1.dll")
    r.write("kiln/chain.ini", "[chain]\nchain/entrypatch-1.dll\n[late]\nchain/entrypatch-1.dll\n")
    r.run()
    print(f"[late entry patch] rc {r.rc}")
    saved = [l for l in r.lines if l.startswith("patch entrypatch-1: entry patched")]
    check(r, len(saved) == 1 and "(saved ff 25)" not in saved[0], f"the late DLL saved the game's own entry bytes, not Kiln's jump ({saved})")
    check(r, ordered(r.lines, ["probe entrypatch-1 kiln-core=1", "patch entrypatch-1: entry patched", "gate entrypatch-1", "main reached"]) and r.rc in (0, -9),
          "the late DLL's gate runs before the game's entry, and the game starts")
    check(r, sum(1 for l in r.lines if l.startswith("gate entrypatch-1")) == 1, "its gate runs once")
    check(r, any("patched the game's entry point; its patch runs now" in x for x in r.proxy_log), "proxy.log notes the patch")
    check(r, any(l.startswith("[I ") and "patched the game's entry point" in l for l in kiln_log(r)) and
          not any(l.startswith(("[W ", "[E ")) and "entry point" in l for l in kiln_log(r)), "kiln.log notes it as information")
    check(r, not any("entry point" in e for e in state(r, "boot.json").get("errors", [])), "boot.json errors: nothing about the entry point")

    # an early patcher and a late one: last in, first out across the two phases
    m, lib_m, profile_m = realcore_game("late-entry-mixed")
    empty_profile(profile_m)
    json.dump(profile_m, open(os.path.join(m.kiln, "profile.json"), "w"))
    for n in ("entrypatch-1", "entrypatch-2"):
        m.dll(n, f"kiln/chain/{n}.dll")
    m.write("kiln/chain.ini", "[chain]\nchain/entrypatch-2.dll\nchain/entrypatch-1.dll\n[late]\nchain/entrypatch-1.dll\n")
    m.run()
    print(f"[late entry patch, mixed] rc {m.rc}")
    check(m, ordered(m.lines, ["patch entrypatch-2: entry patched", "probe entrypatch-1 kiln-core=1", "patch entrypatch-1: entry patched (saved ff 25)",
                               "gate entrypatch-1", "gate entrypatch-2", "main reached"]) and m.rc in (0, -9),
          "the late patch saved the early loader's jump; gates run late one first, then the early one, then the game")
    check(m, sum(1 for l in m.lines if l.startswith("gate ")) == 2, "each gate runs once")

    # a late DLL that patches the entry point and then fails its DllMain is unloaded: its jump points into freed memory
    d, lib_d, profile_d = realcore_game("late-entry-dead")
    empty_profile(profile_d)
    json.dump(profile_d, open(os.path.join(d.kiln, "profile.json"), "w"))
    d.dll("entrypatch-fail", "kiln/chain/entrypatch-fail.dll")
    d.write("kiln/chain.ini", "[chain]\nchain/entrypatch-fail.dll\n[late]\nchain/entrypatch-fail.dll\n")
    d.run()
    print(f"[late entry patch, DLL gone] rc {d.rc}")
    check(d, ordered(d.lines, ["patch entrypatch-fail: entry patched", "main reached"]) and not has(d.lines, "gate entrypatch-fail") and d.rc in (0, -9),
          "a patch whose DLL was unloaded again is removed and the game starts")
    check(d, any("is not in the process any more; its patch was removed" in x for x in d.proxy_log), "proxy.log says the dead patch was removed")
    check(d, any("entrypatch-fail.dll failed to load" in l for l in kiln_log(d)), "kiln.log: the late DLL failed to load")
    # the same DLL loaded early chains as before, and nothing is reported
    e = Game("late-entry-early")
    e.dll("entrypatch-1", "kiln/chain/entrypatch-1.dll")
    e.write("kiln/chain.ini", "[chain]\nchain/entrypatch-1.dll\n")
    e.run()
    check(e, ordered(e.lines, ["dll entrypatch-1 attach", "core boot", "gate entrypatch-1", "main reached"]) and not any("entry point" in x and "patched" in x for x in e.proxy_log),
          "an entry patch made before the gate still unwinds last in, first out, with no line about it")
    q, lib2, profile2 = realcore_game("late-entry-quiet")
    empty_profile(profile2)
    json.dump(profile2, open(os.path.join(q.kiln, "profile.json"), "w"))
    q.dll("entrypatch-1", "kiln/chain/entrypatch-1.dll")
    q.write("kiln/chain.ini", "[chain]\nchain/entrypatch-1.dll\n")
    q.run()
    check(q, has(q.lines, "gate entrypatch-1") and not any("entry point" in l for l in kiln_log(q)) and
          not any("entry point" in x for x in state(q, "boot.json").get("errors", [])), "the real core: nothing said when nothing patched the entry during boot")
    return g


def s_overlap_refused_realcore():
    """A `refused` overlap with no single culprit has null symbol, rva, module and offset. It used to make the report
    throw (nlohmann on null), so hooks.json lost its overlaps, rules and ruleFiles. The overlap and the rules after it are
    logged now (safe idle: kiln.log only)."""
    g, lib, profile = realcore_game("overlap-refused")
    empty_profile(profile)
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    g.write("kiln/state/shroudforge.json", json.dumps({"mode": "hosted", "runtime": "running",
            "native": "types=3/3 game_thread=unavailable(game-thread:original-bytes-mismatch) entity_hooks{}"}))
    json.dump({"format": 1, "rules": [{"id": "always-on", "action": "warn", "message": "rules are reported too"}]},
              open(os.path.join(g.kiln, "compat-rules.json"), "w"))
    g.run()
    print(f"[overlap refused real core] rc {g.rc}")
    log = kiln_log(g)
    check(g, any(l.startswith("[W ") and "compat: overlap: ShroudForge refused its game-thread hook (original-bytes-mismatch)" in l for l in log),
          "kiln.log: the refused overlap is reported")
    check(g, any("compat: rule always-on: rules are reported too" in l for l in log), "kiln.log: the rules after the overlap are reported")
    check(g, not any("report skipped" in l for l in log), "no 'report skipped' warning")
    return g


def s_late_oldproxy():
    """An older proxy (the dev.20 one: OLD_PROXY=<its dinput8.dll>, or $KILN_OUT/oldproxy/dinput8.dll when built there from
    git HEAD~) ignores the unknown [late] section and loads the entries early; the new core boots with it and loads
    nothing late. Skipped when there is no old proxy."""
    old = os.environ.get("OLD_PROXY") or os.path.join(OUT, "oldproxy", "dinput8.dll")
    if not os.path.isfile(old):
        print("[late old proxy] skipped (no old proxy: set OLD_PROXY=<dinput8.dll of dev.20>)")
        return None
    g, lib, profile = realcore_game("late-oldproxy")
    shutil.copy(old, os.path.join(g.dir, "dinput8.dll"))
    for n in ("chain-a", "chain-b"):
        g.dll(n, f"kiln/chain/{n}.dll")
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    g.write("kiln/chain.ini", "[chain]\nchain/chain-a.dll\nchain/chain-b.dll\n[late]\nchain/chain-b.dll\nchain/orphan.dll\n")
    g.run()
    print(f"[late old proxy] rc {g.rc}")
    b, n = state(g, "boot.json"), state(g, "native.json")
    check(g, ordered(g.lines, ["dll chain-a attach", "dll chain-b attach", "main reached"]) and has(g.lines, "probe chain-b kiln-core=0"),
          "the old proxy loads the [late] entry early, as before")
    check(g, [c["status"] for c in b.get("chain", [])] == ["loaded", "loaded"] and not any(c.get("late") for c in b.get("chain", [])),
          "boot.json: both loaded by the proxy, nothing flagged late")
    check(g, "chain" not in n and b.get("errors") == [], "native.json has no chain.late block; the new core boots fine")
    check(g, not any("late:" in l or "native: loading" in l for l in kiln_log(g)), "the core loads nothing late")
    return g


def s_entry_gate_held():
    """A proxy from before the gate put the entry bytes back first (dev.21 before its review fixes: HELD_PROXY=<its
    dinput8.dll>, or $KILN_OUT/heldproxy/dinput8.dll) keeps its jump on the entry point while the core boots. A DLL that
    patches the entry point then saves that jump, and the proxy drops the patch afterwards: the new core says so. Skipped
    when there is no such proxy."""
    held = os.environ.get("HELD_PROXY") or os.path.join(OUT, "heldproxy", "dinput8.dll")
    if not os.path.isfile(held):
        print("[entry gate held] skipped (no such proxy: set HELD_PROXY=<dinput8.dll that knows [late] and restores the entry after kiln_boot>)")
        return None
    g, lib, profile = realcore_game("entry-gate-held")
    empty_profile(profile)
    json.dump(profile, open(os.path.join(g.kiln, "profile.json"), "w"))
    shutil.copy(held, os.path.join(g.dir, "dinput8.dll"))
    g.dll("entrypatch-1", "kiln/chain/entrypatch-1.dll")
    g.write("kiln/chain.ini", "[chain]\nchain/entrypatch-1.dll\n[late]\nchain/entrypatch-1.dll\n")
    g.run()
    print(f"[entry gate held] rc {g.rc}")
    check(g, has(g.lines, "patch entrypatch-1: entry patched (saved ff 25)") and not has(g.lines, "gate entrypatch-1") and has(g.lines, "main reached"),
          "with the older proxy the late DLL saves Kiln's jump and its patch is dropped; the game starts")
    check(g, any(l.startswith("[W ") and "older Kiln proxy" in l and "that patch is lost" in l for l in kiln_log(g)), "kiln.log: the new core warns")
    check(g, any("older Kiln proxy" in e for e in state(g, "boot.json").get("errors", [])), "boot.json errors: the warning")
    return g


SCENARIOS = {f.__name__[2:]: f for f in (s_chain, s_emf, s_engine_bounds, s_emf_limits, s_vanilla, s_nomods, s_profile, s_expired, s_future, s_invalid,
                                          s_server, s_undeletable, s_disabled, s_crash_reporter, s_corrupt, s_fixtures, s_semver,
                                          s_realcore, s_late, s_late_modes, s_late_realcore, s_rules_realcore, s_late_native_realcore, s_late_policy_realcore,
                                          s_late_requires_realcore, s_late_already_loaded_realcore, s_late_loader_and_entry, s_overlap_refused_realcore,
                                          s_late_oldproxy, s_entry_gate_held)}


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
