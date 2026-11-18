// /servers/:id/admin/players: who is in the world (live from the state pushes) as a table, and the selected player's
// form beside it: teleport (to a player, a waypoint, a grave or coordinates), give items, heal, god mode, kill (typed
// confirmation), progression (level, experience, skill points: kiln.progression), tell, inventory. Plus everyone seen
// on this server.
import { useState } from "react";
import { useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer } from "../../api/admin";
import { rich, useT } from "../../i18n";
import { CoordInputs, ItemPicker, NeedsOp, coordText, parseCoords, playerName, rarityLabel, useServerId, withAdmin, type CoordText } from "../common";
import { OfflineNote } from "../ConnectionBar";
import { knownPlayers, saveWaypoint, useHistory } from "../history";
import type { GiveResult, HealthResult, InventoryResult, ItemHit, PermissionsModel } from "../types";
import {
  Badge, Button, Card, HealthBar, Modal, NameId, NumberInput, Page, PageHeader, Segmented, Select, SplitPage, Table, TextInput, Toggle,
  agoS, confirm, fmtPos, promptText, toastError, toastOk,
} from "../ui";

const RARITIES = ["", "Common", "Uncommon", "Rare", "Epic", "Legendary"];

function PlayersPage() {
  const t = useT();
  const st = useAdminState();
  const id = useServerId();
  const hist = useHistory(id);
  const players = st.state?.players ?? [];
  const perms = useAdminQuery<PermissionsModel>("permissions_get", {}, { staleTime: 30_000 });
  const known = knownPlayers(hist, perms.data?.players ?? {});
  const online = new Set(players.map((p) => p.steamId));
  const [view, setView] = useState<"world" | "seen">("world");
  const [selId, setSel] = useState<number | null>(null);
  const sel = players.find((p) => p.entity === selId) ?? players[0] ?? null;
  return (
    <Page fill>
      <PageHeader title={t("admin.players.title")} sub={t("admin.players.sub")} />
      <OfflineNote />
      {st.session ? (
        <NeedsOp ops="players" what={t("admin.players.needsWhat")}>
          <SplitPage form aside={sel && view === "world" ? <PlayerForm key={sel.entity} p={sel} players={players} /> : <span className="k-empty-title">{view === "world" ? t("admin.players.nobodyAside") : t("admin.players.pickInWorld")}</span>}>
            <Segmented aria-label={t("admin.players.title")} value={view} onChange={setView} options={[{ id: "world", label: t("admin.players.tabWorld", { count: players.length }) }, { id: "seen", label: t("admin.players.tabSeen", { count: known.length }) }]} />
            {view === "world" ? (
              <Table aria-label={t("admin.players.worldTable")} fill rows={players} rowKey={(p) => String(p.entity)} selectedKey={sel ? String(sel.entity) : null} onRowClick={(p) => setSel(p.entity)}
                columns={[
                  { key: "name", header: t("admin.players.colPlayer"), sort: (a, b) => playerName(a).localeCompare(playerName(b)), render: (p) => <NameId name={playerName(p)} id={p.steamId ?? t("admin.players.entityId", { entity: p.entity })} /> },
                  { key: "state", header: t("admin.players.colState"), width: "96px", render: (p) => (p.healthMax && (p.health ?? 0) <= 0 ? <span className="k-badge k-tone-err">{t("admin.players.stateDead")}</span> : p.god ? <span className="k-badge k-tone-ember">{t("admin.players.godMode")}</span> : p.admin ? <span className="k-badge k-tone-warn">{t("admin.players.stateAdmin")}</span> : <span className="k-badge k-tone-ok">{t("admin.players.statePlaying")}</span>) },
                  { key: "hp", header: t("admin.players.colHealth"), width: "130px", render: (p) => (p.healthMax ? <HealthBar health={p.health} max={p.healthMax} /> : <span className="k-muted">-</span>) },
                  { key: "pos", header: t("admin.players.colPosition"), width: "140px", align: "right", render: (p) => <span className="k-mono">{fmtPos(p.pos)}</span> },
                ]}
                empty={t("admin.players.nobody")} />
            ) : (
              <Table aria-label={t("admin.players.knownTable")} fill rows={known} rowKey={(r) => r.steamId}
                columns={[
                  { key: "name", header: t("admin.players.colPlayer"), render: (r) => <NameId name={r.name || t("admin.players.unknown")} id={r.steamId} />, sort: (a, b) => a.name.localeCompare(b.name) },
                  { key: "seen", header: t("admin.players.colLastSeen"), width: "130px", render: (r) => (online.has(r.steamId) ? <span className="k-badge k-tone-ok">{t("admin.players.online")}</span> : <span className="k-muted">{r.lastSeen ? agoS(r.lastSeen) : t("admin.players.inPermissions")}</span>), sort: (a, b) => a.lastSeen - b.lastSeen },
                  { key: "first", header: t("admin.players.colFirstSeen"), width: "120px", render: (r) => <span className="k-muted">{r.firstSeen ? agoS(r.firstSeen) : "-"}</span> },
                ]}
                empty={t("admin.players.knownEmpty")} />
            )}
          </SplitPage>
        </NeedsOp>
      ) : null}
    </Page>
  );
}

