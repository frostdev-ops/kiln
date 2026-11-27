#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Bot players on a dedicated LAB server, from lab scripts (tools/lab/README.md, "Bot players").

The server needs Kiln with kiln.labbot (policy "any" and labPort in kiln/config/network.json) and the labbot-test mod
(mods/tests/labbot-test), which opens the control channel labbot:control v1 on kiln.net's lab transport. This module
speaks that channel as a lab peer (tools/nettest's codec): no game client, no Steam login, nothing on the game port.

    import sys; sys.path.insert(0, "<worktree>/tools/labbot"); import labbot
    lab = labbot.Lab(port)                  # the lab's labPort
    bot = lab.spawn("Bot Alder")            # join + wait until its player entity exists
    bot.say("/pet give hound")              # a chat line typed by the bot (commands run with the bot as source)
    bot.command("pet list")                 # Commands.Execute as the bot: result and immediate output
    bot.wait_told("Hound")                  # a line the server told the bot after its last say/command
    bot.move_to(x, y, z)                    # teleport its entity
    bot.give("Food_T2_Dog_Bait", 3)         # the game's own inventory add (Items.Give)
    bot.use("Food_T2_Dog_Bait", at=(x, y, z))  # use it as a hotbar use does; a bait is thrown from its eyes towards at
    bot.leave()                             # logout (final save), waits until it is gone

CLI (one op per call, JSON out):
    python3 -I tools/labbot/labbot.py --port P status|list|players
    python3 -I tools/labbot/labbot.py --port P spawn NAME [--group admin|least|index] [--index N] [--timeout S]
    python3 -I tools/labbot/labbot.py --port P join NAME | info ID | wait ID | leave ID [--drop]
    python3 -I tools/labbot/labbot.py --port P say ID TEXT | command ID LINE | move ID X Y Z | position ID | character ID
    python3 -I tools/labbot/labbot.py --port P give ID ITEM [COUNT] | inventory ID | use ID ITEM [X Y Z] | buffs ID
Exit 0 = ok, 1 = the server answered an error (printed), 2 = no answer (is the lab up with labPort and labbot-test?).
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "nettest"))
import nettest as nt  # noqa: E402

CHANNEL, VERSION = "labbot:control", "1"
M_REQUEST, M_REPLY = 0, 1
PHASES = ["Free", "Connecting", "LoggingIn", "Loading", "Spawning", "InGame", "Leaving", "Left", "Failed"]
# kiln.net forgets a lab peer it heard nothing from for 60 s (kLabIdleMs): its later requests reach no channel. So a stay
# asks for the bot's info every KEEPALIVE seconds, and a call after IDLE_RECONNECT seconds without a frame from this
# peer (a script's own pause) says HELLO again first.
KEEPALIVE = 5.0
IDLE_RECONNECT = 45.0
# labbot-test's use waits for the game to take the use (the first-time spawn intro: Busy) at most `wait` seconds, USE_WAIT
# by default; the client waits USE_MARGIN longer than that for the answer.
USE_WAIT = 30.0
USE_MARGIN = 15.0


class LabBotError(Exception):
    """The server answered an error, or a bot failed or left while waited for."""


class NoAnswer(LabBotError):
    """The server did not answer (lab down, no labPort, labbot-test not installed)."""


class _Peer(nt.Client):
    sent = 0.0  # when this peer last sent the server a frame (a request, a PONG)

    def send(self, data):
        self.sent = time.time()
        super().send(data)

    def set_table(self, table):
        self.table = table
        self.channels = {n: i for i, n, v, _ in table if n == CHANNEL and v == VERSION}


