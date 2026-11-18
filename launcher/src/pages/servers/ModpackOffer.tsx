// The modpack consent modal. Play opens it from the refusal banner ("Get modpack") with the
// game's last-server.json. Nothing downloads before the user ticks the box and presses Install: the backend command
// takes the explicit consent flag. https only unless Settings allow http.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, ShieldAlert } from "lucide-react";
import { useState } from "react";
import * as api from "../../api/commands";
import type { GameId, LastServer, ModpackInstallResult } from "../../api/commands";
import { qk, useSettings } from "../../api/hooks";
import { Banner, Button, KeyValue, Modal, Skeleton, Toggle, errorText, fmtBytes, runTask, shortHash, toast } from "../../ui";
import { notifyActivation, notifyActivationOf } from "../../shell/notices";
import { cookBeforePlay, cookingNotice } from "../../shell/cook";
import { rememberLaunch } from "../../shell/play";
import { useT } from "../../i18n";

export default function ModpackOffer({ gameId, lastServer, open, onClose }: { gameId: GameId; lastServer: LastServer; open: boolean; onClose: () => void }) {
  const t = useT();
  const qc = useQueryClient();
  const settings = useSettings();
  const pack = lastServer.modpack;
  const preview = useQuery({ queryKey: ["modpack_inspect", gameId, pack?.sha256], queryFn: () => api.modpackInspect(gameId, null), enabled: open && !!pack, retry: false });
  const [agree, setAgree] = useState(false);
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<ModpackInstallResult | null>(null);
  const http = !!pack && /^http:\/\//i.test(pack.url);
  const httpOk = !http || !!settings.data?.allowHttpModpacks;
  const name = lastServer.server.name ?? lastServer.server.id ?? t("servers.modpackOffer.thisServer");
  const install = async () => {
    if (!pack) return;
    setBusy(true);
    try {
      const r = await runTask<ModpackInstallResult>(
        () => api.modpackInstall(gameId, { source: { url: pack.url, sha256: pack.sha256 }, consent: true, allowHttp: http, activate: true }),
        { done: t("servers.modpackOffer.installed") },
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
  // Launch starts the active profile (the modpack's, activated by the install) as Play does: the start is timed from
  // here (launch_begin, so its cook and its boot game-data step are steps of the start's bar), its cook first (which
  // writes the active profile again when it changed, then the boot game-data step for OnBoot mods; shell/cook.ts),
  // then the start. As Play's own start ("launch_play"), so the Play surfaces follow it meanwhile.
  const start = useMutation({
    mutationKey: ["launch_play"],
    mutationFn: async () => {
      await api.launchBegin(gameId, "profile").catch(() => {});
      void qc.invalidateQueries({ queryKey: ["launch_progress", gameId] });
      await cookBeforePlay(gameId, cookingNotice);
      return api.launchPlay(gameId, { mode: "profile", profileId: done?.profile.id });
    },
  });
  const launch = async () => {
    setBusy(true);
    try {
      const r = await start.mutateAsync();
      rememberLaunch(gameId, r.mode);
      notifyActivation(r.activation);
      onClose();
    } catch (e) {
      // the start ended before the game existed: the launcher stops timing it
      void api.launchEnd(gameId).catch(() => {});
      notifyActivationOf(e);
      toast.error(e, t("servers.modpackOffer.launchFailed"));
    } finally {
      setBusy(false);
    }
  };
  const p = preview.data;
  return (
    <Modal open={open} onClose={onClose} locked={busy} width={620} title={done ? t("servers.modpackOffer.installed") : t("servers.modpackOffer.usesMods", { name })}
      footer={done ? (
        <><Button variant="ghost" onClick={onClose} disabled={busy}>{t("common.close")}</Button><Button variant="molten" loading={busy} onClick={launch}>{t("servers.modpackOffer.launch")}</Button></>
      ) : (
        <><Button variant="ghost" onClick={dismiss} disabled={busy}>{t("servers.modpackOffer.notNow")}</Button>
          <Button icon={<Download size={16} />} variant="primary" disabled={!pack || !agree || !httpOk || !!preview.error} loading={busy} onClick={install}>{t("servers.modpackOffer.downloadInstall")}</Button></>
      )}>
      {done ? (
        <div className="k-stack">
          <KeyValue rows={[
            [t("servers.modpackOffer.profile"), done.profile.name],
            [t("servers.modpackOffer.mods"), t("servers.modpackOffer.newInLibrary", { n: done.imported.length })],
            done.backup && [t("servers.modpackOffer.saveBackup"), done.backup.id],
          ]} />
          <div className="k-hint">{t("servers.modpackOffer.loadedHint", { profile: done.profile.name, name })}</div>
        </div>
      ) : !pack ? (
        <Banner tone="info" title={t("servers.modpackOffer.noPack")}>{t("servers.modpackOffer.noPackText", { name })}</Banner>
      ) : (
        <div className="k-stack">
          {lastServer.outcome === "rejected" && <Banner tone="warn" title={t("servers.modpackOffer.refused")}>{lastServer.reason ?? t("servers.modpackOffer.modsMissing")}</Banner>}
          <KeyValue rows={[
            [t("servers.modpackOffer.server"), lastServer.server.id ? t("servers.modpackOffer.serverWithId", { name, id: lastServer.server.id }) : name],
            [t("servers.modpackOffer.modpack"), `${pack.name ?? "-"} ${pack.version ?? ""}`],
            [t("servers.modpackOffer.from"), <span className="k-mono">{pack.url}</span>],
            [t("servers.modpackOffer.size"), pack.size != null ? fmtBytes(pack.size) : t("servers.modpackOffer.unknown")],
            ["sha256", <span className="k-mono">{shortHash(pack.sha256)}</span>],
          ]} />
          {preview.isLoading ? <Skeleton lines={2} /> : preview.error ? <Banner tone="err" title={t("servers.modpackOffer.cannotCheck")}>{errorText(preview.error)}</Banner> : p && (
            <div className="k-hint">
              {p.missing.length ? t("servers.modpackOffer.missingHere", { list: p.missing.map((m) => `${m.name ?? m.id} ${m.version}`).join(", ") }) : t("servers.modpackOffer.allThere")}
              {p.nativeCode && ` ${t("servers.modpackOffer.nativeCode")}`}
            </div>
          )}
          {!httpOk && <Banner tone="err" title={t("servers.modpackOffer.notHttps")}>{t("servers.modpackOffer.notHttpsText")}</Banner>}
          <Banner tone="warn" title={t("servers.modpackOffer.runsCode")}>
            <span className="k-row" style={{ gap: 8 }}><ShieldAlert size={16} />{t("servers.modpackOffer.runsCodeText")}</span>
          </Banner>
          <Toggle checked={agree} onChange={setAgree} label={t("servers.modpackOffer.trust", { name })} />
        </div>
      )}
    </Modal>
  );
}
