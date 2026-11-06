// /servers/:id/admin/permissions: Kiln.Permissions: groups (admin flag, inheritance, rules),
// server user groups -> Kiln groups (permissions_map_user_group), per-player groups and rules by SteamID, a check that
// explains a decision, and the registered nodes.
import { useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState } from "../../api/admin";
import type { Json } from "../../api/commands";
import { GROUP, NeedsOp, nodeError, steamIdError, useServerId, withAdmin } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, useHistory } from "../history";
import type { PermissionCheck, PermissionsModel } from "../types";
import { Avatar, Badge, Banner, Button, Card, EmptyState, KeyValue, Page, PageHeader, Select, Table, Tabs, TextInput, Toggle, agoS, confirm, toastError, toastOk } from "../ui";

type Tab = "groups" | "players" | "check" | "nodes";
const LEVEL_TONE: Record<string, "ok" | "warn" | "err"> = { everyone: "ok", admin: "warn", console: "err" };
const show = (allow: boolean | null) => (allow === true ? "allow" : allow === false ? "deny" : "cleared");

function PermissionsPage() {
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
      <PageHeader title="Permissions" sub="Who may use which mod features and commands. Kiln groups hold rules; players get groups through their server user group or one by one." />
      <OfflineNote />
      <NeedsOp ops={["permissions_get", "permissions_group", "permissions_player_node"]} what="Permissions">
        <Tabs<Tab> value={tab} onChange={setTab} items={[
          { id: "groups", label: "Groups", count: d ? Object.keys(d.groups).length : undefined },
          { id: "players", label: "Players" },
          { id: "check", label: "Check" },
          { id: "nodes", label: "Nodes", count: d?.nodes?.length },
        ]} />
        {q.error ? <Banner tone="warn" title="Couldn't read the permissions">{q.error.message}</Banner> : null}
        {!d ? <div className="k-muted">Loading…</div> : null}
        <datalist id="ka-nodes">
          {(d?.nodes ?? []).map((n) => <option key={n.node} value={n.node}>{n.description}</option>)}
          {[...new Set((d?.nodes ?? []).map((n) => n.node.split(".")[0]))].map((m) => <option key={m} value={`${m}.*`}>every node of {m}</option>)}
          <option value="*">every node (not console-only ones)</option>
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
  if (!rules.length) return <span className="k-muted">no rules</span>;
  return (
    <span className="ka-chips">
      {rules.map((r) => {
        const deny = r.startsWith("-");
        const node = r.replace(/^-/, "");
        return (
          <span key={r} className={"ka-chip " + (deny ? "deny" : "allow")} title={deny ? "denied" : "allowed"}>
            {deny ? "−" : "+"} <span className="k-mono">{node}</span>
            <button type="button" title="Remove this rule" onClick={() => onRemove(node)}>×</button>
          </span>
        );
      })}
    </span>
  );
}

function RuleForm({ onAdd }: { onAdd: (node: string, allow: boolean) => void }) {
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
      <TextInput aria-label="Node" list="ka-nodes" placeholder="mymod.command.home or mymod.*" value={node} onChange={(e) => setNode(e.target.value)} onKeyDown={(e) => e.key === "Enter" && add()} style={{ width: 280 }} mono />
      <Select aria-label="Allow or deny" value={allow} onChange={setAllow} options={[{ value: "1", label: "Allow" }, { value: "0", label: "Deny" }]} />
      <Button size="s" onClick={add}>Add rule</Button>
    </span>
  );
}

