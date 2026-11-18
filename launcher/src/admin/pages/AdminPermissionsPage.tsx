// /servers/:id/admin/permissions: Kiln.Permissions: groups (admin flag, inheritance, rules),
// server user groups -> Kiln groups (permissions_map_user_group), per-player groups and rules by SteamID, a check that
// explains a decision, and the registered nodes.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState } from "../../api/admin";
import type { Json } from "../../api/commands";
import { rich, t as tr, useT } from "../../i18n";
import { GROUP, NeedsOp, nodeError, steamIdError, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, useHistory } from "../history";
import type { PermissionCheck, PermissionsModel } from "../types";
import { Avatar, Badge, Banner, Button, Card, EmptyState, KeyValue, Page, PageHeader, Select, Table, Tabs, TextInput, Toggle, agoS, confirm, toastError, toastOk } from "../ui";

type Tab = "groups" | "players" | "check" | "nodes";
const LEVEL_TONE: Record<string, "ok" | "warn" | "err"> = { everyone: "ok", admin: "warn", console: "err" };

function PermissionsPage() {
  const t = useT();
  const id = useServerId();
  const qc = useQueryClient();
  const q = useAdminQuery<PermissionsModel>("permissions_get", {}, { refetchInterval: 8000 });
  const [tab, setTab] = useState<Tab>("groups");
  const [person, setPerson] = useState<string | null>(null);
  const [checkFor, setCheckFor] = useState<{ sid: string; node: string }>({ sid: "", node: "" });
  const put = (m: PermissionsModel) => qc.setQueriesData({ queryKey: ["admin", id, "permissions_get"] }, m);
  const d = q.data;
  return (
    <Page>
      <PageHeader title={t("admin.permissions.title")} sub={t("admin.permissions.sub")} />
      <OfflineNote />
      <NeedsOp ops={["permissions_get", "permissions_group", "permissions_player_node"]} what={t("admin.permissions.title")}>
        <Tabs<Tab> value={tab} onChange={setTab} items={[
          { id: "groups", label: t("admin.permissions.tabGroups"), count: d ? Object.keys(d.groups).length : undefined },
          { id: "players", label: t("admin.pages.players") },
          { id: "check", label: t("admin.permissions.tabCheck") },
          { id: "nodes", label: t("admin.permissions.tabNodes"), count: d?.nodes?.length },
        ]} />
        {q.error ? <Banner tone="warn" title={t("admin.permissions.readFailed")}>{q.error.message}</Banner> : null}
        {!d ? <div className="k-muted">{t("admin.map.loading")}</div> : null}
        <datalist id="ka-nodes">
          {(d?.nodes ?? []).map((n) => <option key={n.node} value={n.node}>{n.description}</option>)}
          {[...new Set((d?.nodes ?? []).map((n) => n.node.split(".")[0]))].map((m) => <option key={m} value={`${m}.*`}>{t("admin.permissions.everyNodeOf", { mod: m })}</option>)}
          <option value="*">{t("admin.permissions.everyNode")}</option>
        </datalist>
        {d && tab === "groups" ? <GroupsTab d={d} put={put} /> : null}
        {d && tab === "players" ? <PlayersTab d={d} put={put} person={person} setPerson={setPerson} check={(sid) => { setCheckFor({ sid, node: "" }); setTab("check"); }} /> : null}
        {d && tab === "check" ? <CheckTab d={d} initial={checkFor} /> : null}
        {d && tab === "nodes" ? <NodesTab d={d} check={(node) => { setCheckFor({ sid: checkFor.sid, node }); setTab("check"); }} /> : null}
      </NeedsOp>
    </Page>
  );
}

/** One permissions edit: the op answers the whole model. */
function useEdit(op: string, put: (m: PermissionsModel) => void) {
  return useAdminCall<PermissionsModel>(op, { onSuccess: (m) => put(m), onError: (e) => toastError(e) });
}

