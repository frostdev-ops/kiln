import { useEffect, useState, type ReactNode } from "react";
import * as api from "../api/commands";
import { Button, Modal, Splash, errorText } from "../ui";
import { useT } from "../i18n";

export function SnapshotGate({ children }: { children: ReactNode }) {
  const t = useT();
  const [status, setStatus] = useState<api.SnapshotStartup | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Asked until the gate is open, then never again: "ready" is final for the session, and a status read twice a
  // second for as long as the launcher runs is work for nothing.
  useEffect(() => {
    let live = true;
    let timer: ReturnType<typeof setInterval> | undefined;
    const stop = () => { if (timer !== undefined) { clearInterval(timer); timer = undefined; } };
    const read = () => api.snapshotStatus().then(
      (s) => { if (!live) return; setStatus(s); if (s.phase === "ready") stop(); },
      (e) => { if (live) setError(errorText(e)); });
    timer = setInterval(() => void read(), 500);
    void read();
    return () => { live = false; stop(); };
  }, []);
  if (status?.phase === "ready") return <>{children}</>;
  if (status?.phase !== "blocked" && !error) return <Splash done={false} />;
  const go = async () => {
    try { await api.snapshotContinue(); setError(null); setStatus(await api.snapshotStatus()); }
    catch (e) { setError(errorText(e)); }
  };
  return <Modal open locked onClose={() => {}} title={t("shell.snapshotGate.title")} footer={<>
    <Button onClick={() => void api.deckQuit()}>{t("shell.dataFolderGate.quit")}</Button>
    {status?.canContinue && <Button variant="primary" onClick={() => void go()}>{t("shell.snapshotGate.goOn")}</Button>}
  </>}>
    <p>{t("shell.snapshotGate.held")}</p>
    {(status?.reasons ?? []).map((reason) => <p key={reason}>{reason}</p>)}
    {error && <p>{error}</p>}
  </Modal>;
}
