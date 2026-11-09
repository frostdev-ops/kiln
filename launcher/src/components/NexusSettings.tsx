// Nexus account consent happens in the user's browser; credentials stay in the backend.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ExternalLink } from "lucide-react";
import { useEffect, useRef } from "react";
import { useLocation } from "react-router-dom";
import * as nx from "../api/nexus";
import { useNexusAccount } from "../shell/catalogInstall";
import { Badge, Button, Card, Skeleton, Toggle, errorText, runTask, toast } from "../ui";

export function NexusCard() {
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
  const signIn = useMutation({ mutationFn: () => runTask<nx.NexusAccount>(nx.nexusSignIn, { failed: "Not signed in" }), onSuccess: (a) => { done(a); toast.ok("Signed in to Nexus Mods"); } });
  const signOut = useMutation({ mutationFn: nx.nexusSignOut, onSuccess: (a) => { done(a); toast.ok("Signed out of Nexus Mods"); }, onError: (e) => toast.error(e) });
  const setHandler = useMutation({ mutationFn: nx.nexusHandlerSet, onSuccess: (s) => qc.setQueryData(nx.nk.handler, s), onError: (e) => toast.error(e, "Not changed") });
  const a = account.data;
  const h = handler.data;
  return (
    <Card title="Nexus Mods" sub="Sign in through your browser.">
      <span id="nexus" ref={anchor} />
      {account.isLoading ? <Skeleton lines={2} /> : account.error ? <span className="k-hint">{errorText(account.error)}</span> : a && (
        <div className="k-stack" style={{ gap: 10 }}>
          <span className="k-row" style={{ gap: 8, flexWrap: "wrap" }} data-testid="nexus-account">
            {a.signedIn ? <>
              <span>{a.name ? `Signed in as ${a.name}` : "Signed in"}</span>
              {!a.error && <Badge tone={a.premium ? "ember" : "idle"}>{a.premium ? "Premium" : "Free"}</Badge>}
            </> : <span>Not signed in.</span>}
          </span>
          {a.error && <span className="k-hint">{a.error.message}</span>}
          {!a.configured && <span className="k-hint">Nexus Mods sign-in is not available in this build.</span>}
          <span className="k-row" style={{ gap: 10 }}>
            <Button variant="secondary" icon={<ExternalLink />} disabled={!a.configured || signOut.isPending} loading={signIn.isPending} onClick={() => signIn.mutate()}>{a.signedIn ? "Reconnect" : "Sign in to Nexus Mods"}</Button>
            {(a.signedIn || signIn.isPending || a.error) && <Button size="s" variant="ghost" loading={signOut.isPending} onClick={() => signOut.mutate()}>Sign out</Button>}
          </span>
        </div>
      )}
      {h && (
        <Toggle checked={h.registered} disabled={!h.supported || setHandler.isPending} onChange={(on) => setHandler.mutate(on)} label="Mod Manager Download links"
          hint={!h.supported ? "Not available on this system." : `Enshrouded's links from Nexus Mods open in Kiln. ${h.previous ? `Other games' links go to ${h.previous}, which gets them all back when this is off.` : "Other games' links are left alone."}${h.registered && !h.active ? " Another program has taken the links since: turn this off and on again." : ""}`} />
      )}
    </Card>
  );
}