function Rules({ rules, onRemove }: { rules: string[]; onRemove: (node: string) => void }) {
  const t = useT();
  if (!rules.length) return <span className="k-muted">{t("admin.permissions.noRules")}</span>;
  return (
    <span className="ka-chips">
      {rules.map((r) => {
        const deny = r.startsWith("-");
        const node = r.replace(/^-/, "");
        return (
          <span key={r} className={"ka-chip " + (deny ? "deny" : "allow")} title={deny ? t("admin.permissions.denied") : t("admin.permissions.allowed")}>
            {deny ? "−" : "+"} <span className="k-mono">{node}</span>
            <button type="button" title={t("admin.permissions.removeRule")} onClick={() => onRemove(node)}>×</button>
          </span>
        );
      })}
    </span>
  );
}

function RuleForm({ onAdd }: { onAdd: (node: string, allow: boolean) => void }) {
  const t = useT();
  const [node, setNode] = useState("");
  const [allow, setAllow] = useState("1");
  const add = () => {
    const n = node.trim().toLowerCase();
    const err = nodeError(n);
    if (err) return toastError({ message: err });
    onAdd(n, allow === "1");
    setNode("");
  };
  return (
    <span className="ka-inline">
      <TextInput aria-label={t("admin.permissions.node")} list="ka-nodes" placeholder={t("admin.permissions.nodePlaceholder")} value={node} onChange={(e) => setNode(e.target.value)} onKeyDown={(e) => e.key === "Enter" && add()} style={{ width: 280 }} mono />
      <Select aria-label={t("admin.permissions.allowOrDeny")} value={allow} onChange={setAllow} options={[{ value: "1", label: t("admin.permissions.allow") }, { value: "0", label: t("admin.permissions.deny") }]} />
      <Button size="s" onClick={add}>{t("admin.permissions.addRule")}</Button>
    </span>
  );
}