function GroupsTab({ d, put }: { d: PermissionsModel; put: (m: PermissionsModel) => void }) {
  const group = useEdit("permissions_group", put);
  const remove = useEdit("permissions_group_remove", put);
  const map = useEdit("permissions_map_user_group", put);
  const reload = useEdit("permissions_reload", put);
  const [name, setName] = useState("");
  const names = Object.keys(d.groups);
  const edit = (args: Record<string, Json>, msg: string) => group.mutate(args, { onSuccess: () => toastOk(msg) });
  const create = () => {
    const g = name.trim();
    if (!GROUP.test(g)) return toastError({ message: "Group names: 1 to 40 of A-Z a-z 0-9 _ : -" });
    edit({ group: g }, `Created group ${g}`);
    setName("");
  };
  const dup: Record<string, string[]> = {};
  for (const u of d.serverUserGroups ?? []) (dup[u.rights] ??= []).push(u.name);
  const file = String(d.file ?? "").replace(/^.*[\\/]kiln[\\/]/, "kiln/").replace(/\\/g, "/");
  return (
    <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
      <Card title="Kiln groups" actions={<span className="ka-inline"><TextInput aria-label="New group" placeholder="new group name" maxLength={40} value={name} onChange={(e) => setName(e.target.value)} onKeyDown={(e) => e.key === "Enter" && create()} style={{ width: 170 }} /><Button size="s" onClick={create}>Create</Button></span>}>
        <div className="ka-list">
          {names.map((g) => {
            const x = d.groups[g];
            const isDefault = g.toLowerCase() === String(d.defaultGroup).toLowerCase();
            const others = names.filter((o) => o !== g && !(x.inherits ?? []).includes(o));
            return (
              <div key={g} className="k-stack" style={{ gap: 8 }}>
                <div className="ka-row-item">
                  <b className="ka-person-name">{g}</b>
                  {isDefault ? <Badge tone="ember" title="Every player is in it">every player</Badge> : null}
                  {x.admin ? <Badge tone="warn">counts as admin</Badge> : null}
                  <span className="k-spacer" />
                  <Toggle checked={!!x.admin} label="Admin" onChange={(v) => edit({ group: g, admin: v }, `${g}: ${v ? "counts as admin" : "no longer admin"}`)} />
                  <Button size="s" variant="ghost" disabled={isDefault} title={isDefault ? "The default group can't be removed" : undefined} onClick={async () => {
                    if (await confirm({ title: `Remove the group ${g}?`, body: <p>Its rules go, and players and user groups lose their membership.</p>, ok: "Remove", danger: true })) remove.mutate({ group: g }, { onSuccess: () => toastOk(`Removed group ${g}`) });
                  }}>Remove</Button>
                </div>
                <div className="ka-inline">
                  <span className="ka-label">Inherits</span>
                  <span className="ka-chips">
                    {(x.inherits ?? []).length ? (x.inherits ?? []).map((i) => (
                      <span key={i} className="ka-chip">{i}<button type="button" title="Stop inheriting" onClick={() => edit({ group: g, inherits: (x.inherits ?? []).filter((y) => y !== i) }, `${g} no longer inherits ${i}`)}>×</button></span>
                    )) : <span className="k-muted">nothing</span>}
                  </span>
                  {others.length ? <Select aria-label="Inherit" value="" onChange={(v) => v && edit({ group: g, inherits: [...(x.inherits ?? []), v] }, `${g} now inherits ${v}`)} options={[{ value: "", label: "+ inherit…" }, ...others]} /> : null}
                </div>
                <div className="ka-inline" style={{ alignItems: "flex-start" }}>
                  <span className="ka-label">Rules</span>
                  <Rules rules={x.nodes ?? []} onRemove={(node) => edit({ group: g, node, allow: null }, `${g}: removed ${node}`)} />
                </div>
                <div className="ka-inline"><span className="ka-label" /><RuleForm onAdd={(node, allow) => edit({ group: g, node, allow }, `${g}: ${show(allow)}s ${node}`)} /></div>
              </div>
            );
          })}
        </div>
      </Card>
      <div className="k-stack" style={{ gap: 16 }}>
        <Card title="Server user groups" sub="The password groups of enshrouded_server.json, mapped to Kiln groups" flush>
          <Table
            aria-label="User groups"
            rows={d.serverUserGroups ?? []}
            rowKey={(u) => u.name}
            rowHeight={72}
            columns={[
              { key: "u", header: "User group", width: "minmax(0, 1fr)", render: (u) => (
                <div><b>{u.name}</b><div className="k-hint">{u.rights === "None" ? "no rights" : u.rights}</div>
                  {dup[u.rights].length > 1 ? <div className="k-hint" style={{ color: "var(--k-warn)" }}>Same rights as {dup[u.rights].filter((n) => n !== u.name).join(", ")}: Kiln can't tell them apart</div> : null}</div>
              ) },
              { key: "k", header: "Kiln groups", width: "minmax(0, 1.2fr)", render: (u) => {
                const mapped = d.userGroups?.[u.name] ?? [];
                return (
                  <div className="ka-flags">
                    {names.map((g) => (
                      <label key={g}><input type="checkbox" checked={mapped.includes(g)} onChange={(e) => {
                        const groups = e.target.checked ? [...mapped, g] : mapped.filter((x) => x !== g);
                        map.mutate({ userGroup: u.name, groups }, { onSuccess: () => toastOk(`User group ${u.name} → ${groups.join(", ") || "no Kiln group"}`) });
                      }} /> {g}</label>
                    ))}
                  </div>
                );
              } },
            ]}
            empty={<EmptyState title="No user groups" hint="The server config has no user groups." />}
          />
        </Card>
        <Banner tone="info">
          A player's own rules come first, then their groups': their own, the ones their user group maps to, <b>{d.defaultGroup}</b>, and what those inherit.
          The most specific rule wins (exact, then the longest <span className="k-mono">prefix.*</span>, then <span className="k-mono">*</span>); a deny wins a tie.
          No rule: the node's own level decides. {d.kickBanIsAdmin ? "Players whose user group may kick and ban count as admins." : ""}
        </Banner>
        <Card title="The file" sub={<span className="k-mono">{file}</span>} actions={<Button size="s" variant="ghost" loading={reload.isPending} onClick={() => reload.mutate({}, { onSuccess: () => toastOk("Re-read permissions.json") })}>Re-read now</Button>}>
          <span className="k-hint">You can also edit it by hand: Kiln reads it again within a second.</span>
        </Card>
      </div>
    </div>
  );
}

function PlayersTab({ d, put, person, setPerson, check }: { d: PermissionsModel; put: (m: PermissionsModel) => void; person: string | null; setPerson: (s: string) => void; check: (sid: string) => void }) {
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
      <Card title="Players" flush>
        <div style={{ padding: "10px 12px", borderBottom: "1px solid var(--k-line)" }} className="ka-inline">
          <TextInput aria-label="SteamID64" placeholder="SteamID64" maxLength={17} value={add} onChange={(e) => setAdd(e.target.value)} style={{ width: 170 }} mono />
          <Button size="s" onClick={() => { const e = steamIdError(add); if (e) return toastError({ message: e }); setPerson(add.trim()); setAdd(""); }}>Open</Button>
        </div>
        <div className="ka-pick" style={{ padding: 6 }}>
          {people.length ? people.map((p) => (
            <button key={p.steamId} className={p.steamId === sel ? "active" : ""} onClick={() => setPerson(p.steamId)}>
              {online.has(p.steamId) ? <Badge tone="ok">on</Badge> : null}<span>{p.name || "Unknown"}</span><small className="k-mono">{p.steamId}</small>
            </button>
          )) : <EmptyState title="No players" hint="Players appear once they have joined while the launcher watched, or once they have rules." />}
        </div>
      </Card>
      {sel ? <PersonPanel d={d} put={put} sid={sel} name={people.find((p) => p.steamId === sel)?.name ?? online.get(sel)?.name ?? null} online={online.get(sel) ?? null} lastSeen={people.find((p) => p.steamId === sel)?.lastSeen ?? 0} check={check} /> : <Card><EmptyState title="Pick a player" /></Card>}
    </div>
  );
}

