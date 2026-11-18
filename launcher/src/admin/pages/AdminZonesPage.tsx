// /servers/:id/admin/zones: protection zones (kiln.gameplay through zones_list/zones_add/
// zones_remove): areas where only their owners (and admins, if allowed) may build, dig, open things, loot or craft, and
// where enemies don't spawn. The zones table and, beside it, the form (add a zone, or change the selected one). The
// centre comes from a player, the map or typed numbers.
import { useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAdminCall, useAdminQuery, useAdminState, type AdminPlayer } from "../../api/admin";
import { useT } from "../../i18n";
import { NeedsOp, playerName, round1, steamIdError, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, useHistory } from "../history";
import type { GameplayStatus, PermissionsModel, Zone } from "../types";
import { zoneHookNotices } from "../zoneHookState";
import { ACTIONS, actionHint, actionLabel, blankZone, useMapFocus, useZoneDraft, zoneArgs, type ZoneForm } from "../zones";
import { MoreHorizontal } from "lucide-react";
import { Banner, Button, Field, Menu, NameId, Page, PageHeader, Select, SplitPage, Table, TextInput, Toggle, confirm, fmtPos, toastError, toastOk } from "../ui";

function ZonesPage() {
  const t = useT();
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
    toastOk(t("admin.zones.centreFromMap"), t("admin.zones.centreFromMapDetail"));
  }, [draft]);
  const setList = (r: Zone[]) => qc.setQueriesData({ queryKey: ["admin", id, "zones_list"] }, r);
  const add = useAdminCall<Zone[]>("zones_add", { onSuccess: (r) => { setList(r); toastOk(form.editing ? t("admin.zones.saved", { name: form.name.trim() }) : t("admin.zones.added", { name: form.name.trim() })); setForm(blankZone()); }, onError: (e) => toastError(e), invalidates: ["gameplay_status"] });
  const remove = useAdminCall<Zone[]>("zones_remove", { onSuccess: (r) => setList(r), onError: (e) => toastError(e), invalidates: ["gameplay_status"] });

  // owner names: players online, the launcher's history, the permission file's notes
  const names = new Map<string, string>();
  for (const k of knownPlayers(hist, perms.data?.players ?? {})) if (k.name) names.set(k.steamId, k.name);
  for (const p of players) if (p.steamId && p.name) names.set(p.steamId, p.name);
  const save = async () => {
    const r = zoneArgs(form);
    if (r.error) return toastError({ message: r.error });
    if (!form.owners.length && !(await confirm({ title: t("admin.zones.noOwnersTitle"), body: <p>{form.allowAdmins ? t("admin.zones.noOwnersAdmins") : t("admin.zones.noOwnersBody")}</p>, ok: t("admin.zones.addAnyway") }))) return;
    add.mutate(r.args!);
  };
  const g = gp.data;
  const edit = (z: Zone) => setForm({ name: z.name, x: String(z.pos.x), y: String(z.pos.y), z: String(z.pos.z), radius: String(z.radius), deny: [...z.deny], owners: [...z.owners], allowAdmins: z.allowAdmins, editing: true });
  const notices = zoneHookNotices(g);
  return (
    <Page fill>
      <PageHeader title={t("admin.zones.title")} sub={g?.available ? t("admin.zones.subDenied", { count: g.denied ?? 0 }) : t("admin.zones.sub")} />
      <OfflineNote />
      <NeedsOp ops={["zones_list", "zones_add", "zones_remove"]} what={t("admin.zones.title")}>
        {g && !g.available ? <Banner tone="warn" title={t("admin.zones.notAvailable")}>{t("admin.zones.noGameplay")}</Banner> : null}
        {notices.length ? <Banner tone="warn" title={t("admin.zones.status")}>{notices.join(" ")}</Banner> : null}
        <SplitPage form aside={<ZoneFormCard form={form} setForm={setForm} players={players} names={names} onSave={save} saving={add.isPending} />}>
          <Table
            aria-label={t("admin.zones.tableLabel")}
            fill
            rows={zones.data ?? []}
            rowKey={(z) => z.name}
            selectedKey={form.editing ? form.name : null}
            onRowClick={edit}
            columns={[
              { key: "n", header: t("admin.zones.colZone"), render: (z) => <NameId name={z.name} id={z.active === false ? t("admin.zones.inactive") : fmtPos(z.pos)} />, sort: (a, b) => a.name.localeCompare(b.name) },
              { key: "d", header: t("admin.zones.colRefuses"), render: (z) => { const list = z.deny.map((a) => actionLabel(a)).join(", "); return <span className="k-muted" title={list} style={{ fontSize: 15.5 }}>{list}</span>; } },
              { key: "o", header: t("admin.zones.colOwners"), render: (z) => { const list = z.owners.map((o) => names.get(o) ?? o).join(", "); return <span title={z.owners.join(", ")} style={{ fontSize: 15.5 }}>{list || <span className="k-muted">{t("admin.zones.nobody")}</span>}{z.allowAdmins ? <span className="k-muted">{t("admin.zones.andAdmins")}</span> : null}</span>; } },
              { key: "r", header: t("admin.zones.colRadius"), width: "70px", align: "right", render: (z) => <span className="k-mono">{t("admin.zones.metres", { n: Math.round(z.radius) })}</span>, sort: (a, b) => a.radius - b.radius },
              { key: "a", header: <span className="k-sr-only">{t("admin.zones.colActions")}</span>, width: "32px", align: "right", render: (z) => (
                <span onClick={(e) => e.stopPropagation()}>
                  <Menu label={t("admin.zones.moreFor", { name: z.name })} icon={<MoreHorizontal />} size="s" items={[
                    { label: t("admin.zones.showOnMap"), onSelect: () => { focus({ x: z.pos.x, z: z.pos.z, zoom: 6 }); nav("../map"); } },
                    { label: t("admin.zones.change"), onSelect: () => edit(z) },
                    { label: t("common.remove"), danger: true, onSelect: async () => { if (await confirm({ title: t("admin.zones.removeTitle", { name: z.name }), body: <p>{t("admin.zones.removeBody")}</p>, ok: t("common.remove"), danger: true })) remove.mutate({ name: z.name }, { onSuccess: () => toastOk(t("admin.zones.removed", { name: z.name })) }); } },
                  ]} />
                </span>
              ) },
            ]}
            empty={t("admin.zones.empty")}
          />
        </SplitPage>
      </NeedsOp>
    </Page>
  );
}