function GroupsTab({ d, put }: { d: PermissionsModel; put: (m: PermissionsModel) => void }) {
  const t = useT();
  const group = useEdit("permissions_group", put);
  const remove = useEdit("permissions_group_remove", put);
  const map = useEdit("permissions_map_user_group", put);
  const reload = useEdit("permissions_reload", put);
  const [name, setName] = useState("");
  const names = Object.keys(d.groups);
  const edit = (args: Record<string, Json>, msg: string) => group.mutate(args, { onSuccess: () => toastOk(msg) });
  const create = () => {
    const g = name.trim();
    if (!GROUP.test(g)) return toastError({ message: t("admin.permissions.groupRule") });
    edit({ group: g }, t("admin.permissions.created", { group: g }));
    setName("");
  };
  const dup: Record<string, string[]> = {};
  for (const u of d.serverUserGroups ?? []) (dup[u.rights] ??= []).push(u.name);
  const file = String(d.file ?? "").replace(/^.*[\\/]kiln[\\/]/, "kiln/").replace(/\\/g, "/");
  return (
    <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
      <Card title={t("admin.permissions.kilnGroups")} actions={<span className="ka-inline"><TextInput aria-label={t("admin.permissions.newGroup")} placeholder={t("admin.permissions.newGroupPlaceholder")} maxLength={40} value={name} onChange={(e) => setName(e.target.value)} onKeyDown={(e) => e.key === "Enter" && create()} style={{ width: 170 }} /><Button size="s" onClick={create}>{t("admin.permissions.create")}</Button></span>}>
        <div className="ka-list">
          {names.map((g) => {
            const x = d.groups[g];
            const isDefault = g.toLowerCase() === String(d.defaultGroup).toLowerCase();
            const others = names.filter((o) => o !== g && !(x.inherits ?? []).includes(o));
            return (
              <div key={g} className="k-stack" style={{ gap: 8 }}>
                <div className="ka-row-item">
                  <b className="ka-person-name">{g}</b>
                  {isDefault ? <Badge tone="ember" title={t("admin.permissions.everyPlayerTip")}>{t("admin.permissions.everyPlayer")}</Badge> : null}
                  {x.admin ? <Badge tone="warn">{t("admin.permissions.countsAdmin")}</Badge> : null}
                  <span className="k-spacer" />
                  <Toggle checked={!!x.admin} label={t("admin.players.stateAdmin")} onChange={(v) => edit({ group: g, admin: v }, v ? t("admin.permissions.groupAdminOn", { group: g }) : t("admin.permissions.groupAdminOff", { group: g }))} />
                  <Button size="s" variant="ghost" disabled={isDefault} title={isDefault ? t("admin.permissions.defaultNoRemove") : undefined} onClick={async () => {
                    if (await confirm({ title: t("admin.permissions.removeGroupTitle", { group: g }), body: <p>{t("admin.permissions.removeGroupBody")}</p>, ok: t("common.remove"), danger: true })) remove.mutate({ group: g }, { onSuccess: () => toastOk(t("admin.permissions.removedGroup", { group: g })) });
                  }}>{t("common.remove")}</Button>
                </div>
                <div className="ka-inline">
                  <span className="ka-label">{t("admin.permissions.inherits")}</span>
                  <span className="ka-chips">
                    {(x.inherits ?? []).length ? (x.inherits ?? []).map((i) => (
                      <span key={i} className="ka-chip">{i}<button type="button" title={t("admin.permissions.stopInheriting")} onClick={() => edit({ group: g, inherits: (x.inherits ?? []).filter((y) => y !== i) }, t("admin.permissions.noLongerInherits", { group: g, other: i }))}>×</button></span>
                    )) : <span className="k-muted">{t("admin.permissions.nothing")}</span>}
                  </span>
                  {others.length ? <Select aria-label={t("admin.permissions.inherit")} value="" onChange={(v) => v && edit({ group: g, inherits: [...(x.inherits ?? []), v] }, t("admin.permissions.nowInherits", { group: g, other: v }))} options={[{ value: "", label: t("admin.permissions.inheritAdd") }, ...others]} /> : null}
                </div>
                <div className="ka-inline" style={{ alignItems: "flex-start" }}>
                  <span className="ka-label">{t("admin.permissions.rules")}</span>
                  <Rules rules={x.nodes ?? []} onRemove={(node) => edit({ group: g, node, allow: null }, t("admin.permissions.removedRule", { who: g, node }))} />
                </div>
                <div className="ka-inline"><span className="ka-label" /><RuleForm onAdd={(node, allow) => edit({ group: g, node, allow }, allow ? t("admin.permissions.groupAllows", { group: g, node }) : t("admin.permissions.groupDenies", { group: g, node }))} /></div>
              </div>
            );
          })}
        </div>
      </Card>
      <div className="k-stack" style={{ gap: 16 }}>
        <Card title={t("admin.permissions.serverUserGroups")} sub={t("admin.permissions.serverUserGroupsSub")} flush>
          <Table
            aria-label={t("admin.permissions.userGroups")}
            rows={d.serverUserGroups ?? []}
            rowKey={(u) => u.name}
            rowHeight={72}
            columns={[
              { key: "u", header: t("admin.permissions.userGroup"), width: "minmax(0, 1fr)", render: (u) => (
                <div><b>{u.name}</b><div className="k-hint">{u.rights === "None" ? t("admin.permissions.noRights") : u.rights}</div>
                  {dup[u.rights].length > 1 ? <div className="k-hint" style={{ color: "var(--k-warn)" }}>{t("admin.permissions.sameRights", { list: dup[u.rights].filter((n) => n !== u.name).join(", ") })}</div> : null}</div>
              ) },
              { key: "k", header: t("admin.permissions.kilnGroups"), width: "minmax(0, 1.2fr)", render: (u) => {
                const mapped = d.userGroups?.[u.name] ?? [];
                return (
                  <div className="ka-flags">
                    {names.map((g) => (
                      <label key={g}><input type="checkbox" checked={mapped.includes(g)} onChange={(e) => {
                        const groups = e.target.checked ? [...mapped, g] : mapped.filter((x) => x !== g);
                        map.mutate({ userGroup: u.name, groups }, { onSuccess: () => toastOk(t("admin.permissions.userGroupMapped", { name: u.name, groups: groups.join(", ") || t("admin.permissions.noKilnGroup") })) });
                      }} /> {g}</label>
                    ))}
                  </div>
                );
              } },
            ]}
            empty={<EmptyState title={t("admin.permissions.noUserGroups")} hint={t("admin.permissions.noUserGroupsHint")} />}
          />
        </Card>
        <Banner tone="info">
          {rich("admin.permissions.howItWorks", { group: <b>{d.defaultGroup}</b>, prefix: <span className="k-mono">prefix.*</span>, star: <span className="k-mono">*</span> })} {d.kickBanIsAdmin ? t("admin.permissions.kickBanAdmin") : ""}
        </Banner>
        <Card title={t("admin.permissions.theFile")} sub={<span className="k-mono">{file}</span>} actions={<Button size="s" variant="ghost" loading={reload.isPending} onClick={() => reload.mutate({}, { onSuccess: () => toastOk(t("admin.permissions.reread")) })}>{t("admin.permissions.rereadNow")}</Button>}>
          <span className="k-hint">{t("admin.permissions.fileHint")}</span>
        </Card>
      </div>
    </div>
  );
}

