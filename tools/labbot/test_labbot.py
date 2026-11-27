#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Host tests of tools/labbot/labbot.py against a fake lab server (kiln.net's lab transport framing, the labbot:control
v1 channel of mods/tests/labbot-test), no game:  python3 -I tools/labbot/test_labbot.py
LABBOT_MODULE_DIR=<dir> tests another copy of labbot.py (the red run)."""
import json
import os
import socket
import sys
import threading
import time
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "nettest"))
sys.path.insert(0, os.environ.get("LABBOT_MODULE_DIR") or HERE)
import labbot  # noqa: E402
import nettest as nt  # noqa: E402

CID = 7


class FakeLab(threading.Thread):
    """Answers HELLO (REJECT naming `required` until a peer lists it), then the control channel's requests with a
    scripted bot: each info moves a joining bot one phase on, until `end_phase`."""

    def __init__(self, required=None, end_phase="InGame", fail_reason="the game found no spawn position", stale=False,
                 channel=True, idle=None):
        super().__init__(daemon=True)
        self.s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.s.bind(("127.0.0.1", 0))
        self.s.settimeout(0.05)
        self.port = self.s.getsockname()[1]
        self.required, self.end_phase, self.fail_reason, self.stale, self.channel = required, end_phase, fail_reason, stale, channel
        # kiln.net forgets a lab peer it heard nothing from for `idle` seconds (kLabIdleMs, 60 s): its later frames come
        # from a peer without a handshake, so no request of it reaches the channel until it says HELLO again
        self.idle, self.heard, self.peers, self.forgotten = idle, {}, set(), 0
        self.hellos, self.requests, self.bots = [], [], {}
        self.stop = False
        self.start()

    def close(self):
        self.stop = True
        self.join(1)
        self.s.close()

    def send(self, addr, w):
        self.s.sendto(bytes(w.b), addr)

    def run(self):
        while not self.stop:
            try:
                b, addr = self.s.recvfrom(70000)
            except (socket.timeout, OSError):
                continue
            if len(b) < 4 or b[:2] != nt.MAGIC:
                continue
            now = time.time()
            if self.idle is not None and addr in self.peers and now - self.heard.get(addr, now) > self.idle:
                self.peers.discard(addr)
                self.forgotten += 1
            self.heard[addr] = now
            r = nt.R(b, 4)
            if b[3] == nt.T_HELLO:
                self.peers.add(addr)
                r.u16(); r.string(); r.u32()
                mods = [(r.string(), r.string(), r.u8()) for _ in range(r.varu())]
                self.hellos.append(mods)
                if self.required and not any(m[0] == self.required[0] for m in mods):
                    w = nt.frame(nt.T_REJECT).string("missing mods").varu(1).string(f"{self.required[0]} {self.required[1]} (missing)").varu(0)
                    self.send(addr, w)
                    continue
                table = [(CID, "labbot:control", "1", False)] if self.channel else []
                w = nt.frame(nt.T_WELCOME).u16(1).string("0.2.0-fake").u32(1).string("refuse").u32(8000).varu(0).varu(len(table))
                for i, n, v, q in table:
                    w.u16(i).string(n).string(v).boolean(q)
                w.varu(0).varu(0)
                self.send(addr, w)
            elif b[3] == nt.T_DATA:
                if addr not in self.peers:
                    continue
                cid, msg = r.u16(), r.u16()
                if cid != CID or msg != labbot.M_REQUEST:
                    continue
                rq = json.loads(nt.R(r.rest()).string())
                self.requests.append(rq)
                if self.stale:  # a reply to an older request first: the client must skip it
                    self.reply(addr, {"seq": rq["seq"] - 1, "ok": True, "stale": True})
                self.reply(addr, dict(self.answer(rq), seq=rq["seq"]))

    def reply(self, addr, o):
        o.setdefault("ok", True)
        self.send(addr, nt.frame(nt.T_DATA).u16(CID).u16(labbot.M_REPLY).raw(nt.W().string(json.dumps(o)).b))

    def bot(self, i):
        return {"id": i, "name": self.bots[i]["name"], "phase": self.bots[i]["phase"], "entity": 16 if self.bots[i]["phase"] == "InGame" else 0,
                "reason": self.bots[i]["reason"], "recordState": 3, "savesAcked": 0}

    def answer(self, rq):
        op = rq["op"]
        if op == "status":
            return {"available": True, "bots": len(self.bots), "groups": 4, "publishes": 9, "why": ""}
        if op == "join":
            if rq["name"] in [b["name"] for b in self.bots.values()]:
                return {"ok": False, "error": "ArgumentException: a live bot has it"}
            i = len(self.bots) + 1
            self.bots[i] = {"name": rq["name"], "phase": "Connecting", "reason": "joining", "group": rq["group"]}
            return {"id": i}
        i = rq.get("id")
        if i not in self.bots:
            return {"ok": False, "error": f"no bot {i}"}
        b = self.bots[i]
        if op == "info":
            order = ["Connecting", "LoggingIn", "Loading", "Spawning", "InGame"]
            if b["phase"] in order and b["phase"] != self.end_phase:
                if self.end_phase == "Failed" and b["phase"] == "Loading":
                    b["phase"], b["reason"] = "Failed", self.fail_reason
                else:
                    b["phase"] = order[order.index(b["phase"]) + 1]
            elif b["phase"] == "Leaving":
                b["phase"], b["reason"] = "Left", "logged out (the game removed the record)"
            return {"bot": self.bot(i)}
        if op == "leave":
            b["phase"] = "Left" if rq.get("drop") else "Leaving"
            return {"id": i}
        if op == "teleport":
            return {"id": i, "position": [rq["x"], rq["y"], rq["z"]]}
        told = getattr(self, "told", [])
        last = max([ln["n"] for ln in told], default=0)
        if op == "told":
            return {"id": i, "lines": [ln for ln in told if ln["n"] > rq.get("since", 0)], "last": last}
        if op in ("say", "command"):  # "told": the newest line number before the bot's line (labbot-test)
            return {"id": i, "echo": rq.get("text") or rq.get("line"), "told": last}
        return {"ok": False, "error": f"unknown op '{op}'"}


class LabBotTests(unittest.TestCase):
    def setUp(self):
        self.fakes = []

    def tearDown(self):
        for f in self.fakes:
            f.close()

    def fake(self, **kw):
        f = FakeLab(**kw)
        self.fakes.append(f)
        return f

    def test_1_handshake_retries_with_the_mods_a_reject_names(self):
        f = self.fake(required=("pets-sample", "0.1.0"))
        lab = labbot.Lab(f.port, timeout=3)
        self.assertEqual(lab.peer.channels.get("labbot:control"), CID)
        self.assertEqual(len(f.hellos), 2)
        self.assertEqual([m[0] for m in f.hellos[1]], ["pets-sample"])

    def test_2_no_control_channel_is_an_error(self):
        f = self.fake(channel=False)
        with self.assertRaisesRegex(labbot.LabBotError, "labbot-test"):
            labbot.Lab(f.port, timeout=3)

    def test_3_no_server_is_no_answer(self):
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
        s.close()
        with self.assertRaises(labbot.NoAnswer):
            labbot.Lab(port, timeout=0.5)

    def test_4_call_matches_its_own_seq_and_raises_on_errors(self):
        f = self.fake(stale=True)
        lab = labbot.Lab(f.port, timeout=3)
        st = lab.status()
        self.assertTrue(st["available"])
        self.assertNotIn("stale", st)
        with self.assertRaisesRegex(labbot.LabBotError, "no bot 99"):
            lab.call("info", id=99)
        self.assertEqual([r["op"] for r in f.requests], ["status", "info"])
        self.assertEqual(f.requests[1]["seq"], f.requests[0]["seq"] + 1)

    def test_5_spawn_waits_until_in_game(self):
        f = self.fake()
        lab = labbot.Lab(f.port, timeout=3)
        b = lab.spawn("Bot Alder", timeout=10)
        self.assertEqual(b.last["phase"], "InGame")
        self.assertEqual(b.entity, 16)
        self.assertEqual(f.bots[1]["group"], "admin")
        self.assertEqual(b.move_to(1.5, 2.5, 3.5), [1.5, 2.5, 3.5])
        self.assertEqual(b.say("/pet give hound")["echo"], "/pet give hound")
        self.assertEqual(f.requests[-1], {"op": "say", "seq": f.requests[-1]["seq"], "id": 1, "text": "/pet give hound"})

    def test_6_a_failed_join_raises_with_the_reason(self):
        f = self.fake(end_phase="Failed")
        lab = labbot.Lab(f.port, timeout=3)
        b = lab.join("Bot Birch", group="least")
        with self.assertRaisesRegex(labbot.LabBotError, "failed: the game found no spawn position"):
            b.wait_ready(timeout=10)
        self.assertEqual(f.bots[1]["group"], "least")

    def test_7_wait_ready_times_out(self):
        f = self.fake(end_phase="Loading")
        lab = labbot.Lab(f.port, timeout=3)
        b = lab.join("Bot Cedar")
        with self.assertRaisesRegex(labbot.LabBotError, "still Loading"):
            b.wait_ready(timeout=0.6)

    def test_8_leave_waits_until_left(self):
        f = self.fake()
        lab = labbot.Lab(f.port, timeout=3)
        b = lab.spawn("Bot Alder", timeout=10)
        i = b.leave(timeout=5)
        self.assertEqual(i["phase"], "Left")
        self.assertIn("logged out", i["reason"])
        d = lab.spawn("Bot Drop", timeout=10)
        self.assertEqual(d.leave(drop=True, timeout=5)["phase"], "Left")
        self.assertTrue([r for r in f.requests if r["op"] == "leave" and r["drop"]])

    def test_10_told_and_wait_told(self):
        f = self.fake()
        f.told = [{"n": 3, "text": "Hound: Alive, Summoned, 100/100", "mask": 0xfffeffff}]
        lab = labbot.Lab(f.port, timeout=3)
        b = lab.spawn("Bot Alder", timeout=10)
        self.assertEqual(b.told()[0]["n"], 3)
        self.assertIn("Summoned", b.wait_told("Summoned", timeout=2)["text"])
        self.assertEqual(f.requests[-1]["since"], 0)
        with self.assertRaisesRegex(labbot.LabBotError, "was not told 'refused'"):
            b.wait_told("refused", timeout=0.5)

    def test_11_a_long_stay_and_an_idle_script_keep_the_lab_peer(self):
        # kiln.net drops a lab peer after 60 s without a frame from it (kLabIdleMs); here after 0.6 s
        f = self.fake(idle=0.6)
        saved = getattr(labbot, "KEEPALIVE", None), getattr(labbot, "IDLE_RECONNECT", None)
        labbot.KEEPALIVE, labbot.IDLE_RECONNECT = 0.2, 0.45
        try:
            lab = labbot.Lab(f.port, timeout=1)
            b = lab.spawn("Bot Alder", timeout=10)
            self.assertEqual(b.stay(2.0)["phase"], "InGame")  # a stay several times the idle limit
            self.assertEqual(f.forgotten, 0, "stay kept the lab peer")
            time.sleep(1.0)  # the script's own pause, longer than the idle limit: the next call says HELLO again
            self.assertEqual(b.info()["phase"], "InGame")
            self.assertEqual(len(f.hellos), 2)
            # a reply that came after its request gave up is dropped when the next reply is read, not kept
            f.stale = True
            lab.status()
            lab.status()
            self.assertEqual(lab.peer.messages, [])
        finally:
            labbot.KEEPALIVE, labbot.IDLE_RECONNECT = saved

    def test_12_wait_told_waits_for_a_reply_to_the_bots_last_line(self):
        f = self.fake()
        f.told = [{"n": 3, "text": "Hound: Ready, Absent", "mask": 0xfffeffff}]  # a reply to an earlier command
        lab = labbot.Lab(f.port, timeout=3)
        b = lab.spawn("Bot Alder", timeout=10)
        self.assertEqual(b.say("/pet summon 1")["told"], 3)
        with self.assertRaisesRegex(labbot.LabBotError, "was not told 'Hound'"):
            b.wait_told("Hound", timeout=0.5)  # line 3 came before the bot's line
        f.told.append({"n": 4, "text": "Hound: Summoned", "mask": 0xfffeffff})
        self.assertEqual(b.wait_told("Hound", timeout=2)["n"], 4)
        self.assertEqual(b.wait_told("Hound", since=0, timeout=2)["n"], 3)  # since= still picks the start
        b.command("pet list")
        with self.assertRaisesRegex(labbot.LabBotError, "was not told"):
            b.wait_told("Summoned", timeout=0.5)  # the cursor moved with the command

    def test_9_cli_exit_codes(self):
        f = self.fake()
        self.assertEqual(labbot.main(["--port", str(f.port), "status"]), 0)
        self.assertEqual(labbot.main(["--port", str(f.port), "info", "5"]), 1)
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
        s.close()
        self.assertEqual(labbot.main(["--port", str(port), "--timeout", "1", "status"]), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
