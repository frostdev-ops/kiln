// /servers/:id/admin/items: search the server's items (item_search: names as Kiln knows them,
// including items added by mods), give one to a player (stackables 1 to 10000; gear with level and rarity), or drop
// it into the world (spawn_item). Content (stats, recipes) is on the Content page.
import { useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer } from "../../api/admin";
import { CoordInputs, NeedsOp, PlayerSelect, coordText, parseCoords, playerName, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import type { GiveResult, ItemHit } from "../types";
import { Badge, Button, Card, EmptyState, Field, NumberInput, Page, PageHeader, Select, Table, TextInput, toastError, toastOk, useDebounced } from "../ui";

const RARITIES = ["Common", "Uncommon", "Rare", "Epic", "Legendary"];

function ItemsPage() {
  const st = useAdminState();
  const players = st.state?.players ?? [];
  const [q, setQ] = useState("");
  const dq = useDebounced(q.trim(), 200);
  const search = useAdminQuery<ItemHit[]>("item_search", { q: dq }, { enabled: dq.length >= 2, staleTime: 60_000 });
  const [sel, setSel] = useState<ItemHit | null>(null);
  return (
    <Page>
      <PageHeader title="Items" sub="The server's item list, as Kiln knows it (mods' new items included)." />
      <OfflineNote />
      <NeedsOp ops="item_search" what="Item search">
        <div className="k-grid k-grid-main" style={{ alignItems: "start" }}>
          <Card title="Search" sub={search.data ? `${search.data.length} found${search.data.length === 50 ? " (first 50)" : ""}` : "Type two letters or more"} flush>
            <div style={{ padding: "12px 16px" }}>
              <TextInput aria-label="Search items" autoFocus placeholder="wood, sword, shroud…" value={q} onChange={(e) => setQ(e.target.value)} />
            </div>
            <Table
              aria-label="Items"
              rows={search.data ?? []}
              rowKey={(r) => String(r.id)}
              selectedKey={sel ? String(sel.id) : null}
              onRowClick={setSel}
              columns={[
                { key: "n", header: "Item", render: (r) => r.name, sort: (a, b) => a.name.localeCompare(b.name) },
                { key: "g", header: "Kind", width: "90px", render: (r) => (r.gear ? <Badge tone="info">gear</Badge> : <span className="k-muted">stacks</span>) },
                { key: "id", header: "Id", width: "120px", align: "right", render: (r) => <span className="k-mono">{r.id}</span> },
              ]}
              empty={<EmptyState title={dq.length >= 2 ? (search.isFetching ? "Searching…" : "No item matches") : "Search the item list"} hint={search.error?.message} />}
            />
          </Card>
          <div className="k-stack" style={{ gap: 16 }}>
            {sel ? <GiveCard item={sel} players={players} /> : <Card><EmptyState title="Pick an item" hint="Then give it to a player or drop it into the world." /></Card>}
          </div>
        </div>
      </NeedsOp>
    </Page>
  );
}

function GiveCard({ item, players }: { item: ItemHit; players: AdminPlayer[] }) {
  const [toSel, setTo] = useState<number | null>(null);
  const to = toSel ?? players[0]?.entity ?? null;
  const [count, setCount] = useState(1);
  const [level, setLevel] = useState<number | null>(null);
  const [rarity, setRarity] = useState("");
  const [whereSel, setWhere] = useState("");
  const where = whereSel || (players[0] ? `p:${players[0].entity}` : "coords");
  const [coords, setCoords] = useState<CoordText>(coordText(null));
  const [amount, setAmount] = useState(1);
  const give = useAdminCall<GiveResult>("give_item", { onSuccess: (r) => toastOk(`Gave ${r.given} × ${item.name}${r.given < r.requested ? ` (only room for ${r.given})` : ""}`), onError: (e) => toastError(e) });
  const drop = useAdminCall<{ entity: number }>("spawn_item", { onSuccess: (r) => toastOk(`Dropped ${item.name} (entity ${r.entity})`), onError: (e) => toastError(e) });
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
      <Card title={item.name} sub={<span className="k-mono">#{item.id}{item.gear ? " · gear" : ""}</span>}>
        <div className="k-stack" style={{ gap: 12 }}>
          {hasGive ? (
            <>
              <Field label="Give to"><PlayerSelect value={to} onChange={setTo} players={players} /></Field>
              <div className="ka-inline">
                <Field label="Amount"><NumberInput value={count} integer min={1} max={10000} onCommit={setCount} style={{ width: 100 }} /></Field>
                {item.gear ? (
                  <>
                    <Field label="Level"><NumberInput value={level} integer min={1} max={100} onCommit={setLevel} placeholder="item's" style={{ width: 90 }} /></Field>
                    <Field label="Rarity"><Select value={rarity} onChange={setRarity} options={[{ value: "", label: "item's own" }, ...RARITIES]} /></Field>
                  </>
                ) : null}
              </div>
              <div className="k-row"><span className="k-spacer" /><Button variant="primary" disabled={to == null} loading={give.isPending} onClick={send}>Give {to != null ? `to ${playerName(players.find((p) => p.entity === to))}` : ""}</Button></div>
            </>
          ) : <div className="k-muted">give_item is not available on this server.</div>}
        </div>
      </Card>
      {hasDrop ? (
        <Card title="Drop into the world" sub="A loot bag on the ground (spawn_item)">
          <div className="k-stack" style={{ gap: 12 }}>
            <div className="ka-inline">
              <Select aria-label="Where" value={where} onChange={setWhere} options={[...players.map((p) => ({ value: `p:${p.entity}`, label: `At ${playerName(p)}` })), { value: "coords", label: "At coordinates" }]} />
              {where === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
            </div>
            <div className="ka-inline">
              <NumberInput aria-label="Amount" value={amount} integer min={1} max={10000} onCommit={setAmount} style={{ width: 100 }} />
              <span className="k-spacer" />
              <Button loading={drop.isPending} onClick={dropIt}>Drop</Button>
            </div>
          </div>
        </Card>
      ) : null}
    </>
  );
}

export default withAdmin(ItemsPage);
