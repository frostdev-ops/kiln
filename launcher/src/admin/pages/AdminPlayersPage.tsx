// /servers/:id/admin/players: who is in the world (live from the state pushes), SteamIDs,
// teleport (to a player, a waypoint, a grave or coordinates), give items, heal, god mode, kill (typed confirmation),
// inventory, tell, progression (level, experience, skill points: kiln.progression); plus everyone
// seen on this server.
import { useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer } from "../../api/admin";
import { CoordInputs, ItemPicker, NeedsOp, coordText, parseCoords, playerName, useServerId, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, saveWaypoint, useHistory } from "../history";
import type { GiveResult, HealthResult, InventoryResult, ItemHit, PermissionsModel } from "../types";
import {
  Avatar, Badge, Button, Card, EmptyState, HealthBar, KeyValue, NumberInput, Page, PageHeader, Select, Table, TextInput, Toggle,
  agoS, confirm, fmtPos, promptText, toastError, toastOk,
} from "../ui";

const RARITIES = ["", "Common", "Uncommon", "Rare", "Epic", "Legendary"];

function PlayersPage() {
  const st = useAdminState();
  const id = useServerId();
  const hist = useHistory(id);
  const players = st.state?.players ?? [];
  const perms = useAdminQuery<PermissionsModel>("permissions_get", {}, { staleTime: 30_000 });
  const known = knownPlayers(hist, perms.data?.players ?? {});
  const online = new Set(players.map((p) => p.steamId));
  return (
    <Page>
      <PageHeader title="Players" sub="Live from the server's state, every second. Actions run on the server at once." />
      <OfflineNote />
      {st.session ? (
        <NeedsOp ops="players" what="The player list">
          {players.length ? (
            <div className="ka-players">
              {players.map((p) => <PlayerCard key={p.entity} p={p} players={players} />)}
            </div>
          ) : (
            <Card>
              <EmptyState title="Nobody is in the world" hint="Players show up here while they're connected. The tools below act on NPCs and spawned creatures on the Combat and Spawn pages." />
            </Card>
          )}
        </NeedsOp>
      ) : null}
      <Card title="Everyone seen on this server" sub="Recorded by the launcher while it was connected, plus the names in the permissions file" flush>
        <Table
          aria-label="Known players"
          rows={known}
          rowKey={(r) => r.steamId}
          columns={[
            { key: "name", header: "Player", render: (r) => <span className="ka-person"><Avatar name={r.name || "?"} online={online.has(r.steamId)} /><span className="ka-person-name">{r.name || "Unknown"}</span></span>, sort: (a, b) => a.name.localeCompare(b.name) },
            { key: "sid", header: "SteamID64", width: "200px", render: (r) => <span className="k-mono">{r.steamId}</span> },
            { key: "seen", header: "Last seen", width: "140px", render: (r) => (online.has(r.steamId) ? <Badge tone="ok">online</Badge> : r.lastSeen ? agoS(r.lastSeen) : "in permissions.json"), sort: (a, b) => a.lastSeen - b.lastSeen },
            { key: "first", header: "First seen", width: "140px", render: (r) => (r.firstSeen ? agoS(r.firstSeen) : "-") },
          ]}
          empty={<EmptyState title="No players yet" hint="Players are remembered once they join while the launcher is connected." />}
        />
      </Card>
    </Page>
  );
}

