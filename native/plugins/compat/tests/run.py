#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Host tests of the compatibility plugins (kiln-greeneye, kiln-flame, kiln-shroudtopia) under plain Wine, no game.

    KILN_OUT=<build dir> native/build.sh compathost
    KILN_OUT=<build dir> python3 native/plugins/compat/tests/run.py [scenario...]

Each scenario builds a scratch game folder under $KILN_OUT/compat/run/<scenario>/ (compathost.exe as enshrouded.exe or
enshrouded_server.exe, the plugin, test mods in the engine's own layout or in a "library" folder named by a
kiln/profile.json), runs it once and checks kiln/logs/kiln.log, the host's RESULT lines and kiln/state/<engine>.json.
Wine prefix: $KILN_OUT/compat/pfx (created on first use). Exit code 0 = every scenario passed.
"""
import json
import os
import shutil
import subprocess
import sys

OUT = os.path.abspath(os.environ.get("KILN_OUT", os.path.join(os.path.dirname(__file__), "../../../build")))
BIN = os.path.join(OUT, "cmake", "bin")
CT = os.path.join(BIN, "compat-tests")
PLUGINS = os.path.join(BIN, "plugins")
WORK = os.path.join(OUT, "compat")
PFX = os.path.join(WORK, "pfx")
RUN = os.path.join(WORK, "run")
WINE = os.environ.get("COMPAT_WINE", "wine")


def winpath(p):
    return "Z:" + os.path.abspath(p).replace("/", "\\")


def env():
    e = dict(os.environ)
    e.update(WINEPREFIX=PFX, WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree=;mshtml=")
    e.pop("DISPLAY", None)
    e.pop("WAYLAND_DISPLAY", None)
    return e


def ensure_prefix():
    if os.path.isfile(os.path.join(PFX, "system.reg")):
        return
    os.makedirs(WORK, exist_ok=True)
    print(f"compat: creating Wine prefix {PFX} (once)")
    subprocess.run([WINE, "wineboot", "-i"], env=env(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=300)


class Game:
    def __init__(self, name, server=False):
        self.name = name
        self.dir = os.path.join(RUN, name)
        shutil.rmtree(self.dir, ignore_errors=True)
        os.makedirs(os.path.join(self.dir, "kiln"), exist_ok=True)
        self.exe = "enshrouded_server.exe" if server else "enshrouded.exe"
        shutil.copy(os.path.join(CT, "compathost.exe"), os.path.join(self.dir, self.exe))
        self.failures = []

    def p(self, *parts):
        return os.path.join(self.dir, *parts)

    def put(self, rel, text):
        path = self.p(rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text if isinstance(text, str) else json.dumps(text, indent=2))

    def dll(self, src, rel):
        path = self.p(rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        shutil.copy(os.path.join(CT, src), path)

    def plugin(self, name):
        shutil.copy(os.path.join(PLUGINS, name), self.p(name))
        return name

    def profile(self, mods, dropins=False):
        self.put("kiln/profile.json", {"format": 1, "id": "testprofile", "generation": 1, "dropins": dropins, "mods": mods})

    def run(self, plugin, wait_ms, *extra):
        cmd = [WINE, self.p(self.exe), plugin, str(wait_ms), *extra]
        r = subprocess.run(cmd, cwd=self.dir, env=env(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
        self.out = r.stdout.decode("utf-8", "replace")
        try:
            with open(self.p("kiln", "logs", "kiln.log"), encoding="utf-8", errors="replace") as f:
                self.log = f.read()
        except OSError:
            self.log = ""
        return self

    def state(self, engine):
        try:
            with open(self.p("kiln", "state", engine + ".json"), encoding="utf-8") as f:
                return json.load(f)
        except (OSError, ValueError):
            return {}

    def expect(self, cond, what):
        if not cond:
            self.failures.append(what)

    def has(self, text):
        self.expect(text in self.log, f"log has {text!r}")

    def lacks(self, text):
        self.expect(text not in self.log, f"log lacks {text!r}")

    def result(self, key):
        for line in self.log.splitlines():
            if f"RESULT {key}=" in line:
                return line.split(f"RESULT {key}=", 1)[1].split(" ")[0]
        return None

    def checks_pass(self, tag, minimum):
        oks = [l for l in self.log.splitlines() if f"[{tag}]" in l and "CHECK " in l and l.endswith(" ok")]
        fails = [l for l in self.log.splitlines() if f"[{tag}]" in l and "CHECK " in l and l.endswith(" fail")]
        self.expect(not fails, f"no failed checks for {tag}: {fails}")
        self.expect(len(oks) >= minimum, f"{tag}: at least {minimum} checks passed (got {len(oks)})")

    def mod_status(self, engine, mod_id, key="id"):
        for m in self.state(engine).get("mods", []):
            if m.get(key) == mod_id:
                return m.get("status")
        return None


def greeneye_mod(g, folder, mod_id, dll_src, side="both", api=2, enabled=True, dll_name=None):
    dll_name = dll_name or dll_src
    g.put(f"{folder}/mod.json", {"id": mod_id, "name": mod_id.upper(), "version": "1.0", "dll": dll_name, "side": side,
                                 "api": api, "enabled": enabled})
    g.dll(dll_src, f"{folder}/{dll_name}")


# ---------------------------------------------------------------------------------------------------- greeneye ------
def greeneye_legacy_client():
    g = Game("greeneye-legacy-client")
    greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
    greeneye_mod(g, "greeneye/gbad", "gbad", "gbad.dll")
    greeneye_mod(g, "greeneye/gserver", "gserver", "greeneye_test.dll", side="server")
    greeneye_mod(g, "greeneye/gnewer", "gnewer", "greeneye_test.dll", api=3)
    greeneye_mod(g, "greeneye/goff", "goff", "greeneye_test.dll", enabled=False)
    greeneye_mod(g, "greeneye/gnoexp", "gnoexp", "dummy.dll")
    g.run(g.plugin("kiln-greeneye.dll"), 300)
    g.checks_pass("greeneye:gtest", 18)
    g.has("[greeneye:gbad] CHECK short_hook_refused ok")
    g.has("GTEST 1.0 (folder): started (active generation 1")
    g.has("GBAD 1.0 (folder): a hook could not be installed")
    g.has("GSERVER 1.0: server only")
    g.has("GNEWER 1.0: needs a newer Greeneye Loader (API 3, this is 2)")
    g.has("GOFF 1.0: disabled in mod.json")
    g.has("no GreeneyeModStart in")
    g.has("side=1 hotReload=0")
    g.expect(g.result("gt_target") == str(5 + 1000), f"gt_target hooked (+1000): {g.result('gt_target')}")
    g.expect(g.result("gt_caller") == str(2 + 6 + 100), f"gt_caller redirected (+100): {g.result('gt_caller')}")
    g.expect(g.result("find_target") == "ok", "find gives the unique match")
    g.expect(g.result("find_wildcard") == "ok", "find with ?? wildcards")
    g.expect(g.result("find_ambiguous") == "null", "find refuses a pattern that is not unique")
    g.expect(g.result("read_original") == "ok", "read gives the bytes before the hook")
    g.expect("live_patched=yes" in g.log, "the live bytes are patched")
    g.expect(g.mod_status("greeneye", "gtest") == "started", "state: gtest started")
    g.expect(g.mod_status("greeneye", "gbad") == "failed", "state: gbad failed")
    g.expect(g.mod_status("greeneye", "gserver") == "skipped", "state: gserver skipped")
    st = g.state("greeneye")
    g.expect(any(m.get("id") == "gtest" and m.get("hooks") == 2 for m in st.get("mods", [])), "state: gtest owns 2 hook sites")
    g.expect(os.path.isfile(g.p("kiln", "logs", "greeneye-loader.log")), "logs/greeneye-loader.log written")
    return g


def greeneye_profile_server():
    g = Game("greeneye-profile-server", server=True)
    greeneye_mod(g, "library/gtest", "gtest", "greeneye_test.dll")
    greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")  # same id in the folder: the profile's copy wins
    greeneye_mod(g, "greeneye/gother", "gother", "greeneye_test.dll")  # not listed: the profile manages greeneye
    g.profile({"greeneye": [{"id": "gtest", "path": winpath(g.p("library", "gtest"))}]})
    g.run(g.plugin("kiln-greeneye.dll"), 300)
    g.checks_pass("greeneye:gtest", 18)
    g.has("GTEST 1.0 (profile): started")
    g.has("side=2 hotReload=0")
    g.lacks("GOTHER")
    g.expect(g.result("gt_target") == "1005", "gt_target hooked once")
    return g


def greeneye_hot_reload_server():
    g = Game("greeneye-hot-reload-server", server=True)
    greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
    g.put("greeneye/hotreload.enable", "")
    g.put("request", "")
    g.run(g.plugin("kiln-greeneye.dll"), 2500, "--at", "600", "--copy", "request", "greeneye\\gtest\\reload")
    g.checks_pass("greeneye:gtest", 35)  # both generations pass every check
    g.has("hot reload on (greeneye\\hotreload.enable)")
    g.has("[greeneye:gtest] generation=2")
    g.has("GTEST: active generation 2")
    with open(g.p("greeneye", "gtest", "hotload.status"), encoding="utf-8") as f:
        g.expect(f.read().startswith("active generation 2"), "hotload.status: active generation 2")
    g.expect(os.path.isdir(g.p("greeneye", "gtest", ".hot")), "shadow copies in .hot")
    g.expect(g.result("gt_target") == "1005", f"the swapped detour runs once: {g.result('gt_target')}")
    g.expect(g.result("gt_caller") == "108", f"redirected call swapped: {g.result('gt_caller')}")
    st = g.state("greeneye")
    g.expect(any(m.get("id") == "gtest" and m.get("generation") == 2 and m.get("hooks") == 2 for m in st.get("mods", [])),
             "state: generation 2, still 2 hook sites")
    return g


def greeneye_aside():
    g = Game("greeneye-aside")
    greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
    g.run(g.plugin("kiln-greeneye.dll"), 200, "--claim-greeneye")
    g.has("kiln-greeneye steps aside: Greeneye Loader runs in this process")
    g.lacks("[greeneye:gtest]")
    g.expect(g.result("gt_target") == "5", "nothing hooked")
    g.expect(g.state("greeneye").get("steppedAside") is True, "state: steppedAside")
    return g


def greeneye_nomods():
    g = Game("greeneye-nomods")
    greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
    g.put("kiln/no-mods", "")
    g.run(g.plugin("kiln-greeneye.dll"), 200)
    g.has("play-once no-mods: nothing loads")
    g.lacks("[greeneye:gtest]")
    g.expect(g.result("gt_target") == "5", "nothing hooked")
    return g


# ---------------------------------------------------------------------------------------------------- flame ---------
def flame_mod(g, folder, mod_id, dll_src, dll_name=None):
    dll_name = dll_name or dll_src
    g.put(f"{folder}/mod.json", {"id": mod_id, "name": mod_id.upper(), "version": "3.2.0"})
    g.put(f"{folder}/flame.json", {"dll": dll_name})
    g.dll(dll_src, f"{folder}/{dll_name}")


def flame_legacy():
    g = Game("flame-legacy")
    flame_mod(g, "mods/ftest", "ftest", "flame_test.dll")
    flame_mod(g, "mods/ffault", "ffault", "flame_fault.dll")
    flame_mod(g, "mods/fnoexp", "fnoexp", "dummy.dll")
    g.put("mods/fbad/mod.json", {"id": "fbad"})
    g.put("mods/fbad/flame.json", {"dll": "..\\outside.dll"})
    g.put("mods/emlmod/mod.json", {"id": "emlmod"})  # not a Flame mod (no flame.json)
    g.run(g.plugin("kiln-flame.dll"), 600)
    g.has("[host] service registered: kiln.flame")
    g.has("[flame:ftest] FTEST 3.2.0 (folder): FlameStartRuntime returned")
    g.has("[flame:ffault] FFAULT 3.2.0: FlameStartRuntime faulted (0xC0000005)")
    g.has("FNOEXP 3.2.0: no FlameStartRuntime in")
    g.has("must stay inside the mod folder")
    g.lacks("emlmod")
    g.has("kiln-flame: 1 of 4 Flame mod(s) started")
    g.expect(os.path.isfile(g.p("mods", "ftest", "started.txt")), "ftest wrote started.txt beside its DLL")
    g.has("RESULT flame_mod ftest status=1")
    g.has("RESULT flame_mod ffault status=2")
    g.expect(g.result("flame_running_ftest") == "yes", "kiln.flame running(ftest)")
    g.expect(g.result("flame_stepped_aside") == "no", "kiln.flame not stepped aside")
    g.expect(g.mod_status("flame", "ftest") == "started", "state: ftest started")
    return g


def flame_profile():
    g = Game("flame-profile", server=True)
    flame_mod(g, "library/ftest", "ftest", "flame_test.dll")
    flame_mod(g, "mods/fother", "fother", "flame_test.dll")  # not listed: the profile manages flame
    g.profile({"flame": [{"id": "ftest", "path": winpath(g.p("library", "ftest"))}]})
    g.run(g.plugin("kiln-flame.dll"), 600)
    g.has("FTEST 3.2.0 (profile): FlameStartRuntime returned")
    g.expect(os.path.isfile(g.p("library", "ftest", "started.txt")), "the mod found its own folder outside <game>/mods")
    g.expect(not os.path.exists(g.p("mods", "fother", "started.txt")), "fother not started")
    return g


def flame_aside():
    g = Game("flame-aside")
    flame_mod(g, "mods/ftest", "ftest", "flame_test.dll")
    g.dll("fakeflame.dll", "dinput8-flame.dll")
    g.run(g.plugin("kiln-flame.dll"), 400, "--load", "dinput8-flame.dll")
    g.has("kiln-flame steps aside: Flame's own loader")
    g.expect(not os.path.exists(g.p("mods", "ftest", "started.txt")), "kiln-flame started nothing")
    g.expect(g.result("flame_stepped_aside") == "yes", "kiln.flame stepped_aside")
    g.expect(g.result("flame_running_ftest") == "yes", "kiln.flame reports ftest as run by Flame's loader")
    return g


# ---------------------------------------------------------------------------------------------------- shroudtopia ---
TEST_MOD_CONFIG = {"active": True, "flag": True, "count": 5, "ratio": 1.5, "title": "kiln", "big": 12345678901,
                   "bools": [True, False], "ints": [1, 2, 3], "floats": [1.5, 2.5], "strings": ["a", "b"], "bigs": [1, 2]}


def shroudtopia_legacy_server():
    g = Game("shroudtopia-legacy-server", server=True)
    cfg = {"active": True, "bootDelay": 200, "updateDelay": 50, "enableLogging": True, "logLevel": "INFO",
           "mods": {"Kiln Test Mod": TEST_MOD_CONFIG, "Kiln Client Mod": {"active": True}}}
    g.put("shroudtopia.json", cfg)  # Shroudtopia's own file: the first start copies it into Kiln's config
    off = json.loads(json.dumps(cfg))
    off["mods"]["Kiln Test Mod"]["active"] = False
    g.put("off.json", off)
    g.dll("shroudtopia_test.dll", "mods/shroudtopia_test.dll")
    g.put("mods/stfolder/mod.json", {"id": "stfolder", "shroudtopia": {"default": {"speed": 3}}})
    g.dll("shroudtopia_client.dll", "mods/stfolder/stfolder.dll")
    flame_mod(g, "mods/ftest", "ftest", "flame_test.dll")  # another loader's folder: never loaded
    g.dll("dummy.dll", "mods/random.dll")                   # no CreateModInstance: never loaded
    g.run(g.plugin("kiln-shroudtopia.dll"), 2600, "--at", "1500", "--copy", "off.json",
          "kiln\\config\\shroudtopia\\shroudtopia.json")
    g.checks_pass("shroudtopia:Kiln Test Mod", 26)
    g.has("settings: took")
    g.has("Registered mod: Kiln Test Mod 2.0 by kiln (folder")
    g.has("Loading mod: Kiln Test Mod")
    g.has("Activating: Kiln Test Mod")
    g.has("[shroudtopia:Kiln Test Mod] third update")
    g.has("Deactivating: Kiln Test Mod")
    g.has("[shroudtopia:Kiln Test Mod] deactivated")
    g.has("Kiln Client Mod: no server support")
    g.has("first start of stfolder: wrote its defaults")
    g.expect(not os.path.exists(g.p("mods", "ftest", "started.txt")), "the Flame mod in mods/ was not loaded")
    g.expect(g.result("gt_target") == str(5 + 7), f"CreateDetour detour still runs: {g.result('gt_target')}")
    with open(g.p("kiln", "config", "shroudtopia", "shroudtopia.json"), encoding="utf-8") as f:
        kcfg = json.load(f)
    g.expect(kcfg.get("mods", {}).get("stfolder", {}).get("speed") == 3, "manifest defaults written to Kiln's settings")
    st = g.state("shroudtopia")
    refused = [r.get("dll", "") for r in st.get("refused", [])]
    g.expect(any(r.endswith("random.dll") for r in refused), "random.dll refused without loading")
    g.expect(g.mod_status("shroudtopia", "Kiln Test Mod", "name") == "inactive", "state: test mod inactive after switch-off")
    g.expect(os.path.isfile(g.p("kiln", "logs", "shroudtopia.log")), "logs/shroudtopia.log written")
    return g


def shroudtopia_profile_client():
    g = Game("shroudtopia-profile-client")
    g.put("kiln/config/shroudtopia/shroudtopia.json",
          {"active": True, "bootDelay": 0, "updateDelay": 50, "mods": {"Kiln Client Mod": TEST_MOD_CONFIG}})
    g.put("library/stc/mod.json", {"id": "stc"})
    g.dll("shroudtopia_client.dll", "library/stc/stc.dll")
    g.dll("shroudtopia_test.dll", "mods/shroudtopia_test.dll")  # not listed: the profile manages shroudtopia
    g.profile({"shroudtopia": [{"id": "stc", "path": winpath(g.p("library", "stc"))}]})
    g.run(g.plugin("kiln-shroudtopia.dll"), 1200)
    g.has("Registered mod: Kiln Client Mod 1.0 by kiln (profile")
    g.has("Activating: Kiln Client Mod")
    g.lacks("Kiln Test Mod")
    g.checks_pass("shroudtopia:Kiln Client Mod", 26)
    return g


def shroudtopia_aside():
    g = Game("shroudtopia-aside")
    g.dll("shroudtopia_test.dll", "mods/shroudtopia_test.dll")
    g.dll("dummy.dll", "shroudtopia.dll")
    g.run(g.plugin("kiln-shroudtopia.dll"), 600, "--load", "shroudtopia.dll")
    g.has("kiln-shroudtopia steps aside: Shroudtopia's loader is loaded")
    g.lacks("Registered mod")
    return g


def idle_without_mods():
    g = Game("idle-without-mods")
    g.put("mods/emlmod/mod.json", {"id": "emlmod"})  # someone else's mod folder
    g.run(g.plugin("kiln-shroudtopia.dll"), 300)
    g.has("kiln-shroudtopia 1.0.0: no Shroudtopia mods")
    g.expect(not os.path.exists(g.p("kiln", "config", "shroudtopia")), "no settings file written")
    g2 = Game("idle-without-mods-greeneye")
    g2.run(g2.plugin("kiln-greeneye.dll"), 200)
    g2.has("kiln-greeneye 1.0.0: no Greeneye mods")
    g2.expect(not os.path.exists(g2.p("kiln", "logs", "greeneye-loader.log")), "no greeneye-loader.log")
    g.failures += g2.failures
    return g


SCENARIOS = {f.__name__.replace("_", "-"): f for f in [
    greeneye_legacy_client, greeneye_profile_server, greeneye_hot_reload_server, greeneye_aside, greeneye_nomods,
    flame_legacy, flame_profile, flame_aside,
    shroudtopia_legacy_server, shroudtopia_profile_client, shroudtopia_aside, idle_without_mods]}


def main():
    names = sys.argv[1:] or list(SCENARIOS)
    ensure_prefix()
    failed = 0
    for n in names:
        g = SCENARIOS[n]()
        if g.failures:
            failed += 1
            print(f"FAIL {n}")
            for f in g.failures:
                print(f"     - {f}")
            print(f"     log: {g.p('kiln', 'logs', 'kiln.log')}")
        else:
            print(f"ok   {n}")
    print(f"{len(names) - failed}/{len(names)} scenarios passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
