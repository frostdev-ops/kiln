// /servers/:id/admin/creatures: Kiln.Creatures through
// Kiln.Admin's creature_* ops: the catalog with spawn, creatures near a player or a spot, and an inspect panel with
// values (set/reset), AI settings and commands. Changes are Kiln.Admin's layers: they end when it unloads.
import { useState } from "react";
import { useAdminCall, useAdminQuery, useHasOp, type AdminPlayer } from "../../api/admin";
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

const KINDS = [{ value: "", label: "Any kind" }, ...["enemy", "wildlife", "animal", "villager"].map((k) => ({ value: k, label: k }))];
const FIELDS = ["level", "faction", "scale", "xp", "speed", "damage", "home", "loot-slots", "loot-off", "feature-on", "feature-off", "arsenal", "loot-labels", "resist:fire", "resist:physical", "resist:magical"];
const ACTIONS = ["stay", "heal", "moveto", "attack", "guard", "stopguard"];

function CreaturesPage() {
  const players = usePlayers();
  const [picked, setPicked] = useState<number | null>(null);
  const status = useAdminQuery<Status>("creature_status", {}, { refetchInterval: 10000 });
  const s = status.data;
  return (
    <Page>
      <PageHeader title="Creatures" sub={s ? `${s.catalog} templates · ${s.entities} creature(s) changed · ${s.layers} change(s) by mods` : "Kiln.Creatures: catalog, values, AI and commands"} />
      <OfflineNote />
      <NeedsOp ops={["creature_status", "creature_catalog", "creature_near", "creature_inspect"]} what="Creature tools">
        <div className="k-grid k-grid-2" style={{ alignItems: "start" }}>
          <NearCard players={players} onPick={setPicked} picked={picked} />
          <InspectCard entity={picked} setEntity={setPicked} players={players} />
        </div>
        <CatalogCard players={players} onSpawned={setPicked} />
      </NeedsOp>
      <div className="k-hint">Values set here are Kiln.Admin's: they go back to the game's (or another mod's) when Kiln.Admin reloads or the server restarts. Spawned creatures stay.</div>
    </Page>
  );
}

/** Where: around a player or typed coordinates. */
function useWhere(players: AdminPlayer[]) {
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
      <Select aria-label="Where" value={at} onChange={setSel} options={[...players.map((p) => ({ value: `p:${p.entity}`, label: `At ${playerName(p)}` })), { value: "coords", label: "At coordinates" }]} />
      {at === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
    </>
  );
  return { ui, pos };
}

function NearCard({ players, onPick, picked }: { players: AdminPlayer[]; onPick: (e: number) => void; picked: number | null }) {
  const where = useWhere(players);
  const [radius, setRadius] = useState(60);
  const [scan, setScan] = useState<{ x: number; y: number; z: number; radius: number } | null>(null);
  const q = useAdminQuery<Creature[]>("creature_near", scan ?? {}, { enabled: !!scan, refetchInterval: 5000 });
  const go = () => {
    const p = where.pos();
    if (!p) return;
    if (radius < 1 || radius > 2000) return toastError({ message: "Radius: 1 to 2000 m." });
    setScan({ x: p.x, y: p.y, z: p.z, radius });
  };
  const list = q.data ?? [];
  return (
    <Card title="Nearby" sub={scan ? `${list.length} within ${scan.radius} m` : "Creatures, villagers and animals around a point"}>
      <div className="k-stack">
        <div className="ka-inline">
          {where.ui}
          <NumberInput aria-label="Radius" value={radius} integer min={1} max={2000} onCommit={setRadius} style={{ width: 90 }} />
          <Button size="s" variant="primary" loading={q.isFetching && !q.data} onClick={go}>Scan</Button>
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        {scan ? (
          <Table aria-label="Creatures nearby" rows={list} rowKey={(r) => String(r.entity)} maxHeight={340} selectedKey={picked == null ? null : String(picked)} onRowClick={(r) => onPick(r.entity)}
            empty={<EmptyState title="Nothing here" hint="Try a larger radius or another spot." />}
            columns={[
              { key: "e", header: "Entity", width: "80px", render: (r) => <span className="k-mono">{r.entity}</span> },
              { key: "n", header: "Creature", render: (r) => r.templateName },
              { key: "l", header: "Level", width: "60px", align: "right", render: (r) => r.level },
              { key: "s", header: "State", width: "110px", render: (r) => (r.dead ? <Badge tone="err">dead</Badge> : r.state) },
              { key: "h", header: "Health", width: "120px", render: (r) => <HealthBar health={r.health} max={r.healthMax} /> },
            ]} />
        ) : null}
      </div>
    </Card>
  );
}

function InspectCard({ entity, setEntity, players }: { entity: number | null; setEntity: (e: number | null) => void; players: AdminPlayer[] }) {
  const q = useAdminQuery<Creature>("creature_inspect", { entity: entity ?? 0 }, { enabled: entity != null && entity >= 1, refetchInterval: 4000 });
  const hasSet = useHasOp("creature_set");
  const hasCmd = useHasOp("creature_command");
  const [field, setField] = useState("level");
  const [value, setValue] = useState("");
  const [action, setAction] = useState("stay");
  const [target, setTarget] = useState<number | null>(null);
  const where = useWhere(players);
  const set = useAdminCall<Creature>("creature_set", { onSuccess: () => { toastOk(`${field} set`); q.refetch(); }, onError: (e) => toastError(e) });
  const reset = useAdminCall<{ reset: number }>("creature_reset", { onSuccess: (r) => { toastOk(`Reset: ${r.reset} change(s) undone`); q.refetch(); }, onError: (e) => toastError(e) });
  const cmd = useAdminCall<{ sent: boolean }>("creature_command", { onSuccess: () => { toastOk(`${action} sent`); q.refetch(); }, onError: (e) => toastError(e) });
  const c = q.data;
  const sendSet = () => {
    if (!c) return;
    let v: string | number | boolean | { x: number; y: number; z: number } = value.trim();
    if (field === "home") {
      const p = where.pos();
      if (!p) return;
      v = p;
    } else if (v === "") return toastError({ message: "Enter a value." });
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
      if (!target) return toastError({ message: "Give the target entity." });
      args.target = target;
      if (action === "attack" && !(await confirm({ title: `Send ${c.templateName} to attack ${target}?`, body: <p>The creature attacks that entity at once.</p>, ok: "Attack" }))) return;
    }
    cmd.mutate(args);
  };
  return (
    <Card title="Inspect" sub="Click a creature in the list, or give an entity id">
      <div className="k-stack">
        <div className="ka-inline">
          <NumberInput aria-label="Entity id" placeholder="entity id" value={entity} integer min={1} onCommit={(e) => setEntity(e)} style={{ width: 140 }} />
          {c ? <Button size="s" variant="ghost" onClick={() => q.refetch()}>Refresh</Button> : null}
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        {c ? (
          <>
            <div className="ka-inline">
              <b>{c.templateName}</b> <Badge>{c.kind}</Badge> <Badge tone="info">{c.faction}</Badge> {c.layers ? <Badge tone="ember">{c.layers} change(s)</Badge> : null}
              <HealthBar health={c.health} max={c.healthMax} />
            </div>
            <KeyValue rows={[
              ["State", c.dead ? "dead" : c.state],
              ["Level", `${c.level} (up to ${c.levelMax})`],
              ["Scale · XP · speed · damage", `${c.scale} · ${c.xp} · ${c.speed} · ${c.damage}`],
              ["Home", <span className="k-mono">{fmtPos(c.home)}</span>],
              ["Target", c.target ?? "-"],
              ["Features off", c.features.disabled],
              ["Loot", c.loot.disabled ? "off" : `${c.loot.min}-${c.loot.max} slot(s)`],
            ]} />
            {hasSet ? (
              <div className="ka-inline">
                <Select aria-label="Field" value={field} onChange={setField} options={FIELDS} />
                {field === "home" ? where.ui : <TextInput aria-label="Value" value={value} placeholder={field.startsWith("feature") ? "Patrol, Roaming" : "value"} onChange={(e) => setValue(e.target.value)} style={{ width: 160 }} />}
                <Button size="s" variant="primary" loading={set.isPending} onClick={sendSet}>Set</Button>
                <Button size="s" variant="ghost" loading={reset.isPending} disabled={!c.layers} onClick={() => reset.mutate({ entity: c.entity })}>Reset</Button>
              </div>
            ) : null}
            {hasCmd ? (
              <div className="ka-inline">
                <Select aria-label="Command" value={action} onChange={setAction} options={ACTIONS} />
                {action === "moveto" ? where.ui : null}
                {action === "attack" || action === "guard" || action === "stopguard" ? <NumberInput aria-label="Target entity" placeholder="target entity" value={target} integer min={1} onCommit={setTarget} style={{ width: 130 }} /> : null}
                <Button size="s" loading={cmd.isPending} onClick={sendCmd}>Send</Button>
              </div>
            ) : null}
          </>
        ) : entity ? null : <EmptyState title="No creature picked" hint="Scan nearby or spawn one from the catalog." />}
      </div>
    </Card>
  );
}