function ZoneFormCard({ form: f, setForm, players, names, onSave, saving }: { form: ZoneForm; setForm: (f: ZoneForm) => void; players: AdminPlayer[]; names: Map<string, string>; onSave: () => void; saving: boolean }) {
  const t = useT();
  const [at, setAt] = useState("");
  const [newOwner, setNewOwner] = useState("");
  const set = (p: Partial<ZoneForm>) => setForm({ ...f, ...p });
  const owners = [...new Set([...players.map((p) => p.steamId).filter(Boolean) as string[], ...f.owners, ...names.keys()])];
  return (
    <form className="k-stack" style={{ gap: 12 }} onSubmit={(e) => { e.preventDefault(); onSave(); }}>
      <div>
        <span className="k-card-title">{f.editing ? t("admin.zones.changeName", { name: f.name }) : t("admin.zones.addZoneTitle")}</span>
        <div className="k-hint">{t("admin.zones.formHint")}</div>
      </div>
      <Field label={t("admin.zones.name")} hint={f.editing ? t("admin.zones.nameHintEdit") : t("admin.zones.nameHintAdd")} htmlFor="zone-name">
        <TextInput id="zone-name" value={f.name} maxLength={64} readOnly={f.editing} placeholder={t("admin.zones.namePlaceholder")} onChange={(e) => set({ name: e.target.value })} />
      </Field>
      <Field label={t("admin.zones.radiusLabel")} htmlFor="zone-r">
        <TextInput id="zone-r" inputMode="numeric" value={f.radius} onChange={(e) => set({ radius: e.target.value })} />
      </Field>
      <Field label={t("admin.zones.centre")}>
        <div className="ka-coords">
          {(["x", "y", "z"] as const).map((k) => <TextInput key={k} className="ka-coord" aria-label={k === "y" ? t("admin.zones.yHeight") : k} placeholder={k} value={f[k]} onChange={(e) => set({ [k]: e.target.value } as Partial<ZoneForm>)} />)}
        </div>
        {players.length ? (
          <div className="ka-inline" style={{ flexWrap: "nowrap" }}>
            <Select aria-label={t("admin.zones.player")} value={at || String(players[0].entity)} onChange={setAt} options={players.map((p) => ({ value: String(p.entity), label: t("admin.zones.whereStands", { name: playerName(p) }) }))} />
            <Button size="s" onClick={() => {
              const p = players.find((q) => q.entity === Number(at || players[0].entity));
              if (!p?.pos) return toastError({ message: t("admin.zones.noPlayerPos") });
              set({ x: String(round1(p.pos.x)), y: String(round1(p.pos.y)), z: String(round1(p.pos.z)), owners: !f.editing && p.steamId && !f.owners.includes(p.steamId) ? [...f.owners, p.steamId] : f.owners });
            }}>{t("admin.zones.use")}</Button>
          </div>
        ) : null}
      </Field>
      <Field label={t("admin.zones.refuseInside")}>
        <div className="ka-flags">
          {ACTIONS.map((a) => (
            <label key={a} title={actionHint(a)}>
              <input type="checkbox" checked={f.deny.includes(a)} onChange={(e) => set({ deny: e.target.checked ? [...f.deny, a] : f.deny.filter((x) => x !== a) })} /> {actionLabel(a)}
            </label>
          ))}
        </div>
      </Field>
      <Field label={t("admin.zones.ownersLabel")}>
        <div className="ka-flags">
          {owners.length ? owners.map((o) => (
            <label key={o} title={o}>
              <input type="checkbox" checked={f.owners.includes(o)} onChange={(e) => set({ owners: e.target.checked ? [...f.owners, o] : f.owners.filter((x) => x !== o) })} />
              {names.get(o) ?? o}
            </label>
          )) : <span className="k-hint">{t("admin.zones.noKnownPlayers")}</span>}
        </div>
        <div className="ka-inline" style={{ flexWrap: "nowrap" }}>
          <TextInput aria-label={t("admin.zones.addOwner")} placeholder="SteamID64" maxLength={17} value={newOwner} onChange={(e) => setNewOwner(e.target.value)} mono />
          <Button size="s" onClick={() => {
            const err = steamIdError(newOwner, true);
            if (err) return toastError({ message: err });
            if (!f.owners.includes(newOwner.trim())) set({ owners: [...f.owners, newOwner.trim()] });
            setNewOwner("");
          }}>{t("common.add")}</Button>
        </div>
      </Field>
      <Toggle checked={f.allowAdmins} onChange={(v) => set({ allowAdmins: v })} label={t("admin.zones.adminsToo")} />
      <div className="k-row" style={{ justifyContent: "flex-end" }}>
        {f.editing ? <Button variant="ghost" onClick={() => setForm(blankZone())}>{t("common.cancel")}</Button> : null}
        <Button type="submit" variant="primary" loading={saving}>{f.editing ? t("admin.zones.saveZone") : t("admin.zones.addZone")}</Button>
      </div>
    </form>
  );
}

export default withAdmin(ZonesPage);