function PlayerForm({ p, players }: { p: AdminPlayer; players: AdminPlayer[] }) {
  const t = useT();
  const id = useServerId();
  const name = playerName(p);
  const heal = useAdminCall<HealthResult & { healed: number }>("heal");
  const god = useAdminCall<{ god: boolean }>("god");
  const kill = useAdminCall<{ killed: boolean; state: string }>("kill");
  const hasCombat = useHasOp("heal");
  const [inv, setInv] = useState(false);
  const askKill = async () => {
    if (!(await confirm({ title: t("admin.players.killTitle", { name }), body: <p>{t("admin.players.killBody", { name })}</p>, typed: name, ok: t("admin.players.kill") }))) return;
    kill.mutate({ entity: p.entity }, { onSuccess: (r) => (r.killed ? toastOk(t("admin.players.killed", { name })) : toastError({ message: `${name}: ${r.state}` })), onError: (e) => toastError(e) });
  };
  const saveSpot = async () => {
    const wp = await promptText({ title: t("admin.players.saveSpot"), ok: t("common.save"), prompt: { placeholder: t("admin.players.waypointPlaceholder"), maxLength: 40, initial: t("admin.players.spotName", { name }).slice(0, 40) } });
    if (wp && p.pos) {
      const err = saveWaypoint(id, wp, p.pos);
      if (err) toastError({ message: err });
      else toastOk(t("admin.players.savedWaypoint", { name: wp }));
    }
  };
  const pos = <span className="k-mono">{fmtPos(p.pos)}</span>;
  const rights = p.permissions || t("admin.players.rightsNone");
  return (
    <>
      <div>
        <div style={{ fontSize: 20, fontWeight: 500, color: "var(--k-text-0)" }}>{name}</div>
        <div className="k-mono k-muted" style={{ fontSize: 14.5 }}>{t("admin.players.idLine", { id: p.steamId ?? t("admin.players.noSteamId"), entity: p.entity })}</div>
        <div className="k-hint">{p.healthMax
          ? rich("admin.players.whereHealth", { pos, health: <span className="k-mono">{p.health} / {p.healthMax}</span>, rights })
          : rich("admin.players.where", { pos, rights })}</div>
      </div>
      <TeleportRow p={p} players={players} />
      <GiveRow p={p} />
      <div className="ka-tool">
        <span className="k-label">{t("admin.players.combat")}</span>
        <div className="ka-inline">
          <Button size="s" disabled={!hasCombat} loading={heal.isPending} onClick={() => heal.mutate({ entity: p.entity }, { onSuccess: (r) => toastOk(t("admin.players.healed", { name }), `+${r.healed ?? 0}`), onError: (e) => toastError(e) })}>{t("admin.players.healFull")}</Button>
          <Button size="s" variant="danger" disabled={!hasCombat} loading={kill.isPending} onClick={askKill}>{t("admin.players.kill")}</Button>
        </div>
        <Toggle checked={!!p.god} disabled={!hasCombat || god.isPending} label={t("admin.players.godMode")} onChange={(on) => god.mutate({ entity: p.entity, on }, { onSuccess: (r) => toastOk(r.god ? t("admin.players.godOn", { name }) : t("admin.players.godOff", { name })), onError: (e) => toastError(e) })} />
      </div>
      <ProgressionRow p={p} />
      <TellRow p={p} />
      <div className="ka-inline">
        <button type="button" className="k-link" onClick={() => setInv(true)}>{t("admin.players.inventory")}</button>
        <button type="button" className="k-link" disabled={!p.pos} onClick={saveSpot}>{t("admin.players.saveSpot")}</button>
      </div>
      <Modal open={inv} onClose={() => setInv(false)} title={t("admin.players.inventoryOf", { name })} width={640} footer={<Button onClick={() => setInv(false)}>{t("common.close")}</Button>}>
        {inv && <Inventory entity={p.entity} />}
      </Modal>
    </>
  );
}