function PlayerCard({ p, players }: { p: AdminPlayer; players: AdminPlayer[] }) {
  const id = useServerId();
  const name = playerName(p);
  const heal = useAdminCall<HealthResult & { healed: number }>("heal");
  const god = useAdminCall<{ god: boolean }>("god");
  const kill = useAdminCall<{ killed: boolean; state: string }>("kill");
  const hasCombat = useHasOp("heal");
  const [inv, setInv] = useState(false);
  const askKill = async () => {
    if (!(await confirm({ title: `Kill ${name}?`, body: <p>{name} dies at once and respawns as after any death, leaving a tombstone as the server's death rules say.</p>, typed: name, ok: "Kill" }))) return;
    kill.mutate({ entity: p.entity }, { onSuccess: (r) => (r.killed ? toastOk(`${name} was killed`) : toastError({ message: `${name}: ${r.state}` })), onError: (e) => toastError(e) });
  };
  return (
    <Card>
      <div className="ka-player-head" style={{ marginBottom: 12 }}>
        <span className="ka-person">
          <Avatar name={name} online />
          <span>
            <span className="ka-person-name">{name}</span>{" "}
            {p.admin ? <Badge tone="warn">admin</Badge> : null} {p.god ? <Badge tone="ember">god mode</Badge> : null}
            {p.healthMax && (p.health ?? 0) <= 0 ? <Badge tone="err">dead</Badge> : null}
            <div className="ka-person-meta">
              <span className="k-mono">{p.steamId ?? "no SteamID"}</span> · entity {p.entity} · at <span className="k-mono">{fmtPos(p.pos)}</span>
            </div>
          </span>
        </span>
        <span className="k-spacer" />
        {p.healthMax ? <span className="ka-inline"><HealthBar health={p.health} max={p.healthMax} /><span className="k-hint">{p.health} / {p.healthMax}</span></span> : null}
      </div>
      <div className="ka-player-tools">
        <KeyValue rows={[["Rights", p.permissions || "none"]]} />
        <TeleportRow p={p} players={players} />
        <GiveRow p={p} />
        <div className="ka-inline">
          <span className="ka-label">Combat</span>
          <Button size="s" disabled={!hasCombat} loading={heal.isPending} onClick={() => heal.mutate({ entity: p.entity }, { onSuccess: (r) => toastOk(`Healed ${name} (+${r.healed ?? 0})`), onError: (e) => toastError(e) })}>
            Heal to full
          </Button>
          <Toggle checked={!!p.god} disabled={!hasCombat || god.isPending} label="God mode" onChange={(on) => god.mutate({ entity: p.entity, on }, { onSuccess: (r) => toastOk(`God mode ${r.god ? "on" : "off"} for ${name}`), onError: (e) => toastError(e) })} />
          <Button size="s" variant="danger" disabled={!hasCombat} loading={kill.isPending} onClick={askKill}>Kill…</Button>
          <span className="k-spacer" />
          <Button size="s" variant="ghost" onClick={() => setInv((v) => !v)}>{inv ? "Hide inventory" : "Inventory"}</Button>
          <Button size="s" variant="ghost" disabled={!p.pos} onClick={async () => {
            const wp = await promptText({ title: "Save this spot as a waypoint", ok: "Save", prompt: { placeholder: "e.g. Home base", maxLength: 40, initial: `${name} spot`.slice(0, 40) } });
            if (wp && p.pos) {
              const err = saveWaypoint(id, wp, p.pos);
              if (err) toastError({ message: err });
              else toastOk(`Saved waypoint ${wp}`);
            }
          }}>Save spot as waypoint</Button>
        </div>
        <ProgressionRow p={p} />
        <TellRow p={p} />
        {inv ? <Inventory entity={p.entity} /> : null}
      </div>
    </Card>
  );
}

function TeleportRow({ p, players }: { p: AdminPlayer; players: AdminPlayer[] }) {
  const id = useServerId();
  const hist = useHistory(id);
  const [target, setTarget] = useState("");
  const [coords, setCoords] = useState<CoordText>(coordText(null));
  const [safe, setSafe] = useState(true);
  const tp = useAdminCall("teleport");
  const tpp = useAdminCall("teleport_to_player");
  const has = useHasOp("teleport");
  const others = players.filter((q) => q.entity !== p.entity);
  const options = [
    { value: "", label: "Teleport to…" },
    ...others.map((q) => ({ value: `player:${q.entity}`, label: `Player: ${playerName(q)}` })),
    ...hist.waypoints.map((w) => ({ value: `waypoint:${w.name}`, label: `Waypoint: ${w.name}` })),
    ...hist.graves.slice(-15).reverse().map((g) => ({ value: `grave:${g.key}`, label: `Grave: ${g.name ?? "someone"} (${agoS(g.at)})` })),
    { value: "coords", label: "Coordinates…" },
  ];
  const go = () => {
    const done = { onSuccess: () => toastOk(`Teleported ${playerName(p)}`), onError: (e: unknown) => toastError(e) };
    if (!target) return toastError({ message: "Pick where to teleport to." });
    if (target.startsWith("player:")) {
      if (!tpp) return;
      return tpp.mutate({ entity: p.entity, target: Number(target.slice(7)) }, done);
    }
    let pos = null;
    if (target === "coords") {
      const r = parseCoords(coords);
      if (r.error) return toastError({ message: r.error });
      pos = r.pos;
    } else if (target.startsWith("waypoint:")) pos = hist.waypoints.find((w) => w.name === target.slice(9))?.pos ?? null;
    else if (target.startsWith("grave:")) {
      const g = hist.graves.find((x) => x.key === target.slice(6));
      pos = g ? { ...g.pos, y: g.pos.y + 1 } : null;
    }
    if (!pos) return toastError({ message: "Unknown target." });
    tp.mutate({ entity: p.entity, x: pos.x, y: pos.y, z: pos.z, safe }, done);
  };
  return (
    <div className="ka-inline">
      <span className="ka-label">Teleport</span>
      <Select aria-label="Teleport target" value={target} onChange={setTarget} options={options} style={{ minWidth: 220 }} />
      {target === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
      {target && !target.startsWith("player:") ? <Toggle checked={safe} onChange={setSafe} label="Find ground" /> : null}
      <Button size="s" variant="primary" disabled={!has} loading={tp.isPending || tpp.isPending} onClick={go}>Go</Button>
    </div>
  );
}

function GiveRow({ p }: { p: AdminPlayer }) {
  const [item, setItem] = useState<ItemHit | null>(null);
  const [count, setCount] = useState(1);
  const [level, setLevel] = useState<number | null>(null);
  const [rarity, setRarity] = useState("");
  const give = useAdminCall<GiveResult>("give_item");
  const has = useHasOp("give_item");
  const send = () => {
    if (!item) return toastError({ message: "Pick an item from the list." });
    if (!Number.isInteger(count) || count < 1 || count > 10000) return toastError({ message: "Amount: 1 to 10000." });
    if (level != null && (!Number.isInteger(level) || level < 1 || level > 100)) return toastError({ message: "Level: 1 to 100." });
    const args: Record<string, number | string> = { entity: p.entity, item: item.id, count };
    if (item.gear && level != null) args.level = level;
    if (item.gear && rarity) args.rarity = rarity;
    give.mutate(args, {
      onSuccess: (r) => toastOk(`Gave ${r.given} × ${item.name}${r.given < r.requested ? ` (only room for ${r.given})` : ""}`),
      onError: (e) => toastError(e),
    });
  };
  return (
    <div className="ka-inline">
      <span className="ka-label">Give item</span>
      <div style={{ width: 300 }}><ItemPicker value={item} onPick={setItem} /></div>
      <NumberInput aria-label="Amount" value={count} integer min={1} max={10000} onCommit={setCount} style={{ width: 90 }} />
      {item?.gear ? (
        <>
          <NumberInput aria-label="Level" placeholder="level" value={level} integer min={1} max={100} onCommit={setLevel} style={{ width: 80 }} />
          <Select aria-label="Rarity" value={rarity} onChange={setRarity} options={RARITIES.map((r) => ({ value: r, label: r || "rarity" }))} />
        </>
      ) : null}
      <Button size="s" variant="primary" disabled={!has} loading={give.isPending} onClick={send}>Give</Button>
    </div>
  );
}

// Character progression (Kiln.Admin Ops.Progression.cs over kiln.progression; docs/kiln/services/progression.md).
interface ProgressionSnap {
  level: number; levelCap: number; experience: number; experienceNeeded: number; experiencePending: number;
  skillPoints: number; skillPointsTotal: number; skillPointsSpent: number; skillPointsFromLevel: number;
  skillPointsFromKnowledge: number; givableSkillPoints: number; unlockedNodes: number; atLevelCap: boolean;
  overBudget: boolean; hasLevel: boolean; hasSkills: boolean; hasPoints: boolean;
}
interface ProgressionView { entity: number; name: string; progression?: ProgressionSnap }
interface ProgressionResultView { ok: boolean; op: string; status: string; applied: number; reason: string; after?: ProgressionSnap | null }
interface ProgressionSetView { steps: ProgressionResultView[] }

function ProgressionRow({ p }: { p: AdminPlayer }) {
  const name = playerName(p);
  const has = useHasOp("players_progression");
  const q = useAdminQuery<ProgressionView>("players_progression", { entity: p.entity }, { refetchInterval: 5_000 });
  const opts = { invalidates: ["players_progression"] };
  const setLevel = useAdminCall<ProgressionSetView>("players_progression_set", opts);
  const giveXp = useAdminCall<ProgressionResultView>("give_xp", opts);
  const givePts = useAdminCall<ProgressionResultView>("give_skill_points", opts);
  const respec = useAdminCall<ProgressionResultView>("skill_respec", opts);
  const [level, setLevelText] = useState<number | null>(null);
  const [xp, setXp] = useState(1000);
  const [pts, setPts] = useState(1);
  if (!has) return null;
  const s = q.data?.progression;
  const told = (what: string) => ({
    onSuccess: (r: ProgressionResultView) => (r.ok ? toastOk(`${what} for ${name}`, r.reason || undefined) : toastError({ message: r.reason || r.status })),
    onError: (e: unknown) => toastError(e),
  });
  const doLevel = () => {
    if (level == null || !Number.isInteger(level) || level < 1 || level > (s?.levelCap || 1000)) return toastError({ message: `Level: 1 to ${s?.levelCap || "the cap"}.` });
    setLevel.mutate({ entity: p.entity, level }, {
      onSuccess: (r) => { const x = r.steps[0]; if (x?.ok) toastOk(`${name} is level ${x.after?.level ?? level}`, x.reason || undefined); else toastError({ message: x?.reason || "refused" }); },
      onError: (e) => toastError(e),
    });
  };
  const doRespec = async () => {
    if (!(await confirm({ title: `Respec ${name}?`, body: <p>Every skill node of {name} is locked again and all spent skill points come back. It is free, like the game's own reset.</p>, ok: "Respec" }))) return;
    respec.mutate({ entity: p.entity }, told("Skill points reset"));
  };
  const busy = setLevel.isPending || giveXp.isPending || givePts.isPending || respec.isPending;
  return (
    <div className="ka-inline">
      <span className="ka-label">Progression</span>
      {s?.hasLevel ? (
        <>
          <Badge tone="info">level {s.level}/{s.levelCap}</Badge>
          <span className="k-hint" title="Experience into the level / what the level needs">
            {s.atLevelCap ? "at the level cap" : `${s.experience.toLocaleString()} / ${s.experienceNeeded.toLocaleString()} XP`}
            {s.experiencePending ? ` (+${s.experiencePending.toLocaleString()})` : ""}
          </span>
          {s.hasPoints ? (
            <span className="k-hint" title={`${s.skillPointsFromLevel} from levels, ${s.skillPointsFromKnowledge} from skill-point knowledge; ${s.unlockedNodes} nodes unlocked`}>
              · {s.skillPoints} of {s.skillPointsTotal} skill points unspent
            </span>
          ) : null}
          {s.overBudget ? <Badge tone="err" >over budget: the game resets the tree at the next login</Badge> : null}
        </>
      ) : <span className="k-muted">{q.error ? q.error.message : q.data ? "no progression data" : "reading…"}</span>}
      <span className="k-spacer" />
      <NumberInput aria-label="Level" placeholder="level" value={level} integer min={1} max={s?.levelCap || 1000} onCommit={setLevelText} style={{ width: 70 }} />
      <Button size="s" disabled={busy || !s?.hasLevel} loading={setLevel.isPending} onClick={doLevel}>Set level</Button>
      <NumberInput aria-label="Experience" value={xp} integer min={1} max={100000000} onCommit={setXp} style={{ width: 90 }} />
      <Button size="s" disabled={busy || !s?.hasLevel || s.atLevelCap} loading={giveXp.isPending} onClick={() => giveXp.mutate({ entity: p.entity, amount: xp }, told(`Gave ${xp} XP`))}>Give XP</Button>
      <NumberInput aria-label="Skill points" value={pts} integer min={1} max={1000} onCommit={setPts} style={{ width: 60 }} />
      <Button size="s" disabled={busy || !s?.hasPoints || !s.givableSkillPoints} loading={givePts.isPending}
        title={s ? `Given as the game's skill-point knowledge: ${s.givableSkillPoints} more can be given; for more, raise the level` : undefined}
        onClick={() => givePts.mutate({ entity: p.entity, amount: pts }, told(`Gave ${pts} skill point(s)`))}>Give points</Button>
      <Button size="s" variant="ghost" disabled={busy || !s?.hasSkills} loading={respec.isPending} onClick={doRespec}>Respec…</Button>
    </div>
  );
}

function TellRow({ p }: { p: AdminPlayer }) {
  const [text, setText] = useState("");
  const tell = useAdminCall("tell");
  const has = useHasOp("tell");
  if (!has) return null;
  const send = () => {
    const t = text.trim();
    if (!t || t.length > 500) return toastError({ message: "A message of 1 to 500 characters." });
    tell.mutate({ entity: p.entity, text: t }, { onSuccess: () => { toastOk(`Told ${playerName(p)}`); setText(""); }, onError: (e) => toastError(e) });
  };
  return (
    <div className="ka-inline">
      <span className="ka-label">Message</span>
      <TextInput aria-label="Message" value={text} maxLength={500} placeholder={`A chat line only ${playerName(p)} sees`} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && send()} style={{ flex: 1, minWidth: 240 }} />
      <Button size="s" loading={tell.isPending} onClick={send}>Send</Button>
    </div>
  );
}

function Inventory({ entity }: { entity: number }) {
  const q = useAdminQuery<InventoryResult>("inventory", { entity }, { refetchInterval: 10_000 });
  if (q.error) return <div className="k-error-text">{q.error.message}</div>;
  if (!q.data) return <div className="k-muted">Reading the inventory…</div>;
  const list = q.data.inventories ?? [];
  if (!list.length) return <div className="k-muted">No inventories reported.</div>;
  return (
    <div className="k-grid k-grid-3">
      {list.map((inv) => (
        <Card key={inv.link} title={inv.category} sub={`${inv.slots.length} slots used`}>
          <div className="ka-list">
            {inv.slots.map((s, i) => (
              <div key={i} className="ka-row-item">
                <span>{s.name ?? `#${s.item}`}</span>
                {s.level ? <Badge tone="info">lv {s.level}</Badge> : null}
                {s.rarity ? <Badge tone="ember">{s.rarity}</Badge> : null}
                {s.equipped ? <Badge tone="ok">equipped</Badge> : null}
                <span className="k-spacer" />
                <span className="k-muted">× {s.count}</span>
              </div>
            ))}
          </div>
        </Card>
      ))}
    </div>
  );
}

export default withAdmin(PlayersPage);