function PersonPanel({ d, put, sid, name, online, lastSeen, check }: { d: PermissionsModel; put: (m: PermissionsModel) => void; sid: string; name: string | null; online: { admin: boolean; permissions: string } | null; lastSeen: number; check: (sid: string) => void }) {
  const node = useEdit("permissions_player_node", put);
  const group = useEdit("permissions_player_group", put);
  const rec = d.players?.[sid] ?? { groups: [], nodes: [], name: null };
  const groups = Object.keys(d.groups).filter((g) => !rec.groups.includes(g));
  const who = name || rec.name || sid;
  const nameArg: Record<string, Json> = name ? { name: name.slice(0, 64) } : {};
  const ug = online ? (d.serverUserGroups ?? []).filter((u) => u.rights === online.permissions).map((u) => u.name) : [];
  const via = ug.length === 1 ? d.userGroups?.[ug[0]] ?? [] : [];
  return (
    <Card title={<span className="ka-person"><Avatar name={who} online={!!online} /><span>{who}</span></span>} sub={<span className="k-mono">{sid}{rec.name && rec.name !== name ? ` · note "${rec.name}"` : ""}</span>}
      actions={<Button size="s" onClick={() => check(sid)}>Check a node…</Button>}>
      <div className="k-stack" style={{ gap: 14 }}>
        {online ? (
          <KeyValue rows={[
            ["In the world", <>{online.admin ? <Badge tone="warn">admin</Badge> : null} {online.permissions}</>],
            ["User group", ug.length === 1 ? <><b>{ug[0]}</b>{via.length ? ` → ${via.join(", ")}` : " (mapped to no Kiln group)"}</> : ug.length ? `ambiguous: ${ug.join(" or ")}` : "none matches these rights"],
          ]} />
        ) : lastSeen ? <span className="k-hint">Last seen {agoS(lastSeen)}</span> : null}
        <div className="ka-inline">
          <span className="ka-label">Groups</span>
          <span className="ka-chips">
            {rec.groups.length ? rec.groups.map((g) => (
              <span key={g} className="ka-chip">{g}<button type="button" title="Take out of this group" onClick={() => group.mutate({ steamId: sid, group: g, add: false }, { onSuccess: () => toastOk(`${who} left ${g}`) })}>×</button></span>
            )) : <span className="k-muted">only the groups everyone gets</span>}
          </span>
          {groups.length ? <Select aria-label="Add to group" value="" onChange={(g) => g && group.mutate({ steamId: sid, group: g, add: true, ...nameArg }, { onSuccess: () => toastOk(`${who} joined ${g}`) })} options={[{ value: "", label: "+ add to group…" }, ...groups]} /> : null}
        </div>
        <div className="ka-inline" style={{ alignItems: "flex-start" }}>
          <span className="ka-label">Own rules</span>
          <Rules rules={rec.nodes} onRemove={(n) => node.mutate({ steamId: sid, node: n, allow: null }, { onSuccess: () => toastOk(`${who}: removed ${n}`) })} />
        </div>
        <div className="ka-inline"><span className="ka-label" /><RuleForm onAdd={(n, allow) => node.mutate({ steamId: sid, node: n, allow, ...nameArg }, { onSuccess: () => toastOk(`${who}: ${show(allow)}ed ${n}`) })} /></div>
        <span className="k-hint">A player's own rules beat every group rule.</span>
      </div>
    </Card>
  );
}

