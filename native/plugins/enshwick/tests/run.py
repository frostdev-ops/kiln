#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Host tests of kiln-enshwick under plain Wine, no game.

    KILN_OUT=<build dir> native/build.sh kiln-enshwick compathost
    KILN_OUT=<build dir> python3 native/plugins/enshwick/tests/run.py [scenario...]

Each scenario lays out a scratch game folder under $KILN_OUT/enshwick-tests/run/<scenario>/ with compat's
compathost.exe (a stand-in for the game and kiln-core) as enshrouded_server.exe or enshrouded.exe, kiln-enshwick.dll,
and a loader: the stand-in bin/enshwick-tests/enshwick.dll, which records how it was started, or with ENSHWICK_REAL set
to Enshwick's real enshwick.dll (tools/build-runtime.sh), the real loader, which starts, finds no game it knows in
compathost.exe and stops. Checks kiln/logs/kiln.log, kiln/state/enshwick.json, the loader's logs/enshwick_*.log and the
files in mods/. Exit code 0 = every scenario passed.
"""
import glob
import json
import os
import shutil
import subprocess
import sys

OUT = os.path.abspath(os.environ.get("KILN_OUT", os.path.join(os.path.dirname(__file__), "../../../build")))
BIN = os.path.join(OUT, "cmake", "bin")
HOST = os.path.join(BIN, "compat-tests", "compathost.exe")
TESTS = os.path.join(BIN, "enshwick-tests")
PLUGIN = os.path.join(BIN, "plugins", "kiln-enshwick.dll")
REAL = os.environ.get("ENSHWICK_REAL", "")
WORK = os.path.join(OUT, "enshwick-tests")
PFX = os.path.join(WORK, "pfx")
RUN = os.path.join(WORK, "run")
WINE = os.environ.get("COMPAT_WINE", "wine")


def env(extra=None):
    e = dict(os.environ)
    e.update(WINEPREFIX=PFX, WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree=;mshtml=")
    e.pop("DISPLAY", None)
    e.pop("WAYLAND_DISPLAY", None)
    e.update(extra or {})
    return e


def ensure_prefix():
    os.makedirs(WORK, exist_ok=True)
    cmd = [WINE, "cmd", "/c", "exit", "0"] if os.path.isfile(os.path.join(PFX, "system.reg")) else [WINE, "wineboot", "-i"]
    r = subprocess.run(cmd, env=env(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
    return r.returncode == 0


class Game:
    def __init__(self, name, server=True):
        self.name = name
        self.dir = os.path.join(RUN, name)
        shutil.rmtree(self.dir, ignore_errors=True)
        os.makedirs(os.path.join(self.dir, "kiln"), exist_ok=True)
        self.exe = "enshrouded_server.exe" if server else "enshrouded.exe"
        shutil.copy(HOST, self.p(self.exe))
        shutil.copy(PLUGIN, self.p("kiln-enshwick.dll"))
        self.failures = []
        self.log = self.ens = ""

    def p(self, *parts):
        return os.path.join(self.dir, *parts)

    def put(self, rel, text=""):
        path = self.p(rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text if isinstance(text, str) else json.dumps(text, indent=2))

    def copy(self, src, rel):
        os.makedirs(os.path.dirname(self.p(rel)), exist_ok=True)
        shutil.copy(src, self.p(rel))

    def bundled(self, real=False):
        self.copy(REAL if real else os.path.join(TESTS, "enshwick.dll"), "kiln/plugins/enshwick/enshwick.dll")
        self.put("kiln/plugins/enshwick/enshwick-version.txt", "v1.0.0-alpha.13\n")

    def game_loader(self):
        self.copy(os.path.join(TESTS, "enshwick.dll"), "mods/enshwick/enshwick.dll")

    def pack(self, rel, mod_id):
        self.put(f"{rel}/pack.toml", f'id = "{mod_id}"\nname = "{mod_id}"\nversion = "1.0.0"\n')

    def luau(self, rel):
        self.put(f"{rel}/main.luau", "print('hello')\n")

    def dllmod(self, rel):
        self.copy(os.path.join(TESTS, "fakemod.dll"), f"{rel}/fakemod.dll")

    def profile(self, entries, dropins=False):
        self.put("kiln/profile.json", {"format": 1, "id": "testprofile", "generation": 1, "dropins": dropins,
                                       "mods": {"enshwick": entries}})

    def run(self, wait_ms=1500, *extra, envx=None):
        cmd = [WINE, self.p(self.exe), "kiln-enshwick.dll", str(wait_ms), *extra]
        r = subprocess.run(cmd, cwd=self.dir, env=env(envx), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)
        self.out = r.stdout.decode("utf-8", "replace")
        self.expect(r.returncode == 0, f"host exit status {r.returncode}: {self.out[-1500:]}")
        self.log = read(self.p("kiln", "logs", "kiln.log"))
        logs = sorted(glob.glob(self.p("logs", "enshwick_*.log")))
        self.ens = "".join(read(f) for f in logs)
        self.has("RESULT done")
        return self

    def state(self):
        try:
            return json.loads(read(self.p("kiln", "state", "enshwick.json")))
        except ValueError:
            return {}

    def expect(self, cond, what):
        if not cond:
            self.failures.append(what)

    def has(self, text):
        self.expect(text in self.log, f"kiln.log has {text!r}")

    def lacks(self, text):
        self.expect(text not in self.log, f"kiln.log lacks {text!r}")

    def ens_has(self, text):
        self.expect(text in self.ens, f"enshwick log has {text!r}")

    def status(self, want):
        got = self.state().get("status")
        self.expect(got == want, f"state status {want!r} (got {got!r}, reason {self.state().get('reason')!r})")


def read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


# ---- scenarios ----

def bundled_server():
    """Legacy layout: Kiln's bundled loader starts on the server, holds the entry until release, runs hand-installed mods."""
    g = Game("bundled_server")
    g.bundled()
    g.pack("mods/you.material-stacks", "you.material-stacks")
    g.luau("mods/you.welcome")
    g.dllmod("mods/you.dllmod")
    g.put("mods/flame-mod/flame.json", "{}")  # another engine's folder: not listed as Enshwick's
    g.run()
    g.status("started")
    g.has("loader ")
    g.has("(bundled, v1.0.0-alpha.13)")
    g.has("mods/you.material-stacks (pack, folder): run")
    g.has("mods/you.welcome (luau, folder): run")
    g.has("mods/you.dllmod (dll, folder): run")
    g.lacks("mods/flame-mod")
    g.has("server held")
    g.ens_has("stub_abi=2 missing=0")
    g.ens_has("release=1 the server's own code starts after the hooks")
    g.has("[enshwick:log]")  # the loader's key lines mirrored into kiln.log
    g.expect(g.state().get("release") == 1, "state release == 1")
    return g


