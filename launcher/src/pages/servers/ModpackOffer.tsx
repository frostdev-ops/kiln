// The modpack consent modal. Play opens it from the refusal banner ("Get modpack") with the
// game's last-server.json. Nothing downloads before the user ticks the box and presses Install: the backend command
// takes the explicit consent flag. https only unless Settings allow http.
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, ShieldAlert } from "lucide-react";
import { useState } from "react";
import * as api from "../../api/commands";
import type { GameId, LastServer, ModpackInstallResult } from "../../api/commands";
import { qk, useSettings } from "../../api/hooks";
import { Banner, Button, KeyValue, Modal, Skeleton, Toggle, errorText, fmtBytes, runTask, shortHash, toast } from "../../ui";
import { notifyActivation, notifyActivationOf } from "../../shell/notices";
import { cookBeforePlay, cookingNotice } from "../../shell/cook";

export default function ModpackOffer({ gameId, lastServer, open, onClose }: { gameId: GameId; lastServer: LastServer; open: boolean; onClose: () => void }) {
  const qc = useQueryClient();
  const settings = useSettings();
  const pack = lastServer.modpack;
  const preview = useQuery({ queryKey: ["modpack_inspect", gameId, pack?.sha256], queryFn: () => api.modpackInspect(gameId, null), enabled: open && !!pack, retry: false });
  const [agree, setAgree] = useState(false);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<ModpackInstallResult | null>(null);
  const http = !!pack && /^http:\/\//i.test(pack.url);
  const httpOk = !http || !!settings.data?.allowHttpModpacks;
  const name = lastServer.server.name ?? lastServer.server.id ?? "this server";
  const install = async () => {
    if (!pack) return;
    setBusy(true);
    try {
      const r = await runTask<ModpackInstallResult>(
        () => api.modpackInstall(gameId, { source: { url: pack.url, sha256: pack.sha256 }, consent: true, allowHttp: http, activate: true }),
        { done: "Modpack installed" },
      );
      setDone(r);
      notifyActivation(r.activation);
      qc.invalidateQueries({ queryKey: qk.profiles });
      qc.invalidateQueries({ queryKey: qk.active(gameId) });
      qc.invalidateQueries({ queryKey: qk.pending(gameId) });
    } catch { /* toasted */ } finally {
      setBusy(false);
    }
  };
  const dismiss = async () => {
    try { if (lastServer.server.id) await api.modpackDismiss(gameId, lastServer.server.id); } catch { /* not important */ }
    qc.invalidateQueries({ queryKey: qk.pending(gameId) });
    onClose();
  };
  // Launch starts the active profile (the modpack's, activated by the install) as Play does: its cook first (which writes
  // the active profile again when it changed, then the boot game-data step for OnBoot mods; shell/cook.ts), then the start
  const launch = async () => {
    setBusy(true);
    try {
      await cookBeforePlay(gameId, cookingNotice);
      const r = await api.launchPlay(gameId, { mode: "profile", profileId: done?.profile.id });
      notifyActivation(r.activation);
      onClose();
    } catch (e) {
      notifyActivationOf(e);
      toast.error(e, "Launch failed");
    } finally {
      setBusy(false);
    }
  };
  const p = preview.data;
  return (
    <Modal open={open} onClose={onClose} locked={busy} width={620} title={done ? "Modpack installed" : `${name} uses mods`}
      footer={done ? (
        <><Button variant="ghost" onClick={onClose} disabled={busy}>Close</Button><Button variant="molten" loading={busy} onClick={launch}>Launch</Button></>
      ) : (
        <><Button variant="ghost" onClick={dismiss} disabled={busy}>Not now</Button>
          <Button icon={<Download size={16} />} variant="primary" disabled={!pack || !agree || !httpOk || !!preview.error} loading={busy} onClick={install}>Download and install</Button></>
      )}>
      {done ? (
        <div className="k-stack">
          <KeyValue rows={[
            ["Profile", done.profile.name],
            ["Mods", `${done.imported.length} new in the library`],
            done.backup && ["Save backup", done.backup.id],
          ]} />
          <div className="k-hint">{done.profile.name} is loaded: Play starts the game with these mods, then join {name} again.</div>
        </div>
      ) : !pack ? (
        <Banner tone="info" title="No modpack">{name} did not say where to get its mods. Ask its admin.</Banner>
      ) : (
        <div className="k-stack">
          {lastServer.outcome === "rejected" && <Banner tone="warn" title="The server refused the join">{lastServer.reason ?? "Mods are missing."}</Banner>}
          <KeyValue rows={[
            ["Server", `${name}${lastServer.server.id ? ` (${lastServer.server.id})` : ""}`],
            ["Modpack", `${pack.name ?? "-"} ${pack.version ?? ""}`],
            ["From", <span className="k-mono">{pack.url}</span>],
            ["Size", pack.size != null ? fmtBytes(pack.size) : "unknown"],
            ["sha256", <span className="k-mono">{shortHash(pack.sha256)}</span>],
          ]} />
          {preview.isLoading ? <Skeleton lines={2} /> : preview.error ? <Banner tone="err" title="Cannot check it">{errorText(preview.error)}</Banner> : p && (
            <div className="k-hint">
              {p.missing.length ? `Missing here: ${p.missing.map((m) => `${m.name ?? m.id} ${m.version}`).join(", ")}.` : "Everything it lists is in your library already."}
              {p.nativeCode && " It contains native code (DLLs)."}
            </div>
          )}
          {!httpOk && <Banner tone="err" title="Not https">This modpack comes over plain http. Allow http modpacks in Settings to take it.</Banner>}
          <Banner tone="warn" title="This runs code on your PC">
            <span className="k-row" style={{ gap: 8 }}><ShieldAlert size={16} />Mods are programs. Install only from servers you trust. The download is checked against the hashes the server sent.</span>
          </Banner>
          <Toggle checked={agree} onChange={setAgree} label={`I trust ${name} and want its mods`} />
        </div>
      )}
    </Modal>
  );
}
