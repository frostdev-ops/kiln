// /servers/:id/admin/zones: protection zones (kiln.gameplay through zones_list/zones_add/
// zones_remove): areas where only their owners (and admins, if allowed) may build, dig, open things, loot or craft, and
// where enemies don't spawn. The zones table and, beside it, the form (add a zone, or change the selected one). The
// centre comes from a player, the map or typed numbers.
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAdminCall, useAdminQuery, useAdminState, type AdminPlayer } from "../../api/admin";
import { NeedsOp, playerName, round1, steamIdError, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, useHistory } from "../history";
import type { GameplayStatus, PermissionsModel, Zone } from "../types";
import { ACTIONS, ACTION_HINT, ACTION_LABEL, blankZone, useMapFocus, useZoneDraft, zoneArgs, type ZoneForm } from "../zones";
import { MoreHorizontal } from "lucide-react";
import { Banner, Button, Field, Menu, NameId, Page, PageHeader, Select, SplitPage, Table, TextInput, Toggle, confirm, fmtPos, toastError, toastOk } from "../ui";

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
  const edit = (z: Zone) => setForm({ name: z.name, x: String(z.pos.x), y: String(z.pos.y), z: String(z.pos.z), radius: String(z.radius), deny: [...z.deny], owners: [...z.owners], allowAdmins: z.allowAdmins, editing: true });
  const hooks = ([["buildHooks", "building"], ["terraformHook", "digging"], ["intercept", "opening and looting"], ["craftHook", "crafting"], ["spawnHook", "enemy spawns"]] as const);
  const off = g?.available ? hooks.filter(([k]) => !g[k]).map(([, l]) => l) : [];
  return (
    <Page fill>
      <PageHeader title="Protection zones" sub={`Only owners may build, dig, open, loot or craft inside. Zones are spheres and take effect at once.${g?.available ? ` ${g.denied ?? 0} refused so far.` : ""}`} />
      <OfflineNote />
      <NeedsOp ops={["zones_list", "zones_add", "zones_remove"]} what="Protection zones">
        {g && !g.available ? <Banner tone="warn" title="Not available">kiln.gameplay didn't start on this server build.</Banner> : null}
        {off.length ? <Banner tone="warn" title="Partly off">This server build does not refuse {off.join(", ")}.</Banner> : null}
        <SplitPage form aside={<ZoneFormCard form={form} setForm={setForm} players={players} names={names} onSave={save} saving={add.isPending} />}>
          <Table
            aria-label="Zones"
            fill
            rows={zones.data ?? []}
            rowKey={(z) => z.name}
            selectedKey={form.editing ? form.name : null}
            onRowClick={edit}
            columns={[
              { key: "n", header: "Zone", render: (z) => <NameId name={z.name} id={z.active === false ? "inactive" : fmtPos(z.pos)} />, sort: (a, b) => a.name.localeCompare(b.name) },
              { key: "d", header: "Refuses", render: (z) => { const t = z.deny.map((a) => ACTION_LABEL[a] ?? a).join(", "); return <span className="k-muted" title={t} style={{ fontSize: 15.5 }}>{t}</span>; } },
              { key: "o", header: "Owners", render: (z) => { const t = z.owners.map((o) => names.get(o) ?? o).join(", "); return <span title={z.owners.join(", ")} style={{ fontSize: 15.5 }}>{t || <span className="k-muted">Nobody</span>}{z.allowAdmins ? <span className="k-muted">, admins</span> : null}</span>; } },
              { key: "r", header: "Radius", width: "70px", align: "right", render: (z) => <span className="k-mono">{Math.round(z.radius)} m</span>, sort: (a, b) => a.radius - b.radius },
              { key: "a", header: <span className="k-sr-only">Actions</span>, width: "32px", align: "right", render: (z) => (
                <span onClick={(e) => e.stopPropagation()}>
                  <Menu label={`More for ${z.name}`} icon={<MoreHorizontal />} size="s" items={[
                    { label: "Show on the map", onSelect: () => { focus({ x: z.pos.x, z: z.pos.z, zoom: 6 }); nav("../map"); } },
                    { label: "Change", onSelect: () => edit(z) },
                    { label: "Remove", danger: true, onSelect: async () => { if (await confirm({ title: `Remove ${z.name}?`, body: <p>Its protection ends at once.</p>, ok: "Remove", danger: true })) remove.mutate({ name: z.name }, { onSuccess: () => toastOk(`Removed ${z.name}`) }); } },
                  ]} />
                </span>
              ) },
            ]}
            empty="No protection zones. Add one here, or right-click the map and pick Protect an area."
          />
        </SplitPage>
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
    <form className="k-stack" style={{ gap: 12 }} onSubmit={(e) => { e.preventDefault(); onSave(); }}>
      <div>
        <span className="k-card-title">{f.editing ? `Change ${f.name}` : "Add a zone"}</span>
        <div className="k-hint">Kiln refuses these in the game's own code. Not every action has been tried in a real game yet.</div>
      </div>
      <Field label="Name" hint={f.editing ? "Saving keeps the name and replaces the rest." : "The name of an existing zone replaces it."} htmlFor="zone-name">
        <TextInput id="zone-name" value={f.name} maxLength={64} readOnly={f.editing} placeholder="Main base" onChange={(e) => set({ name: e.target.value })} />
      </Field>
      <Field label="Radius in metres, 1 to 2000" htmlFor="zone-r">
        <TextInput id="zone-r" inputMode="numeric" value={f.radius} onChange={(e) => set({ radius: e.target.value })} />
      </Field>
      <Field label="Centre">
        <div className="ka-coords">
          {(["x", "y", "z"] as const).map((k) => <TextInput key={k} className="ka-coord" aria-label={k === "y" ? "y, height" : k} placeholder={k} value={f[k]} onChange={(e) => set({ [k]: e.target.value } as Partial<ZoneForm>)} />)}
        </div>
        {players.length ? (
          <div className="ka-inline" style={{ flexWrap: "nowrap" }}>
            <Select aria-label="Player" value={at || String(players[0].entity)} onChange={setAt} options={players.map((p) => ({ value: String(p.entity), label: `Where ${playerName(p)} stands` }))} />
            <Button size="s" onClick={() => {
              const p = players.find((q) => q.entity === Number(at || players[0].entity));
              if (!p?.pos) return toastError({ message: "No position for that player." });
              set({ x: String(round1(p.pos.x)), y: String(round1(p.pos.y)), z: String(round1(p.pos.z)), owners: !f.editing && p.steamId && !f.owners.includes(p.steamId) ? [...f.owners, p.steamId] : f.owners });
            }}>Use</Button>
          </div>
        ) : null}
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
      <Field label="Owners, who may do everything inside">
        <div className="ka-flags">
          {owners.length ? owners.map((o) => (
            <label key={o} title={o}>
              <input type="checkbox" checked={f.owners.includes(o)} onChange={(e) => set({ owners: e.target.checked ? [...f.owners, o] : f.owners.filter((x) => x !== o) })} />
              {names.get(o) ?? o}
            </label>
          )) : <span className="k-hint">No known players yet.</span>}
        </div>
        <div className="ka-inline" style={{ flexWrap: "nowrap" }}>
          <TextInput aria-label="Add an owner by SteamID64" placeholder="SteamID64" maxLength={17} value={newOwner} onChange={(e) => setNewOwner(e.target.value)} mono />
          <Button size="s" onClick={() => {
            const err = steamIdError(newOwner, true);
            if (err) return toastError({ message: err });
            if (!f.owners.includes(newOwner.trim())) set({ owners: [...f.owners, newOwner.trim()] });
            setNewOwner("");
          }}>Add</Button>
        </div>
      </Field>
      <Toggle checked={f.allowAdmins} onChange={(v) => set({ allowAdmins: v })} label="Admins may act inside too" />
      <div className="k-row" style={{ justifyContent: "flex-end" }}>
        {f.editing ? <Button variant="ghost" onClick={() => setForm(blankZone())}>Cancel</Button> : null}
        <Button type="submit" variant="primary" loading={saving}>{f.editing ? "Save zone" : "Add zone"}</Button>
      </div>
    </form>
  );
}

export default withAdmin(ZonesPage);
