// "Restore the game's data" (eml_data_plan / _apply / _undo / _latest, dev.29): the way out when another loader left
// archive files and backups in the game folder that Kiln's EML writer cannot prove it wrote, and every EML mod fails.
// The player verifies the game files in Steam, the launcher moves the leftover files into a dated folder under
// kiln/eml/displaced/ (never deleting one) and can move them back. The list of files comes from what the writer read
// at the last start; when Steam's check changed the game's index since, the plan says to start once more first.
// docs/kiln/engines.md, "Restore the game's data".
import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import * as api from "../api/commands";
import { t, useT } from "../i18n";
import { Banner, Button, Modal, errorText } from "../ui";

export function RestoreDataOffer({ gameId }: { gameId: api.GameId }) {
  const t = useT();
  const TITLE = t("restoreDataOffer.title");
  const plan = useQuery({ queryKey: ["eml_data_plan", gameId], queryFn: () => api.emlDataPlan(gameId), retry: false, refetchOnWindowFocus: false });
  // the key of EngineStatus.tsx's engineStatusKey (not imported: that file imports this one)
  const engines = useQuery({ queryKey: ["engine_status", gameId] as const, queryFn: () => api.engineStatus(gameId) });
  const latest = useQuery({ queryKey: ["eml_data_latest", gameId], queryFn: () => api.emlDataLatest(gameId), enabled: !plan.isFetching, retry: false });
  const [open, setOpen] = useState(false);
  const [verified, setVerified] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [result, setMoved] = useState<api.DataRestoreMoved | null | undefined>(undefined);
  const moved = result === null ? null : result ?? latest.data;
  const action = async (undo: boolean) => {
    setBusy(true); setError("");
    try {
      if (undo && moved) { await api.emlDataUndo(gameId, moved.folder); setMoved(null); await plan.refetch(); }
      else if (plan.data && verified) { setMoved(await api.emlDataApply(gameId, plan.data.fingerprint, verified)); setOpen(false); }
    } catch (e) { setVerified(false); setError(errorText(e)); void plan.refetch(); } finally { setBusy(false); }
  };
  if (!plan.data && !moved) {
    // no plan: say why only when the last start reported unproven data (the index changed since, the game runs, …)
    const state = engines.data?.find((e) => e.kind === "eml")?.state;
    if (plan.error && hasUnprovenRecovery(state)) return <Banner tone="warn" title={TITLE}>{errorText(plan.error)}</Banner>;
    return null;
  }
  return <>
    <Banner tone="warn" title={TITLE}>
      {moved ? <><p>{t("restoreDataOffer.moved", { folder: moved.folder })}</p><ul>{moved.files.map((f) => <li key={f.from}>{f.from}</li>)}</ul>
        <p>{t("restoreDataOffer.freshBackup")}</p>
        <Button disabled={busy} onClick={() => void action(true)}>{t("restoreDataOffer.undo")}</Button></>
        : <><p>{t("restoreDataOffer.offer")}</p>
          <Button onClick={() => { setVerified(false); setError(""); setOpen(true); }}>{TITLE}</Button></>}
      {error && <p role="alert">{error}</p>}
    </Banner>
    <Modal open={open} onClose={() => setOpen(false)} locked={busy} title={TITLE} footer={<>
      <Button variant="ghost" disabled={busy} onClick={() => setOpen(false)}>{t("common.cancel")}</Button>
      <Button disabled={!verified || busy} onClick={() => void action(false)}>{t("restoreDataOffer.moveAside")}</Button></>}>
      <p>{t("restoreDataOffer.explain")}</p>
      <p>{t("restoreDataOffer.repaired")}</p>
      <ul>{plan.data?.files.map((f) => <li key={f.from}>{f.from} ({size(f.size)})</li>)}</ul>
      <label><input type="checkbox" checked={verified} onChange={(e) => setVerified(e.target.checked)} />{" "}{t("restoreDataOffer.verified")}</label>
      {error && <p role="alert">{error}</p>}
    </Modal>
  </>;
}

const size = (bytes: number) => bytes >= 1024 * 1024 ? t("restoreDataOffer.mb", { size: (bytes / (1024 * 1024)).toFixed(1) }) : t("restoreDataOffer.kb", { size: Math.max(1, Math.round(bytes / 1024)) });

/** The last start's EML state says the writer found game data it cannot prove it wrote (`recovery.unproven`). */
export function hasUnprovenRecovery(state: unknown): boolean {
  if (!state || typeof state !== "object" || !("recovery" in state)) return false;
  const r = state.recovery;
  return !!r && typeof r === "object" && "unproven" in r && r.unproven === true;
}
