// /servers/:id/admin/spawn: spawn creatures by template (picked from the creature catalog, the
// enemies the server has seen, or a GUID), 1 to 20 at a time, next to a player or at coordinates; remove everything
// spawned from here. The seen types as a table; the spawn form beside it.
import { useMemo, useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState, useHasOp } from "../../api/admin";
import { prettyTemplate, useCreatureCatalog } from "../catalog";
import { CoordInputs, NeedsOp, coordText, parseCoords, playerName, useServerId, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { labelEnemy, useHistory } from "../history";
import type { EnemiesResult, SpawnResult } from "../types";
import { Button, Field, NumberInput, Page, PageHeader, Select, SplitPage, SuggestInput, Table, TextInput, confirm, toastError, toastOk, type Suggestion } from "../ui";

const GUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
interface Pick {
  guid: string;
  label: string;
  template?: string;
}

function SpawnPage() {
  const st = useAdminState();
  const id = useServerId();
  const hist = useHistory(id);
  const players = st.state?.players ?? [];
  const catalog = useCreatureCatalog();
  const [at, setAt] = useState<string>("");
  const where = at || (players[0] ? `p:${players[0].entity}` : "coords");
  const [coords, setCoords] = useState<CoordText>(coordText(null));
  const [count, setCount] = useState(1);
  const [spread, setSpread] = useState(3);
  const [text, setText] = useState("");
  const [pick, setPick] = useState<Pick | null>(null);
  const scanPos = where.startsWith("p:") ? players.find((p) => p.entity === Number(where.slice(2)))?.pos : parseCoords(coords).pos;
  const seen = useAdminQuery<EnemiesResult>("enemies", scanPos ? { near: { x: scanPos.x, y: scanPos.y, z: scanPos.z }, radius: 80 } : {}, { refetchInterval: 15_000 });
  const spawn = useAdminCall<SpawnResult>("spawn");
  const despawn = useAdminCall<{ removed: number }>("despawn_spawned", { onSuccess: (r) => toastOk(`Removed ${r.removed}`), onError: (e) => toastError(e) });
  const hasDespawn = useHasOp("despawn_spawned");

  const all = useMemo<Pick[]>(() => {
    const m = new Map<string, Pick>();
    for (const c of catalog.data ?? []) m.set(c.guid, { guid: c.guid, label: c.name, template: c.template });
    for (const c of seen.data?.catalog ?? []) if (!m.has(c.guid)) m.set(c.guid, { guid: c.guid, label: hist.enemyLabels[c.guid] || prettyTemplate(c.name), template: c.name });
    return [...m.values()];
  }, [catalog.data, seen.data, hist.enemyLabels]);
  const sugg = useMemo<Suggestion<Pick>[]>(() => {
    const q = text.trim().toLowerCase();
    if (q.length < 2 || pick?.label === text) return [];
    return all.filter((c) => c.label.toLowerCase().includes(q) || (c.template ?? "").toLowerCase().includes(q) || c.guid.startsWith(q)).slice(0, 30)
      .map((c) => ({ key: c.guid, label: c.label, sub: <span className="k-mono">{c.template ?? c.guid.slice(0, 13)}</span>, value: c }));
  }, [all, text, pick]);

  const run = (p: Pick, n = count) => {
    let pos;
    if (where.startsWith("p:")) {
      const pl = players.find((x) => x.entity === Number(where.slice(2)));
      if (!pl?.pos) return toastError({ message: "No position for that player." });
      pos = { x: pl.pos.x + 6, y: pl.pos.y, z: pl.pos.z };
    } else {
      const r = parseCoords(coords);
      if (r.error) return toastError({ message: r.error });
      pos = r.pos!;
    }
    if (!Number.isInteger(n) || n < 1 || n > 20) return toastError({ message: "Spawn 1 to 20 at a time." });
    if (!GUID.test(p.guid)) return toastError({ message: "That isn't a creature template GUID." });
    spawn.mutate({ guid: p.guid, count: n, x: pos.x, y: pos.y, z: pos.z, spread }, {
      onSuccess: (r) => toastOk(`Spawned ${r.spawned} × ${p.label}${r.failed ? `, ${r.failed} failed` : ""}`),
      onError: (e) => toastError(e),
    });
  };
  const fromText = (): Pick | null => pick ?? (GUID.test(text.trim()) ? { guid: text.trim(), label: text.trim() } : null);

  const alive = st.state?.spawned ?? 0;
  return (
    <Page fill>
      <PageHeader title="Spawn" sub={`${alive} spawned from here alive, ${all.length} creature types known${seen.data ? `, ${seen.data.total} enemies loaded` : ""}. Everything spawned here can be removed again.`} />
      <OfflineNote />
      <NeedsOp ops="spawn" what="Spawning">
        <SplitPage form aside={
          <form className="k-stack" style={{ gap: 12 }} onSubmit={(e) => { e.preventDefault(); const p = fromText(); if (!p) toastError({ message: "Pick a creature from the list, or paste a template GUID." }); else run(p); }}>
            <span className="k-card-title">Spawn a creature</span>
            <Field label="Creature" htmlFor="spawn-what" hint={pick ? <span className="k-mono">{pick.guid}</span> : undefined}>
              <SuggestInput id="spawn-what" value={text} placeholder={all.length ? `Search ${all.length} creatures, or a GUID` : "Paste a template GUID"} suggestions={sugg}
                onChange={(v) => { setText(v); setPick(null); }} onPick={(s) => { setPick(s.value); setText(s.value.label); }} onEnter={() => { const p = fromText(); if (p) run(p); }} />
            </Field>
            <Field label="Where" htmlFor="spawn-where">
              <Select id="spawn-where" value={where} onChange={setAt} options={[...players.map((p) => ({ value: `p:${p.entity}`, label: `Next to ${playerName(p)}` })), { value: "coords", label: "At coordinates" }]} />
              {where === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
            </Field>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 8 }}>
              <Field label="How many, 1 to 20" htmlFor="spawn-n"><NumberInput id="spawn-n" value={count} integer min={1} max={20} onCommit={setCount} /></Field>
              <Field label="Spread, metres" htmlFor="spawn-s"><NumberInput id="spawn-s" value={spread} min={0} max={50} onCommit={setSpread} /></Field>
            </div>
            <div className="k-row" style={{ justifyContent: "flex-end" }}>
              <Button type="submit" variant="primary" loading={spawn.isPending}>Spawn</Button>
            </div>
            {!catalog.data?.length ? <p className="k-hint">No local creature catalog: the list holds the enemy types the server has seen near players.</p> : null}
            {hasDespawn ? (
              <div className="ka-tool">
                <span className="k-hint">{alive} spawned from here {alive === 1 ? "is" : "are"} alive.</span>
                <div><Button variant="danger" size="s" disabled={!alive} loading={despawn.isPending} onClick={async () => {
                  if (await confirm({ title: "Remove spawned creatures?", body: <p>Removes every creature spawned through Kiln.Admin since it loaded ({alive} alive).</p>, ok: "Remove", danger: true })) despawn.mutate({});
                }}>Remove {alive || ""} spawned</Button></div>
              </div>
            ) : null}
          </form>
        }>
          <div className="k-card-head"><div className="k-card-titles"><h2 className="k-card-title">Seen near players</h2><span className="k-card-sub">Enemy types the server reported. Name them for the picker.</span></div></div>
          <Table
            aria-label="Seen enemy types"
            fill
            rows={seen.data?.catalog ?? []}
            rowKey={(r) => r.guid}
            columns={[
              { key: "label", header: "Name", render: (r) => <TextInput aria-label={`Name for ${r.name}`} defaultValue={hist.enemyLabels[r.guid] ?? (catalog.data?.find((c) => c.guid === r.guid)?.name ?? prettyTemplate(r.name))} onBlur={(e) => labelEnemy(id, r.guid, e.target.value)} style={{ height: 32 }} /> },
              { key: "t", header: "Template", render: (r) => <span className="k-mono k-muted">{r.name}</span> },
              { key: "seen", header: "Seen", width: "60px", align: "right", render: (r) => <span className="k-mono">{r.seen}</span> },
              { key: "go", header: <span className="k-sr-only">Spawn</span>, width: "84px", align: "right", render: (r) => <Button size="s" disabled={spawn.isPending} onClick={() => run({ guid: r.guid, label: hist.enemyLabels[r.guid] || prettyTemplate(r.name) }, 1)}>Spawn 1</Button> },
            ]}
            empty={seen.data ? "No enemies seen yet. The server learns the types it sees around the spawn point." : "Not scanned yet."}
          />
        </SplitPage>
      </NeedsOp>
    </Page>
  );
}

export default withAdmin(SpawnPage);
