// Getting a catalog mod into the game, shared by the desktop Catalog page, the Deck Catalog screen and the nxm link
// listeners (docs/kiln/launcher/catalog.md):
//   premium account  nexus_download: download, check, import, add to the active profile (Undo: profile_revert)
//   free account     the file's page on Nexus Mods opens (Nexus's rule: free downloads go through the site); its
//                    "Mod Manager Download" sends an nxm link back here when Settings has that on
//   nxm link         nexus_link_install, the same as premium with the link's key
// Updating is the same Add on the newer file, only ever on the user's click.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef } from "react";
import { useNavigate } from "react-router-dom";
import * as api from "../api/commands";
import type { GameStatus } from "../api/commands";
import { invalidateGame, qk, useGame, useGameStatus } from "../api/hooks";
import * as nx from "../api/nexus";
import type { CatalogFile, InstallReport, NexusAccount } from "../api/nexus";
import { runTask, toast } from "../ui";

export const useNexusAccount = () => useQuery({ queryKey: nx.nk.account, queryFn: nx.nexusAccount, staleTime: 60_000, retry: false });

/** The requirements worth offering: on Nexus, for Enshrouded, not Kiln, not installed. */
export const offered = (r: InstallReport) => r.requirements.filter((q) => !q.kiln && !q.otherGame && !q.installed);

// A UI switch can unmount the listener while its task is running. Deliver the result to the current UI, or keep
// it until that UI mounts, so the install's Undo and requirements are not lost.
const installingLinks = new Set<string>();
const waitingReports: InstallReport[] = [];
let reportListener: ((r: InstallReport) => void) | undefined;
const reportLink = (r: InstallReport) => reportListener ? reportListener(r) : waitingReports.push(r);

export type AddOutcome = { kind: "added"; report: InstallReport } | { kind: "opened"; url: string } | { kind: "none" };

export function useCatalogInstall() {
  const qc = useQueryClient();
  const { game } = useGame();
  const ref = useRef(game);
  ref.current = game;

  const refresh = (r?: InstallReport | null) => {
    for (const k of [["library_list"], ["profile_resolve"], qk.profiles, nx.nk.installed, ["nexus_search"], ["nexus_mod"]]) qc.invalidateQueries({ queryKey: k });
    if (r?.add.profileId) qc.invalidateQueries({ queryKey: qk.profile(r.add.profileId) });
    if (ref.current) invalidateGame(qc, ref.current.id);
  };

  /** Why nothing can be added now (null: ready). */
  const blocked = (): string | null => {
    const g = ref.current;
    if (!g) return "Find Enshrouded on Play first.";
    const st = qc.getQueryData<GameStatus>(qk.gameStatus(g.id));
    if (st && !st.runtime.installed) return "Install Kiln first.";
    return null;
  };

  const undo = async (r: InstallReport) => {
    if (!r.add.profileId || !r.add.changes.length) return;
    try {
      await api.profileRevert(r.add.profileId, r.add.changes);
      refresh(r);
      toast.ok(`Removed ${r.name}`);
    } catch (e) { toast.error(e, "Undo failed"); }
  };

  /** Add (or update to) `file` of a mod: premium downloads here, free opens the file's page. */
  const add = async (mod: { id: string; name: string }, file: CatalogFile | null, account: NexusAccount | undefined, filesUrl?: string): Promise<AddOutcome> => {
    const pageUrl = filesUrl ?? `https://www.nexusmods.com/enshrouded/mods/${mod.id}?tab=files${file ? `&file_id=${file.versionGsid}&nmm=1` : ""}`;
    if (!account?.premium || !file) {
      try { await api.appOpenUrl(pageUrl); } catch (e) { toast.error(e); return { kind: "none" }; }
      return { kind: "opened", url: pageUrl };
    }
    const why = blocked();
    if (why) { toast.info(why); return { kind: "none" }; }
    try {
      const r = await runTask<InstallReport>(() => nx.nexusDownload(ref.current!.id, mod.id, file.versionGsid), { failed: "Not added" });
      refresh(r);
      return { kind: "added", report: r };
    } catch (e) {
      const k = api.toKilnError(e);
      const url = (k.detail as { url?: string } | undefined)?.url;
      if (k.code === "refused" && url) { void api.appOpenUrl(url).catch(() => {}); return { kind: "opened", url }; }
      return { kind: "none" };
    }
  };

  /** A Mod Manager Download link that arrived. */
  const installLink = async (linkId: string): Promise<InstallReport | null> => {
    const g = ref.current;
    if (!g) return null;
    try {
      const r = await runTask<InstallReport>(() => nx.nexusLinkInstall(g.id, linkId), { failed: "Not added" });
      refresh(r);
      return r;
    } catch { refresh(null); return null; }
  };

  return { add, installLink, undo, blocked, refresh };
}

