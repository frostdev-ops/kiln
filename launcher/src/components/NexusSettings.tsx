// Nexus account consent happens in the user's browser; credentials stay in the backend.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ExternalLink } from "lucide-react";
import { useEffect, useRef } from "react";
import { useLocation } from "react-router-dom";
import * as nx from "../api/nexus";
import { useNexusAccount } from "../shell/catalogInstall";
import { useT } from "../i18n";
import { Badge, Button, Card, Skeleton, Toggle, errorText, runTask, toast } from "../ui";

export function NexusCard() {
  const t = useT();
  const qc = useQueryClient();
  const account = useNexusAccount();
  const handler = useQuery({ queryKey: nx.nk.handler, queryFn: nx.nexusHandlerStatus, retry: false });
  const loc = useLocation();
  const anchor = useRef<HTMLSpanElement>(null);
  useEffect(() => { if (loc.hash === "#nexus") anchor.current?.scrollIntoView({ block: "start", behavior: "smooth" }); }, [loc.hash, account.isSuccess]);
  const done = (a: nx.NexusAccount) => {
    qc.setQueryData(nx.nk.account, a);
    for (const k of [["nexus_search"], ["nexus_mod"], nx.nk.installed]) qc.invalidateQueries({ queryKey: k });
  };
  const signIn = useMutation({ mutationFn: () => runTask<nx.NexusAccount>(nx.nexusSignIn, { failed: t("nexusSettings.notSignedInFailed") }), onSuccess: (a) => { done(a); toast.ok(t("nexusSettings.signedInToast")); } });
  const signOut = useMutation({ mutationFn: nx.nexusSignOut, onSuccess: (a) => { done(a); toast.ok(t("nexusSettings.signedOutToast")); }, onError: (e) => toast.error(e) });
  const setHandler = useMutation({ mutationFn: nx.nexusHandlerSet, onSuccess: (s) => qc.setQueryData(nx.nk.handler, s), onError: (e) => toast.error(e, t("nexusSettings.notChanged")) });
  const a = account.data;
  const h = handler.data;
  return (
    <Card title={t("nexusSettings.title")} sub={t("nexusSettings.sub")}>
      <span id="nexus" ref={anchor} />
      {account.isLoading ? <Skeleton lines={2} /> : account.error ? <span className="k-hint">{errorText(account.error)}</span> : a && (
        <div className="k-stack" style={{ gap: 10 }}>
          <span className="k-row" style={{ gap: 8, flexWrap: "wrap" }} data-testid="nexus-account">
            {a.signedIn ? <>
              <span>{a.name ? t("nexusSettings.signedInAs", { name: a.name }) : t("nexusSettings.signedIn")}</span>
              {!a.error && <Badge tone={a.premium ? "ember" : "idle"}>{a.premium ? t("nexusSettings.premium") : t("nexusSettings.free")}</Badge>}
            </> : <span>{t("nexusSettings.notSignedIn")}</span>}
          </span>
          {a.error && <span className="k-hint">{a.error.message}</span>}
          {!a.configured && <span className="k-hint">{t("nexusSettings.notInBuild")}</span>}
          <span className="k-row" style={{ gap: 10 }}>
            <Button variant="secondary" icon={<ExternalLink />} disabled={!a.configured || signOut.isPending} loading={signIn.isPending} onClick={() => signIn.mutate()}>{a.signedIn ? t("nexusSettings.reconnect") : t("nexusSettings.signIn")}</Button>
            {(a.signedIn || signIn.isPending || a.error) && <Button size="s" variant="ghost" loading={signOut.isPending} onClick={() => signOut.mutate()}>{t("nexusSettings.signOut")}</Button>}
          </span>
        </div>
      )}
      {h && (
        <Toggle checked={h.registered} disabled={!h.supported || setHandler.isPending} onChange={(on) => setHandler.mutate(on)} label={t("nexusSettings.handler")}
          hint={!h.supported ? t("nexusSettings.handlerUnsupported") : [
            t("nexusSettings.handlerHint"),
            h.previous ? t("nexusSettings.handlerPrevious", { program: h.previous }) : t("nexusSettings.handlerAlone"),
            h.registered && !h.active ? t("nexusSettings.handlerTaken") : null,
          ].filter(Boolean).join(" ")} />
      )}
    </Card>
  );
}
