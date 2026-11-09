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
    e.update(WINEPREFIX=PFX, WINEDEBUG="-all", WINEDLLOVERRIDES="mscoree=;mshtml=;winedbg.exe=d")
    # No window may reach the desktop (a crash dialog included): no X display, and a Wayland socket that does not
    # exist (unset, libwayland falls back to $XDG_RUNTIME_DIR/wayland-0, the desktop); winedbg is off.
    e.pop("DISPLAY", None)
    e["WAYLAND_DISPLAY"] = "kiln-no-display"
    return e


def ensure_prefix():
    os.makedirs(WORK, exist_ok=True)
    command = [WINE, "cmd", "/c", "exit", "0"]
    if not os.path.isfile(os.path.join(PFX, "system.reg")):
        print(f"compat: creating Wine prefix {PFX} (once)")
        command = [WINE, "wineboot", "-i"]
    result = subprocess.run(command, env=env(), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
    if result.returncode:
        print("BLOCKED: Wine could not initialize; no compatibility scenario ran.")
        print(result.stdout.decode("utf-8", "replace").strip())
        return False
    return True


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
        self.expect(r.returncode == 0, f"host exit status {r.returncode}: {self.out[-1500:]}")
        try:
            with open(self.p("kiln", "logs", "kiln.log"), encoding="utf-8", errors="replace") as f:
                self.log = f.read()
        except OSError:
            self.log = ""
        self.has("RESULT done")
        self.checks_pass("compat", 21)
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
    g.has("CHECK lease_inactive_during_prepare ok")
    g.has("CHECK prepare_can_call_own_detour ok")
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
    g.profile({"flame": [{"id": "ftest", "path": winpath(g.p("library", "ftest")) + "\\"}]})
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
    # the detour on the exe's function is a kiln.hooks v2 raw link; the one in the mod's own memory block is outside the
    # exe image, which kiln.hooks refuses, so it is a plain safetyhook detour
    g.expect(g.result("hooks_raw_links") == "1", f"CreateDetour on the exe: one kiln.hooks raw link: {g.result('hooks_raw_links')}")
    g.expect(g.result("hooks_outside_image") == "1", f"CreateDetour outside the exe: refused by kiln.hooks, safetyhook: {g.result('hooks_outside_image')}")
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



def invalid_profiles():
    combined = None
    for engine in ("flame", "greeneye", "shroudtopia"):
        for bad in ('{broken', '[]', '{"mods":{"' + engine + '":{}}}', '{"mods":{"' + engine + '":[{"path":4}]}}',
                    json.dumps({"dropins": True, "mods": {engine: [{"id": "ftest\0", "path": "../mods/ftest", "enabled": False}]}}),
                    json.dumps({"mods": {engine: [{"id": 5, "path": "../mods/ftest"}]}})):
            g = Game("invalid-profile-" + engine + "-" + str(len(bad)))
            if engine == "flame":
                flame_mod(g, "mods/ftest", "ftest", "flame_test.dll")
            elif engine == "greeneye":
                greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
            else:
                g.dll("shroudtopia_test.dll", "mods/test.dll")
            g.put("kiln/profile.json", bad)
            g.run(g.plugin("kiln-" + engine + ".dll"), 200)
            g.lacks("Registered mod")
            g.lacks("FlameStartRuntime returned")
            g.lacks("(folder): started")
            if combined is None:
                combined = g
            else:
                combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def path_boundaries():
    # Inside the mod folder nothing may lead out: a subfolder that is a link to an outside folder, a manifest with "..",
    # and a mod folder (in the engine's folder) that is itself a link. A plain mod beside them must still start, so the
    # scan provably ran. Not covered: a symlink to a *file*. Under Wine every Win32/NT query follows it (only symlinks
    # to directories are flagged as reparse points), so the plugins cannot see it; on Windows CheckedPath refuses it.
    combined = None
    for engine, export_dll in (("flame", "flame_test.dll"), ("greeneye", "greeneye_test.dll"), ("shroudtopia", "shroudtopia_test.dll")):
        g = Game("path-boundaries-" + engine)
        base = "greeneye" if engine == "greeneye" else "mods"
        g.dll(export_dll, "outside.dll")
        g.dll(export_dll, "outside/" + export_dll)
        if engine == "flame":
            flame_mod(g, base + "/good", "good", export_dll)
            flame_mod(g, base + "/link", "link", export_dll)
            os.unlink(g.p(base, "link", export_dll))
            g.put(base + "/link/flame.json", {"dll": "sub\\" + export_dll})
            g.put(base + "/escape/mod.json", {"id": "escape"})
            g.put(base + "/escape/flame.json", {"dll": "..\\..\\outside.dll"})
            flame_mod(g, "library/external", "external", export_dll)
        elif engine == "greeneye":
            greeneye_mod(g, base + "/good", "good", export_dll)
            g.put(base + "/link/mod.json", {"id": "link", "side": "both", "api": 2, "dll": "sub\\" + export_dll})
            g.put(base + "/escape/mod.json", {"id": "escape", "side": "both", "dll": "..\\..\\outside.dll"})
            greeneye_mod(g, "library/external", "external", export_dll)
        else:
            g.put("kiln/config/shroudtopia/shroudtopia.json", {"active": True, "bootDelay": 0, "updateDelay": 50})
            g.put(base + "/good/mod.json", {"id": "good"})
            g.dll(export_dll, base + "/good/good.dll")
            g.put(base + "/escape/mod.json", {"id": "../../outside"})
            g.put("library/external/mod.json", {"id": "external"})
            g.dll("shroudtopia_client.dll", "library/external/external.dll")
        if engine != "shroudtopia":
            os.symlink(g.p("outside"), g.p(base, "link", "sub"))
        os.symlink(g.p("library/external"), g.p(base, "external"))
        g.run(g.plugin("kiln-" + engine + ".dll"), 900)
        if engine == "shroudtopia":
            g.has("Registered mod: Kiln Test Mod")
            g.lacks("Kiln Client Mod")  # the DLL behind the linked mod folder
        else:
            g.expect(g.mod_status(engine, "good") == "started", "the plain mod beside them started")
            for mod in ("link", "escape", "external"):
                g.expect(g.mod_status(engine, mod) != "started", mod + " was refused")
        g.expect(not os.path.exists(g.p("started.txt")), "outside DLL was not entered")
        g.expect(not os.path.exists(g.p("outside/started.txt")), "DLL behind a linked subfolder was not entered")
        g.expect(not os.path.exists(g.p("library/external/started.txt")), "external folder DLL was not entered")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def flame_dllmain_fault():
    g = Game("flame-dllmain-fault")
    flame_mod(g, "mods/afault", "afault", "flame_dllmain_fault.dll")
    flame_mod(g, "mods/ftest", "ftest", "flame_test.dll")
    g.run(g.plugin("kiln-flame.dll"), 700)
    g.expect(g.mod_status("flame", "afault") == "failed", "DllMain fault recorded")
    g.expect(g.mod_status("flame", "ftest") == "started", "next mod starts after DllMain fault")
    return g


def flame_fault_running():
    g = Game("flame-fault-running")
    flame_mod(g, "mods/ftest", "ftest", "flame_fault.dll")
    g.run(g.plugin("kiln-flame.dll"), 500)
    g.expect(g.mod_status("flame", "ftest") == "failed", "faulted start recorded")
    g.expect(g.result("flame_running_ftest") == "yes", "faulted runtime may retain threads/hooks; competitors keep yielding")
    return g


def flame_lifetime():
    g = Game("flame-lifetime")
    flame_mod(g, "mods/aslow", "aslow", "flame_slow.dll")
    flame_mod(g, "mods/ftest", "ftest", "flame_test.dll")
    g.run(g.plugin("kiln-flame.dll"), 900, "--free-at", "80")
    g.expect(g.result("plugin_pinned") == "yes", "Flame worker's plugin remains mapped after FreeLibrary")
    g.expect(os.path.exists(g.p("mods/aslow/started.txt")), "in-flight Flame call returned safely")
    g.expect(not os.path.exists(g.p("mods/ftest/started.txt")), "queued Flame mod stopped on unload")
    return g


def flame_late_loader():
    g = Game("flame-late-loader")
    flame_mod(g, "mods/aloader", "aloader", "flame_load_loader.dll")
    flame_mod(g, "mods/ftest", "ftest", "flame_test.dll")
    g.dll("fakeflame.dll", "dinput8-flame.dll")
    g.run(g.plugin("kiln-flame.dll"), 700)
    g.expect(g.result("flame_stepped_aside") == "yes", "late Flame loader detected before next dispatch")
    g.expect(g.result("flame_running_ftest") == "yes", "late handoff reports the folder mod as run by Flame")
    g.expect(not os.path.exists(g.p("mods/ftest/started.txt")), "next mod is left to Flame")
    return g


def flame_dependency_loader():
    g = Game("flame-dependency-loader")
    flame_mod(g, "mods/ftest", "ftest", "flame_dependency.dll")
    g.dll("fakeflame.dll", "fakeflame.dll")
    g.run(g.plugin("kiln-flame.dll"), 700)
    g.expect(g.result("flame_stepped_aside") == "yes", "loader brought in by mod dependency detected")
    g.expect(not os.path.exists(g.p("mods/ftest/started.txt")), "runtime left to the real loader after dependency load")
    return g


def greeneye_hot_factory_faults():
    combined = None
    for marker in ("hot-fault", "hot-invalid"):
        g = Game("greeneye-" + marker)
        greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
        g.put("greeneye/gtest/" + marker, "")
        g.run(g.plugin("kiln-greeneye.dll"), 400)
        g.expect(g.mod_status("greeneye", "gtest") == "failed", "bad hot factory generation rejected")
        g.expect(g.result("gt_target") == "5", "failed hot factory did not publish hooks")
        g.has("CHECK retained_original_callable ok")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def shroudtopia_late_loader():
    g = Game("shroudtopia-late-loader")
    g.dll("shroudtopia_test.dll", "mods/test.dll")
    g.dll("dummy.dll", "shroudtopia.dll")
    g.put("kiln/config/shroudtopia/shroudtopia.json", {"active": True, "bootDelay": 500, "updateDelay": 50})
    g.run(g.plugin("kiln-shroudtopia.dll"), 900, "--load-at", "100", "shroudtopia.dll")
    g.expect(g.result("late_loader") == "ok", "late real loader stand-in loaded")
    g.lacks("Registered mod")
    g.expect(g.state("shroudtopia").get("steppedAside") is True, "Shroudtopia yields during boot delay")
    return g


def shroudtopia_lifetime():
    g = Game("shroudtopia-lifetime", server=True)
    g.dll("shroudtopia_test.dll", "mods/test.dll")
    g.put("kiln/config/shroudtopia/shroudtopia.json", {"active": True, "bootDelay": 0, "updateDelay": 50,
                                                  "mods": {"Kiln Test Mod": TEST_MOD_CONFIG}})
    g.run(g.plugin("kiln-shroudtopia.dll"), 1000, "--free-at", "400")
    g.expect(g.result("plugin_pinned") == "yes", "Shroudtopia callbacks remain mapped after FreeLibrary")
    g.expect(g.result("gt_target") == "12", "mod detour/trampoline remains executable after unload")
    g.expect(g.result("patch_restored") == "yes", "owned byte patches restored after worker stop")
    return g


def shroudtopia_renamed_loader():
    g = Game("shroudtopia-renamed-loader")
    g.dll("shroudtopia_test.dll", "mods/test.dll")
    # Fake proxy marker: Shroudtopia's winmm can have any filename when chained.
    g.dll("fakeshroud.dll", "kiln/chain/renamed-winmm.dll")
    g.run(g.plugin("kiln-shroudtopia.dll"), 400, "--load", "kiln\\chain\\renamed-winmm.dll")
    g.lacks("Registered mod")
    g.expect(g.state("shroudtopia").get("steppedAside") is True, "renamed chained Shroudtopia proxy detected")
    return g


def shroudtopia_config_replacements():
    combined = None
    for invalid in (False, True):
        g = Game("shroudtopia-config-" + ("invalid" if invalid else "same-time"), server=True)
        cfg = {"active": True, "bootDelay": 0, "updateDelay": 50, "mods": {"Kiln Test Mod": TEST_MOD_CONFIG}}
        g.put("kiln/config/shroudtopia/shroudtopia.json", cfg)
        g.dll("shroudtopia_test.dll", "mods/test.dll")
        if invalid:
            g.put("replacement.json", '[]')
        else:
            off = json.loads(json.dumps(cfg))
            off["mods"]["Kiln Test Mod"]["active"] = False
            g.put("replacement.json", off)
        stamp = os.stat(g.p("kiln/config/shroudtopia/shroudtopia.json")).st_mtime_ns
        os.utime(g.p("replacement.json"), ns=(stamp, stamp))
        g.run(g.plugin("kiln-shroudtopia.dll"), 1200, "--at", "500", "--copy", "replacement.json",
              "kiln\\config\\shroudtopia\\shroudtopia.json")
        if invalid:
            g.expect(g.mod_status("shroudtopia", "Kiln Test Mod", "name") == "active", "invalid replacement retains last valid config")
        else:
            g.has("Deactivating: Kiln Test Mod")
            g.expect(g.mod_status("shroudtopia", "Kiln Test Mod", "name") == "inactive", "same-mtime replacement reloads")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def shroudtopia_faults():
    combined = None
    for stage in ("factory-fault", "metadata-fault", "load-fault", "load-throw", "activate-fault", "update-fault", "deactivate-fault"):
        g = Game("shroudtopia-" + stage, server=True)
        cfg = {"active": True, "bootDelay": 0, "updateDelay": 50, "mods": {"Kiln Test Mod": TEST_MOD_CONFIG}}
        g.put("kiln/config/shroudtopia/shroudtopia.json", cfg)
        g.dll("shroudtopia_test.dll", "mods/" + stage + ".dll")
        cfg = json.loads(json.dumps(cfg))
        cfg["mods"]["Kiln Test Mod"]["active"] = False
        g.put("off.json", cfg)
        g.run(g.plugin("kiln-shroudtopia.dll"), 900, "--at", "400", "--copy", "off.json",
              "kiln\\config\\shroudtopia\\shroudtopia.json")
        if stage.startswith(("factory", "metadata")):
            g.expect(bool(g.state("shroudtopia").get("refused")), "factory/metadata fault refused")
        else:
            g.expect(g.mod_status("shroudtopia", "Kiln Test Mod", "name") == "faulted", stage + " recorded")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def profile_id_precedence():
    combined = None
    for engine in ("flame", "greeneye", "shroudtopia"):
        g = Game("profile-id-" + engine)
        if engine == "flame":
            flame_mod(g, "library/listed", "manifest-other-id", "flame_test.dll")
            flame_mod(g, "mods/blocked", "blocked", "flame_test.dll")
        elif engine == "greeneye":
            greeneye_mod(g, "library/listed", "manifest-other-id", "greeneye_test.dll")
            greeneye_mod(g, "greeneye/blocked", "blocked", "greeneye_test.dll")
        else:
            g.put("library/listed/mod.json", {"id": "manifest-other-id"})
            g.dll("shroudtopia_test.dll", "library/listed/manifest-other-id.dll")
            g.put("mods/blocked/mod.json", {"id": "blocked"})
            g.dll("shroudtopia_test.dll", "mods/blocked/blocked.dll")
        g.profile({engine: [{"id": "blocked", "path": winpath(g.p("library/listed")), "enabled": False}]}, dropins=True)
        g.run(g.plugin("kiln-" + engine + ".dll"), 500)
        g.lacks("Registered mod")
        g.lacks("FlameStartRuntime returned")
        g.lacks("(folder): started")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def greeneye_chaining():
    combined = None
    for prehook in (False, True):
        g = Game("greeneye-chaining-" + str(prehook))
        greeneye_mod(g, "greeneye/a", "a", "greeneye_test.dll")
        greeneye_mod(g, "greeneye/b", "b", "greeneye_test.dll")
        g.run(g.plugin("kiln-greeneye.dll"), 700, *(('--prehook',) if prehook else ()))
        g.checks_pass("greeneye:a", 25)
        g.checks_pass("greeneye:b", 25)
        g.expect(g.result("gt_target") == str(2005 + (7 if prehook else 0)), "both Greeneye hooks and host safetyhook chain")
        g.expect(g.result("gt_caller") == "208", "both call redirects chain")
        if prehook:
            g.expect(g.result("prehook") == "ok", "host safetyhook installed")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def greeneye_lifetime():
    g = Game("greeneye-lifetime", server=True)
    greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
    g.put("greeneye/hotreload.enable", "")
    g.run(g.plugin("kiln-greeneye.dll"), 900, "--free-at", "400")
    g.expect(g.result("plugin_pinned") == "yes", "Greeneye host/trampoline remains mapped after FreeLibrary")
    g.expect(g.result("gt_target") == "1005", "Greeneye hook remains callable after plugin unload")
    return g


def ancestor_symlinks():
    # A profile entry reached through a symlinked ancestor (a Steam library or Kiln's library behind a link, as Proton's
    # Z: drive itself is) is the player's layout: the mod loads. Containment starts at the mod folder.
    combined = None
    for engine, dll in (("flame", "flame_test.dll"), ("greeneye", "greeneye_test.dll"), ("shroudtopia", "shroudtopia_test.dll")):
        g = Game("ancestor-symlink-" + engine)
        if engine == "flame":
            flame_mod(g, "external/ftest", "ftest", dll)
        elif engine == "greeneye":
            greeneye_mod(g, "external/ftest", "ftest", dll)
        else:
            g.put("kiln/config/shroudtopia/shroudtopia.json", {"active": True, "bootDelay": 0, "updateDelay": 50})
            g.put("external/ftest/mod.json", {"id": "ftest"})
            g.dll(dll, "external/ftest/ftest.dll")
        os.symlink(g.p("external"), g.p("library-link"))
        g.profile({engine: [{"id": "ftest", "path": winpath(g.p("library-link/ftest")) + "\\"}]})
        g.run(g.plugin("kiln-" + engine + ".dll"), 900)
        if engine == "flame":
            g.has("FTEST 3.2.0 (profile): FlameStartRuntime returned")
            g.expect(os.path.isfile(g.p("external/ftest/started.txt")), "the mod ran from behind the linked ancestor")
        elif engine == "greeneye":
            g.expect(g.mod_status("greeneye", "ftest") == "started", "state: ftest started")
        else:
            g.has("Registered mod: Kiln Test Mod 2.0 by kiln (profile")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def greeneye_hot_callback_faults():
    combined = None
    for marker in ("prepare-fault", "ready-fault"):
        g = Game("greeneye-" + marker, server=True)
        greeneye_mod(g, "greeneye/gtest", "gtest", "greeneye_test.dll")
        g.put("greeneye/hotreload.enable", "")
        g.put("greeneye/gtest/" + marker, "")
        g.put("request-reload", "")
        g.run(g.plugin("kiln-greeneye.dll"), 1800, "--at", "500", "--copy", "request-reload", "greeneye\\gtest\\reload")
        g.expect(g.result("gt_target") == "1005", "faulted prepare/ready retains old active hook")
        g.expect(any(m.get("id") == "gtest" and m.get("generation") == 1 for m in g.state("greeneye").get("mods", [])),
                 "faulted callback leaves generation 1 active")
        g.has("stays active")
        if combined is None:
            combined = g
        else:
            combined.failures += [g.name + ": " + f for f in g.failures]
    return combined


def greeneye_manifest_bounds():
    g = Game("greeneye-manifest-bounds", server=True)
    greeneye_mod(g, "greeneye/overflow", "overflow", "greeneye_test.dll", api=4294967298)
    greeneye_mod(g, "greeneye/huge", "huge", "greeneye_test.dll", api=10 ** 50)
    greeneye_mod(g, "greeneye/long", "long", "greeneye_test.dll", dll_name="x" * 130 + ".dll")
    g.put("greeneye/hotreload.enable", "")
    g.run(g.plugin("kiln-greeneye.dll"), 700)
    g.expect(g.mod_status("greeneye", "overflow") == "rejected", "32-bit API overflow refused")
    g.expect(g.mod_status("greeneye", "huge") == "rejected", "integer parser overflow refused")
    g.expect(g.mod_status("greeneye", "long") == "started", "long shadow basename did not abort CRT")
    return g


def greeneye_default_dll_profile():
    g = Game("greeneye-default-dll-profile")
    g.put("library/original/mod.json", {"id": "original", "side": "both"})
    g.dll("greeneye_test.dll", "library/original/original.dll")
    g.profile({"greeneye": [{"id": "profile-id", "path": winpath(g.p("library/original"))}]})
    g.run(g.plugin("kiln-greeneye.dll"), 700)
    g.expect(g.mod_status("greeneye", "profile-id") == "started", "profile identity preserves manifest default DLL filename")
    return g


def shroudtopia_loader_named_mod():
    g = Game("shroudtopia-loader-named-mod", server=True)
    g.dll("shroudtopia_test.dll", "mods/shroudtopia.dll")
    g.put("kiln/config/shroudtopia/shroudtopia.json", {"active": True, "bootDelay": 0, "updateDelay": 50,
                                                  "mods": {"Kiln Test Mod": TEST_MOD_CONFIG}})
    g.run(g.plugin("kiln-shroudtopia.dll"), 700)
    g.has("Registered mod: Kiln Test Mod")
    g.expect(g.state("shroudtopia").get("steppedAside") is False, "CreateModInstance mod named shroudtopia.dll is not a loader")
    return g

SCENARIOS = {f.__name__.replace("_", "-"): f for f in [
    greeneye_legacy_client, greeneye_profile_server, greeneye_hot_reload_server, greeneye_aside, greeneye_nomods,
    flame_legacy, flame_profile, flame_aside,
    shroudtopia_legacy_server, shroudtopia_profile_client, shroudtopia_aside, idle_without_mods,
    invalid_profiles, path_boundaries, flame_dllmain_fault, flame_fault_running, flame_lifetime, flame_late_loader, flame_dependency_loader,
    greeneye_hot_factory_faults, shroudtopia_late_loader, shroudtopia_lifetime, shroudtopia_renamed_loader,
    shroudtopia_config_replacements, shroudtopia_faults, profile_id_precedence,
    greeneye_chaining, greeneye_lifetime, ancestor_symlinks, greeneye_hot_callback_faults,
    greeneye_manifest_bounds, shroudtopia_loader_named_mod, greeneye_default_dll_profile]}


def main():
    names = sys.argv[1:] or list(SCENARIOS)
    if not ensure_prefix():
        return 2
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
