// "Open web panel" (0.2.0-dev.31.12): what Overview says about an open panel (src/pages/servers/webPanel.ts). Each
// server's panel has its own loopback origin through the launcher's gateway, which closes by itself after its last tab.
// (0.2.0-dev.31.19) Or the panel's own address on the internet (Kilnwarden's hosted address), and why it opened on this
// computer when that address failed the launcher's check.
import { expect, test } from "bun:test";
import type { WebPanelInfo } from "../src/api/commands";
import { t } from "../src/i18n";
import { shortFingerprint, webPanelLines, webPanelOf } from "../src/pages/servers/webPanel";

const panel = (p: Partial<WebPanelInfo> = {}): WebPanelInfo => ({
  serverId: "lab", origin: "https://127.88.4.9:41234", fingerprint: "ab".repeat(30) + "cdef", via: "local", isolated: true, idleCloseS: 120, connections: 1,
  public: null, fallback: null, ...p,
});

test("a fingerprint shows its first four and last two bytes", () => {
  expect(shortFingerprint("ab".repeat(30) + "cdef")).toBe("AB:AB:AB:AB…CD:EF");
  expect(shortFingerprint("AB:CD:EF:01:23:45:67:89:" + "00:".repeat(23) + "9A")).toBe("AB:CD:EF:01…00:9A");
  expect(shortFingerprint("abc")).toBe("ABC");
});

test("the panel of this server, not another's", () => {
  const list = [panel({ serverId: "a" }), panel({ serverId: "lab", origin: "https://127.70.1.2:40000" })];
  expect(webPanelOf(list, "lab")?.origin).toBe("https://127.70.1.2:40000");
  expect(webPanelOf(list, "zzz")).toBeNull();
  expect(webPanelOf(undefined, "lab")).toBeNull();
});

test("where it is open, its certificate and when it closes", () => {
  expect(webPanelLines(panel(), t)).toEqual([
    "Web panel open at https://127.88.4.9:41234",
    "Certificate AB:AB:AB:AB…CD:EF",
    "Opened in your browser. It closes by itself 2 minutes after its last tab.",
  ]);
});

test("through SSH, without TLS, and on a system that could not keep the cookies apart", () => {
  const lines = webPanelLines(panel({ via: "ssh", fingerprint: null, isolated: false, origin: "http://127.0.0.1:40001", idleCloseS: 30 }), t);
  expect(lines[0]).toBe("Web panel open at http://127.0.0.1:40001 (through SSH)");
  expect(lines).not.toContainEqual(expect.stringContaining("Certificate"));
  expect(lines[1]).toBe("Opened in your browser. It closes by itself 1 minute after its last tab."); // at least a minute
  expect(lines[2]).toContain("share their sign-in cookies");
});

test("a panel opened at its own address says so, without the gateway's lines", () => {
  const hosted = panel({ via: "hosted", origin: "https://alpha.kiln.nexus", public: "https://alpha.kiln.nexus/", fingerprint: "cd".repeat(32), idleCloseS: 0, connections: 0 });
  expect(webPanelLines(hosted, t)).toEqual([
    "Web panel open at https://alpha.kiln.nexus, your panel's own address",
    "Opened in your browser and signed in with a one-time login.",
  ]);
});

test("when the panel's own address failed the check, it opened on this computer and says why", () => {
  const unreachable = webPanelLines(panel({ fallback: "unreachable" }), t);
  expect(unreachable[0]).toBe("Web panel open at https://127.88.4.9:41234");
  expect(unreachable.at(-1)).toBe("Your panel's own address didn't answer in time, so it opened on this computer.");
  expect(webPanelLines(panel({ fallback: "refused" }), t).at(-1)).toBe(
    "Your panel's own address isn't ready for the launcher's sign-in, so it opened on this computer.",
  );
  expect(webPanelLines(panel(), t).join(" ")).not.toContain("own address");
});
