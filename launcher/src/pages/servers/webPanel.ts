// "Open web panel" on a server's Overview (0.2.0-dev.31.12): what the window says about an open panel. Each server's
// panel has its own loopback origin (https://127.x.y.z:port) through the launcher's gateway, which closes by itself
// after the panel's last tab (kiln_manage::admin::web_panel). (0.2.0-dev.31.19) Or the panel's own address on the
// internet (Kilnwarden's hosted address), when its file names one that passes the launcher's check; when that check
// fails, it opened on this computer and says why. (0.2.0-dev.31.20) The server's "Always open the web panel on this
// computer" setting (web-panel-settings.json, kiln_manage::admin::web_panel::settings) keeps it on this computer. Pure,
// for the unit tests.
import type { WebPanelInfo, WebPanelSettings } from "../../api/commands";

/** The open panel of `serverId`, if any. */
export const webPanelOf = (list: WebPanelInfo[] | undefined, serverId: string) => list?.find((p) => p.serverId === serverId) ?? null;

/** A certificate fingerprint as people compare them: AB:CD:EF:01…9A:BC (the first four bytes and the last two). */
export function shortFingerprint(hex: string): string {
  const h = hex.replace(/[^0-9a-fA-F]/g, "").toUpperCase();
  if (h.length < 12) return h;
  const pairs = (s: string) => s.match(/../g)!.join(":");
  return `${pairs(h.slice(0, 8))}…${pairs(h.slice(-4))}`;
}

/** (0.2.0-dev.31.20) Whether the server's "Always open the web panel on this computer" is on (off until the launcher
 *  has said). */
export const alwaysLocal = (s: WebPanelSettings | undefined) => s?.alwaysLocal === true;

/** (0.2.0-dev.31.20) "Open on this computer instead": after an open at the panel's own address, unless the server always
 *  opens on this computer (then Open web panel itself does). */
export const offerHere = (last: WebPanelInfo | null, always: boolean) => !always && last?.via === "hosted";

type T = (key: string, vars?: Record<string, string | number>) => string;

/** The status lines under the buttons: where it is open (and how), the fingerprint, when it closes, a warning when
 *  this system could not keep the server's cookies apart. */
export function webPanelLines(p: WebPanelInfo, t: T): string[] {
  // the panel's own address: the browser went there by itself, so no gateway, certificate or closing time to tell
  if (p.via === "hosted") return [t("servers.overview.webPanelHosted", { origin: p.origin }), t("servers.overview.webPanelHostedNote")];
  const where = t("servers.overview.webPanelOpen", { origin: p.origin }) + (p.via === "ssh" ? ` (${t("servers.overview.webPanelViaSsh")})` : "");
  const lines = [where];
  if (p.fingerprint) lines.push(t("servers.overview.webPanelFingerprint", { fingerprint: shortFingerprint(p.fingerprint) }));
  lines.push(t("servers.overview.webPanelNote", { count: Math.max(1, Math.round(p.idleCloseS / 60)) }));
  if (!p.isolated) lines.push(t("servers.overview.webPanelShared"));
  if (p.fallback) lines.push(t(p.fallback === "unreachable" ? "servers.overview.webPanelFallbackUnreachable" : "servers.overview.webPanelFallbackRefused"));
  return lines;
}
