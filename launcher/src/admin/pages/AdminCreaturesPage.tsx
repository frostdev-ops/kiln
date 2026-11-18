// /servers/:id/admin/creatures: Kiln.Creatures through
// Kiln.Admin's creature_* ops: the catalog with spawn, creatures near a player or a spot, and an inspect panel with
// values (set/reset), AI settings and commands. Changes are Kiln.Admin's layers: they end when it unloads.
import { useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp, type AdminPlayer } from "../../api/admin";
import { t as tr, useT } from "../../i18n";
import { CoordInputs, NeedsOp, coordText, parseCoords, playerName, usePlayers, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { Badge, Button, Card, EmptyState, HealthBar, KeyValue, NumberInput, Page, PageHeader, Select, Table, TextInput, confirm, fmtPos, toastError, toastOk } from "../ui";

interface Template { guid: string; name: string; display: string; kind: string; faction: string; level: number; levelMax: number; boss: boolean; arsenal: string | null; runtime: boolean }
interface Creature {
  entity: number; templateName: string; kind: string; state: string; faction: string; level: number; levelMax: number; health: number; healthMax: number;
  scale: number; xp: number; speed: number; damage: number; features: { template: string; enabled: string; disabled: string };
  target: number | null; guard: number | null; home: { x: number; y: number; z: number }; loot: { labels: string | null; min: number; max: number; disabled: boolean };
  dead: boolean; ready: boolean; layers: number;
}
interface Status { catalog: number; entities: number; layers: number; arsenalCount: number; commands: boolean; aiHooks: boolean }

const kinds = () => [{ value: "", label: tr("admin.creatures.anyKind") }, ...["enemy", "wildlife", "animal", "villager"].map((k) => ({ value: k, label: k }))];
const FIELDS = ["level", "faction", "scale", "xp", "speed", "damage", "home", "loot-slots", "loot-off", "feature-on", "feature-off", "arsenal", "loot-labels", "resist:fire", "resist:physical", "resist:magical"];
const ACTIONS = ["stay", "heal", "moveto", "attack", "guard", "stopguard"];

function CreaturesPage() {
  const t = useT();
  const players = usePlayers();
  const [picked, setPicked] = useState<number | null>(null);
  const status = useAdminQuery<Status>("creature_status", {}, { refetchInterval: 10000 });
  const s = status.data;
  return (
    <Page>
      <PageHeader title={t("admin.creatures.title")} sub={s ? t("admin.creatures.sub", { templates: s.catalog, creatures: t("admin.creatures.creatureCount", { count: s.entities }), changes: t("admin.creatures.changeCount", { count: s.layers }) }) : t("admin.creatures.subNone")} />
      <OfflineNote />
      <NeedsOp ops={["creature_status", "creature_catalog", "creature_near", "creature_inspect"]} what={t("admin.creatures.what")}>
        <div className="ka-split">
          <div className="k-stack" style={{ gap: 22, minWidth: 0 }}>
            <NearCard players={players} onPick={setPicked} picked={picked} />
            <CatalogCard players={players} onSpawned={setPicked} />
          </div>
          <div className="k-stack" style={{ gap: 12, minWidth: 0 }}>
            <InspectCard entity={picked} setEntity={setPicked} players={players} />
            <p className="k-hint">{t("admin.creatures.footHint")}</p>
          </div>
        </div>
      </NeedsOp>
    </Page>
  );
}

/** Where: around a player or typed coordinates. */
function useWhere(players: AdminPlayer[]) {
  const t = useT();
  const [sel, setSel] = useState("");
  const at = sel || (players[0] ? `p:${players[0].entity}` : "coords");
  const [coords, setCoords] = useState<CoordText>(coordText(null));
  const pos = () => {
    if (at.startsWith("p:")) return players.find((p) => p.entity === Number(at.slice(2)))?.pos ?? null;
    const r = parseCoords(coords);
    if (r.error) {
      toastError({ message: r.error });
      return null;
    }
    return r.pos;
  };
  const ui = (
    <>
      <Select aria-label={t("admin.items.where")} value={at} onChange={setSel} options={[...players.map((p) => ({ value: `p:${p.entity}`, label: t("admin.items.atPlayer", { name: playerName(p) }) })), { value: "coords", label: t("admin.items.atCoords") }]} />
      {at === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
    </>
  );
  return { ui, pos };
}

function NearCard({ players, onPick, picked }: { players: AdminPlayer[]; onPick: (e: number) => void; picked: number | null }) {
  const t = useT();
  const where = useWhere(players);
  const [radius, setRadius] = useState(60);
  const [scan, setScan] = useState<{ x: number; y: number; z: number; radius: number } | null>(null);
  const q = useAdminQuery<Creature[]>("creature_near", scan ?? {}, { enabled: !!scan, refetchInterval: 5000 });
  const go = () => {
    const p = where.pos();
    if (!p) return;
    if (radius < 1 || radius > 2000) return toastError({ message: t("admin.zones.radiusRange") });
    setScan({ x: p.x, y: p.y, z: p.z, radius });
  };
  const list = q.data ?? [];
  return (
    <Card title={t("admin.creatures.nearby")} sub={scan ? t("admin.creatures.nearbySub", { n: list.length, radius: scan.radius }) : t("admin.creatures.nearbyHint")}>
      <div className="k-stack">
        <div className="ka-inline">
          {where.ui}
          <NumberInput aria-label={t("admin.zones.colRadius")} value={radius} integer min={1} max={2000} onCommit={setRadius} style={{ width: 90 }} />
          <Button size="s" loading={q.isFetching && !q.data} onClick={go}>{t("admin.combat.scan")}</Button>
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        {scan ? (
          <Table aria-label={t("admin.combat.nearby")} rows={list} rowKey={(r) => String(r.entity)} maxHeight={340} selectedKey={picked == null ? null : String(picked)} onRowClick={(r) => onPick(r.entity)}
            empty={<EmptyState title={t("admin.creatures.nothingHere")} hint={t("admin.creatures.nothingHereHint")} />}
            columns={[
              { key: "e", header: t("admin.combat.colEntity"), width: "80px", render: (r) => <span className="k-mono">{r.entity}</span> },
              { key: "n", header: t("admin.spawn.creature"), render: (r) => r.templateName },
              { key: "l", header: t("admin.players.level"), width: "60px", align: "right", render: (r) => r.level },
              { key: "s", header: t("admin.players.colState"), width: "110px", render: (r) => (r.dead ? <Badge tone="err">{t("admin.combat.dead")}</Badge> : r.state) },
              { key: "h", header: t("admin.players.colHealth"), width: "120px", render: (r) => <HealthBar health={r.health} max={r.healthMax} /> },
            ]} />
        ) : null}
      </div>
    </Card>
  );
}

function InspectCard({ entity, setEntity, players }: { entity: number | null; setEntity: (e: number | null) => void; players: AdminPlayer[] }) {
  const t = useT();
  const q = useAdminQuery<Creature>("creature_inspect", { entity: entity ?? 0 }, { enabled: entity != null && entity >= 1, refetchInterval: 4000 });
  const hasSet = useHasOp("creature_set");
  const hasCmd = useHasOp("creature_command");
  const [field, setField] = useState("level");
  const [value, setValue] = useState("");
  const [action, setAction] = useState("stay");
  const [target, setTarget] = useState<number | null>(null);
  const where = useWhere(players);
  const set = useAdminCall<Creature>("creature_set", { onSuccess: () => { toastOk(t("admin.creatures.fieldSet", { field })); q.refetch(); }, onError: (e) => toastError(e) });
  const reset = useAdminCall<{ reset: number }>("creature_reset", { onSuccess: (r) => { toastOk(t("admin.creatures.resetDone", { n: r.reset })); q.refetch(); }, onError: (e) => toastError(e) });
  const cmd = useAdminCall<{ sent: boolean }>("creature_command", { onSuccess: () => { toastOk(t("admin.creatures.actionSent", { action })); q.refetch(); }, onError: (e) => toastError(e) });
  const c = q.data;
  const sendSet = () => {
    if (!c) return;
    let v: string | number | boolean | { x: number; y: number; z: number } = value.trim();
    if (field === "home") {
      const p = where.pos();
      if (!p) return;
      v = p;
    } else if (v === "") return toastError({ message: t("admin.creatures.enterValue") });
    else if (/^-?\d+(\.\d+)?$/.test(v)) v = Number(v);
    else if (v === "true" || v === "false") v = v === "true";
    set.mutate({ entity: c.entity, field, value: v });
  };
  const sendCmd = async () => {
    if (!c) return;
    const args: Record<string, number | string> = { entity: c.entity, action };
    if (action === "moveto") {
      const p = where.pos();
      if (!p) return;
      Object.assign(args, p);
    }
    if (action === "attack" || action === "guard" || action === "stopguard") {
      if (!target) return toastError({ message: t("admin.creatures.giveTarget") });
      args.target = target;
      if (action === "attack" && !(await confirm({ title: t("admin.creatures.attackTitle", { name: c.templateName, target }), body: <p>{t("admin.creatures.attackBody")}</p>, ok: t("admin.creatures.attack") }))) return;
    }
    cmd.mutate(args);
  };
  return (
    <Card title={t("admin.creatures.inspect")} sub={t("admin.creatures.inspectSub")}>
      <div className="k-stack">
        <div className="ka-inline">
          <NumberInput aria-label={t("admin.combat.entityId")} placeholder={t("admin.combat.entityIdPlaceholder")} value={entity} integer min={1} onCommit={(e) => setEntity(e)} style={{ width: 140 }} />
          {c ? <Button size="s" variant="ghost" onClick={() => q.refetch()}>{t("common.refresh")}</Button> : null}
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        {c ? (
          <>
            <div className="ka-inline">
              <b>{c.templateName}</b> <Badge>{c.kind}</Badge> <Badge tone="info">{c.faction}</Badge> {c.layers ? <Badge tone="ember">{t("admin.creatures.layers", { n: c.layers })}</Badge> : null}
              <HealthBar health={c.health} max={c.healthMax} />
            </div>
            <KeyValue rows={[
              [t("admin.players.colState"), c.dead ? t("admin.combat.dead") : c.state],
              [t("admin.players.level"), t("admin.creatures.levelUpTo", { level: c.level, max: c.levelMax })],
              [t("admin.creatures.kvStats"), `${c.scale} · ${c.xp} · ${c.speed} · ${c.damage}`],
              [t("admin.creatures.kvHome"), <span className="k-mono">{fmtPos(c.home)}</span>],
              [t("admin.creatures.kvTarget"), c.target ?? "-"],
              [t("admin.creatures.kvFeaturesOff"), c.features.disabled],
              [t("admin.creatures.kvLoot"), c.loot.disabled ? t("admin.map.off") : t("admin.creatures.lootSlots", { min: c.loot.min, max: c.loot.max })],
            ]} />
            {hasSet ? (
              <div className="ka-inline">
                <Select aria-label={t("admin.creatures.field")} value={field} onChange={setField} options={FIELDS} />
                {field === "home" ? where.ui : <TextInput aria-label={t("admin.gameData.value")} value={value} placeholder={field.startsWith("feature") ? "Patrol, Roaming" : t("admin.creatures.valuePlaceholder")} onChange={(e) => setValue(e.target.value)} style={{ width: 160 }} />}
                <Button size="s" loading={set.isPending} onClick={sendSet}>{t("admin.gameData.set")}</Button>
                <Button size="s" variant="ghost" loading={reset.isPending} disabled={!c.layers} onClick={() => reset.mutate({ entity: c.entity })}>{t("common.reset")}</Button>
              </div>
            ) : null}
            {hasCmd ? (
              <div className="ka-inline">
                <Select aria-label={t("admin.creatures.command")} value={action} onChange={setAction} options={ACTIONS} />
                {action === "moveto" ? where.ui : null}
                {action === "attack" || action === "guard" || action === "stopguard" ? <NumberInput aria-label={t("admin.creatures.targetEntity")} placeholder={t("admin.creatures.targetPlaceholder")} value={target} integer min={1} onCommit={setTarget} style={{ width: 130 }} /> : null}
                <Button size="s" loading={cmd.isPending} onClick={sendCmd}>{t("admin.players.send")}</Button>
              </div>
            ) : null}
          </>
        ) : entity ? null : <EmptyState title={t("admin.creatures.noneTitle")} hint={t("admin.creatures.noneHint")} />}
      </div>
    </Card>
  );
}

function CatalogCard({ players, onSpawned }: { players: AdminPlayer[]; onSpawned: (e: number) => void }) {
  const t = useT();
  const [filter, setFilter] = useState("");
  const [kind, setKind] = useState("");
  const [count, setCount] = useState(1);
  const hasSpawn = useHasOp("creature_spawn");
  const where = useWhere(players);
  const q = useAdminQuery<Template[]>("creature_catalog", { ...(filter.trim() ? { filter: filter.trim() } : {}), ...(kind ? { kind } : {}) }, { staleTime: 60000 });
  const spawn = useAdminCall<{ entities: number[] }>("creature_spawn", { onSuccess: (r) => { toastOk(t("admin.creatures.spawned", { n: r.entities.length, list: r.entities.join(", ") })); if (r.entities[0]) onSpawned(r.entities[0]); }, onError: (e) => toastError(e) });
  const doSpawn = (tpl: Template) => {
    const p = where.pos();
    if (!p) return;
    spawn.mutate({ what: tpl.name, count, ...p });
  };
  const list = q.data ?? [];
  return (
    <Card title={t("admin.creatures.catalog")} sub={q.data ? t("admin.creatures.templates", { n: list.length }) : t("admin.creatures.catalogHint")}>
      <div className="k-stack">
        <div className="ka-inline">
          <TextInput aria-label={t("admin.gameData.search")} placeholder={t("admin.creatures.searchNames")} value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 220 }} />
          <Select aria-label={t("admin.items.colKind")} value={kind} onChange={setKind} options={kinds()} />
          {hasSpawn ? (
            <>
              <span className="k-hint">{t("admin.spawn.title")}</span>
              <NumberInput aria-label={t("admin.creatures.count")} value={count} integer min={1} max={50} onCommit={setCount} style={{ width: 70 }} />
              {where.ui}
            </>
          ) : null}
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        <Table aria-label={t("admin.creatures.catalogTable")} rows={list} rowKey={(tpl) => tpl.guid} maxHeight={380}
          columns={[
            { key: "n", header: t("admin.spawn.colTemplate"), render: (tpl) => <span>{tpl.display || tpl.name} {tpl.display ? <span className="k-hint k-mono">{tpl.name}</span> : null}</span>, sort: (a, b) => a.name.localeCompare(b.name) },
            { key: "k", header: t("admin.items.colKind"), width: "90px", render: (tpl) => tpl.kind },
            { key: "f", header: t("admin.creatures.faction"), width: "110px", render: (tpl) => tpl.faction },
            { key: "l", header: t("admin.players.level"), width: "80px", render: (tpl) => `${tpl.level}-${tpl.levelMax}` },
            { key: "b", header: "", width: "70px", render: (tpl) => (tpl.boss ? <Badge tone="ember">{t("admin.creatures.boss")}</Badge> : null) },
            ...(hasSpawn ? [{ key: "s", header: "", width: "80px", render: (tpl: Template) => <Button size="s" loading={spawn.isPending} onClick={() => doSpawn(tpl)}>{t("admin.spawn.title")}</Button> }] : []),
          ]} />
      </div>
    </Card>
  );
}

export default withAdmin(CreaturesPage);