def game_loader_preferred():
    """Both copies: the game's own mods/enshwick/enshwick.dll runs (what the player's mods were installed with)."""
    g = Game("game_loader_preferred")
    g.bundled()
    g.game_loader()
    g.pack("mods/you.a", "you.a")
    g.run()
    g.status("started")
    g.has("(game)")
    g.ens_has("mods\\enshwick\\enshwick.dll")
    g.ens_has("ready: 1 of 1 mods running")  # the loader's own folder is not a mod
    return g


def no_enshwick_mods():
    """A loader but no Enshwick mod: the loader does not start; "always": true starts it."""
    g = Game("no_enshwick_mods")
    g.bundled()
    g.put("mods/flame-mod/flame.json", "{}")
    g.run()
    g.status("nothing")
    g.has("no Enshwick mods; the loader does not start")
    g.expect(g.ens == "", "the loader wrote no log")
    g.put("kiln/config/kiln-enshwick.json", '{"always": true}')
    g.run()
    g.status("started")
    g.ens_has("stub_abi=2")  # started; the stand-in counts every folder, the real loader only Enshwick's
    return g


def bundled_by_config():
    """kiln-enshwick.json {"loader": "bundled"} picks Kiln's copy over the game's."""
    g = Game("bundled_by_config")
    g.bundled()
    g.game_loader()
    g.luau("mods/you.l")
    g.put("kiln/config/kiln-enshwick.json", '{"loader": "bundled"}')
    g.run()
    g.status("started")
    g.ens_has("kiln\\plugins\\enshwick\\enshwick.dll")
    return g