function PlayersTab({ d, put, person, setPerson, check }: { d: PermissionsModel; put: (m: PermissionsModel) => void; person: string | null; setPerson: (s: string) => void; check: (sid: string) => void }) {
  const t = useT();
  const id = useServerId();
  const hist = useHistory(id);
  const st = useAdminState();
  const online = new Map((st.state?.players ?? []).filter((p) => p.steamId).map((p) => [p.steamId!, p]));
  const people = knownPlayers(hist, d.players ?? {});
  for (const [sid, p] of online) if (!people.some((x) => x.steamId === sid)) people.unshift({ steamId: sid, name: p.name, firstSeen: 0, lastSeen: Date.now() / 1000 });
  const [add, setAdd] = useState("");
  const sel = person ?? people[0]?.steamId ?? null;
  return (
    <div className="ka-split list-left">
      <Card title={t("admin.pages.players")} flush>
        <div style={{ padding: "10px 12px", borderBottom: "1px solid var(--k-line)" }} className="ka-inline">
          <TextInput aria-label="SteamID64" placeholder="SteamID64" maxLength={17} value={add} onChange={(e) => setAdd(e.target.value)} style={{ width: 170 }} mono />
          <Button size="s" onClick={() => { const e = steamIdError(add); if (e) return toastError({ message: e }); setPerson(add.trim()); setAdd(""); }}>{t("common.open")}</Button>
        </div>
        <div className="ka-pick" style={{ padding: 6 }}>
          {people.length ? people.map((p) => (
            <button key={p.steamId} className={p.steamId === sel ? "active" : ""} onClick={() => setPerson(p.steamId)}>
              {online.has(p.steamId) ? <Badge tone="ok">{t("admin.permissions.on")}</Badge> : null}<span>{p.name || t("admin.players.unknown")}</span><small className="k-mono">{p.steamId}</small>
            </button>
          )) : <EmptyState title={t("admin.permissions.noPlayers")} hint={t("admin.permissions.noPlayersHint")} />}
        </div>
      </Card>
      {sel ? <PersonPanel d={d} put={put} sid={sel} name={people.find((p) => p.steamId === sel)?.name ?? online.get(sel)?.name ?? null} online={online.get(sel) ?? null} lastSeen={people.find((p) => p.steamId === sel)?.lastSeen ?? 0} check={check} /> : <Card><EmptyState title={t("admin.common.pickPlayer")} /></Card>}
    </div>
  );
}