/** The desktop toast for an added mod: its name, Undo, and "Get <requirement>" for what it needs from Nexus Mods. */
export function useReportToast() {
  const nav = useNavigate();
  const { undo } = useCatalogInstall();
  return (r: InstallReport, update = false) => {
    const need = offered(r);
    const more = [
      need.length ? `Needs ${need.map((q) => q.name).join(", ")}.` : r.add.missing.length ? `Needs ${r.add.missing.map((m) => m.id).join(", ")}.` : "",
      r.add.running ? "Applies at the next start." : "",
      !r.add.profileId && (r.add.import.imported.length || r.add.import.existing.length) ? "No profile is active." : "",
    ].filter(Boolean).join(" ");
    const actions = [
      ...(r.add.changes.length ? [{ label: "Undo", onClick: () => void undo(r) }] : []),
      ...need.slice(0, 2).map((q) => ({ label: `Get ${q.name}`, onClick: () => nav(`/catalog?mod=${q.modId}`) })),
    ];
    if (r.add.added.length) toast.ok(`${update ? "Updated" : "Added"} ${r.name}`, more || undefined, { actions });
    else if (r.add.already.length) toast.info(`${r.name} is in ${r.add.profileName ?? "the profile"} already`, more || undefined, { actions });
    else toast.info(`${r.name} is in the library`, more || undefined, { actions });
  };
}

/** Takes the nxm links that arrive (`kiln://nxm`) and adds each; `onReport` shows the outcome (a toast, a dialog). */
export function useNxmLinks(onReport: (r: InstallReport) => void) {
  const { installLink, blocked } = useCatalogInstall();
  const { game } = useGame();
  const status = useGameStatus(game?.id);
  const cb = useRef(onReport);
  cb.current = onReport;
  useEffect(() => {
    const listener = (r: InstallReport) => cb.current(r);
    reportListener = listener;
    waitingReports.splice(0).forEach(listener);
    return () => { if (reportListener === listener) reportListener = undefined; };
  }, []);
  useEffect(() => {
    let live = true;
    const take = async () => {
      let links: nx.PendingLink[] = [];
      try { links = await nx.nexusLinks(); } catch { return; }
      for (const l of links) {
        if (!live || installingLinks.has(l.id)) continue;
        const why = blocked();
        if (why) { toast.info("A Nexus Mods download is waiting", why); return; }
        // The first inbox read can beat game discovery/status. A readiness change runs take again below.
        if (!status.data?.runtime.installed) return;
        installingLinks.add(l.id);
        const r = await installLink(l.id);
        installingLinks.delete(l.id);
        if (r) reportLink(r);
      }
    };
    let off: (() => void) | undefined;
    // Subscribe before reading the inbox so a link arriving between the read and registration is not missed.
    api.onKilnEvent("kiln://nxm", () => void take()).then((f) => {
      if (!live) { f(); return; }
      off = f;
      void take();
    }).catch(() => { if (live) void take(); });
    return () => { live = false; off?.(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [game?.id, status.data?.runtime.installed]);
}

/** Desktop: mounted once in the shell. */
export function NxmListener() {
  const show = useReportToast();
  useNxmLinks((r) => show(r));
  return null;
}