def profile_sync():
    """A profile's Enshwick mods are copied into mods/ with a marker; stale copies go; the player's folders stay."""
    g = Game("profile_sync")
    g.bundled()
    g.pack("kiln/library/enshwick/you.a", "you.a")
    g.dllmod("kiln/library/enshwick/you.dll")
    g.luau("kiln/library/enshwick/you.off")
    g.put("mods/you.stale/.kiln-managed", "source=Z:\\old\nfingerprint=0\n")
    g.pack("mods/you.stale", "you.stale")
    g.pack("mods/player.own", "player.own")  # installed by hand
    g.pack("mods/you.hand", "you.hand")       # installed by hand, also listed in the profile
    g.pack("kiln/library/enshwick/you.hand", "you.hand")
    g.profile([
        {"id": "you.a", "path": "library/enshwick/you.a"},
        {"id": "you.dll", "path": "library/enshwick/you.dll"},
        {"id": "you.off", "path": "library/enshwick/you.off", "enabled": False},
        {"id": "you.hand", "path": "library/enshwick/you.hand"},
        {"id": "../evil", "path": "library/enshwick/you.a"},
    ])
    g.run()
    g.status("started")
    g.expect(os.path.isfile(g.p("mods/you.a/pack.toml")), "mods/you.a copied")
    g.expect(os.path.isfile(g.p("mods/you.a/.kiln-managed")), "mods/you.a has the marker")
    g.expect(os.path.isfile(g.p("mods/you.dll/fakemod.dll")), "mods/you.dll copied")
    g.expect(not os.path.exists(g.p("mods/you.off")), "mods/you.off (disabled) not copied")
    g.expect(not os.path.exists(g.p("mods/you.stale")), "mods/you.stale removed")
    g.expect(os.path.isfile(g.p("mods/player.own/pack.toml")), "mods/player.own untouched")
    g.expect(not os.path.exists(g.p("mods/you.hand/.kiln-managed")), "mods/you.hand (player's) untouched")
    g.expect(not os.path.exists(g.p("evil")) and not glob.glob(g.p("mods/.*kiln-tmp")), "no escape, no temp left")
    g.has("mods/you.a (pack, profile): copy")
    g.has("mods/you.dll (dll, profile): copy")
    g.has("mods/you.off (unknown, profile): off")
    g.has("mods/you.stale (, kiln): remove")
    g.has("mods/you.hand (pack, profile): conflict")
    g.has("not a usable folder name")
    g.has("mods/player.own (pack, folder): run: installed by hand")
    g.ens_has("mod folder you.a initialised")
    g.ens_has("mod folder you.dll initialised")
    # second run: nothing to copy
    g.run()
    g.has("mods/you.a (pack, profile): keep")
    return g


def no_mods():
    """Play-once no-mods: the loader never starts and mods/ is left alone."""
    g = Game("no_mods")
    g.bundled()
    g.pack("mods/you.a", "you.a")
    g.put("kiln/no-mods", "")
    g.run()
    g.status("nothing")
    g.has("Enshwick's loader does not start")
    g.expect(g.ens == "", "the loader wrote no log")
    return g


def step_aside_stub_loaded():
    """Enshwick's own powrprof.dll is loaded: Kiln steps aside and starts nothing."""
    g = Game("step_aside_stub_loaded")
    g.bundled()
    g.copy(os.path.join(TESTS, "powrprof_stub.dll"), "powrprof.dll")
    g.run(1500, "--load", "powrprof.dll")
    g.status("stepped-aside")
    g.has("kiln-enshwick steps aside: Enshwick's own powrprof.dll is loaded")
    g.expect(g.ens == "", "the loader wrote no log")
    return g


def stub_present_not_loaded():
    """Enshwick's powrprof.dll is there but did not load (Wine without the override): Kiln runs Enshwick."""
    g = Game("stub_present_not_loaded")
    g.bundled()
    g.pack("mods/you.a", "you.a")
    g.copy(os.path.join(TESTS, "powrprof_stub.dll"), "powrprof.dll")
    g.run()
    g.status("started")
    g.has("powrprof.dll is Enshwick's stub but did not load")
    return g