function CatalogCard({ players, onSpawned }: { players: AdminPlayer[]; onSpawned: (e: number) => void }) {
  const [filter, setFilter] = useState("");
  const [kind, setKind] = useState("");
  const [count, setCount] = useState(1);
  const hasSpawn = useHasOp("creature_spawn");
  const where = useWhere(players);
  const q = useAdminQuery<Template[]>("creature_catalog", { ...(filter.trim() ? { filter: filter.trim() } : {}), ...(kind ? { kind } : {}) }, { staleTime: 60000 });
  const spawn = useAdminCall<{ entities: number[] }>("creature_spawn", { onSuccess: (r) => { toastOk(`Spawned ${r.entities.length}: ${r.entities.join(", ")}`); if (r.entities[0]) onSpawned(r.entities[0]); }, onError: (e) => toastError(e) });
  const doSpawn = (t: Template) => {
    const p = where.pos();
    if (!p) return;
    spawn.mutate({ what: t.name, count, ...p });
  };
  const list = q.data ?? [];
  return (
    <Card title="Catalog" sub={q.data ? `${list.length} template(s)` : "Every creature and NPC template the server knows"}>
      <div className="k-stack">
        <div className="ka-inline">
          <TextInput aria-label="Search" placeholder="Search names" value={filter} onChange={(e) => setFilter(e.target.value)} style={{ width: 220 }} />
          <Select aria-label="Kind" value={kind} onChange={setKind} options={KINDS} />
          {hasSpawn ? (
            <>
              <span className="k-hint">Spawn</span>
              <NumberInput aria-label="Count" value={count} integer min={1} max={50} onCommit={setCount} style={{ width: 70 }} />
              {where.ui}
            </>
          ) : null}
        </div>
        {q.error ? <div className="k-error-text">{q.error.message}</div> : null}
        <Table aria-label="Creature catalog" rows={list} rowKey={(t) => t.guid} maxHeight={380}
          columns={[
            { key: "n", header: "Template", render: (t) => <span>{t.display || t.name} {t.display ? <span className="k-hint k-mono">{t.name}</span> : null}</span>, sort: (a, b) => a.name.localeCompare(b.name) },
            { key: "k", header: "Kind", width: "90px", render: (t) => t.kind },
            { key: "f", header: "Faction", width: "110px", render: (t) => t.faction },
            { key: "l", header: "Level", width: "80px", render: (t) => `${t.level}-${t.levelMax}` },
            { key: "b", header: "", width: "70px", render: (t) => (t.boss ? <Badge tone="ember">boss</Badge> : null) },
            ...(hasSpawn ? [{ key: "s", header: "", width: "80px", render: (t: Template) => <Button size="s" loading={spawn.isPending} onClick={() => doSpawn(t)}>Spawn</Button> }] : []),
          ]} />
      </div>
    </Card>
  );
}

export default withAdmin(CreaturesPage);
