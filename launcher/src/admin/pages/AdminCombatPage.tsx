// /servers/:id/admin/combat: health of everyone in the world, heal, god mode, kill (typed
// confirmation); any entity's health by id (NPCs, spawned creatures) with damage and kill; creatures near a player or
// a spot; and the deaths feed with the killer (kiln.combat's EntityDeathEvent, kept per server by the launcher).
import { useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer } from "../../api/admin";
import { CoordInputs, NeedsOp, coordText, parseCoords, playerName, useServerId, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { useHistory, type DeathRecord } from "../history";
import type { EnemiesResult, HealthResult } from "../types";
import { Avatar, Badge, Button, Card, EmptyState, HealthBar, NumberInput, Page, PageHeader, Select, Table, Toggle, agoS, confirm, fmtPos, toastError, toastOk, whenS } from "../ui";

const ORIGIN: Record<string, string> = { Game: "in combat", Kiln: "by an admin action", Other: "fall, drowning or the like" };

function CombatPage() {
  const st = useAdminState();
  const players = st.state?.players ?? [];
  return (
    <Page>
      <PageHeader title="Combat" sub="Health, healing, god mode and kills; who died to what." />
      <OfflineNote />
      <NeedsOp ops={["health", "heal", "god", "kill"]} what="Combat tools">
        <Card title="In the world" sub="Health updates with every state push">
          {players.length ? <div className="ka-list">{players.map((p) => <PlayerRow key={p.entity} p={p} />)}</div> : <EmptyState title="Nobody is in the world" hint="Players show up here while they're connected. Use the entity tools below for NPCs and creatures." />}
        </Card>
        <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
          <EntityCard />
          <NearCard players={players} />
        </div>
      </NeedsOp>
      <DeathsCard />
      <div className="k-hint">God mode is held by Kiln.Admin: it ends when Kiln.Admin reloads or the server restarts. Killing leaves the usual tombstone.</div>
    </Page>
  );
}

function PlayerRow({ p }: { p: AdminPlayer }) {
  const name = playerName(p);
  const heal = useAdminCall<{ healed: number }>("heal", { onSuccess: (r) => toastOk(`Healed ${name} (+${r.healed ?? 0})`), onError: (e) => toastError(e) });
  const god = useAdminCall<{ god: boolean }>("god", { onSuccess: (r) => toastOk(`God mode ${r.god ? "on" : "off"} for ${name}`), onError: (e) => toastError(e) });
  const kill = useAdminCall<{ killed: boolean; state: string }>("kill", { onSuccess: (r) => (r.killed ? toastOk(`${name} was killed`) : toastError({ message: `${name}: ${r.state || "no effect"}` })), onError: (e) => toastError(e) });
  const pct = p.healthMax ? Math.round(((p.health ?? 0) / p.healthMax) * 100) : null;
  const dead = p.healthMax && (p.health ?? 0) <= 0;
  const busy = heal.isPending || god.isPending || kill.isPending;
  return (
    <div className="ka-row-item" style={{ gap: 16 }}>
      <span className="ka-person" style={{ minWidth: 220 }}>
        <Avatar name={name} online />
        <span>
          <span className="ka-person-name">{name}</span> {p.god ? <Badge tone="ember">god mode</Badge> : null} {dead ? <Badge tone="err">dead</Badge> : null}
          <div className="ka-person-meta">{pct == null ? "health unknown" : `${p.health} / ${p.healthMax} health`}</div>
        </span>
      </span>
      <HealthBar health={p.health} max={p.healthMax} />
      <span className="k-spacer" />
      <Button size="s" disabled={busy || pct === 100} title={pct === 100 ? "Already at full health" : undefined} onClick={() => heal.mutate({ entity: p.entity })}>Heal to full</Button>
      <Toggle checked={!!p.god} disabled={busy} label="God mode" onChange={(on) => god.mutate({ entity: p.entity, on })} />
      <Button size="s" variant="danger" disabled={busy} onClick={async () => {
        if (await confirm({ title: `Kill ${name}?`, body: <p>{name} dies at once and respawns as after any death, leaving a tombstone with their things as the server's death rules say.</p>, typed: name, ok: "Kill" })) kill.mutate({ entity: p.entity });
      }}>Kill…</Button>
    </div>
  );
}

/** Any entity by id: health, damage, kill (Kiln.Admin's health/kill/damage take any entity). */
function EntityCard() {
  const [entity, setEntity] = useState<number | null>(null);
  const [amount, setAmount] = useState(50);
  const hasDamage = useHasOp("damage");
  const health = useAdminQuery<HealthResult>("health", { entity: entity ?? 0 }, { enabled: entity != null && entity >= 1, refetchInterval: 3000 });
  const damage = useAdminCall<{ state: string; health: number; killed: boolean }>("damage", { onSuccess: (r) => { toastOk(`Hit: ${r.state}, health ${r.health}${r.killed ? " (killed)" : ""}`); health.refetch(); }, onError: (e) => toastError(e) });
  const kill = useAdminCall<{ killed: boolean; state: string }>("kill", { onSuccess: (r) => { (r.killed ? toastOk(`Entity ${entity} killed`) : toastError({ message: r.state })); health.refetch(); }, onError: (e) => toastError(e) });
  const h = health.data;
  return (
    <Card title="Any entity" sub="NPCs, villagers, creatures: by entity id (from Spawn, the map or the creature list)">
      <div className="k-stack">
        <div className="ka-inline">
          <NumberInput aria-label="Entity id" placeholder="entity id" value={entity} integer min={1} onCommit={setEntity} style={{ width: 140 }} />
          {entity ? <Button size="s" variant="ghost" onClick={() => health.refetch()}>Read health</Button> : null}
        </div>
        {health.error ? <div className="k-error-text">{health.error.message}</div> : null}
        {h ? (
          <>
            <div className="ka-inline">
              <span className="k-mono">#{h.entity}</span>
              <HealthBar health={h.health} max={h.healthMax} />
              <span className="k-hint">{h.health} / {h.healthMax}</span>
              {h.dead ? <Badge tone="err">dead</Badge> : null}
              {h.god ? <Badge tone="ember">invulnerable</Badge> : null}
            </div>
            <div className="ka-inline">
              {hasDamage ? (
                <>
                  <NumberInput aria-label="Damage" value={amount} integer min={1} max={1000000} onCommit={setAmount} style={{ width: 100 }} />
                  <Button size="s" loading={damage.isPending} onClick={() => damage.mutate({ entity: h.entity, amount })}>Damage</Button>
                </>
              ) : null}
              <Button size="s" variant="danger" loading={kill.isPending} onClick={async () => {
                if (await confirm({ title: `Kill entity ${h.entity}?`, body: <p>A killing blow without credit. Players respawn; creatures drop their loot as usual.</p>, typed: String(h.entity), ok: "Kill" })) kill.mutate({ entity: h.entity });
              }}>Kill…</Button>
            </div>
          </>
        ) : null}
      </div>
    </Card>
  );
}

/** Creatures within a radius of a player or a spot (the `enemies` op). */
function NearCard({ players }: { players: AdminPlayer[] }) {
  const [atSel, setAt] = useState("");
  const at = atSel || (players[0] ? `p:${players[0].entity}` : "coords");
  const [coords, setCoords] = useState<CoordText>(coordText(null));
  const [radius, setRadius] = useState(80);
  const [scan, setScan] = useState<{ near: { x: number; y: number; z: number }; radius: number } | null>(null);
  const q = useAdminQuery<EnemiesResult>("enemies", scan ?? {}, { enabled: !!scan, refetchInterval: 5000 });
  const go = () => {
    let near = null;
    if (at.startsWith("p:")) near = players.find((p) => p.entity === Number(at.slice(2)))?.pos ?? null;
    else {
      const r = parseCoords(coords);
      if (r.error) return toastError({ message: r.error });
      near = r.pos;
    }
    if (!near) return toastError({ message: "No position for that player." });
    if (radius < 1 || radius > 500) return toastError({ message: "Radius: 1 to 500 m." });
    setScan({ near, radius });
  };
  const list = q.data?.near ?? [];
  return (
    <Card title="Creatures nearby" sub={q.data && scan ? `${list.length} within ${scan.radius} m · ${q.data.total} loaded on the server` : "Scans NetworkEnemy entities around a point"}>
      <div className="k-stack">
        <div className="ka-inline">
          <Select aria-label="Around" value={at} onChange={setAt} options={[...players.map((p) => ({ value: `p:${p.entity}`, label: `Around ${playerName(p)}` })), { value: "coords", label: "Around coordinates" }]} />
          {at === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
          <NumberInput aria-label="Radius" value={radius} integer min={1} max={500} onCommit={setRadius} style={{ width: 80 }} />
          <Button size="s" loading={q.isFetching && !q.data} onClick={go}>Scan</Button>
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        {scan && q.data ? (
          <Table
            aria-label="Creatures nearby"
            rows={list}
            rowKey={(r) => String(r.entity)}
            maxHeight={320}
            columns={[
              { key: "e", header: "Entity", width: "90px", render: (r) => <span className="k-mono">{r.entity}</span> },
              { key: "n", header: "Creature", render: (r) => r.name ?? r.guid ?? "?" },
              { key: "p", header: "Where", width: "150px", render: (r) => <span className="k-mono">{fmtPos(r.pos)}</span> },
            ]}
            empty={<EmptyState title="No creatures there" />}
          />
        ) : null}
      </div>
    </Card>
  );
}

function DeathsCard() {
  const id = useServerId();
  const h = useHistory(id);
  const rows = [...h.deaths].reverse().slice(0, 200);
  const killer = (d: DeathRecord) =>
    d.killerKind === "none" || (!d.killer && !d.killerName) ? (
      <span className="k-muted">nobody ({ORIGIN[d.origin] ?? d.origin ?? "?"})</span>
    ) : (
      <span className="ka-death-killer">
        {d.killerName ?? `entity ${d.killer}`} {d.killerKind ? <Badge tone={d.killerKind === "player" ? "warn" : "idle"}>{d.killerKind}</Badge> : null}
        {d.origin && d.origin !== "Game" ? <span className="k-muted">{ORIGIN[d.origin] ?? d.origin}</span> : null}
      </span>
    );
  return (
    <Card title="Deaths" sub="Kept by the launcher from kiln.combat's death events; newest first" flush>
      <Table
        aria-label="Deaths"
        rows={rows}
        rowKey={(r) => r.key}
        columns={[
          { key: "t", header: "When", width: "130px", render: (r) => <span title={whenS(r.time)}>{agoS(r.time)}</span>, sort: (a, b) => a.time - b.time },
          { key: "v", header: "Player", width: "180px", render: (r) => r.victimName ?? `player ${r.entity}` },
          { key: "k", header: "Killed by", render: killer },
          { key: "p", header: "Where", width: "170px", render: (r) => <span className="k-mono">{fmtPos(r.pos)}</span> },
        ]}
        empty={<EmptyState title="No deaths recorded" hint="Kiln reports each player death with who or what dealt the killing blow." />}
      />
    </Card>
  );
}

export default withAdmin(CombatPage);
