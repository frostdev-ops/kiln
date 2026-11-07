// Settings > Nexus Mods (docs/kiln/launcher/catalog.md): the user's own API key (checked with Nexus Mods before it is
// kept; stored in the OS keyring, else in the launcher's folder readable only by the user; never shown again), the
// account it belongs to (free or premium), and the opt-in for Mod Manager Download (nxm://) links: Enshrouded's open in
// Kiln, other games' go to the handler that was there before (Vortex, Mod Organizer 2), which comes back when it is off.
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ExternalLink } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useLocation } from "react-router-dom";
import * as api from "../api/commands";
import * as nx from "../api/nexus";
import { useNexusAccount } from "../shell/catalogInstall";
import { Badge, Button, Card, Field, Skeleton, TextInput, Toggle, errorText, toast } from "../ui";

export function NexusCard() {
  const qc = useQueryClient();
  const account = useNexusAccount();
  const handler = useQuery({ queryKey: nx.nk.handler, queryFn: nx.nexusHandlerStatus, retry: false });
  const [key, setKey] = useState("");
  const loc = useLocation();
  const anchor = useRef<HTMLSpanElement>(null);
  // the Catalog's "Settings" buttons link here (/settings#nexus)
  useEffect(() => { if (loc.hash === "#nexus") anchor.current?.scrollIntoView({ block: "start", behavior: "smooth" }); }, [loc.hash, account.isSuccess]);
  const done = (a: nx.NexusAccount) => {
    qc.setQueryData(nx.nk.account, a);
    for (const k of [["nexus_search"], ["nexus_mod"], nx.nk.installed]) qc.invalidateQueries({ queryKey: k });
  };
  const save = useMutation({ mutationFn: () => nx.nexusKeySet(key), onSuccess: (a) => { setKey(""); done(a); toast.ok("Key saved"); }, onError: (e) => toast.error(e, "Key not saved") });
  const clear = useMutation({ mutationFn: nx.nexusKeyClear, onSuccess: (a) => { done(a); toast.ok("Key removed"); }, onError: (e) => toast.error(e) });
  const setHandler = useMutation({ mutationFn: nx.nexusHandlerSet, onSuccess: (s) => qc.setQueryData(nx.nk.handler, s), onError: (e) => toast.error(e, "Not changed") });
  const a = account.data;
  const h = handler.data;
  const where = a?.keyPlace === "keyring" ? "Kept in your system keyring." : a?.keyPlace === "file" ? "Kept in the launcher's folder, readable only by you." : null;
  return (
    <Card title="Nexus Mods" sub="The catalog and downloads use your own API key." >
      <span id="nexus" ref={anchor} />
      {account.isLoading ? <Skeleton lines={2} /> : account.error ? <span className="k-hint">{errorText(account.error)}</span> : a && (
        <div className="k-stack" style={{ gap: 10 }}>
          <span className="k-row" style={{ gap: 8, flexWrap: "wrap" }} data-testid="nexus-account">
            {a.keySet ? (
              <>
                <span>{a.name ? `Signed in as ${a.name}` : "Key saved"}</span>
                {!a.error && <Badge tone={a.premium ? "ember" : "idle"}>{a.premium ? "Premium" : "Free"}</Badge>}
                {a.error && <Badge tone={a.error.code === "auth_failed" ? "err" : "warn"}>{a.error.code === "auth_failed" ? "Refused" : "Not checked"}</Badge>}
              </>
            ) : <span>No API key yet.</span>}
          </span>
          {a.error && <span className="k-hint">{a.error.message}</span>}
          {a.keySet && <span className="k-hint">{where} {a.premium ? "Add downloads in the launcher." : "Free accounts download on the Nexus Mods site; Mod Manager Download brings the mod here."}</span>}
          <Field label={a.keySet ? "Replace the key" : "Personal API key"}>
            <div className="k-input-group">
              <TextInput type="password" autoComplete="off" spellCheck={false} value={key} onChange={(e) => setKey(e.target.value)} placeholder="Paste it here" aria-label="Nexus Mods API key" style={{ flex: 1 }} />
              <Button variant="secondary" disabled={!key.trim()} loading={save.isPending} onClick={() => save.mutate()}>Save</Button>
            </div>
          </Field>
          <span className="k-row" style={{ gap: 10 }}>
            <Button size="s" variant="ghost" icon={<ExternalLink />} onClick={() => void api.appOpenUrl(a.keyPage).catch((e) => toast.error(e))}>Get your API key</Button>
            {a.keySet && <Button size="s" variant="ghost" loading={clear.isPending} onClick={() => clear.mutate()}>Remove the key</Button>}
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