function TeleportRow({ p, players }: { p: AdminPlayer; players: AdminPlayer[] }) {
  const t = useT();
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
    { value: "", label: t("admin.players.teleportTo") },
    ...others.map((q) => ({ value: `player:${q.entity}`, label: t("admin.players.tpPlayer", { name: playerName(q) }) })),
    ...hist.waypoints.map((w) => ({ value: `waypoint:${w.name}`, label: t("admin.players.tpWaypoint", { name: w.name }) })),
    ...hist.graves.slice(-15).reverse().map((g) => ({ value: `grave:${g.key}`, label: t("admin.players.tpGrave", { name: g.name ?? t("admin.common.someone"), ago: agoS(g.at) }) })),
    { value: "coords", label: t("admin.players.tpCoords") },
  ];
  const go = () => {
    const done = { onSuccess: () => toastOk(t("admin.players.teleported", { name: playerName(p) })), onError: (e: unknown) => toastError(e) };
    if (!target) return toastError({ message: t("admin.players.pickTarget") });
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
    if (!pos) return toastError({ message: t("admin.players.unknownTarget") });
    tp.mutate({ entity: p.entity, x: pos.x, y: pos.y, z: pos.z, safe }, done);
  };
  return (
    <div className="ka-tool">
      <label className="k-label" htmlFor={`tp-${p.entity}`}>{t("admin.players.teleport")}</label>
      <div className="ka-inline" style={{ flexWrap: "nowrap" }}>
        <Select id={`tp-${p.entity}`} value={target} onChange={setTarget} options={options} />
        <Button size="s" disabled={!has || !target} loading={tp.isPending || tpp.isPending} onClick={go}>{t("admin.players.go")}</Button>
      </div>
      {target === "coords" ? <CoordInputs value={coords} onChange={setCoords} /> : null}
      {target && !target.startsWith("player:") ? <Toggle checked={safe} onChange={setSafe} label={t("admin.players.findGround")} /> : null}
    </div>
  );
}