class Lab:
    """One lab peer on the server's lab transport, with the control channel."""

    def __init__(self, port, name="labbot", timeout=10.0, verbose=False):
        self.port, self.name, self.timeout, self.verbose = port, name, timeout, verbose
        self.seq = 0
        self.mods = []
        self.peer = self._connect()

    def _connect(self):
        port, timeout = self.port, self.timeout
        channels = [(CHANNEL, VERSION, False)]
        # the server's default policy refuses a peer without the mods its clients must have: ask again with those
        for _ in range(4):
            p = _Peer(port, self.name, self.verbose)
            p.hello(self.mods, channels, {"labbot.client": self.name})
            p.wait(lambda: p.welcome is not None or p.reject is not None, timeout)
            if p.welcome is not None:
                break
            if p.reject is None:
                p.s.close()
                raise NoAnswer(f"no WELCOME from 127.0.0.1:{port} in {timeout:.0f} s (is the lab up, with labPort {port}?)")
            add = [(d.split()[0], d.split()[1].lstrip("v"), 2) for d in p.reject[1]
                   if len(d.split()) >= 2 and not d.startswith("channel ") and "(missing)" in d]
            if not add:
                p.s.close()
                raise LabBotError(f"the server refused the lab peer: {p.reject[0]}: {p.reject[1]}")
            self.mods += [m for m in add if m not in self.mods]
            p.s.close()
        else:
            raise LabBotError("the server kept refusing the lab peer")
        if CHANNEL not in p.channels:
            p.s.close()
            raise LabBotError(f"the server has no {CHANNEL} v{VERSION} (is mods/tests/labbot-test installed?)")
        return p

    def close(self):
        self.peer.s.close()

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()

    def call(self, op, timeout=None, **args):
        """One request; the reply's JSON. Raises LabBotError when it is not ok."""
        if time.time() - self.peer.sent > IDLE_RECONNECT:  # kiln.net may have forgotten this peer: a new one
            self.peer.s.close()
            self.peer = self._connect()
        self.seq += 1
        seq = self.seq
        self.peer.data(CHANNEL, M_REQUEST, nt.W().string(json.dumps(dict(args, op=op, seq=seq))).b)
        end = time.time() + (timeout or self.timeout)
        cid = self.peer.channels[CHANNEL]
        while time.time() < end:
            rep, keep = None, []
            for c, m, body in self.peer.messages:
                if c != cid or m != M_REPLY:
                    keep.append((c, m, body))
                    continue
                r = json.loads(nt.R(body).string())
                if r.get("seq") == seq:
                    rep = r
                elif r.get("seq", 0) > seq:
                    keep.append((c, m, body))
                # else: the reply to an earlier request that gave up waiting: dropped
            self.peer.messages = keep
            if rep is not None:
                if not rep.get("ok"):
                    raise LabBotError(f"{op}: {rep.get('error')}")
                return rep
            self.peer.pump(0.05)
        raise NoAnswer(f"{op}: no reply in {timeout or self.timeout:.0f} s")

    # ---- the server ----
    def status(self):
        return self.call("status")

    def players(self):
        return self.call("players")["players"]

    def bots(self):
        return self.call("list")["bots"]

    # ---- bots ----
    def join(self, name, group="admin", index=0):
        """Starts a join; the Bot is not in game yet (wait_ready)."""
        return Bot(self, self.call("join", name=name, group=group, index=index)["id"], name)

    def spawn(self, name, group="admin", index=0, timeout=90.0):
        """join + wait_ready: a bot with its player entity in the world."""
        b = self.join(name, group, index)
        b.wait_ready(timeout)
        return b

    def bot(self, bot_id):
        info = self.call("info", id=bot_id)["bot"]
        return Bot(self, bot_id, info["name"])

    # ---- probes (labbot-test's lab helpers; the template and component names are the game's) ----
    def spawn_entity(self, template, x, y, z, yaw=0, timeout=20.0):
        """Spawns a game template (transient: never saved), e.g. a wild animal next to a bot; returns its entity."""
        return self.call("spawn", timeout=timeout, template=template, x=x, y=y, z=z, yaw=yaw)["entity"]

    def near(self, x, y, z, radius, component="PickupItem"):
        """Entities with `component` within `radius` metres, nearest first: [{"entity", "template", "name", "position",
        "distance"}]. A placed or thrown item (an animal bait) is a PickupItem."""
        return self.call("near", x=x, y=y, z=z, radius=radius, component=component)["entities"]

    def component(self, entity, name):
        """The entity's component `name` as bytes (at most 1024), or None when it has none."""
        r = self.call("component", entity=entity, name=name)
        return bytes.fromhex(r["hex"]) if r.get("has") else None

    def destroy(self, entity):
        return self.call("destroy", entity=entity)["destroyed"]