function PersonPanel({ d, put, sid, name, online, lastSeen, check }: { d: PermissionsModel; put: (m: PermissionsModel) => void; sid: string; name: string | null; online: { admin: boolean; permissions: string } | null; lastSeen: number; check: (sid: string) => void }) {
  const t = useT();
  const node = useEdit("permissions_player_node", put);
  const group = useEdit("permissions_player_group", put);
  const rec = d.players?.[sid] ?? { groups: [], nodes: [], name: null };
  const groups = Object.keys(d.groups).filter((g) => !rec.groups.includes(g));
  const who = name || rec.name || sid;
  const nameArg: Record<string, Json> = name ? { name: name.slice(0, 64) } : {};
  const ug = online ? (d.serverUserGroups ?? []).filter((u) => u.rights === online.permissions).map((u) => u.name) : [];
  const via = ug.length === 1 ? d.userGroups?.[ug[0]] ?? [] : [];
  return (
    <Card title={<span className="ka-person"><Avatar name={who} online={!!online} /><span>{who}</span></span>} sub={<span className="k-mono">{rec.name && rec.name !== name ? t("admin.permissions.sidNote", { sid, note: rec.name }) : sid}</span>}
      actions={<Button size="s" onClick={() => check(sid)}>{t("admin.permissions.checkNode")}</Button>}>
      <div className="k-stack" style={{ gap: 14 }}>
        {online ? (
          <KeyValue rows={[
            [t("admin.combat.inWorld"), <>{online.admin ? <Badge tone="warn">{t("admin.permissions.adminBadge")}</Badge> : null} {online.permissions}</>],
            [t("admin.permissions.userGroup"), ug.length === 1 ? <>{via.length ? rich("admin.permissions.ugVia", { group: <b>{ug[0]}</b>, via: via.join(", ") }) : rich("admin.permissions.ugNone", { group: <b>{ug[0]}</b> })}</> : ug.length ? t("admin.permissions.ambiguous", { list: ug.join(t("admin.permissions.orSep")) }) : t("admin.permissions.noneMatches")],
          ]} />
        ) : lastSeen ? <span className="k-hint">{t("admin.permissions.lastSeen", { ago: agoS(lastSeen) })}</span> : null}
        <div className="ka-inline">
          <span className="ka-label">{t("admin.permissions.tabGroups")}</span>
          <span className="ka-chips">
            {rec.groups.length ? rec.groups.map((g) => (
              <span key={g} className="ka-chip">{g}<button type="button" title={t("admin.permissions.takeOut")} onClick={() => group.mutate({ steamId: sid, group: g, add: false }, { onSuccess: () => toastOk(t("admin.permissions.left", { who, group: g })) })}>×</button></span>
            )) : <span className="k-muted">{t("admin.permissions.onlyEveryone")}</span>}
          </span>
          {groups.length ? <Select aria-label={t("admin.permissions.addToGroup")} value="" onChange={(g) => g && group.mutate({ steamId: sid, group: g, add: true, ...nameArg }, { onSuccess: () => toastOk(t("admin.permissions.joined", { who, group: g })) })} options={[{ value: "", label: t("admin.permissions.addToGroupOption") }, ...groups]} /> : null}
        </div>
        <div className="ka-inline" style={{ alignItems: "flex-start" }}>
          <span className="ka-label">{t("admin.permissions.ownRules")}</span>
          <Rules rules={rec.nodes} onRemove={(n) => node.mutate({ steamId: sid, node: n, allow: null }, { onSuccess: () => toastOk(t("admin.permissions.removedRule", { who, node: n })) })} />
        </div>
        <div className="ka-inline"><span className="ka-label" /><RuleForm onAdd={(n, allow) => node.mutate({ steamId: sid, node: n, allow, ...nameArg }, { onSuccess: () => toastOk(allow ? t("admin.permissions.playerAllowed", { who, node: n }) : t("admin.permissions.playerDenied", { who, node: n })) })} /></div>
        <span className="k-hint">{t("admin.permissions.ownBeats")}</span>
      </div>
    </Card>
  );
}

function explainFrom(from: string | null) {
  if (!from) return "";
  const [src, rest] = from.split(":");
  if (src === "default") return tr("admin.permissions.fromDefault", { level: String(rest) });
  if (src === "player") return tr("admin.permissions.fromPlayer");
  if (src === "group") return tr("admin.permissions.fromGroup", { group: String(rest) });
  return from;
}

