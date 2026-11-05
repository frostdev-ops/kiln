#!/usr/bin/env python3
"""KAP/1 mock server and client for the Kiln Launcher.

Protocol: docs/kiln/launcher/admin-protocol.md. The mock answers every Kiln.Admin op from canned results with the
shapes of a real Kiln.Admin's (the fixture below), plus a small
simulated world (two players walking about, time of day, zones, permissions, configs, content edits, backups), and
pushes `state` every second. The browser mock (launcher/src/admin/mock/) uses the same fixture file.

Fixtures: the committed launcher/src/admin/mock/fixtures/synthetic.json has the real result shapes with invented game
content (items "Test Sword T2", creatures "Creature A", content fields); a real capture (`capture`) holds game names
and stays local: launcher/assets-local/fixtures/kiln-admin.capture.json (gitignored), used instead when present.

    mock-admin.py serve [--port 0] [--dir DIR] [--token HEX | --token-file FILE] [--fixture-hello] [--players N]
        Listens on 127.0.0.1. With --dir, writes DIR/kiln/data/kiln-admin/{endpoint.json,admin-token} like Kiln.Admin
        (so kiln_manage::admin::discover finds it as a server folder). The default token is the test vector's
        (schema/fixtures/kap/handshake.json); --fixture-hello also sends its hello frame byte for byte (fixed nonce).
    mock-admin.py call (--server DIR | --endpoint HOST:PORT --token-file FILE) OP [JSON-ARGS]
        One request against any KAP/1 server (the mock or a real Kiln.Admin); prints the res frame.
    mock-admin.py watch (--server DIR | --endpoint ...) [--topic state] [--count 3]
        Subscribes and prints pushes.
    mock-admin.py capture --server DIR [--out FILE]
        Calls the read-only ops of a real Kiln.Admin and writes a local capture (game names; gitignored path).
    mock-admin.py synthesize [--from CAPTURE] [--out FILE]
        Writes the committed synthetic fixture: Kiln's own results (ops, mods, configs, commands, permissions, network)
        from a capture (or the current synthetic file), everything game-derived generated.
    mock-admin.py selftest
        Starts the mock on a free port, checks the handshake test vector, a wrong mac, ops, unknown ops, a push, ping.
"""
from __future__ import annotations

import argparse
import asyncio
import copy
import hashlib
import hmac
import json
import math
import os
import random
import re
import secrets
import socket
import struct
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
VECTOR = REPO / "schema/fixtures/kap/handshake.json"
SYNTHETIC = REPO / "launcher/src/admin/mock/fixtures/synthetic.json"
CAPTURED = REPO / "launcher/assets-local/fixtures/kiln-admin.capture.json"
PROTO = "kap/1"
MAX_CLIENT_FRAME = 1 << 20
MAX_SERVER_FRAME = 16 << 20
MAX_CONNECTIONS = 4
MAX_INFLIGHT = 64
PING_S, IDLE_S, HANDSHAKE_S = 15, 45, 5
SLOW = {"mod_test": 1.5, "backup_create": 0.8, "save_now": 0.5, "mod_reload": 0.6, "mod_unload": 0.4, "mod_load": 0.6,
        "teleport": 0.3, "teleport_to_player": 0.3, "give_item": 0.2, "spawn": 0.4, "kill": 0.2, "damage": 0.1}


def mac(token: bytes, label: str, nonce: str, cnonce: str) -> str:
    return hmac.new(token, f"{label}\n{nonce}\n{cnonce}".encode(), hashlib.sha256).hexdigest()


def frame(obj) -> bytes:
    data = json.dumps(obj, separators=(",", ":"), ensure_ascii=False).encode()
    return struct.pack("<I", len(data)) + data


class AdminError(Exception):
    pass


# ---------------------------------------------------------------------------------------------- the simulated server

ACTIONS = ["build", "break", "terraform", "interact", "loot", "craft", "spawn"]
NODE = re.compile(r"^(\*|[a-z0-9_:-]+(\.[a-z0-9_:-]+)*(\.\*)?)$")