def other_powrprof():
    """Another tool's powrprof.dll (SET, Shared XP...) is loaded: Kiln still runs Enshwick and says so."""
    g = Game("other_powrprof")
    g.bundled()
    g.pack("mods/you.a", "you.a")
    g.copy(os.path.join(TESTS, "powrprof_other.dll"), "powrprof.dll")
    g.run(1500, "--load", "powrprof.dll")
    g.status("started")
    g.has("powrprof.dll belongs to another tool")
    return g


def stub_claimed():
    """A stub already claimed the process (its mutex exists): Kiln steps aside."""
    g = Game("stub_claimed")
    g.bundled()
    g.copy(os.path.join(TESTS, "claim.dll"), "claim.dll")
    g.run(1500, "--load", "claim.dll")
    g.status("stepped-aside")
    g.has("already claimed this process")
    return g


def client_no_hold():
    """On the client the main thread does not wait; release answers 0."""
    g = Game("client_no_hold", server=False)
    g.bundled()
    g.pack("mods/you.a", "you.a")
    g.run()
    g.status("started")
    g.lacks("server held")
    g.ens_has("release=0")
    return g


def hold_expires():
    """A loader that takes longer than the hold: the server goes on after 10 s; release answers 2."""
    g = Game("hold_expires")
    g.bundled()
    g.pack("mods/you.a", "you.a")
    g.run(14000, envx={"ENSHWICK_FAKE": "slow"})
    g.has("did not arm within 10000 ms")
    g.ens_has("release=2")
    g.expect(g.state().get("release") == 2, "state release == 2")
    return g


def no_loader():
    """No loader anywhere: Kiln says so and runs on."""
    g = Game("no_loader")
    g.pack("mods/you.a", "you.a")
    g.run()
    g.status("no-loader")
    g.has("Enshwick mods stay off")
    return g


def broken_loader():
    """A file named enshwick.dll that is not a loader (no enshwick_start): refused before loading."""
    g = Game("broken_loader")
    g.copy(os.path.join(TESTS, "fakemod.dll"), "kiln/plugins/enshwick/enshwick.dll")
    g.pack("mods/you.a", "you.a")
    g.run()
    g.status("no-loader")
    return g


def real_loader():
    """Enshwick's real loader (built from source): starts under Kiln, opens its log, finds no game it knows, stops."""
    g = Game("real_loader")
    g.bundled(real=True)
    g.pack("mods/you.a", "you.a")
    g.run(8000)
    g.status("started")
    g.ens_has("Enshwick 1.0.0-alpha.13 starting on")
    g.has("Enshwick's start returned")
    g.has("[enshwick:log]")
    return g


SCENARIOS = [bundled_server, game_loader_preferred, no_enshwick_mods, bundled_by_config, profile_sync, no_mods, step_aside_stub_loaded,
             stub_present_not_loaded, other_powrprof, stub_claimed, client_no_hold, hold_expires, no_loader,
             broken_loader]


def main():
    for f in (HOST, PLUGIN, os.path.join(TESTS, "enshwick.dll")):
        if not os.path.isfile(f):
            print(f"missing {f}: build with KILN_OUT={OUT} native/build.sh kiln-enshwick compathost")
            return 2
    if not ensure_prefix():
        print("BLOCKED: Wine could not initialize")
        return 2
    scenarios = SCENARIOS + ([real_loader] if REAL else [])
    want = set(sys.argv[1:])
    failed = 0
    for s in scenarios:
        if want and s.__name__ not in want:
            continue
        g = s()
        if g.failures:
            failed += 1
            print(f"FAIL {s.__name__}")
            for f in g.failures:
                print(f"   - {f}")
            print("   kiln.log tail:\n     " + "\n     ".join(g.log.splitlines()[-25:]))
        else:
            print(f"ok   {s.__name__}")
    print(f"enshwick host tests: {'all passed' if not failed else f'{failed} failed'}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
