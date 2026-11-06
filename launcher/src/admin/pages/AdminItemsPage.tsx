// /servers/:id/admin/items: search the server's items (item_search: names as Kiln knows them, including items added by
// mods); the selected item's form beside the table: give it to a player (stackables 1 to 10000; gear with level and
// rarity), or drop it into the world (spawn_item). Content (stats, recipes) is on the Content page.
import { useMemo, useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer } from "../../api/admin";
import { CoordInputs, NeedsOp, PlayerSelect, coordText, itemNames, parseCoords, playerName, useModItemNames, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { GiveResult, ItemHit } from "../types";
import { Button, Field, NameId, NumberInput, Page, Select, SplitPage, Table, WellInput, toastError, toastOk, useDebounced } from "../ui";

const RARITIES = ["Common", "Uncommon", "Rare", "Epic", "Legendary"];

function ItemsPage() {
  const st = useAdminState();
  const players = st.state?.players ?? [];
  const [q, setQ] = useState("");
  const dq = useDebounced(q.trim(), 200);
  const search = useAdminQuery<ItemHit[]>("item_search", { q: dq }, { enabled: dq.length >= 2, staleTime: 60_000 });
  const [selId, setSel] = useState<number | null>(null);
  const mod = useModItemNames();
  const rows = useMemo(() => (search.data ?? []).map((r) => ({ ...r, ...itemNames(r, mod) })), [search.data, mod]);
  const sel = rows.find((r) => r.id === selId) ?? rows[0] ?? null;
  const count = dq.length < 2 ? "" : search.isFetching ? "Searching" : `${rows.length}${rows.length === 50 ? "+" : ""} found`;
  return (
    <Page fill>
      <OfflineNote />
      <NeedsOp ops="item_search" what="Item search">
        <SplitPage form aside={sel ? <GiveForm key={sel.id} item={sel} players={players} /> : <span className="k-empty-title">Pick an item, then give it to a player or drop it into the world.</span>}>
          <WellInput label="Search items" autoFocus value={q} onChange={(e) => setQ(e.target.value)} note={count} style={{ fontSize: 16 }} />
          <Table aria-label="Items" fill rows={rows} rowKey={(r) => String(r.id)} rowHeight={44} selectedKey={sel ? String(sel.id) : null} onRowClick={(r) => setSel(r.id)}
            columns={[
              { key: "n", header: "Item", sort: (a, b) => a.name.localeCompare(b.name), render: (r) => <NameId name={r.name} id={r.debug ?? undefined} /> },
              { key: "g", header: "Kind", width: "64px", render: (r) => <span className={`k-badge k-tone-${r.gear ? "ember" : "idle"}`}>{r.gear ? "gear" : "stacks"}</span> },
              { key: "id", header: "Id", width: "110px", align: "right", render: (r) => <span className="k-mono" style={{ color: "#d0beac" }}>{r.id}</span> },
            ]}
            empty={dq.length >= 2 ? (search.isFetching ? "Searching." : search.error?.message ?? "No item matches that.") : "Type two letters or more: wood, sword, shroud."} />
        </SplitPage>
      </NeedsOp>
    </Page>
  );
}

function GiveForm({ item, players }: { item: Omit<ItemHit, "debug"> & { debug: string | null }; players: AdminPlayer[] }) {
  const [toSel, setTo] = useState<number | null>(null);
  const to = toSel ?? players[0]?.entity ?? null;
  const [count, setCount] = useState(1);
  const [level, setLevel] = useState<number | null>(null);
  const [rarity, setRarity] = useState("");
  const [whereSel, setWhere] = useState("");
  const where = whereSel || (players[0] ? `p:${players[0].entity}` : "coords");
  const [coords, setCoords] = useState<CoordText>(coordText(null));
  const [amount, setAmount] = useState(1);
  const give = useAdminCall<GiveResult>("give_item", { onSuccess: (r) => toastOk(`Gave ${r.given} × ${item.name}`, r.given < r.requested ? `Only room for ${r.given}.` : undefined), onError: (e) => toastError(e) });
  const drop = useAdminCall<{ entity: number }>("spawn_item", { onSuccess: (r) => toastOk(`Dropped ${item.name}`, `Entity ${r.entity}.`), onError: (e) => toastError(e) });
  const hasGive = useHasOp("give_item");
  const hasDrop = useHasOp("spawn_item");
  const send = () => {
    if (to == null) return toastError({ message: "Pick a player." });
    if (!Number.isInteger(count) || count < 1 || count > 10000) return toastError({ message: "Amount: 1 to 10000." });
    const args: Record<string, number | string> = { entity: to, item: item.id, count };
    if (item.gear && level != null) {
      if (level < 1 || level > 100) return toastError({ message: "Level: 1 to 100." });
      args.level = level;
    }
    if (item.gear && rarity) args.rarity = rarity;
    give.mutate(args);
  };
  const dropIt = () => {
    let pos;
    if (where.startsWith("p:")) pos = players.find((p) => p.entity === Number(where.slice(2)))?.pos;
    else {
      const r = parseCoords(coords);
      if (r.error) return toastError({ message: r.error });
      pos = r.pos;
    }
    if (!pos) return toastError({ message: "No position for that player." });
    if (!Number.isInteger(amount) || amount < 1 || amount > 10000) return toastError({ message: "Amount: 1 to 10000." });
    drop.mutate({ item: item.id, amount, x: pos.x + (where.startsWith("p:") ? 1.5 : 0), y: pos.y + 0.5, z: pos.z });
  };
  return (
    <>
      <form className="k-stack" style={{ gap: 10 }} onSubmit={(e) => { e.preventDefault(); send(); }}>
        <div>
          <span style={{ fontSize: 20, fontWeight: 500, color: "var(--k-text-0)", overflowWrap: "anywhere" }}>{item.name}</span>
          <div className="k-mono k-muted" style={{ fontSize: 14.5, overflowWrap: "anywhere" }}>{item.debug ? `${item.debug}, ` : ""}#{item.id}, {item.gear ? "gear" : "stacks"}</div>
        </div>
        {hasGive ? (
          <>
            <Field label="Give to" htmlFor="give-to"><PlayerSelect id="give-to" value={to} onChange={setTo} players={players} placeholder="Pick a player" /></Field>
            <div style={{ display: "grid", gridTemplateColumns: item.gear ? "1fr 1fr 1.2fr" : "1fr 2fr", gap: 8 }}>
              <Field label="Amount" htmlFor="give-n"><NumberInput id="give-n" value={count} integer min={1} max={10000} onCommit={setCount} /></Field>
              {item.gear ? (
                <>
                  <Field label="Level" htmlFor="give-l"><NumberInput id="give-l" value={level} integer min={1} max={100} onCommit={setLevel} placeholder="item's" /></Field>
                  <Field label="Rarity" htmlFor="give-r"><Select id="give-r" value={rarity} onChange={setRarity} options={[{ value: "", label: "Item's own" }, ...RARITIES]} /></Field>
                </>
              ) : <span />}
            </div>
            <div className="k-row-between">
              <span className="k-hint">{to == null ? "Needs someone online." : `To ${playerName(players.find((p) => p.entity === to))}.`}</span>
              <Button type="submit" variant="primary" disabled={to == null} loading={give.isPending}>Give</Button>
            </div>
          </>
        ) : <span className="k-hint">This Kiln.Admin has no give_item op.</span>}
      </form>
      {hasDrop && (
        <form className="k-stack" style={{ gap: 10, paddingTop: 14, boxShadow: "inset 0 1px 0 var(--k-line)" }} onSubmit={(e) => { e.preventDefault(); dropIt(); }}>
          <div><span className="k-card-title">Drop into the world</span><div className="k-hint">A loot bag on the ground.</div></div>
          {players.length > 0 && (
            <Select aria-label="Where" value={where} onChange={setWhere} options={[...players.map((p) => ({ value: `p:${p.entity}`, label: `At ${playerName(p)}` })), { value: "coords", label: "At coordinates" }]} />
          )}
          {where === "coords" && <CoordInputs value={coords} onChange={setCoords} />}
          <div className="k-row-between">
            <div style={{ width: 110 }}><NumberInput aria-label="Amount to drop" value={amount} integer min={1} max={10000} onCommit={setAmount} /></div>
            <Button type="submit" loading={drop.isPending}>Drop</Button>
          </div>
        </form>
      )}
    </>
  );
}

export default withAdmin(ItemsPage);