function CheckTab({ d, initial }: { d: PermissionsModel; initial: { sid: string; node: string } }) {
  const t = useT();
  const id = useServerId();
  const hist = useHistory(id);
  const people = knownPlayers(hist, d.players ?? {});
  const [sid, setSid] = useState(initial.sid);
  const [node, setNode] = useState(initial.node);
  const [res, setRes] = useState<(PermissionCheck & { who: string }) | null>(null);
  const run = useAdminCall<PermissionCheck>("permissions_check");
  const go = () => {
    const e1 = steamIdError(sid);
    if (e1) return toastError({ message: t("admin.permissions.pickOrSid") });
    const n = node.trim().toLowerCase();
    const e2 = nodeError(n);
    if (e2) return toastError({ message: e2 });
    run.mutate({ steamId: sid.trim(), node: n }, { onSuccess: (r) => setRes({ ...r, who: people.find((p) => p.steamId === sid.trim())?.name || sid }), onError: (e) => { setRes(null); toastError(e); } });
  };
  return (
    <>
      <Card title={t("admin.permissions.checkTitle")} sub={t("admin.permissions.checkSub")}>
        <div className="ka-inline">
          <Select aria-label={t("admin.zones.player")} value={people.some((p) => p.steamId === sid) ? sid : ""} onChange={(v) => v && setSid(v)} options={[{ value: "", label: t("admin.permissions.playerOption") }, ...people.map((p) => ({ value: p.steamId, label: p.name || p.steamId }))]} />
          <span className="k-muted">{t("admin.permissions.or")}</span>
          <TextInput aria-label="SteamID64" placeholder="SteamID64" maxLength={17} value={sid} onChange={(e) => setSid(e.target.value)} style={{ width: 170 }} mono />
          <TextInput aria-label={t("admin.permissions.node")} list="ka-nodes" placeholder={t("admin.permissions.nodeCheckPlaceholder")} value={node} onChange={(e) => setNode(e.target.value)} onKeyDown={(e) => e.key === "Enter" && go()} style={{ width: 300 }} mono />
          <Button variant="primary" loading={run.isPending} onClick={go}>{t("admin.permissions.tabCheck")}</Button>
        </div>
      </Card>
      {res ? (
        <Card>
          <div className="k-stack">
            <div className="ka-inline"><Badge tone={res.allowed ? "ok" : "err"}>{res.allowed ? t("admin.permissions.allowedCap") : t("admin.permissions.deniedCap")}</Badge><span className="k-mono">{res.node}</span><span className="k-muted">{t("admin.permissions.forWho", { who: res.who })}</span></div>
            <KeyValue rows={[
              [t("admin.permissions.decidedBy"), <>{res.rule ? rich("admin.permissions.ruleX", { rule: <span className="k-mono">{res.rule}</span> }) : t("admin.permissions.noRuleMatched")} {res.from ? <span className="k-muted">({explainFrom(res.from)})</span> : null}</>],
              [t("admin.permissions.nodeLevel"), <Badge tone={LEVEL_TONE[String(res.level).toLowerCase()] ?? "idle"}>{String(res.level).toLowerCase()}</Badge>],
              [t("admin.permissions.tabGroups"), res.groups.length ? res.groups.join(", ") : "-"],
            ]} />
          </div>
        </Card>
      ) : null}
    </>
  );
}

function NodesTab({ d, check }: { d: PermissionsModel; check: (node: string) => void }) {
  const t = useT();
  return (
    <Card title={t("admin.permissions.registered")} sub={t("admin.permissions.registeredSub")} flush>
      <Table
        aria-label={t("admin.permissions.tabNodes")}
        rows={d.nodes ?? []}
        rowKey={(n) => n.node}
        columns={[
          { key: "n", header: t("admin.permissions.node"), width: "minmax(0, 1.4fr)", render: (n) => <span className="k-mono">{n.node}</span>, sort: (a, b) => a.node.localeCompare(b.node) },
          { key: "l", header: t("admin.permissions.default"), width: "110px", render: (n) => <Badge tone={LEVEL_TONE[n.level] ?? "idle"}>{n.level}</Badge> },
          { key: "d", header: t("admin.permissions.what"), render: (n) => n.description ?? "" },
          { key: "o", header: t("admin.content.mod"), width: "120px", render: (n) => <span className="k-mono k-muted">{n.owner ?? ""}</span> },
          { key: "c", header: "", width: "90px", align: "right", render: (n) => <Button size="s" variant="ghost" onClick={() => check(n.node)}>{t("admin.permissions.checkMore")}</Button> },
        ]}
        empty={<EmptyState title={t("admin.permissions.noNodes")} />}
      />
    </Card>
  );
}

export default withAdmin(PermissionsPage);