class Bot:
    def __init__(self, lab, bot_id, name):
        self.lab, self.id, self.name = lab, bot_id, name
        self.last = None
        self.told_since = 0  # the newest server chat line before the bot's last say/command (wait_told's start)

    def __repr__(self):
        return f"Bot({self.id}, {self.name!r}, {self.last['phase'] if self.last else '?'})"

    def info(self):
        self.last = self.lab.call("info", id=self.id)["bot"]
        return self.last

    @property
    def entity(self):
        return (self.last or self.info())["entity"]

    def wait_phase(self, phases, timeout=90.0, poll=0.25):
        """Polls until the bot's phase is one of `phases`; raises when it ended in another or the time is up."""
        end = time.time() + timeout
        while True:
            i = self.info()
            if i["phase"] in phases:
                return i
            if i["phase"] in ("Left", "Failed", "Free"):
                raise LabBotError(f"bot {self.id} '{self.name}' {i['phase'].lower()}: {i['reason']}")
            if time.time() > end:
                raise LabBotError(f"bot {self.id} '{self.name}' still {i['phase']} after {timeout:.0f} s: {i['reason']}")
            time.sleep(poll)

    def wait_ready(self, timeout=90.0):
        """Until the bot is in game (its player entity exists)."""
        return self.wait_phase(("InGame",), timeout)

    def say(self, text):
        """A chat line typed by the bot: chat subscribers and Kiln's commands ('/...') see it as the bot's own.
        The reply's "told" is the newest server line before it: wait_told then waits for a line after it."""
        rep = self.lab.call("say", id=self.id, text=text)
        self.told_since = rep.get("told", self.told_since)
        return rep

    def command(self, line):
        """Kiln's command `line` (no prefix) run as the bot: found, denied, done, error and the immediate output (and
        "told", as for say)."""
        rep = self.lab.call("command", id=self.id, line=line)
        self.told_since = rep.get("told", self.told_since)
        return rep

    def move_to(self, x, y, z, timeout=20.0):
        """Teleports the bot's entity; returns where it is afterwards."""
        return self.lab.call("teleport", timeout=timeout, id=self.id, x=x, y=y, z=z).get("position")

    def position(self):
        return self.lab.call("position", id=self.id)["position"]

    def character(self):
        """The bot's character key (per-character data such as pets is keyed by it)."""
        return self.lab.call("character", id=self.id)["key"]

    def told(self, since=0):
        """The server's chat lines to this bot since line number `since` (a command's replies, "[Server] ..."):
        [{"n", "text", "mask", "connection"}].
        Two lines the server adds in the same step show only the second (labbot-test reads the newest inbox line)."""
        return self.lab.call("told", id=self.id, since=since)["lines"]

    def wait_told(self, pattern, since=None, timeout=20.0):
        """Waits for a line to this bot containing `pattern` (a substring) that the server added after the bot's last
        say or command (or after line `since`); returns it, or raises LabBotError. So a reply to an earlier command
        never answers for the newest one."""
        if since is None:
            since = self.told_since
        end = time.time() + timeout
        while True:
            for ln in self.told(since):
                if pattern in ln["text"]:
                    return ln
            if time.time() > end:
                raise LabBotError(f"bot {self.id} '{self.name}' was not told '{pattern}' in {timeout:.0f} s")
            time.sleep(0.25)

    # ---- using items (since dev.32 labbot-2; docs/re/labbot.md "Using an item") ----
    def give(self, item, count=1, timeout=20.0):
        """Gives the bot `count` x `item` (a name such as "Food_T2_Dog_Bait", or an item id) through the game's own
        inventory add (Items.Give); returns {"status", "done", ...}; raises LabBotError when nothing was given."""
        return self.lab.call("give", timeout=timeout, id=self.id, item=item, count=count)

    def inventory(self):
        """The bot's non-empty slots: [{"slot", "item", "name", "count", "category"}]."""
        return self.lab.call("inventory", id=self.id)["slots"]

    def count(self, item):
        """How many of `item` (a name or an item id) the bot has, over all its slots."""
        return sum(s["count"] for s in self.inventory() if item in (s["name"], s["item"]))

    @staticmethod
    def _look(at=None, yaw=None, pitch=None, eye=None, distance=None):
        o = {}
        if at is not None:
            o["at"] = list(at)
        for k, v in (("yaw", yaw), ("pitch", pitch), ("eye", eye), ("distance", distance)):
            if v is not None:
                o[k] = v
        return o

    def look(self, at=None, yaw=None, pitch=None, eye=None, distance=None):
        """Sets the bot's client camera (LabBots.SetCamera): at its eyes (`eye` metres above its feet, 1.7 by default)
        looking at `at` (x, y, z) or along `yaw` (degrees, 0 = +Z, 90 = +X) and `pitch` (degrees, positive looks down;
        with `at`, it replaces the line of sight's); `distance`: the camera's boom. Returns the camera it set."""
        return self.lab.call("look", id=self.id, **self._look(at, yaw, pitch, eye, distance))

    def ready(self, ready=True):
        """The bot's client says it is ready for gameplay (LabBots.SetReady); use() says so itself. The game then plays a
        new character's first-time spawn intro (Player_Spawn_Initial_FirstTime_PodWakeup, 17.7 s) before it can use an
        item, so a script that will use items calls this right after the join."""
        return self.lab.call("ready", id=self.id, ready=ready)

    def use(self, item=None, slot=None, at=None, yaw=None, pitch=None, eye=None, distance=None, wait=None, timeout=None):
        """Uses `item` (a name or id: the first slot holding it) or the item in `slot` the way the game does for a hotbar
        use (Abilities.UseItem: the item's own sequence with that slot as the used item; the game takes the item when its
        sequence's consume event runs). The bot says it is ready for gameplay first; with `at` or `yaw`/`pitch` its camera
        is set first: a thrown item (an animal bait: 5 m/s up, 10 m/s along the view) starts at its eyes and flies
        towards `at`. The server waits while the game answers NotReady or Busy, at most `wait` seconds (30 by default):
        a bot that just became ready plays the game's first-time spawn intro (17.7 s), so call ready() early. Returns
        {"result": "Started", "slot", "item", "name", "countBefore", "tries", "waited", "look"?}; raises LabBotError with
        the game's answer (NotConsumable, Busy, NotReady, ...) and the bot's states otherwise. `timeout`: how long this
        client waits for the answer, at least `wait` + USE_MARGIN (15) seconds."""
        args = self._look(at, yaw, pitch, eye, distance)
        timeout = max(timeout or 0.0, (USE_WAIT if wait is None else float(wait)) + USE_MARGIN)
        if wait is not None:
            args["wait"] = wait
        if slot is not None:
            args["slot"] = slot
        else:
            args["item"] = item
        return self.lab.call("use", timeout=timeout, id=self.id, **args)

    def buffs(self):
        """The bot's buffs: [{"id", "slot", "level", "seconds"}] (a meal's buff is in a Food slot)."""
        return self.lab.call("buffs", id=self.id)["buffs"]

    def stay(self, seconds):
        """Keeps the bot in the world, doing nothing, for `seconds` (it only moves when moved); returns its info.
        Asks for the bot's info every KEEPALIVE seconds meanwhile, which keeps this lab peer known to kiln.net, and
        raises LabBotError when the bot left or failed during the stay."""
        end = time.time() + seconds
        while True:
            i = self.info()
            if i["phase"] in ("Left", "Failed", "Free"):
                raise LabBotError(f"bot {self.id} '{self.name}' {i['phase'].lower()} during its stay: {i['reason']}")
            left = end - time.time()
            if left <= 0:
                return i
            self.lab.peer.pump(min(KEEPALIVE, left))  # answers the server's pings too

    def leave(self, drop=False, wait=True, timeout=60.0):
        """A logout (the final save, then the game removes the bot) or a drop; waits until it is gone."""
        self.lab.call("leave", id=self.id, drop=drop)
        if not wait:
            return self.info()
        end = time.time() + timeout
        while time.time() < end:
            i = self.info()
            if i["phase"] in ("Left", "Failed", "Free"):
                return i
            time.sleep(0.25)
        raise LabBotError(f"bot {self.id} '{self.name}' did not leave in {timeout:.0f} s ({self.last['phase']})")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--port", type=int, required=True, help="the lab's labPort")
    ap.add_argument("--timeout", type=float, default=90.0)
    ap.add_argument("--group", default="admin")
    ap.add_argument("--index", type=int, default=0)
    ap.add_argument("--drop", action="store_true")
    ap.add_argument("op")
    ap.add_argument("args", nargs="*")
    a = ap.parse_args(argv)
    try:
        lab = Lab(a.port, timeout=min(10.0, a.timeout))
        op, x = a.op, a.args
        if op == "status":
            out = lab.status()
        elif op == "list":
            out = lab.bots()
        elif op == "players":
            out = lab.players()
        elif op in ("join", "spawn"):
            b = lab.join(x[0], a.group, a.index) if op == "join" else lab.spawn(x[0], a.group, a.index, a.timeout)
            out = b.info()
        else:
            b = lab.bot(int(x[0]))
            if op == "info":
                out = b.info()
            elif op == "wait":
                out = b.wait_ready(a.timeout)
            elif op == "leave":
                out = b.leave(a.drop, timeout=a.timeout)
            elif op == "say":
                out = b.say(" ".join(x[1:]))
            elif op == "command":
                out = b.command(" ".join(x[1:]))
            elif op == "move":
                out = b.move_to(float(x[1]), float(x[2]), float(x[3]))
            elif op == "position":
                out = b.position()
            elif op == "character":
                out = b.character()
            elif op == "give":
                out = b.give(x[1], int(x[2]) if len(x) > 2 else 1)
            elif op == "inventory":
                out = b.inventory()
            elif op == "use":
                out = b.use(x[1], at=tuple(float(v) for v in x[2:5]) if len(x) >= 5 else None)
            elif op == "buffs":
                out = b.buffs()
            else:
                ap.error(f"unknown op {op}")
        print(json.dumps(out, indent=1))
        lab.close()
        return 0
    except NoAnswer as e:
        print(f"labbot: {e}", file=sys.stderr)
        return 2
    except LabBotError as e:
        print(f"labbot: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