class World:
    """Canned Kiln.Admin: the captured results plus a little state that ops change."""

    def __init__(self, fixture: dict, players: int = 2):
        self.fx = fixture
        r = fixture.get("results", {})
        self.r = r
        self.t0 = time.time()
        self.step = 0
        self.ops = list(r.get("status", {}).get("ops") or fixture.get("ops") or [])
        self.server = fixture.get("server") or {"id": "srv-mock", "name": "Mock server"}
        self.kiln = r.get("status", {}).get("kiln", "0.2.0-dev")
        seed = fixture.get("seed", {})
        self.players = [dict(p, home=tuple(p["home"])) for p in copy.deepcopy(seed.get("players") or [])][:players]
        self.time = dict(r.get("time_get") or {"hours": 9.5, "clock": "09:30", "day": True, "dayMinutes": 30, "nightMinutes": 12, "weather": True})
        self.settings = dict((r.get("settings_get") or {}).get("values") or {})
        self.zones = copy.deepcopy(r.get("zones_list") or seed.get("zones") or [])
        self.perm = copy.deepcopy(r.get("permissions_get") or {"defaultGroup": "default", "groups": {"default": {"nodes": []}}, "players": {}, "userGroups": {}, "nodes": []})
        self.configs = copy.deepcopy(r.get("config_list") or [])
        self.mods = copy.deepcopy(r.get("mods") or [])
        self.backups = copy.deepcopy(r.get("backup_list") or seed.get("backups") or [])
        self.save = dict(r.get("save_status") or {})
        self.content = copy.deepcopy(fixture.get("content", {}))
        self.changes: dict[tuple, str] = {}
        self.spawned: list[int] = []
        self.next_entity = 90000
        self.combat_deaths: list[dict] = copy.deepcopy(seed.get("combatDeaths") or [])
        self.log: list[str] = []
        self.history: list[str] = []

    # -------------------------------------------------------------------------------- state
    def tick(self):
        self.step += 25
        t = time.time() - self.t0
        for i, p in enumerate(self.players):
            hx, hy, hz = p["home"]
            a = t / (40 + 15 * i) + i
            p["pos"] = {"x": round(hx + 60 * math.cos(a), 2), "y": round(hy + 3 * math.sin(a * 3), 2), "z": round(hz + 45 * math.sin(a), 2)}
        h = (self.time["hours"] + 24 / (60 * (self.time.get("dayMinutes", 30) + self.time.get("nightMinutes", 12)))) % 24
        self.set_hours(h)

    def set_hours(self, h: float):
        self.time["hours"] = round(h, 4)
        self.time["clock"] = f"{int(h):02d}:{int((h % 1) * 60):02d}"
        self.time["day"] = 6 <= h < 20

    def player_list(self):
        return [{k: v for k, v in p.items() if k != "home"} for p in self.players]

    def state(self):
        st = {"clock": round(self.step / 24.5, 2), "kiln": self.kiln, "ready": True, "players": self.player_list(),
              "deaths": [], "spawned": len(self.spawned)}
        if self.combat_deaths:
            st["combatDeaths"] = self.combat_deaths[-50:]
        loaded = sum(1 for m in self.mods if m.get("state") == "Loaded")
        st["mods"] = {"loaded": loaded, "failed": sum(1 for m in self.mods if m.get("state") == "Failed"),
                      "disabled": sum(1 for m in self.mods if m.get("state") == "Disabled"), "phase": "Running"}
        return st

    # -------------------------------------------------------------------------------- helpers
    def player(self, req, key="entity"):
        e = req.get(key)
        if not isinstance(e, (int, float)) or e < 1 or e != int(e):
            raise AdminError(f"{key} must be an entity id")
        p = next((p for p in self.players if p["entity"] == e), None)
        if not p:
            raise AdminError("That player is no longer in the world.")
        return p

    @staticmethod
    def num(req, key):
        v = req.get(key)
        if isinstance(v, bool) or not isinstance(v, (int, float)) or not math.isfinite(v):
            raise AdminError(f"{key} must be a number")
        return float(v)

    def say(self, line: str):
        self.log.append(f"[I {time.strftime('%H:%M:%S')}.000] [mod:kiln-admin] {line}")

    # -------------------------------------------------------------------------------- ops
    def run(self, op: str, req: dict):
        h = getattr(self, "op_" + op, None)
        if h:
            return h(req)
        if op in self.r:  # read-only ops: the captured answer
            return copy.deepcopy(self.r[op])
        raise AdminError(f"the mock has no answer for '{op}'")

    def op_ping(self, _):
        return {"pong": True, "clock": self.step / 24.5, "kiln": self.kiln}

    def op_status(self, _):
        return {"kiln": self.kiln, "features": (self.r.get("status") or {}).get("features", {}), "ops": self.ops}

    def op_admin_info(self, _):
        info = copy.deepcopy(self.r.get("admin_info") or {})
        info.update(ops=self.ops, uptimeS=round(time.time() - self.t0, 1), server=self.server)
        return info

    def op_players(self, _):
        return self.player_list()

    def op_teleport(self, req):
        p = self.player(req)
        to = {k: self.num(req, k) for k in "xyz"}
        hx, hy, hz = to["x"], to["y"], to["z"]
        p["home"] = (hx, hy, hz)
        return {"entity": p["entity"], "to": to, "method": "kiln-teleport", "position": to, "distance": 0.5, "steps": 3, "flags": "Moved"}

    def op_teleport_to_player(self, req):
        p, o = self.player(req), self.player(req, "target")
        p["home"] = (o["pos"]["x"] + 2, o["pos"]["y"] + 1, o["pos"]["z"])
        return {"entity": p["entity"], "to": o["pos"], "method": "kiln-teleport", "position": o["pos"], "distance": 2.0, "steps": 2, "flags": "Moved"}

    def op_enemies(self, req):
        out = copy.deepcopy(self.r.get("enemies") or {"near": [], "catalog": [], "total": 0})
        c = req.get("near")
        if isinstance(c, dict):
            cat = self.fx.get("creatures") or []
            rnd = random.Random(int(c.get("x", 0)) ^ int(c.get("z", 0)))
            for i in range(min(6, len(cat))):
                g = cat[rnd.randrange(len(cat))]
                out["near"].append({"entity": 70000 + i, "pos": {"x": c["x"] + rnd.uniform(-60, 60), "y": c.get("y", 40), "z": c["z"] + rnd.uniform(-60, 60)},
                                    "guid": g["guid"], "name": g["template"]})
            out["total"] = max(out.get("total", 0), len(out["near"]))
        return out

    def items(self):
        return self.fx.get("items") or []

    def op_item_search(self, req):
        q = req.get("q")
        if not isinstance(q, str):
            raise AdminError("q required")
        ql = q.lower()
        hits = [i for i in self.items() if ql in i["name"].lower()]
        hits.sort(key=lambda i: (not i["name"].lower().startswith(ql), len(i["name"])))
        return hits[:50]

    def op_inventory(self, req):
        p = self.player(req)
        items = self.items()[:12]
        slots = [{"item": it["id"], "count": 1 if it.get("gear") else 5 + 7 * n, "name": it["name"]} for n, it in enumerate(items)]
        return {"inventories": [{"link": 1, "entity": p["entity"] + 1, "category": "Backpack", "slots": slots[:8]},
                                {"link": 2, "entity": p["entity"] + 2, "category": "Equipment", "slots": slots[8:]}],
                "generic": 40, "available": 32}

    def op_give_item(self, req):
        self.player(req)
        item = int(self.num(req, "item"))
        count = int(self.num(req, "count"))
        if not 1 <= count <= 100000:
            raise AdminError("count must be 1 to 100000")
        if not any(i["id"] == item for i in self.items()):
            raise AdminError(f"unknown item {item}")
        return {"given": count, "requested": count, "stacks": max(1, count // 500), "status": "Done"}

    def op_spawn(self, req):
        g = req.get("guid")
        if not isinstance(g, str) or not re.fullmatch(r"[0-9a-fA-F-]{36}", g):
            raise AdminError("guid must be a template GUID")
        count = int(self.num(req, "count")) if "count" in req else 1
        if not 1 <= count <= 20:
            raise AdminError("count must be 1 to 20")
        for k in "xyz":
            self.num(req, k)
        for _ in range(count):
            self.next_entity += 1
            self.spawned.append(self.next_entity)
        return {"spawned": count, "failed": 0, "reason": None}

    def op_despawn_spawned(self, _):
        n = len(self.spawned)
        self.spawned.clear()
        return {"removed": n, "failed": 0, "missing": 0}

    def op_spawn_item(self, req):
        self.next_entity += 1
        return {"entity": self.next_entity}

    def op_settings_get(self, req):
        paths = req.get("paths")
        vals = {k: v for k, v in self.settings.items() if not isinstance(paths, list) or k in paths}
        return {"values": vals, "write_supported": True}

    def op_settings_set(self, req):
        path = req.get("path")
        if not isinstance(path, str):
            raise AdminError("path required")
        if "value" not in req:
            raise AdminError("value required")
        if path not in self.settings:
            raise AdminError(f"unknown setting '{path}'")
        before, v = self.settings[path], req["value"]
        if type(before) is bool and type(v) is not bool:
            raise AdminError(f"{path} is a bool")
        if isinstance(before, (int, float)) and not isinstance(before, bool) and (isinstance(v, bool) or not isinstance(v, (int, float))):
            raise AdminError(f"{path} is a number")
        self.settings[path] = v
        return {"path": path, "before": before, "after": v}

    def op_settings_save(self, _):
        return {"saved": True}

    def op_time_get(self, _):
        return dict(self.time)

    def op_time_set(self, req):
        if isinstance(req.get("clock"), str):
            m = re.fullmatch(r"(\d{1,2}):(\d{2})", req["clock"].strip())
            if not m or int(m[1]) > 23 or int(m[2]) > 59:
                raise AdminError("clock must look like 19:45")
            self.set_hours(int(m[1]) + int(m[2]) / 60)
        else:
            h = self.num(req, "hours")
            if not 0 <= h < 24:
                raise AdminError("hours must be 0 to <24")
            self.set_hours(h)
        return dict(self.time)

    def op_weather_reset(self, _):
        return {"reset": True}

    def op_broadcast(self, req):
        text = (req.get("text") or "").strip() if isinstance(req.get("text"), str) else ""
        if not text:
            raise AdminError("text required")
        if len(text) > 500:
            raise AdminError("text is longer than 500 characters")
        self.say(f"broadcast: {text}")
        return {"delivered": len(self.players)}

    def op_tell(self, req):
        self.player(req)
        return self.op_broadcast(req) and {"delivered": True}

    def commands(self):
        return self.r.get("commands_describe") or []

    def op_command_run(self, req):
        line = req.get("line")
        if not isinstance(line, str) or not line.strip():
            raise AdminError("line required")
        line = line.strip()
        if len(line) > 1000:
            raise AdminError("line must be 1 to 1000 characters")
        name = line.lstrip("/").split(" ")[0].lower()
        known = {c["name"]: c for c in self.commands()}
        for c in self.commands():
            for a in c.get("aliases") or []:
                known.setdefault(a, c)
        self.say(f"dashboard command: {line} -> ok")
        if name not in known:
            return {"name": name, "found": False, "ok": False, "denied": False, "canceled": False, "parseError": None,
                    "error": f"unknown command '{name}'", "output": []}
        if name in ("time",) and len(line.split()) > 1:
            try:
                self.set_hours(float(line.split()[1]) % 24)
            except ValueError:
                pass
        if name == "help":
            out = [f"/{c['name']} - {c.get('description') or ''}" for c in self.commands()]
        else:
            out = [f"(mock) ran {line}"]
        return {"name": known[name]["name"], "found": True, "ok": True, "denied": False, "canceled": False, "parseError": None, "error": None, "output": out}

    def op_command(self, req):
        r = self.op_command_run(req)
        if not r["found"]:
            raise AdminError(r["error"])
        return {"output": r["output"]}

    def op_command_complete(self, req):
        text = req.get("text") or ""
        body = text[1:] if text.startswith("/") else text
        off = 1 if text.startswith("/") else 0
        if " " not in body:
            names = sorted({c["name"] for c in self.commands()} | {a for c in self.commands() for a in (c.get("aliases") or [])})
            return {"start": off, "suggestions": [n for n in names if n.startswith(body.lower())][:40]}
        name, rest = body.split(" ", 1)
        cmd = next((c for c in self.commands() if c["name"] == name or name in (c.get("aliases") or [])), None)
        start = off + len(name) + 1 + (len(rest) - len(rest.split(" ")[-1]))
        word = rest.split(" ")[-1]
        sugg = []
        if cmd:
            depth = len(rest.split(" ")) - 1
            node = cmd.get("tree") or {}
            for _ in range(depth):
                kids = node.get("children") or []
                node = kids[0] if kids else {}
            for ch in node.get("children") or []:
                if ch.get("kind") == "literal":
                    sugg.append(ch["name"])
                elif ch.get("options"):
                    sugg += ch["options"]
                elif ch.get("type") == "player":
                    sugg += [p["name"] for p in self.players]
                else:
                    sugg.append(f"<{ch.get('name')}>")
        return {"start": start, "suggestions": [s for s in sugg if s.lower().startswith(word.lower()) or s.startswith("<")][:40]}

    def op_health(self, req):
        e = req.get("entity")
        p = next((p for p in self.players if p["entity"] == e), None)
        if p:
            return {"entity": e, "health": p["health"], "healthMax": p["healthMax"], "dead": p["health"] <= 0, "god": p["god"]}
        if e in self.spawned:
            return {"entity": e, "health": 300, "healthMax": 300, "dead": False, "god": False}
        raise AdminError("That entity has no health.")

    def op_heal(self, req):
        p = self.player(req)
        amount = req.get("amount")
        before = p["health"]
        p["health"] = p["healthMax"] if not isinstance(amount, (int, float)) else min(p["healthMax"], p["health"] + int(amount))
        return {"entity": p["entity"], "healed": p["health"] - before, "health": p["health"]}

    def op_god(self, req):
        p = self.player(req)
        p["god"] = bool(req["on"]) if isinstance(req.get("on"), bool) else not p["god"]
        return {"entity": p["entity"], "god": p["god"]}

    def op_kill(self, req):
        e = req.get("entity")
        p = next((p for p in self.players if p["entity"] == e), None)
        if p:
            if p["god"]:
                return {"entity": e, "killed": False, "state": "Invulnerable"}
            p["health"] = 0
            self.combat_deaths.append({"entity": e, "killer": 0, "origin": "Kiln", "pos": dict(p["pos"]), "time": int(time.time()),
                                       "victimName": p["name"], "killerKind": "none"})
            asyncio.get_event_loop().call_later(5, lambda: p.__setitem__("health", p["healthMax"]))
            return {"entity": e, "killed": True, "state": "Killed"}
        if e in self.spawned:
            self.spawned.remove(e)
            return {"entity": e, "killed": True, "state": "Killed"}
        raise AdminError("That entity has no health.")

    def op_damage(self, req):
        e = req.get("entity")
        amount = int(self.num(req, "amount"))
        p = next((p for p in self.players if p["entity"] == e), None)
        if not p:
            raise AdminError("That entity has no health.")
        if not p["god"]:
            p["health"] = max(0, p["health"] - amount)
        return {"entity": e, "state": "Hit", "health": p["health"], "killed": p["health"] == 0}

    def op_save_status(self, _):
        return dict(self.save)

    def op_save_now(self, _):
        self.save["revision"] = (self.save.get("revision") or 0) + 1
        self.save["lastSaved"] = time.strftime("%Y-%m-%dT%H:%M:%S.0000000+00:00", time.gmtime())
        self.save["lastReason"] = "Requested"
        self.save["written"] = (self.save.get("written") or 0) + 1
        return {"revision": self.save["revision"], "ms": 212.4}

    def op_backup_list(self, _):
        return copy.deepcopy(self.backups)

    def op_backup_create(self, req):
        name = req.get("name")
        if not isinstance(name, str) or not re.fullmatch(r"(?!\.)[A-Za-z0-9._-]{1,64}", name):
            raise AdminError("name: letters, digits, - _ . (up to 64)")
        self.op_save_now({})
        stamp = time.strftime("%Y%m%d-%H%M%S", time.gmtime())
        b = {"name": name, "folder": f"{stamp}-{name}", "created": time.strftime("%Y-%m-%dT%H:%M:%S.0000000+00:00", time.gmtime()),
             "world": self.save.get("world"), "revision": self.save["revision"], "bytes": 6_481_920, "files": 3, "dataBundles": 2, "reason": "Backup"}
        self.backups.append(b)
        return b

    def op_backup_restore(self, req):
        name = req.get("name")
        b = next((b for b in self.backups if b["folder"] == name or b["name"] == name), None)
        if not b:
            raise AdminError(f"no backup '{name}'")
        self.save["pendingRestore"] = b["folder"]
        return {"staged": b, "note": "restart the server to restore"}

    def op_backup_cancel_restore(self, _):
        had = bool(self.save.get("pendingRestore"))
        self.save["pendingRestore"] = None
        return {"canceled": had}

    def op_zones_list(self, _):
        return copy.deepcopy(self.zones)

    def op_gameplay_status(self, _):
        g = dict(self.r.get("gameplay_status") or {"available": True})
        g["zones"] = len(self.zones)
        return g

    def op_zones_add(self, req):
        name = (req.get("name") or "").strip() if isinstance(req.get("name"), str) else ""
        if not name or len(name) > 64:
            raise AdminError("name required (up to 64 characters)")
        radius = self.num(req, "radius")
        if not 1 <= radius <= 2000:
            raise AdminError("radius must be 1 to 2000 metres")
        deny = req.get("deny") if isinstance(req.get("deny"), list) else ["build", "break", "terraform", "interact", "loot"]
        for a in deny:
            if a not in ACTIONS:
                raise AdminError(f"unknown action '{a}' (build, break, terraform, interact, loot, craft, spawn)")
        owners = [str(o) for o in (req.get("owners") or [])]
        for o in owners:
            if not o.isdigit() or int(o) <= 76561197960265728:
                raise AdminError("owners must be SteamID64s")
        z = {"name": name, "id": 100 + len(self.zones), "active": True, "pos": {k: self.num(req, k) for k in "xyz"}, "radius": radius,
             "deny": deny, "owners": owners, "allowAdmins": req.get("allowAdmins", True) is not False}
        self.zones = [x for x in self.zones if x["name"].lower() != name.lower()] + [z]
        self.say(f"zone '{name}' set: radius {radius:.0f} m")
        return self.op_zones_list({})

    def op_zones_remove(self, req):
        name = req.get("name")
        if not any(z["name"].lower() == str(name).lower() for z in self.zones):
            raise AdminError(f"no zone named '{name}'")
        self.zones = [z for z in self.zones if z["name"].lower() != str(name).lower()]
        return self.op_zones_list({})

    # content: captured entries; set/reset change the field's value in the copy
    def entry(self, req):
        kind = req.get("kind")
        if kind not in ("items", "recipes", "loot", "drops"):
            raise AdminError("kind must be items, recipes, loot or drops")
        key = req.get("entry")
        if not key:
            raise AdminError("entry required")
        for e in self.content.get("entries", {}).get(kind, []):
            if e["name"] == key or e["id"] == key or e["id"].lower() == str(key).lower():
                return e
        raise AdminError(f"no {kind} entry '{key}'")

    def op_content_search(self, req):
        kind = req.get("kind")
        if kind not in ("items", "recipes", "loot", "drops"):
            raise AdminError("kind must be items, recipes, loot or drops")
        q = (req.get("q") or "").strip()
        if not q:
            raise AdminError("q required")
        names = self.content.get("names", {}).get(kind, [])
        hits = [n for n in names if q.lower() in n["name"].lower() or n["id"] == q]
        hits.sort(key=lambda n: (n["name"].lower() != q.lower(), not n["name"].lower().startswith(q.lower()), len(n["name"])))
        for h in hits:
            h["modified"] = any(k[0] == kind and k[1] == h["name"] for k in self.changes)
        return hits[: int(req.get("limit") or 30)]

    def op_content_entry(self, req):
        e = copy.deepcopy(self.entry(req))
        for f in e["fields"]:
            k = (req["kind"], e["name"], f["path"])
            if k in self.changes:
                f["value"] = self.changes[k]
            f["changed"] = f.get("value") != f.get("original")
        e["modified"] = any(f["changed"] for f in e["fields"])
        return e

    def field(self, req):
        e = self.entry(req)
        path = req.get("field")
        if not path:
            raise AdminError("field required")
        f = next((f for f in e["fields"] if f["path"] == path), None)
        if not f:
            raise AdminError(f"no field '{path}'")
        return e, f

    def op_content_get(self, req):
        e, f = self.field(req)
        return {"value": self.changes.get((req["kind"], e["name"], f["path"]), f.get("value")), "original": f.get("original")}

    def op_content_set(self, req):
        e, f = self.field(req)
        if not f.get("writable"):
            raise AdminError(f"{f['path']} is read-only")
        v = req.get("value")
        opts = f.get("options") or []
        if f.get("enumKind") == "enum":
            if v not in opts:
                raise AdminError(f"{v} is not one of {', '.join(opts)}")
        elif f.get("enumKind") == "flags":
            for x in [t.strip() for t in str(v).split(",") if t.strip()]:
                if x not in opts:
                    raise AdminError(f"{x} is not one of {', '.join(opts)}")
        elif f.get("valueType") == "bool":
            if type(v) is not bool:
                raise AdminError(f"{f['path']} needs true or false")
        elif f.get("valueType") in ("int", "float", "time"):
            if isinstance(v, bool) or not isinstance(v, (int, float)):
                raise AdminError(f"{f['path']} needs a number")
            if f.get("valueType") == "int" and v != int(v):
                raise AdminError(f"{f['path']} needs a whole number")
        text = ("true" if v else "false") if isinstance(v, bool) else (str(int(v)) if isinstance(v, float) and v.is_integer() and f.get("valueType") == "int" else str(v))
        self.changes[(req["kind"], e["name"], f["path"])] = text
        return {"value": text, "original": f.get("original")}

    def op_content_reset(self, req):
        e, f = self.field(req)
        had = self.changes.pop((req["kind"], e["name"], f["path"]), None) is not None
        return {"reset": had, "value": f.get("original"), "original": f.get("original")}

    def op_content_status(self, _):
        s = dict(self.r.get("content_status") or {})
        s["changed"] = (s.get("changed") or 0) + len(self.changes)
        return s

    def op_content_reload(self, _):
        return {"report": (self.r.get("content_report") or {}).get("report", "{}")}

    # configs: validation like Kiln's ConfigFile.TrySet (types, ranges, options)
    def op_config_list(self, _):
        return copy.deepcopy(self.configs)

    def cfg(self, req):
        cid = req.get("id")
        if not isinstance(cid, str):
            raise AdminError("id required")
        c = next((c for c in self.configs if c["id"] == cid), None)
        if not c:
            raise AdminError(f"no config '{cid}'")
        return c

    def op_config_get(self, req):
        return copy.deepcopy(self.cfg(req))

    def op_config_reload(self, req):
        return copy.deepcopy(self.cfg(req))

    def op_config_set(self, req):
        c = self.cfg(req)
        path = req.get("path")
        e = next((e for e in c["entries"] if e["path"] == path), None)
        if not e:
            raise AdminError(f"no setting '{path}' in {c['id']}")
        v = req.get("value")
        t = e["type"]
        if t == "bool" and type(v) is not bool:
            raise AdminError(f"{path}: true or false")
        if t in ("int", "float"):
            if isinstance(v, bool) or not isinstance(v, (int, float)):
                raise AdminError(f"{path}: a number")
            if t == "int" and v != int(v):
                raise AdminError(f"{path}: a whole number")
            if e.get("min") is not None and v < e["min"] or e.get("max") is not None and v > e["max"]:
                raise AdminError(f"{path}: {e.get('min')} to {e.get('max')}")
        if t == "enum" and v not in (e.get("options") or []):
            raise AdminError(f"{path}: one of {', '.join(e.get('options') or [])}")
        if t == "string" and not isinstance(v, str):
            raise AdminError(f"{path}: text")
        if t.startswith("list") and not isinstance(v, list):
            raise AdminError(f"{path}: a list")
        if e.get("requiresRestart"):
            e["fileValue"] = v
        else:
            e["value"] = v
            e.pop("fileValue", None)
        return copy.deepcopy(c)

    # permissions: the model shape of Kiln.Permissions ToJson
    def node(self, req):
        n = req.get("node")
        if not isinstance(n, str) or not NODE.match(n.strip().lower()):
            raise AdminError("node required")
        return n.strip().lower()

    def sid(self, req):
        v = str(req.get("steamId") or "")
        if not v.isdigit() or v == "0":
            raise AdminError("steamId must be a SteamID64")
        return v

    @staticmethod
    def set_rule(rules: list, node: str, allow):
        rules[:] = [r for r in rules if r.lstrip("-") != node]
        if allow is True:
            rules.append(node)
        elif allow is False:
            rules.append("-" + node)

    def op_permissions_get(self, _):
        return copy.deepcopy(self.perm)

    def op_permissions_player_node(self, req):
        sid, node = self.sid(req), self.node(req)
        p = self.perm.setdefault("players", {}).setdefault(sid, {"name": req.get("name"), "groups": [], "nodes": []})
        self.set_rule(p["nodes"], node, req.get("allow"))
        return self.op_permissions_get({})

    def op_permissions_player_group(self, req):
        sid = self.sid(req)
        g = req.get("group")
        if g not in self.perm["groups"]:
            raise AdminError(f"no group '{g}'")
        p = self.perm.setdefault("players", {}).setdefault(sid, {"name": req.get("name"), "groups": [], "nodes": []})
        if req.get("add", True):
            if g not in p["groups"]:
                p["groups"].append(g)
        else:
            p["groups"] = [x for x in p["groups"] if x != g]
        return self.op_permissions_get({})

    def op_permissions_group(self, req):
        g = req.get("group")
        if not isinstance(g, str) or not re.fullmatch(r"[A-Za-z0-9_:-]{1,40}", g):
            raise AdminError("group required")
        grp = self.perm["groups"].setdefault(g, {"admin": False, "inherits": [], "nodes": []})
        if isinstance(req.get("admin"), bool):
            grp["admin"] = req["admin"]
        if isinstance(req.get("inherits"), list):
            for i in req["inherits"]:
                if i not in self.perm["groups"]:
                    raise AdminError(f"no group '{i}'")
            grp["inherits"] = req["inherits"]
        if req.get("node") is not None:
            self.set_rule(grp.setdefault("nodes", []), self.node(req), req.get("allow"))
        return self.op_permissions_get({})

    def op_permissions_group_remove(self, req):
        g = req.get("group")
        if g == self.perm.get("defaultGroup"):
            raise AdminError("the default group can't be removed")
        if g not in self.perm["groups"]:
            raise AdminError(f"no group '{g}'")
        del self.perm["groups"][g]
        for x in self.perm["groups"].values():
            x["inherits"] = [i for i in x.get("inherits", []) if i != g]
        for p in self.perm.get("players", {}).values():
            p["groups"] = [i for i in p["groups"] if i != g]
        return self.op_permissions_get({})

    def op_permissions_map_user_group(self, req):
        ug = req.get("userGroup")
        if not isinstance(ug, str) or not ug:
            raise AdminError("userGroup required")
        self.perm.setdefault("userGroups", {})[ug] = list(req.get("groups") or [])
        return self.op_permissions_get({})

    def op_permissions_check(self, req):
        node = self.node(req)
        sid = str(req.get("steamId") or "")
        p = self.perm.get("players", {}).get(sid, {"groups": [], "nodes": []})
        groups = [self.perm.get("defaultGroup", "default")] + p.get("groups", [])
        rules = [(r, "player") for r in p.get("nodes", [])] + [(r, f"group:{g}") for g in groups for r in self.perm["groups"].get(g, {}).get("nodes", [])]
        for r, frm in rules:
            n = r.lstrip("-")
            if n == node or n == "*" or (n.endswith(".*") and node.startswith(n[:-1])):
                return {"allowed": not r.startswith("-"), "node": node, "level": "Admin", "rule": r, "from": frm, "groups": groups}
        level = next((x["level"] for x in self.perm.get("nodes", []) if x["node"] == node), "everyone")
        return {"allowed": level.lower() == "everyone", "node": node, "level": level.capitalize(), "rule": None, "from": f"default:{level}", "groups": groups}

    def op_permissions_reload(self, _):
        return self.op_permissions_get({})

    # mods
    def mod(self, req):
        mid = req.get("id")
        if not isinstance(mid, str):
            raise AdminError("id required")
        if mid.lower() == "kiln-admin":
            raise AdminError("Kiln.Admin can't reload or unload itself from the dashboard; use kiln/control or a restart")
        m = next((m for m in self.mods if m["id"] == mid), None)
        if not m:
            raise AdminError(f"no mod '{mid}'")
        return m

    def op_mods(self, _):
        return copy.deepcopy(self.mods)

    def op_mod_reload(self, req):
        m = self.mod(req)
        m["state"], m["error"] = "Loaded", None
        return {"ok": True, "message": f"reloaded {m['id']}", "affected": [m["id"]]}

    def op_mod_unload(self, req):
        m = self.mod(req)
        if m["state"] != "Loaded":
            return {"ok": False, "message": f"{m['id']} is not loaded", "affected": []}
        m["state"] = "Unloaded"
        return {"ok": True, "message": f"unloaded {m['id']}", "affected": [m["id"]]}

    def op_mod_load(self, req):
        folder = req.get("folder")
        if not isinstance(folder, str) or not folder or "/" in folder or "\\" in folder or ".." in folder:
            raise AdminError("folder must be a folder name under kiln/mods")
        m = next((m for m in self.mods if m.get("folder") == folder), None)
        if not m:
            return {"ok": False, "message": f"no mod folder '{folder}'", "affected": []}
        m["state"], m["error"] = "Loaded", None
        return {"ok": True, "message": f"loaded {m['id']}", "affected": [m["id"]]}

    def op_mod_test(self, req):
        mid = req.get("id")
        m = next((m for m in self.mods if m["id"] == mid), None)
        if not m or m["state"] != "Loaded":
            raise AdminError(f"{mid} is not loaded")
        results = [{"name": f"{mid}.Loads", "outcome": "Pass", "message": None}, {"name": f"{mid}.Config", "outcome": "Pass", "message": None}]
        return {"mod": mid, "version": m.get("version"), "ms": 412.0, "passed": 2, "failed": 0, "skipped": 0, "file": f"kiln/logs/tests/{mid}.json", "results": results}

    def op_crash_reports(self, req):
        name = req.get("name")
        reports = self.fx.get("crashReports") or self.fx.get("seed", {}).get("crashReports") or []
        if isinstance(name, str) and name:
            r = next((r for r in reports if r["name"] == name), None)
            if not r:
                raise AdminError(f"no crash report '{name}'")
            return {"name": name, "text": r.get("text", "")}
        return [{k: v for k, v in r.items() if k != "text"} for r in reports]

    def op_log_tail(self, req):
        n = req.get("lines", 200)
        if not isinstance(n, int) or not 1 <= n <= 5000:
            raise AdminError("lines: 1 to 5000")
        base = (self.r.get("log_tail") or {}).get("lines", [])
        lines = (base + self.log)[-n:]
        return {"file": "kiln.log", "lines": lines, "size": sum(len(x) + 1 for x in lines)}

    def op_admin_token_rotate(self, _):
        return {"rotated": True}


# ---------------------------------------------------------------------------------------------- KAP server

class Conn:
    def __init__(self, n, reader, writer):
        self.n, self.reader, self.writer = n, reader, writer
        self.ready = False
        self.sub = set()
        self.seq = {"state": 0, "log": 0}
        self.inflight: set[int] = set()
        self.last_recv = self.last_send = time.monotonic()
        self.closed = False


class MockServer:
    def __init__(self, world: World, token: bytes, fixture_hello: bool = False, quiet: bool = False):
        self.world, self.token, self.fixture_hello, self.quiet = world, token, fixture_hello, quiet
        self.conns: list[Conn] = []
        self.count = 0
        self.port = 0
        self.log_sent = 0

    def info(self, msg):
        if not self.quiet:
            print(f"[mock-admin] {msg}", flush=True)

    async def send(self, c: Conn, obj):
        if c.closed:
            return
        data = frame(obj)
        if len(data) - 4 > MAX_SERVER_FRAME:
            obj = {"t": "res", "id": obj.get("id"), "ok": False, "op": obj.get("op"), "error": "result over the frame limit", "code": "too_large"}
            data = frame(obj)
        c.writer.write(data)
        c.last_send = time.monotonic()
        try:
            await c.writer.drain()
        except ConnectionError:
            c.closed = True

    async def read(self, c: Conn, timeout=None):
        head = await asyncio.wait_for(c.reader.readexactly(4), timeout)
        (n,) = struct.unpack("<I", head)
        if n == 0:
            raise ValueError("bad_frame:zero-length frame")
        if n > MAX_CLIENT_FRAME:
            raise ValueError(f"too_large:frames from clients are at most {MAX_CLIENT_FRAME} bytes")
        data = await c.reader.readexactly(n)
        c.last_recv = time.monotonic()
        try:
            obj = json.loads(data)
        except ValueError:
            raise ValueError("bad_frame:not a JSON object")
        if not isinstance(obj, dict):
            raise ValueError("bad_frame:not a JSON object")
        return obj

    async def fatal(self, c: Conn, code, message):
        await self.send(c, {"t": "error", "code": code, "message": message})
        c.closed = True
        c.writer.close()

    async def handle(self, reader, writer):
        self.count += 1
        c = Conn(self.count, reader, writer)
        if len(self.conns) >= MAX_CONNECTIONS:
            await self.fatal(c, "busy", f"at most {MAX_CONNECTIONS} KAP connections")
            return
        self.conns.append(c)
        try:
            await self.session(c)
        except (asyncio.IncompleteReadError, ConnectionError):
            pass
        except ValueError as e:
            code, _, msg = str(e).partition(":")
            await self.fatal(c, code, msg)
        finally:
            c.closed = True
            if c in self.conns:
                self.conns.remove(c)
            try:
                writer.close()
            except Exception:  # noqa: BLE001
                pass
            self.info(f"#{c.n} closed")

    async def session(self, c: Conn):
        vec = json.loads(VECTOR.read_text())
        if self.fixture_hello:
            nonce = vec["nonce"]
            payload = vec["helloPayload"].encode()
            c.writer.write(struct.pack("<I", len(payload)) + payload)
            await c.writer.drain()
        else:
            nonce = secrets.token_hex(32)
            await self.send(c, {"t": "hello", "proto": PROTO, "nonce": nonce, "kiln": self.world.kiln, "admin": "0.2.0-mock",
                                "server": self.world.server, "auth": ["hmac-sha256"]})
        try:
            f = await self.read(c, HANDSHAKE_S)
        except asyncio.TimeoutError:
            await self.fatal(c, "auth_required", "authenticate within 5 s")
            return
        if f.get("t") != "auth":
            await self.fatal(c, "auth_required", "authenticate first (auth frame)")
            return
        if f.get("proto") != PROTO:
            await self.fatal(c, "version", f"this server speaks {PROTO}")
            return
        cnonce, m = str(f.get("cnonce", "")), str(f.get("mac", ""))
        if not re.fullmatch(r"[0-9a-f]{64}", cnonce) or not hmac.compare_digest(m, mac(self.token, "kap1-auth", nonce, cnonce)):
            await asyncio.sleep(0.25)
            await self.fatal(c, "auth_failed", "wrong mac")
            self.info(f"#{c.n} authentication failed")
            return
        session = secrets.token_hex(8)
        await self.send(c, {"t": "ready", "session": session, "mac": mac(self.token, "kap1-ready", nonce, cnonce), "ops": self.world.ops,
                            "topics": ["state", "log"], "limits": {"maxFrame": MAX_SERVER_FRAME, "maxClientFrame": MAX_CLIENT_FRAME,
                                                                  "inflight": MAX_INFLIGHT, "pingS": PING_S, "idleS": IDLE_S}})
        c.ready = True
        self.info(f"#{c.n} ({f.get('client', 'client')}) session {session} ready")
        while not c.closed:
            f = await self.read(c)
            await self.dispatch(c, f)

    async def dispatch(self, c: Conn, f: dict):
        t = f.get("t")
        fid = f.get("id")
        good_id = isinstance(fid, int) and not isinstance(fid, bool) and fid >= 0
        if t == "req":
            if not good_id:
                await self.send(c, {"t": "error", "code": "bad_frame", "message": "req needs an unsigned integer id"})
                return
            op = f.get("op")
            args = f.get("args", {})
            if not isinstance(op, str) or not op:
                await self.send(c, {"t": "error", "code": "bad_frame", "message": "req needs an op", "id": fid})
                return
            if not isinstance(args, dict):
                await self.send(c, {"t": "error", "code": "bad_frame", "message": "args must be an object", "id": fid})
                return
            if fid in c.inflight:
                await self.send(c, {"t": "error", "code": "bad_frame", "message": f"request {fid} is already in flight", "id": fid})
                return
            if len(c.inflight) >= MAX_INFLIGHT:
                await self.send(c, {"t": "res", "id": fid, "ok": False, "op": op, "ms": 0, "error": f"at most {MAX_INFLIGHT} requests in flight", "code": "busy"})
                return
            c.inflight.add(fid)
            asyncio.ensure_future(self.request(c, fid, op, args))
        elif t in ("sub", "unsub"):
            if not good_id:
                await self.send(c, {"t": "error", "code": "bad_frame", "message": f"{t} needs an unsigned integer id"})
                return
            topic = f.get("topic")
            if topic not in ("state", "log"):
                await self.send(c, {"t": "res", "id": fid, "ok": False, "op": t, "ms": 0, "error": f"no topic '{topic}' (topics: state, log)", "code": "unknown_topic"})
                return
            (c.sub.add if t == "sub" else c.sub.discard)(topic)
            await self.send(c, {"t": "res", "id": fid, "ok": True, "op": t})
        elif t == "ping":
            out = {"t": "pong", "id": fid if good_id else 0}
            if "ts" in f:
                out["ts"] = f["ts"]
            await self.send(c, out)
        elif t == "pong":
            pass
        elif t == "bye":
            c.closed = True
            c.writer.close()
        elif t == "auth":
            await self.send(c, {"t": "error", "code": "bad_frame", "message": "already authenticated"})
        else:
            e = {"t": "error", "code": "bad_frame", "message": "frame without t" if t is None else f"unknown frame type '{t}'"}
            if good_id:
                e["id"] = fid
            await self.send(c, e)

    async def request(self, c: Conn, fid: int, op: str, args: dict):
        t0 = time.monotonic()
        try:
            if op not in self.world.ops:
                res = {"t": "res", "id": fid, "ok": False, "op": op, "ms": 0, "error": f"unknown op '{op}'", "code": "unknown_op"}
            else:
                await asyncio.sleep(SLOW.get(op, 0.02))
                try:
                    data = self.world.run(op, dict(args, op=op))
                    res = {"t": "res", "id": fid, "ok": True, "op": op, "ms": int((time.monotonic() - t0) * 1000), "data": data}
                except AdminError as e:
                    res = {"t": "res", "id": fid, "ok": False, "op": op, "ms": int((time.monotonic() - t0) * 1000), "error": str(e), "code": "op_failed"}
                except Exception as e:  # noqa: BLE001 - a bug in the mock: report it like Kiln.Admin would
                    res = {"t": "res", "id": fid, "ok": False, "op": op, "ms": 0, "error": f"{type(e).__name__}: {e}", "code": "op_failed"}
            await self.send(c, res)
        finally:
            c.inflight.discard(fid)

    async def ticker(self):
        while True:
            await asyncio.sleep(1.0)
            self.world.tick()
            st = self.world.state()
            new_log = self.world.log[self.log_sent:]
            self.log_sent = len(self.world.log)
            now = time.monotonic()
            for c in list(self.conns):
                if not c.ready or c.closed:
                    continue
                if "state" in c.sub:
                    c.seq["state"] += 1
                    await self.send(c, {"t": "push", "topic": "state", "seq": c.seq["state"], "data": st})
                if "log" in c.sub and new_log:
                    c.seq["log"] += 1
                    await self.send(c, {"t": "push", "topic": "log", "seq": c.seq["log"], "data": {"lines": new_log[-256:]}})
                if now - c.last_recv > IDLE_S:
                    await self.send(c, {"t": "bye", "reason": "idle"})
                    c.closed = True
                    c.writer.close()
                elif now - c.last_send > PING_S:
                    await self.send(c, {"t": "ping", "id": int(now), "ts": int(time.time() * 1000)})

    async def bye_all(self, reason="shutdown"):
        for c in list(self.conns):
            await self.send(c, {"t": "bye", "reason": reason})
            c.writer.close()


def load_fixture(path: str | None = None) -> dict:
    for f in ([Path(path)] if path else [CAPTURED, SYNTHETIC]):
        if f.is_file():
            return json.loads(f.read_text())
    print(f"[mock-admin] no fixture ({path or SYNTHETIC}): answering from an empty one", file=sys.stderr)
    return {"results": {"status": {"kiln": "0.2.0-dev", "ops": ["ping", "status"], "features": {}}}}


def write_endpoint(base: Path, port: int, token_hex: str, server: dict):
    d = base / "kiln/data/kiln-admin"
    d.mkdir(parents=True, exist_ok=True)
    tok = d / "admin-token"
    tok.write_text(token_hex + "\n")
    os.chmod(tok, 0o600)
    ep = {"format": 1, "protocol": PROTO, "host": "127.0.0.1", "port": port, "pid": os.getpid(), "unixPid": os.getpid(),
          "started": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "kiln": "0.2.0-dev", "admin": "0.2.0-mock",
          "server": server, "token": "admin-token", "bridge": None}
    tmp = d / "endpoint.json.tmp"
    tmp.write_text(json.dumps(ep, indent=2))
    tmp.replace(d / "endpoint.json")
    return d / "endpoint.json"


async def serve(args) -> int:
    vec = json.loads(VECTOR.read_text())
    token_hex = args.token or (Path(args.token_file).read_text().strip() if args.token_file else vec["token"])
    world = World(load_fixture(args.fixture), players=args.players)
    if args.fixture_hello:
        world.server = {"id": "srv-4b8e2c", "name": "Ember Isles"}
    srv = MockServer(world, bytes.fromhex(token_hex), args.fixture_hello, args.quiet)
    server = await asyncio.start_server(srv.handle, "127.0.0.1", args.port)
    srv.port = server.sockets[0].getsockname()[1]
    ep = write_endpoint(Path(args.dir), srv.port, token_hex, world.server) if args.dir else None
    print(f"[mock-admin] KAP/1 on 127.0.0.1:{srv.port} ({len(world.ops)} ops){f'; endpoint {ep}' if ep else ''}", flush=True)
    tick = asyncio.ensure_future(srv.ticker())
    try:
        async with server:
            await server.serve_forever()
    except asyncio.CancelledError:
        pass
    finally:
        tick.cancel()
        await srv.bye_all()
        if ep:
            ep.unlink(missing_ok=True)
    return 0


# ---------------------------------------------------------------------------------------------- client

class Client:
    """A blocking KAP/1 client (tests, capture)."""

    def __init__(self, host: str, port: int, token: bytes, timeout=10.0):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.token = token
        self.next = 0
        self.hello = self.recv()
        if self.hello.get("t") != "hello":
            raise RuntimeError(f"expected hello, got {self.hello}")
        cnonce = secrets.token_hex(32)
        self.send({"t": "auth", "proto": PROTO, "cnonce": cnonce, "mac": mac(token, "kap1-auth", self.hello["nonce"], cnonce), "client": "mock-admin.py"})
        self.ready = self.recv()
        if self.ready.get("t") != "ready":
            raise RuntimeError(f"handshake refused: {self.ready}")
        if self.ready.get("mac") != mac(token, "kap1-ready", self.hello["nonce"], cnonce):
            raise RuntimeError("the server's ready mac does not match: it does not know the token")
        self.max_frame = int(self.ready.get("limits", {}).get("maxFrame") or MAX_SERVER_FRAME)
        self.pending: list[dict] = []

    def send(self, obj):
        self.sock.sendall(frame(obj))

    def recv_exact(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("closed")
            buf += chunk
        return buf

    def recv(self):
        (n,) = struct.unpack("<I", self.recv_exact(4))
        if n > getattr(self, "max_frame", MAX_SERVER_FRAME):
            raise RuntimeError(f"frame of {n} bytes over maxFrame")
        return json.loads(self.recv_exact(n))

    def call(self, op, args=None):
        self.next += 1
        rid = self.next
        self.send({"t": "req", "id": rid, "op": op, "args": args or {}})
        while True:
            f = self.recv()
            if f.get("t") == "res" and f.get("id") == rid:
                return f
            if f.get("t") == "ping":
                self.send({"t": "pong", "id": f.get("id"), "ts": f.get("ts")})
            elif f.get("t") == "error" and f.get("id") == rid:
                return f
            else:
                self.pending.append(f)

    def close(self):
        try:
            self.send({"t": "bye", "reason": "done"})
        finally:
            self.sock.close()


def endpoint_of(args):
    if getattr(args, "server", None):
        d = Path(args.server).expanduser() / "kiln/data/kiln-admin"
        ep = json.loads((d / "endpoint.json").read_text())
        return ep["host"], int(ep["port"]), bytes.fromhex((d / "admin-token").read_text().strip())
    host, _, port = args.endpoint.rpartition(":")
    return host or "127.0.0.1", int(port), bytes.fromhex(Path(args.token_file).read_text().strip())


def cmd_call(args) -> int:
    host, port, token = endpoint_of(args)
    c = Client(host, port, token)
    r = c.call(args.op, json.loads(args.json) if args.json else {})
    c.close()
    print(json.dumps(r, indent=2, ensure_ascii=False))
    return 0 if r.get("ok") else 1


def cmd_watch(args) -> int:
    host, port, token = endpoint_of(args)
    c = Client(host, port, token)
    c.send({"t": "sub", "id": 999999, "topic": args.topic})
    got = 0
    while got < args.count:
        f = c.recv()
        if f.get("t") == "push":
            got += 1
            print(json.dumps(f, ensure_ascii=False)[:2000])
    c.close()
    return 0


# read-only ops whose answers make the fixture (none of them changes the server)
CAPTURE = ["status", "admin_info", "players", "enemies", "settings_get", "time_get", "commands", "commands_describe", "save_status",
           "backup_list", "gameplay_status", "zones_list", "content_status", "content_definitions", "content_report", "sync_status",
           "config_list", "permissions_get", "events_stats", "mods", "crash_reports", "native_status", "network_status", "log_tail"]
CAPTURE_SEARCH = {"items": ["Wood", "Sword", "Shroud"], "recipes": ["Wood", "Bandage"], "loot": ["Chest"], "drops": ["Wolf"]}


def anonymize(obj, server_dir: str):
    text = json.dumps(obj, ensure_ascii=False)
    win = "Z:" + server_dir.replace("/", "\\")
    for a, b in ((win.replace("\\", "\\\\"), "Z:\\\\srv\\\\enshrouded\\\\lab"), (server_dir, "/srv/enshrouded/lab"), (str(Path.home()), "/home/player")):
        text = text.replace(a, b)
    return json.loads(text)


def cmd_capture(args) -> int:
    host, port, token = endpoint_of(args)
    c = Client(host, port, token, timeout=30)
    out = {"description": "Results of a real Kiln.Admin (read-only ops), captured by tools/launcher/mock-admin.py capture. Holds game names: local only (gitignored), never commit.",
           "captured": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "server": c.hello.get("server"), "ops": c.ready.get("ops"),
           "limits": c.ready.get("limits"), "results": {}, "errors": {}, "content": {"names": {}, "entries": {}}}
    for op in CAPTURE:
        if op not in c.ready["ops"]:
            continue
        r = c.call(op, {"lines": 120} if op == "log_tail" else {})
        (out["results"] if r.get("ok") else out["errors"])[op] = r.get("data") if r.get("ok") else r.get("error")
        print(f"{op}: {'ok' if r.get('ok') else r.get('error')}", file=sys.stderr)
    for kind, qs in CAPTURE_SEARCH.items():
        names, entries = {}, []
        f = c.call("content_fields", {"kind": kind})
        if f.get("ok"):
            out["results"].setdefault("content_fields", {})[kind] = f["data"]
        for q in qs:
            r = c.call("content_search", {"kind": kind, "q": q, "limit": 30})
            for h in r.get("data") or []:
                names[h["name"]] = h
        for h in list(names.values())[:6]:
            r = c.call("content_entry", {"kind": kind, "entry": h["name"]})
            if r.get("ok"):
                entries.append(r["data"])
        out["content"]["names"][kind] = list(names.values())
        out["content"]["entries"][kind] = entries
        print(f"content {kind}: {len(names)} names, {len(entries)} entries", file=sys.stderr)
    items = {}
    for q in ["wood", "stone", "iron", "sword", "bow", "potion", "arrow", "torch", "shroud", "bandage", "meat", "seed"]:
        r = c.call("item_search", {"q": q})
        for i in r.get("data") or []:
            items[i["id"]] = i
    out["items"] = list(items.values())
    reports = []
    for rep in (out["results"].get("crash_reports") or [])[:3]:
        r = c.call("crash_reports", {"name": rep["name"]})
        if r.get("ok"):
            reports.append({**rep, "text": r["data"]["text"][:20000]})
    out["crashReports"] = reports
    c.close()
    server_dir = str(Path(args.server).expanduser().resolve()) if args.server else ""
    out = anonymize(out, server_dir) if server_dir else out
    out.setdefault("creatures", [])
    dst = Path(args.out)
    if dst.is_file():  # keep the hand-made parts (creatures, extra crash reports) of an older capture
        old = json.loads(dst.read_text())
        for k in ("creatures",):
            if old.get(k) and not out.get(k):
                out[k] = old[k]
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n")
    print(f"wrote {dst} ({dst.stat().st_size} bytes)", file=sys.stderr)
    return 0


# ---------------------------------------------------------------------------------------------- synthetic fixture

# Kiln's own results (no game content): copied from a capture into the synthetic fixture
KILN_OWNED = ["status", "admin_info", "players", "enemies", "settings_get", "time_get", "commands", "commands_describe",
              "backup_list", "gameplay_status", "zones_list", "content_status", "content_report", "sync_status", "config_list",
              "permissions_get", "events_stats", "mods", "crash_reports", "native_status", "network_status"]
RARITY = ["Common", "Uncommon", "Rare", "Epic", "Legendary"]
ITEM_KINDS = [("Sword", "Weapon", "Weapons", True), ("Axe", "Weapon", "Weapons", True), ("Bow", "Weapon", "Weapons", True),
              ("Staff", "Weapon", "Weapons", True), ("Shield", "Equipment", "Equipment", True), ("Helmet", "Equipment", "Equipment", True),
              ("Chest Armor", "Equipment", "Equipment", True), ("Pickaxe", "Tool", "Tools", True), ("Logs", "Material", "Materials", False),
              ("Stone", "Material", "Materials", False), ("Ore", "Material", "Materials", False), ("Ingot", "Material", "Materials", False),
              ("Fiber", "Material", "Materials", False), ("Resin", "Material", "Materials", False), ("Arrow", "Ammo", "Ammunition", False),
              ("Potion", "Consumable", "Consumables", False), ("Bandage", "Consumable", "Consumables", False), ("Meal", "Food", "Food", False),
              ("Seed", "Seed", "Seeds", False), ("Torch", "Tool", "Tools", False), ("Lantern", "Tool", "Tools", True), ("Rope", "Material", "Materials", False)]


def _sid(text: str) -> int:
    return int(hashlib.sha256(text.encode()).hexdigest()[:8], 16) | 0x10000000


def _guid(text: str) -> str:
    h = hashlib.sha256(text.encode()).hexdigest()
    return f"{h[:8]}-{h[8:12]}-4{h[13:16]}-a{h[17:20]}-{h[20:32]}"


def _field(path, typ, vt, value, writable=True, enum="plain", options=None):
    f = {"path": path, "type": typ, "valueType": vt, "writable": writable, "enumKind": enum, "value": value, "original": value, "changed": False}
    if options:
        f["options"] = options
    return f


def synthetic_items():
    out = []
    for name, debug, cat, gear in ITEM_KINDS:
        for t in range(1, 6):
            label = f"Test {name} T{t}"
            dbg = f"Test_{debug}_{name.replace(' ', '')}_T{t}"
            out.append({"id": _sid(dbg), "name": label, "gear": gear, "debug": dbg, "category": cat, "maxStack": 1 if gear else 50 * t})
    return out


def synthetic_content(items):
    def item_fields(it, tier):
        gear = it["gear"]
        return [
            _field("itemId", "uint32", "int", str(it["id"]), writable=False),
            _field("objectId", "Guid", "guid", _guid(it["debug"]), writable=False),
            _field("maxStackSize", "uint16", "int", str(it["maxStack"])),
            _field("generateRarity", "bool", "bool", "true" if gear else "false"),
            _field("rarity", "ItemRarity", "int", RARITY[min(4, tier - 1)], enum="enum", options=RARITY),
            _field("disableRarityGeneration", "ItemRarityMask", "int", "", enum="flags", options=RARITY),
            _field("category", "ItemCategory", "int", it["category"], enum="enum", options=sorted({k[2] for k in ITEM_KINDS})),
            _field("weight", "float32", "float", f"{0.5 * tier:g}"),
            _field("sellValue", "uint32", "int", str(10 * tier)),
            _field("durability.max", "uint16", "int", str(100 * tier if gear else 0)),
            _field("durability.lossPerUse", "float32", "float", "0.5" if gear else "0"),
            _field("damage.base", "float32", "float", f"{12.5 * tier:g}" if gear else "0"),
            _field("damage.element", "DamageElement", "int", "None", enum="enum", options=["None", "Fire", "Frost", "Shock", "Poison"]),
            _field("damage.critMultiplier", "float32", "float", "1.5"),
            _field("equipment.slot", "EquipmentSlot", "int", "MainHand" if gear else "None", enum="enum",
                   options=["None", "Head", "Chest", "Hands", "Legs", "Feet", "MainHand", "OffHand", "Ring"]),
            _field("equipment.hideMask", "HideMask", "int", "", enum="flags", options=["Hair", "Beard", "Ears", "Body"]),
            _field("equipment.allowInWater", "bool", "bool", "true"),
            _field("uiActionHints.alwaysShow", "bool", "bool", "false"),
            _field("useCooldown", "Time", "time", "0.75s"),
            _field("description", "LocaTagId", "string", f"A test item ({it['name']})", writable=False),
        ]

    entries = {"items": [], "recipes": [], "loot": [], "drops": []}
    names = {"items": [], "recipes": [], "loot": [], "drops": []}
    for it in items:
        names["items"].append({"id": f"0x{it['id']:08x}", "name": it["debug"], "modified": False})
    for want in ("Test_Weapon_Sword_T2", "Test_Material_Logs_T1", "Test_Equipment_Shield_T1"):
        it = next(i for i in items if i["debug"] == want)
        entries["items"].append({"kind": "items", "id": f"0x{it['id']:08x}", "name": it["debug"], "guid": _guid(it["debug"]), "modified": False,
                                 "fields": item_fields(it, int(want[-1]))})
    recipe_items = [i for i in items if i["gear"] or i["category"] in ("Consumables", "Ammunition")][:30]
    logs, ingot = next(i for i in items if i["debug"] == "Test_Material_Logs_T1"), next(i for i in items if i["debug"] == "Test_Material_Ingot_T2")
    for n, it in enumerate(recipe_items):
        rname = f"Recipe_{it['debug']}"
        names["recipes"].append({"id": f"0x{_sid(rname):08x}", "name": rname, "modified": False})
        if n < 3:
            entries["recipes"].append({"kind": "recipes", "id": f"0x{_sid(rname):08x}", "name": rname, "guid": _guid(rname), "modified": False,
                "fields": [_field("craftingDuration", "Time", "time", "4s"), _field("station", "CraftingStation", "int", "Workbench", enum="enum",
                           options=["Hand", "Workbench", "Forge", "Alchemy"]), _field("unlocked", "bool", "bool", "false"),
                           _field("outputs[0].count", "uint16", "int", "1"), _field("inputs[0].count", "uint16", "int", "6"), _field("inputs[1].count", "uint16", "int", "2")],
                "inputs": [{"item": logs["debug"], "name": logs["debug"], "count": 6, "category": 0}, {"item": ingot["debug"], "name": ingot["debug"], "count": 2, "category": 0}],
                "outputs": [{"item": it["debug"], "name": it["debug"], "count": 1, "category": 0}]})
    for t in range(1, 6):
        for kind in ("Chest", "Barrel", "Crate", "Urn"):
            lname = f"Loot_Test_{kind}_T{t}"
            names["loot"].append({"id": f"0x{_sid(lname):08x}", "name": lname, "modified": False})
            if len(entries["loot"]) < 2:
                entries["loot"].append({"kind": "loot", "id": f"0x{_sid(lname):08x}", "name": lname, "guid": _guid(lname), "modified": False,
                    "fields": [_field("rolls", "uint8", "int", str(t + 1)), _field("chanceEmpty", "float32", "float", "0.1"),
                               _field("tier", "LootTier", "int", f"T{t}", enum="enum", options=[f"T{i}" for i in range(1, 6)])]})
    for c in "ABCDEFGHIJ":
        dname = f"Drop_Test_Creature_{c}"
        names["drops"].append({"id": f"0x{_sid(dname):08x}", "name": dname, "modified": False})
        if len(entries["drops"]) < 2:
            stacks = [{"index": 0, "item": logs["debug"], "name": logs["debug"], "countMin": 1, "countMax": 3, "chance": 0.75, "weight": 1.0, "group": 0},
                      {"index": 1, "item": ingot["debug"], "name": ingot["debug"], "countMin": 1, "countMax": 1, "chance": 0.2, "weight": 0.5, "group": 0}]
            entries["drops"].append({"kind": "drops", "id": f"0x{_sid(dname):08x}", "name": dname, "guid": _guid(dname), "modified": False,
                "fields": [_field("stacks[0].chance", "float32", "float", "0.75"), _field("stacks[0].countMin", "uint16", "int", "1"),
                           _field("stacks[0].countMax", "uint16", "int", "3"), _field("stacks[1].chance", "float32", "float", "0.2")], "stacks": stacks})
    fields = {k: [{x: f[x] for x in ("path", "type", "valueType", "writable", "enumKind", "options") if x in f} for f in v[0]["fields"]] for k, v in entries.items()}
    return {"names": names, "entries": entries}, fields


def synthetic_creatures():
    kinds = ["Melee", "Ranged", "Caster", "Brute", "Scout", "Swarm", "Elite"]
    out = []
    for i, c in enumerate("ABCDEFGHIJKLMN"):
        tpl = f"Enemy_Test_{c}_{kinds[i % len(kinds)]}"
        out.append({"guid": _guid(tpl), "template": tpl, "name": f"Creature {c}"})
    return out


def _clean_permissions(p: dict) -> dict:
    """Server user groups get neutral names (the game's default server config names them)."""
    p = copy.deepcopy(p)
    ren = {}
    for i, g in enumerate(p.get("serverUserGroups") or []):
        new = ["Admins", "Builders", "Guests", "Visitors", "Group E", "Group F"][min(i, 5)]
        ren[g["name"]] = new
        g["name"] = new
    p["userGroups"] = {ren.get(k, k): v for k, v in (p.get("userGroups") or {}).items()}
    return p


def cmd_synthesize(args) -> int:
    src = Path(args.src) if args.src else (CAPTURED if CAPTURED.is_file() else SYNTHETIC)
    base = json.loads(src.read_text())
    r = base.get("results", {})
    results = {k: copy.deepcopy(r[k]) for k in KILN_OWNED if k in r}
    if "permissions_get" in results:
        results["permissions_get"] = _clean_permissions(results["permissions_get"])
    results["save_status"] = {**(r.get("save_status") or {}), "world": _guid("synthetic world")}
    # mods' new definitions: the shapes of Kiln's NewItems sample, with test items as their bases
    defs = []
    for kind, name, based_on, disp, mod, extra in [
        ("item", "Kiln_Sample_TestShard", "Test_Material_Resin_T1", "Test Shard", "newitems", {}),
        ("item", "Kiln_Sample_TestEdge", "Test_Weapon_Sword_T2", "Test Edge", "newitems", {"fields": {"damage.base": 40, "rarity": "Rare"}}),
        ("item", "Kiln_Sample_TestLantern", "Test_Tool_Lantern_T1", "Test Lantern", "newitems", {"texts": {"de": {"name": "Testlaterne"}}}),
        ("recipe", "Kiln_Sample_TestEdge_Recipe", "Recipe_Test_Weapon_Sword_T2", "Test Edge", "newitems",
         {"inputs": [{"item": "Test_Material_Ingot_T2", "count": 2}, {"item": "Kiln_Sample_TestShard", "count": 3}], "outputs": [{"item": "Kiln_Sample_TestEdge", "count": 1}], "station": "hand"}),
    ]:
        guid = _guid(name)
        definition = {"guid": guid, "name": name, "base": based_on, "displayName": disp, "description": f"A synthetic {kind} for the mock.", **extra}
        defs.append({"kind": kind, "guid": guid, "id": f"0x{_sid(name):08x}", "name": name, "base": based_on, "owner": f"mod {mod}", "source": "",
                     "state": "registered", "worlds": ["server"], "version": 0, "mod": mod, "displayName": disp,
                     "languages": ["*", "de"] if "texts" in extra else ["*"], "definition": definition})
    results["content_definitions"] = defs
    items = synthetic_items()
    content, fields = synthetic_content(items)
    results["content_fields"] = fields
    results["log_tail"] = {"file": "kiln.log", "size": 4096, "lines": [
        "[I 11:16:15.902] [kiln] Kiln 0.2.0-dev starting (server)",
        "[I 11:16:16.123] [kiln] boot complete; handing control to the game",
        "[I 11:16:18.935] [mod:bigstacks] stack sizes x4 (max 9999) for every stackable item",
        "[I 11:16:19.148] [mod:kiln-admin] KAP/1 on 127.0.0.1:26020",
        "[I 11:16:19.750] [service:kiln-content.dll] registries ready: 110 items, 30 recipes (synthetic)",
        "[W 11:16:20.002] [mod:claims] no claims.json yet; starting empty",
        "[I 11:16:30.771] [service:kiln-saves.dll] saves: world written: revision 3015"]}
    seed = copy.deepcopy(base.get("seed") or {})
    for dth in seed.get("combatDeaths", []):
        if dth.get("killerKind") == "creature":
            dth["killerName"] = "Creature B"
    out = {"description": "Synthetic Kiln.Admin results for the mocks (tools/launcher/mock-admin.py synthesize): Kiln's own results "
                          "from a capture; game content invented (Test items, Creature A..N, content fields). Safe to commit.",
           "synthesized": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()), "server": base.get("server") or {"id": "srv-mock", "name": "Mock server"},
           "ops": base.get("ops") or (results.get("status") or {}).get("ops"), "limits": base.get("limits"), "results": results,
           "content": content, "items": [{"id": i["id"], "name": i["name"], "gear": i["gear"]} for i in items],
           "creatures": synthetic_creatures(), "crashReports": [], "seed": seed}
    dst = Path(args.out)
    dst.parent.mkdir(parents=True, exist_ok=True)
    dst.write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n")
    print(f"wrote {dst} ({dst.stat().st_size} bytes) from {src}", file=sys.stderr)
    return 0


# ---------------------------------------------------------------------------------------------- selftest

def cmd_selftest(_args) -> int:
    import subprocess
    import tempfile
    vec = json.loads(VECTOR.read_text())
    assert mac(bytes.fromhex(vec["token"]), "kap1-auth", vec["nonce"], vec["cnonce"]) == vec["mac"], "auth mac vector"
    assert mac(bytes.fromhex(vec["token"]), "kap1-ready", vec["nonce"], vec["cnonce"]) == vec["serverMac"], "ready mac vector"
    tmp = tempfile.mkdtemp(prefix="kap-mock-")
    p = subprocess.Popen([sys.executable, __file__, "serve", "--dir", tmp, "--fixture-hello", "--quiet", "--fixture", str(SYNTHETIC)], stdout=subprocess.PIPE, text=True)
    try:
        line = p.stdout.readline()
        port = int(re.search(r":(\d+)", line)[1])
        # the hello frame is the vector's, byte for byte
        s = socket.create_connection(("127.0.0.1", port), timeout=5)
        head = s.recv(4)
        n = struct.unpack("<I", head)[0]
        body = b""
        while len(body) < n:
            body += s.recv(n - len(body))
        assert (head + body).hex() == vec["helloFrameHex"], "hello frame bytes"
        s.sendall(frame({"t": "auth", "proto": PROTO, "cnonce": vec["cnonce"], "mac": vec["mac"], "client": "selftest"}))
        (n,) = struct.unpack("<I", s.recv(4))
        ready = json.loads(s.recv(n))
        assert ready["t"] == "ready" and ready["mac"] == vec["serverMac"], f"ready: {ready}"
        assert ready["limits"]["maxFrame"] == MAX_SERVER_FRAME
        s.close()
        # wrong mac
        s = socket.create_connection(("127.0.0.1", port), timeout=5)
        (n,) = struct.unpack("<I", s.recv(4)); s.recv(n)
        s.sendall(frame({"t": "auth", "proto": PROTO, "cnonce": vec["cnonce"], "mac": "00" * 32}))
        (n,) = struct.unpack("<I", s.recv(4))
        err = json.loads(s.recv(n))
        assert err == {"t": "error", "code": "auth_failed", "message": "wrong mac"}, err
        s.close()
        # ops through the client (token from the endpoint folder)
        ns = argparse.Namespace(server=tmp)
        host, port2, token = endpoint_of(ns)
        c = Client(host, port2, token)
        assert c.call("ping")["ok"]
        r = c.call("time_set", {"hours": 25})
        assert not r["ok"] and r["code"] == "op_failed" and "0 to <24" in r["error"], r
        r = c.call("no_such_op")
        assert r["code"] == "unknown_op", r
        r = c.call("zones_add", {"name": "Selftest", "x": 1, "y": 2, "z": 3, "radius": 10})
        assert r["ok"] and any(z["name"] == "Selftest" for z in r["data"]), r
        c.send({"t": "sub", "id": 50, "topic": "state"})
        push = None
        for _ in range(10):
            f = c.recv()
            if f.get("t") == "push":
                push = f
                break
        assert push and push["topic"] == "state" and push["seq"] == 1 and push["data"]["ready"], push
        c.send({"t": "ping", "id": 77, "ts": 123})
        for _ in range(10):
            f = c.recv()
            if f.get("t") == "pong":
                assert f == {"t": "pong", "id": 77, "ts": 123}, f
                break
        c.send({"t": "wat", "id": 5})
        for _ in range(10):
            f = c.recv()
            if f.get("t") == "error":
                assert f["code"] == "bad_frame" and f["id"] == 5, f
                break
        c.close()
        print("selftest: PASS (vector macs, hello bytes, ready mac, auth_failed, ops, op_failed, unknown_op, push seq 1, pong, bad_frame)")
        return 0
    finally:
        p.terminate()
        p.wait(5)


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    sub = ap.add_subparsers(dest="cmd")
    s = sub.add_parser("serve")
    s.add_argument("--port", type=int, default=0)
    s.add_argument("--dir")
    s.add_argument("--token")
    s.add_argument("--token-file")
    s.add_argument("--fixture-hello", action="store_true")
    s.add_argument("--players", type=int, default=2)
    s.add_argument("--quiet", action="store_true")
    s.add_argument("--fixture", help=f"default: {CAPTURED.relative_to(REPO)} when present, else {SYNTHETIC.relative_to(REPO)}")
    for name in ("call", "watch", "capture"):
        x = sub.add_parser(name)
        x.add_argument("--server")
        x.add_argument("--endpoint")
        x.add_argument("--token-file")
        if name == "call":
            x.add_argument("op")
            x.add_argument("json", nargs="?")
        if name == "watch":
            x.add_argument("--topic", default="state")
            x.add_argument("--count", type=int, default=3)
        if name == "capture":
            x.add_argument("--out", default=str(CAPTURED))
    y = sub.add_parser("synthesize")
    y.add_argument("--from", dest="src", default=None)
    y.add_argument("--out", default=str(SYNTHETIC))
    sub.add_parser("selftest")
    args = ap.parse_args(argv)
    if args.cmd in ("call", "watch", "capture") and not args.server and not (args.endpoint and args.token_file):
        ap.error("give --server DIR, or --endpoint HOST:PORT and --token-file FILE")
    if args.cmd == "serve" or args.cmd is None:
        if args.cmd is None:
            args = ap.parse_args(["serve"] + (argv or sys.argv[1:]))
        try:
            return asyncio.run(serve(args))
        except KeyboardInterrupt:
            return 0
    return {"call": cmd_call, "watch": cmd_watch, "capture": cmd_capture, "synthesize": cmd_synthesize, "selftest": cmd_selftest}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