function explainFrom(from: string | null) {
  if (!from) return "";
  const [src, rest] = from.split(":");
  if (src === "default") return `the node's default: ${rest}`;
  if (src === "player") return "the player's own rule";
  if (src === "group") return `group ${rest}`;
  return from;
}

function CheckTab({ d, initial }: { d: PermissionsModel; initial: { sid: string; node: string } }) {
  const id = useServerId();
  const hist = useHistory(id);
  const people = knownPlayers(hist, d.players ?? {});
  const [sid, setSid] = useState(initial.sid);
  const [node, setNode] = useState(initial.node);
  const [res, setRes] = useState<(PermissionCheck & { who: string }) | null>(null);
  const run = useAdminCall<PermissionCheck>("permissions_check");
  const go = () => {
    const e1 = steamIdError(sid);
    if (e1) return toastError({ message: "Pick a player or give a SteamID64." });
    const n = node.trim().toLowerCase();
    const e2 = nodeError(n);
    if (e2) return toastError({ message: e2 });
    run.mutate({ steamId: sid.trim(), node: n }, { onSuccess: (r) => setRes({ ...r, who: people.find((p) => p.steamId === sid.trim())?.name || sid }), onError: (e) => { setRes(null); toastError(e); } });
  };
  return (
    <>
      <Card title="Check a permission" sub="As Kiln decides it right now">
        <div className="ka-inline">
          <Select aria-label="Player" value={people.some((p) => p.steamId === sid) ? sid : ""} onChange={(v) => v && setSid(v)} options={[{ value: "", label: "Player…" }, ...people.map((p) => ({ value: p.steamId, label: p.name || p.steamId }))]} />
          <span className="k-muted">or</span>
          <TextInput aria-label="SteamID64" placeholder="SteamID64" maxLength={17} value={sid} onChange={(e) => setSid(e.target.value)} style={{ width: 170 }} mono />
          <TextInput aria-label="Node" list="ka-nodes" placeholder="node, e.g. mymod.command.home" value={node} onChange={(e) => setNode(e.target.value)} onKeyDown={(e) => e.key === "Enter" && go()} style={{ width: 300 }} mono />
          <Button variant="primary" loading={run.isPending} onClick={go}>Check</Button>
        </div>
      </Card>
      {res ? (
        <Card>
          <div className="k-stack">
            <div className="ka-inline"><Badge tone={res.allowed ? "ok" : "err"}>{res.allowed ? "Allowed" : "Denied"}</Badge><span className="k-mono">{res.node}</span><span className="k-muted">for {res.who}</span></div>
            <KeyValue rows={[
              ["Decided by", <>{res.rule ? <>rule <span className="k-mono">{res.rule}</span></> : "no rule matched"} {res.from ? <span className="k-muted">({explainFrom(res.from)})</span> : null}</>],
              ["Node level", <Badge tone={LEVEL_TONE[String(res.level).toLowerCase()] ?? "idle"}>{String(res.level).toLowerCase()}</Badge>],
              ["Groups", res.groups.length ? res.groups.join(", ") : "-"],
            ]} />
          </div>
        </Card>
      ) : null}
    </>
  );
}

function NodesTab({ d, check }: { d: PermissionsModel; check: (node: string) => void }) {
  return (
    <Card title="Registered nodes" sub="What loaded mods ask for; commands register <mod>.command.<name>" flush>
      <Table
        aria-label="Nodes"
        rows={d.nodes ?? []}
        rowKey={(n) => n.node}
        columns={[
          { key: "n", header: "Node", width: "minmax(0, 1.4fr)", render: (n) => <span className="k-mono">{n.node}</span>, sort: (a, b) => a.node.localeCompare(b.node) },
          { key: "l", header: "Default", width: "110px", render: (n) => <Badge tone={LEVEL_TONE[n.level] ?? "idle"}>{n.level}</Badge> },
          { key: "d", header: "What", render: (n) => n.description ?? "" },
          { key: "o", header: "Mod", width: "120px", render: (n) => <span className="k-mono k-muted">{n.owner ?? ""}</span> },
          { key: "c", header: "", width: "90px", align: "right", render: (n) => <Button size="s" variant="ghost" onClick={() => check(n.node)}>Check…</Button> },
        ]}
        empty={<EmptyState title="No nodes registered" />}
      />
    </Card>
  );
}

export default withAdmin(PermissionsPage);
