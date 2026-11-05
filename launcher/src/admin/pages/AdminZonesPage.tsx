// /servers/:id/admin/zones: protection zones (kiln.gameplay through zones_list/zones_add/
// zones_remove): areas where only their owners (and admins, if allowed) may build, dig, open things, loot or craft, and
// where enemies don't spawn. The centre comes from a player, the map or typed numbers.
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAdminCall, useAdminQuery, useAdminState, type AdminPlayer } from "../../api/admin";
import { NeedsOp, playerName, round1, steamIdError, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, useHistory } from "../history";
import type { GameplayStatus, PermissionsModel, Zone } from "../types";
import { ACTIONS, ACTION_HINT, ACTION_LABEL, blankZone, useMapFocus, useZoneDraft, zoneArgs, type ZoneForm } from "../zones";
import { Badge, Banner, Button, Card, EmptyState, Field, Page, PageHeader, Select, Table, TextInput, Toggle, confirm, fmtPos, toastError, toastOk } from "../ui";

function ZonesPage() {
  const id = useServerId();
  const qc = useQueryClient();
  const nav = useNavigate();
  const st = useAdminState();
  const players = st.state?.players ?? [];
  const zones = useAdminQuery<Zone[]>("zones_list", {}, { refetchInterval: 6000 });
  const gp = useAdminQuery<GameplayStatus>("gameplay_status", {}, { refetchInterval: 10_000 });
  const perms = useAdminQuery<PermissionsModel>("permissions_get", {}, { staleTime: 30_000 });
  const hist = useHistory(id);
  const [form, setForm] = useState<ZoneForm>(blankZone);
  const draft = useZoneDraft();
  const focus = useMapFocus((s) => s.set);
  useEffect(() => {
    if (!draft.draft) return;
    setForm((f) => ({ ...(f.editing ? blankZone() : f), x: String(round1(draft.draft!.x)), y: String(round1(draft.draft!.y)), z: String(round1(draft.draft!.z)) }));
    draft.set(null);
    toastOk("Centre taken from the map", "Name the zone and pick what it refuses.");
  }, [draft]);
  const setList = (r: Zone[]) => qc.setQueriesData({ queryKey: ["admin", id, "zones_list"] }, r);
  const add = useAdminCall<Zone[]>("zones_add", { onSuccess: (r) => { setList(r); toastOk(`${form.editing ? "Saved" : "Added"} ${form.name.trim()}`); setForm(blankZone()); }, onError: (e) => toastError(e), invalidates: ["gameplay_status"] });
  const remove = useAdminCall<Zone[]>("zones_remove", { onSuccess: (r) => setList(r), onError: (e) => toastError(e), invalidates: ["gameplay_status"] });

  // owner names: players online, the launcher's history, the permission file's notes
  const names = new Map<string, string>();
  for (const k of knownPlayers(hist, perms.data?.players ?? {})) if (k.name) names.set(k.steamId, k.name);
  for (const p of players) if (p.steamId && p.name) names.set(p.steamId, p.name);
  const save = async () => {
    const r = zoneArgs(form);
    if (r.error) return toastError({ message: r.error });
    if (!form.owners.length && !(await confirm({ title: "A zone without owners?", body: <p>Nobody{form.allowAdmins ? " but admins" : ""} may do the ticked things inside it.</p>, ok: "Add anyway" }))) return;
    add.mutate(r.args!);
  };
  const g = gp.data;
  return (
    <Page>
      <PageHeader title="Protection zones" sub="Only their owners may build, dig, open containers, loot or craft inside. They take effect at once and survive restarts." />
      <OfflineNote />
      <NeedsOp ops={["zones_list", "zones_add", "zones_remove"]} what="Protection zones">
        {g && !g.available ? <Banner tone="warn" title="Protection zones aren't available">kiln.gameplay didn't start on this server build.</Banner> : null}
        <div className="k-grid k-grid-main" style={{ alignItems: "start" }}>
          <div className="k-stack" style={{ gap: 16 }}>
            <Card title="Zones" sub={zones.data ? `${zones.data.length} zone${zones.data.length === 1 ? "" : "s"}` : undefined} flush>
              <Table
                aria-label="Zones"
                rows={zones.data ?? []}
                rowKey={(z) => z.name}
                rowHeight={56}
                columns={[
                  { key: "n", header: "Zone", width: "minmax(0, 1.2fr)", render: (z) => <div><div className="ka-person-name">{z.name} {z.active === false ? <Badge tone="warn">inactive</Badge> : null}</div><div className="k-mono k-muted">{fmtPos(z.pos)}</div></div>, sort: (a, b) => a.name.localeCompare(b.name) },
                  { key: "d", header: "Refuses", width: "minmax(0, 1.5fr)", render: (z) => { const t = z.deny.map((a) => ACTION_LABEL[a] ?? a).join(", "); return <span className="ka-ellipsis" title={t} style={{ display: "block" }}>{t}</span>; } },
                  { key: "o", header: "Owners", render: (z) => { const t = z.owners.map((o) => names.get(o) ?? o).join(", "); return <div title={z.owners.join(", ")}><div className="ka-ellipsis">{t || <span className="k-muted">nobody</span>}</div>{z.allowAdmins ? <div className="k-hint">+ admins</div> : null}</div>; } },
                  { key: "r", header: "Radius", width: "70px", align: "right", render: (z) => `${Math.round(z.radius)} m`, sort: (a, b) => a.radius - b.radius },
                  { key: "a", header: "", width: "200px", align: "right", render: (z) => (
                    <div className="k-row" style={{ justifyContent: "flex-end", flexWrap: "nowrap" }}>
                      <Button size="s" variant="ghost" onClick={() => { focus({ x: z.pos.x, z: z.pos.z, zoom: 6 }); nav("../map"); }}>Map</Button>
                      <Button size="s" onClick={() => setForm({ name: z.name, x: String(z.pos.x), y: String(z.pos.y), z: String(z.pos.z), radius: String(z.radius), deny: [...z.deny], owners: [...z.owners], allowAdmins: z.allowAdmins, editing: true })}>Edit</Button>
                      <Button size="s" variant="ghost" onClick={async () => { if (await confirm({ title: `Remove the zone ${z.name}?`, body: <p>Its protection ends at once.</p>, ok: "Remove", danger: true })) remove.mutate({ name: z.name }, { onSuccess: () => toastOk(`Removed ${z.name}`) }); }}>Remove</Button>
                    </div>
                  ) },
                ]}
                empty={<EmptyState title="No protection zones" hint="Add one here, or right-click the map and pick “Protect an area here…”." />}
              />
            </Card>
            <ZoneFormCard form={form} setForm={setForm} players={players} names={names} onSave={save} saving={add.isPending} />
          </div>
          <div className="k-stack" style={{ gap: 16 }}>
            {g?.available ? (
              <Card title="Protection in this server build" sub={`${g.denied ?? 0} refused · ${g.canceled ?? 0} canceled by mods`}>
                <div className="ka-list">
                  {([["buildHooks", "Building and dismantling"], ["terraformHook", "Digging and terraforming"], ["intercept", "Opening, using and looting"], ["craftHook", "Crafting"], ["spawnHook", "Enemy spawns"]] as const).map(([k, l]) => (
                    <div key={k} className="ka-row-item">{g[k] ? <Badge tone="ok">ok</Badge> : <Badge tone="warn">off</Badge>}<span>{l}</span></div>
                  ))}
                </div>
              </Card>
            ) : null}
            <Banner tone="info">Players who aren't owners are refused the ticked actions inside a zone, admins too unless allowed. Zones are spheres: they reach as far up and down. The map draws them.</Banner>
          </div>
        </div>
      </NeedsOp>
    </Page>
  );
}

function ZoneFormCard({ form: f, setForm, players, names, onSave, saving }: { form: ZoneForm; setForm: (f: ZoneForm) => void; players: AdminPlayer[]; names: Map<string, string>; onSave: () => void; saving: boolean }) {
  const [at, setAt] = useState("");
  const [newOwner, setNewOwner] = useState("");
  const set = (p: Partial<ZoneForm>) => setForm({ ...f, ...p });
  const owners = [...new Set([...players.map((p) => p.steamId).filter(Boolean) as string[], ...f.owners, ...names.keys()])];
  return (
    <Card title={f.editing ? `Change ${f.name}` : "Add a zone"} actions={<Badge tone="warn" title="Kiln refuses these in the game's own code; no real player has tried every action yet">untested in game</Badge>}>
      <div className="k-stack" style={{ gap: 14 }}>
        <div className="k-grid k-grid-2">
          <Field label="Name" hint={f.editing ? "Saving keeps the name and replaces the rest." : "The name of an existing zone replaces it."}>
            <TextInput value={f.name} maxLength={64} readOnly={f.editing} placeholder="e.g. Main base" onChange={(e) => set({ name: e.target.value })} />
          </Field>
          <Field label="Radius (metres)" hint="1 to 2000">
            <TextInput inputMode="numeric" value={f.radius} onChange={(e) => set({ radius: e.target.value })} />
          </Field>
        </div>
        <Field label="Centre">
          <div className="ka-inline">
            <div className="ka-coords">
              {(["x", "y", "z"] as const).map((k) => <TextInput key={k} className="ka-coord" aria-label={k} placeholder={k === "y" ? "y (height)" : k} value={f[k]} onChange={(e) => set({ [k]: e.target.value } as Partial<ZoneForm>)} />)}
            </div>
            {players.length ? (
              <>
                <Select aria-label="Player" value={at || String(players[0].entity)} onChange={setAt} options={players.map((p) => ({ value: String(p.entity), label: `where ${playerName(p)} stands` }))} />
                <Button size="s" onClick={() => {
                  const p = players.find((q) => q.entity === Number(at || players[0].entity));
                  if (!p?.pos) return toastError({ message: "No position for that player." });
                  set({ x: String(round1(p.pos.x)), y: String(round1(p.pos.y)), z: String(round1(p.pos.z)), owners: !f.editing && p.steamId && !f.owners.includes(p.steamId) ? [...f.owners, p.steamId] : f.owners });
                }}>Use</Button>
              </>
            ) : null}
          </div>
        </Field>
        <Field label="Refuse inside">
          <div className="ka-flags">
            {ACTIONS.map((a) => (
              <label key={a} title={ACTION_HINT[a]}>
                <input type="checkbox" checked={f.deny.includes(a)} onChange={(e) => set({ deny: e.target.checked ? [...f.deny, a] : f.deny.filter((x) => x !== a) })} /> {ACTION_LABEL[a]}
              </label>
            ))}
          </div>
        </Field>
        <Field label="Owners (may do everything inside)">
          <div className="k-stack">
            <div className="ka-flags">
              {owners.length ? owners.map((o) => (
                <label key={o}>
                  <input type="checkbox" checked={f.owners.includes(o)} onChange={(e) => set({ owners: e.target.checked ? [...f.owners, o] : f.owners.filter((x) => x !== o) })} />
                  {names.get(o) ?? "Unknown"} <span className="k-mono k-muted">{o}</span>
                </label>
              )) : <span className="k-muted">No known players yet.</span>}
            </div>
            <div className="ka-inline">
              <TextInput aria-label="Add a SteamID64" placeholder="add a SteamID64" maxLength={17} value={newOwner} onChange={(e) => setNewOwner(e.target.value)} style={{ width: 190 }} mono />
              <Button size="s" onClick={() => {
                const err = steamIdError(newOwner, true);
                if (err) return toastError({ message: err });
                if (!f.owners.includes(newOwner.trim())) set({ owners: [...f.owners, newOwner.trim()] });
                setNewOwner("");
              }}>Add</Button>
            </div>
          </div>
        </Field>
        <Toggle checked={f.allowAdmins} onChange={(v) => set({ allowAdmins: v })} label="Admins may act inside too" />
        <div className="k-row">
          <Button variant="primary" loading={saving} onClick={onSave}>{f.editing ? "Save zone" : "Add zone"}</Button>
          {f.editing ? <Button variant="ghost" onClick={() => setForm(blankZone())}>Cancel</Button> : null}
        </div>
      </div>
    </Card>
  );
}

export default withAdmin(ZonesPage);