function GiveRow({ p }: { p: AdminPlayer }) {
  const t = useT();
  const [item, setItem] = useState<ItemHit | null>(null);
  const [count, setCount] = useState(1);
  const [level, setLevel] = useState<number | null>(null);
  const [rarity, setRarity] = useState("");
  const give = useAdminCall<GiveResult>("give_item");
  const has = useHasOp("give_item");
  const send = () => {
    if (!item) return toastError({ message: t("admin.players.pickItem") });
    if (!Number.isInteger(count) || count < 1 || count > 10000) return toastError({ message: t("admin.players.amountRange") });
    if (level != null && (!Number.isInteger(level) || level < 1 || level > 100)) return toastError({ message: t("admin.players.levelRange") });
    const args: Record<string, number | string> = { entity: p.entity, item: item.id, count };
    if (item.gear && level != null) args.level = level;
    if (item.gear && rarity) args.rarity = rarity;
    give.mutate(args, {
      onSuccess: (r) => toastOk(r.given < r.requested ? t("admin.players.gavePartial", { given: r.given, item: item.name }) : t("admin.players.gave", { given: r.given, item: item.name })),
      onError: (e) => toastError(e),
    });
  };
  return (
    <div className="ka-tool">
      <label className="k-label" htmlFor={`give-${p.entity}`}>{t("admin.players.giveItem")}</label>
      <ItemPicker id={`give-${p.entity}`} value={item} onPick={setItem} placeholder={t("admin.players.searchItems")} />
      <div className="ka-inline" style={{ flexWrap: "nowrap" }}>
        <NumberInput aria-label={t("admin.players.amount")} value={count} integer min={1} max={10000} onCommit={setCount} />
        {item?.gear ? (
          <>
            <NumberInput aria-label={t("admin.players.level")} placeholder={t("admin.players.levelPlaceholder")} value={level} integer min={1} max={100} onCommit={setLevel} />
            <Select aria-label={t("admin.players.rarity")} value={rarity} onChange={setRarity} options={RARITIES.map((r) => ({ value: r, label: r ? rarityLabel(r) : t("admin.players.rarity") }))} />
          </>
        ) : null}
        <Button size="s" disabled={!has || !item} loading={give.isPending} onClick={send}>{t("admin.players.give")}</Button>
      </div>
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
  const t = useT();
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
  const told = (done: string) => ({
    onSuccess: (r: ProgressionResultView) => (r.ok ? toastOk(done, r.reason || undefined) : toastError({ message: r.reason || r.status })),
    onError: (e: unknown) => toastError(e),
  });
  const doLevel = () => {
    if (level == null || !Number.isInteger(level) || level < 1 || level > (s?.levelCap || 1000))
      return toastError({ message: s?.levelCap ? t("admin.players.levelToCap", { cap: s.levelCap }) : t("admin.players.levelToTheCap") });
    setLevel.mutate({ entity: p.entity, level }, {
      onSuccess: (r) => { const x = r.steps[0]; if (x?.ok) toastOk(t("admin.players.isLevel", { name, level: x.after?.level ?? level }), x.reason || undefined); else toastError({ message: x?.reason || "refused" }); },
      onError: (e) => toastError(e),
    });
  };
  const doRespec = async () => {
    if (!(await confirm({ title: t("admin.players.respecTitle", { name }), body: <p>{t("admin.players.respecBody", { name })}</p>, ok: t("admin.players.respec") }))) return;
    respec.mutate({ entity: p.entity }, told(t("admin.players.respecDone", { name })));
  };
  const busy = setLevel.isPending || giveXp.isPending || givePts.isPending || respec.isPending;
  return (
    <div className="ka-tool">
      <span className="k-label">{t("admin.players.progression")}</span>
      {s?.hasLevel ? (
        <span className="k-hint">
          <span className="k-tone-info">{t("admin.players.levelOf", { level: s.level, cap: s.levelCap })}</span>
          {s.atLevelCap ? t("admin.players.atCap") : t("admin.players.xpOf", { xp: s.experience, needed: s.experienceNeeded })}
          {s.experiencePending ? t("admin.players.xpPending", { pending: s.experiencePending }) : ""}
          {s.hasPoints ? t("admin.players.pointsUnspent", { points: s.skillPoints, total: s.skillPointsTotal }) : "."}
          {s.overBudget ? <span className="k-tone-err">{t("admin.players.overBudget")}</span> : null}
        </span>
      ) : <span className="k-hint">{q.error ? q.error.message : q.data ? t("admin.players.noProgression") : t("admin.players.reading")}</span>}
      <div className="ka-grid3">
        <NumberInput aria-label={t("admin.players.level")} placeholder={t("admin.players.levelPlaceholder")} value={level} integer min={1} max={s?.levelCap || 1000} onCommit={setLevelText} />
        <Button size="s" disabled={busy || !s?.hasLevel} loading={setLevel.isPending} onClick={doLevel}>{t("admin.players.setLevel")}</Button>
        <NumberInput aria-label={t("admin.players.experience")} value={xp} integer min={1} max={100000000} onCommit={setXp} />
        <Button size="s" disabled={busy || !s?.hasLevel || s.atLevelCap} loading={giveXp.isPending} onClick={() => giveXp.mutate({ entity: p.entity, amount: xp }, told(t("admin.players.gaveXp", { xp, name })))}>{t("admin.players.giveXp")}</Button>
        <NumberInput aria-label={t("admin.players.skillPoints")} value={pts} integer min={1} max={1000} onCommit={setPts} />
        <Button size="s" disabled={busy || !s?.hasPoints || !s.givableSkillPoints} loading={givePts.isPending}
          title={s ? t("admin.players.givePointsTip", { count: s.givableSkillPoints }) : undefined}
          onClick={() => givePts.mutate({ entity: p.entity, amount: pts }, told(t("admin.players.gavePoints", { points: pts, name })))}>{t("admin.players.givePoints")}</Button>
      </div>
      <div><button type="button" className="k-link" disabled={busy || !s?.hasSkills} onClick={doRespec}>{t("admin.players.respecTree")}</button></div>
    </div>
  );
}

function TellRow({ p }: { p: AdminPlayer }) {
  const t = useT();
  const [text, setText] = useState("");
  const tell = useAdminCall("tell");
  const has = useHasOp("tell");
  if (!has) return null;
  const send = () => {
    const msg = text.trim();
    if (!msg || msg.length > 500) return toastError({ message: t("admin.players.messageRange") });
    tell.mutate({ entity: p.entity, text: msg }, { onSuccess: () => { toastOk(t("admin.players.told", { name: playerName(p) })); setText(""); }, onError: (e) => toastError(e) });
  };
  return (
    <div className="ka-tool">
      <label className="k-label" htmlFor={`tell-${p.entity}`}>{t("admin.players.message")}</label>
      <div className="ka-inline" style={{ flexWrap: "nowrap" }}>
        <TextInput id={`tell-${p.entity}`} value={text} maxLength={500} placeholder={t("admin.players.onlySees", { name: playerName(p) })} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && send()} />
        <Button size="s" loading={tell.isPending} onClick={send}>{t("admin.players.send")}</Button>
      </div>
    </div>
  );
}

function Inventory({ entity }: { entity: number }) {
  const t = useT();
  const q = useAdminQuery<InventoryResult>("inventory", { entity }, { refetchInterval: 10_000 });
  if (q.error) return <div className="k-error-text">{q.error.message}</div>;
  if (!q.data) return <div className="k-muted">{t("admin.players.readingInventory")}</div>;
  const list = q.data.inventories ?? [];
  if (!list.length) return <div className="k-muted">{t("admin.players.noInventories")}</div>;
  return (
    <div className="k-stack" style={{ gap: 16 }}>
      {list.map((inv) => (
        <Card key={inv.link} title={inv.category} sub={t("admin.players.slotsUsed", { slots: inv.slots.length })}>
          <div className="ka-list">
            {inv.slots.map((s, i) => (
              <div key={i} className="ka-row-item">
                <span>{s.name ?? `#${s.item}`}</span>
                {s.level ? <Badge tone="info">{t("admin.players.levelBadge", { level: s.level })}</Badge> : null}
                {s.rarity ? <Badge tone="ember">{s.rarity}</Badge> : null}
                {s.equipped ? <Badge tone="ok">{t("admin.players.equipped")}</Badge> : null}
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
