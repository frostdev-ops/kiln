// /servers/:id/admin/combat: health of everyone in the world, heal, god mode, kill (typed
// confirmation); any entity's health by id (NPCs, spawned creatures) with damage and kill; creatures near a player or
// a spot; and the deaths feed with the killer (kiln.combat's EntityDeathEvent, kept per server by the launcher).
import { useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer } from "../../api/admin";
import { t as tr, useT } from "../../i18n";
import { CoordInputs, NeedsOp, coordText, parseCoords, playerName, useServerId, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { useHistory, type DeathRecord } from "../history";
import type { EnemiesResult, HealthResult } from "../types";
import { Avatar, Badge, Button, Card, EmptyState, HealthBar, NumberInput, Page, PageHeader, Select, Table, Toggle, agoS, confirm, fmtPos, toastError, toastOk, whenS } from "../ui";

/** A death's origin as shown (unknown origins as they are). */
function originText(o: string | undefined): string | undefined {
  switch (o) {
    case "Game": return tr("admin.combat.originGame");
    case "Kiln": return tr("admin.combat.originKiln");
    case "Other": return tr("admin.combat.originOther");
    default: return o;
  }
}

function CombatPage() {
  const t = useT();
  const st = useAdminState();
  const players = st.state?.players ?? [];
  return (
    <Page>
      <PageHeader title={t("admin.combat.title")} sub={t("admin.combat.sub")} />
      <OfflineNote />
      <NeedsOp ops={["health", "heal", "god", "kill"]} what={t("admin.combat.what")}>
        <Card title={t("admin.combat.inWorld")} sub={t("admin.combat.inWorldSub")}>
          {players.length ? <div className="ka-list">{players.map((p) => <PlayerRow key={p.entity} p={p} />)}</div> : <EmptyState title={t("admin.combat.nobody")} hint={t("admin.combat.nobodyHint")} />}
        </Card>
        <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
          <EntityCard />
          <NearCard players={players} />
        </div>
      </NeedsOp>
      <DeathsCard />
      <div className="k-hint">{t("admin.combat.footHint")}</div>
    </Page>
  );
}

function PlayerRow({ p }: { p: AdminPlayer }) {
  const t = useT();
  const name = playerName(p);
  const heal = useAdminCall<{ healed: number }>("heal", { onSuccess: (r) => toastOk(t("admin.combat.healed", { name, n: r.healed ?? 0 })), onError: (e) => toastError(e) });
  const god = useAdminCall<{ god: boolean }>("god", { onSuccess: (r) => toastOk(r.god ? t("admin.players.godOn", { name }) : t("admin.players.godOff", { name })), onError: (e) => toastError(e) });
  const kill = useAdminCall<{ killed: boolean; state: string }>("kill", { onSuccess: (r) => (r.killed ? toastOk(t("admin.players.killed", { name })) : toastError({ message: r.state ? `${name}: ${r.state}` : t("admin.combat.noEffect", { name }) })), onError: (e) => toastError(e) });
  const pct = p.healthMax ? Math.round(((p.health ?? 0) / p.healthMax) * 100) : null;
  const dead = p.healthMax && (p.health ?? 0) <= 0;
  const busy = heal.isPending || god.isPending || kill.isPending;
  return (
    <div className="ka-row-item" style={{ gap: 16 }}>
      <span className="ka-person" style={{ minWidth: 220 }}>
        <Avatar name={name} online />
        <span>
          <span className="ka-person-name">{name}</span> {p.god ? <Badge tone="ember">{t("admin.combat.godBadge")}</Badge> : null} {dead ? <Badge tone="err">{t("admin.combat.dead")}</Badge> : null}
          <div className="ka-person-meta">{pct == null ? t("admin.combat.healthUnknown") : t("admin.ui.healthTitle", { health: String(p.health), max: p.healthMax })}</div>
        </span>
      </span>
      <HealthBar health={p.health} max={p.healthMax} />
      <span className="k-spacer" />
      <Button size="s" disabled={busy || pct === 100} title={pct === 100 ? t("admin.combat.fullHealth") : undefined} onClick={() => heal.mutate({ entity: p.entity })}>{t("admin.players.healFull")}</Button>
      <Toggle checked={!!p.god} disabled={busy} label={t("admin.players.godMode")} onChange={(on) => god.mutate({ entity: p.entity, on })} />
      <Button size="s" variant="danger" disabled={busy} onClick={async () => {
        if (await confirm({ title: t("admin.players.killTitle", { name }), body: <p>{t("admin.combat.killBody", { name })}</p>, typed: name, ok: t("admin.players.kill") })) kill.mutate({ entity: p.entity });
      }}>{t("admin.combat.killMore")}</Button>
    </div>
  );
}

/** Any entity by id: health, damage, kill (Kiln.Admin's health/kill/damage take any entity). */
function EntityCard() {
  const t = useT();
  const [entity, setEntity] = useState<number | null>(null);
  const [amount, setAmount] = useState(50);
  const hasDamage = useHasOp("damage");
  const health = useAdminQuery<HealthResult>("health", { entity: entity ?? 0 }, { enabled: entity != null && entity >= 1, refetchInterval: 3000 });
  const damage = useAdminCall<{ state: string; health: number; killed: boolean }>("damage", { onSuccess: (r) => { toastOk(r.killed ? t("admin.combat.hitKilled", { state: r.state, health: r.health }) : t("admin.combat.hit", { state: r.state, health: r.health })); health.refetch(); }, onError: (e) => toastError(e) });
  const kill = useAdminCall<{ killed: boolean; state: string }>("kill", { onSuccess: (r) => { (r.killed ? toastOk(t("admin.combat.entityKilled", { entity: String(entity) })) : toastError({ message: r.state })); health.refetch(); }, onError: (e) => toastError(e) });
  const h = health.data;
  return (
    <Card title={t("admin.combat.anyEntity")} sub={t("admin.combat.anyEntitySub")}>
      <div className="k-stack">
        <div className="ka-inline">
          <NumberInput aria-label={t("admin.combat.entityId")} placeholder={t("admin.combat.entityIdPlaceholder")} value={entity} integer min={1} onCommit={setEntity} style={{ width: 140 }} />
          {entity ? <Button size="s" variant="ghost" onClick={() => health.refetch()}>{t("admin.combat.readHealth")}</Button> : null}
        </div>
        {health.error ? <div className="k-error-text">{health.error.message}</div> : null}
        {h ? (
          <>
            <div className="ka-inline">
              <span className="k-mono">#{h.entity}</span>
              <HealthBar health={h.health} max={h.healthMax} />
              <span className="k-hint">{h.health} / {h.healthMax}</span>
              {h.dead ? <Badge tone="err">{t("admin.combat.dead")}</Badge> : null}
              {h.god ? <Badge tone="ember">{t("admin.combat.invulnerable")}</Badge> : null}
            </div>
            <div className="ka-inline">
              {hasDamage ? (
                <>
                  <NumberInput aria-label={t("admin.combat.damage")} value={amount} integer min={1} max={1000000} onCommit={setAmount} style={{ width: 100 }} />
                  <Button size="s" loading={damage.isPending} onClick={() => damage.mutate({ entity: h.entity, amount })}>{t("admin.combat.damage")}</Button>
                </>
              ) : null}
              <Button size="s" variant="danger" loading={kill.isPending} onClick={async () => {
                if (await confirm({ title: t("admin.combat.killEntityTitle", { entity: h.entity }), body: <p>{t("admin.combat.killEntityBody")}</p>, typed: String(h.entity), ok: t("admin.players.kill") })) kill.mutate({ entity: h.entity });
              }}>{t("admin.combat.killMore")}</Button>
            </div>
          </>
        ) : null}
      </div>
    </Card>
  );
}

/** Creatures within a radius of a player or a spot (the `enemies` op). */
function NearCard({ players }: { players: AdminPlayer[] }) {
  const t = useT();
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
    if (!near) return toastError({ message: t("admin.zones.noPlayerPos") });
    if (radius < 1 || radius > 500) return toastError({ message: t("admin.combat.radiusRange") });
    setScan({ near, radius });
  };
  const list = q.data?.near ?? [];
  return (
    <Card title={t("admin.combat.nearby")} sub={q.data && scan ? t("admin.combat.nearbySub", { n: list.length, radius: scan.radius, total: q.data.total }) : t("admin.combat.nearbyHint")}>
      <div className="k-stack">
        <div className="ka-inline">
          <Select aria-label={t("admin.combat.around")} value={at} onChange={setAt} options={[...players.map((p) => ({ value: `p:${p.entity}`, label: t("admin.combat.aroundPlayer", { name: playerName(p) }) })), { value: "coords", label: t("admin.combat.aroundCoords") }]} />
          {at === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
          <NumberInput aria-label={t("admin.zones.colRadius")} value={radius} integer min={1} max={500} onCommit={setRadius} style={{ width: 80 }} />
          <Button size="s" loading={q.isFetching && !q.data} onClick={go}>{t("admin.combat.scan")}</Button>
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        {scan && q.data ? (
          <Table
            aria-label={t("admin.combat.nearby")}
            rows={list}
            rowKey={(r) => String(r.entity)}
            maxHeight={320}
            columns={[
              { key: "e", header: t("admin.combat.colEntity"), width: "90px", render: (r) => <span className="k-mono">{r.entity}</span> },
              { key: "n", header: t("admin.spawn.creature"), render: (r) => r.name ?? r.guid ?? "?" },
              { key: "p", header: t("admin.items.where"), width: "150px", render: (r) => <span className="k-mono">{fmtPos(r.pos)}</span> },
            ]}
            empty={<EmptyState title={t("admin.combat.noCreatures")} />}
          />
        ) : null}
      </div>
    </Card>
  );
}

function DeathsCard() {
  const t = useT();
  const id = useServerId();
  const h = useHistory(id);
  const rows = [...h.deaths].reverse().slice(0, 200);
  const killer = (d: DeathRecord) =>
    d.killerKind === "none" || (!d.killer && !d.killerName) ? (
      <span className="k-muted">{t("admin.combat.nobodyKiller", { origin: originText(d.origin) ?? "?" })}</span>
    ) : (
      <span className="ka-death-killer">
        {d.killerName ?? t("admin.combat.entityLower", { entity: String(d.killer) })} {d.killerKind ? <Badge tone={d.killerKind === "player" ? "warn" : "idle"}>{d.killerKind}</Badge> : null}
        {d.origin && d.origin !== "Game" ? <span className="k-muted">{originText(d.origin)}</span> : null}
      </span>
    );
  return (
    <Card title={t("admin.combat.deaths")} sub={t("admin.combat.deathsSub")} flush>
      <Table
        aria-label={t("admin.combat.deaths")}
        rows={rows}
        rowKey={(r) => r.key}
        columns={[
          { key: "t", header: t("admin.saves.colWhen"), width: "130px", render: (r) => <span title={whenS(r.time)}>{agoS(r.time)}</span>, sort: (a, b) => a.time - b.time },
          { key: "v", header: t("admin.players.colPlayer"), width: "180px", render: (r) => r.victimName ?? t("admin.combat.playerLower", { entity: r.entity }) },
          { key: "k", header: t("admin.combat.killedBy"), render: killer },
          { key: "p", header: t("admin.items.where"), width: "170px", render: (r) => <span className="k-mono">{fmtPos(r.pos)}</span> },
        ]}
        empty={<EmptyState title={t("admin.map.noDeaths")} hint={t("admin.combat.noDeathsHint")} />}
      />
    </Card>
  );
}

export default withAdmin(CombatPage);
