// MapView: the admin map: a Leaflet map built on the Enshrouded Interactive Map (MIT, Nicolas Ferreira;
// licenses/Enshrouded-Interactive-Map-LICENSE.txt) with the side panel (markers, find & filter, live), the search box, the context menus and the live overlays: players
// from the state pushes, waypoints and graves (launcher history), protection zones (zones_list, editable from the map
// with zones_add / zones_remove), enemies near players, routes. Assets: assetsRoot() + "map/" (../catalog.ts).
import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { adminHost, callAdmin, useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer, type AdminPos } from "../../api/admin";
import { formatLocale, rich, t as tr, useT } from "../../i18n";
import { assetsRoot, prettyTemplate } from "../catalog";
import { playerName, useServerId } from "../common";
import { deleteWaypoint, saveWaypoint, useHistory } from "../history";
import type { EnemiesResult, Zone } from "../types";
import { Banner, Button, TextInput, confirm, toastError, toastOk, agoS } from "../ui";
import { ACTIONS, actionLabel, DEFAULT_DENY, useMapFocus, useZoneDraft, zoneArgs } from "../zones";
import { Height, KIND_COLOR, MapEngine, markerSetKey, posTxt, withLevel, type FindData, type Finder, type FinderKind, type Look, type MarkerData, type MenuTarget, type Route, type Spot } from "./engine";
import "./map.css";

/** The map art's base URL (read when used: the host may set it after this module loaded). */
export const mapRoot = () => `${assetsRoot()}map/`;
const STORE = "kiln.admin.map.v1";

interface MapUi {
  vis: Record<string, boolean> | null;
  names: boolean;
  hideFound: boolean;
  found: number[];
  finders: Finder[];
  view: { x: number; z: number; zoom: number } | null;
  tab: "layers" | "find" | "live";
  side: boolean;
  look: Look;
  chestLv: [number, number];
  surfaceOnly: boolean;
  enemies: boolean;
  route: Route | null;
  follow: number | null;
  open: Record<string, boolean>;
  /** Marker text language (a game language code; null: the system language's best match). */
  lang: string | null;
  /** The marker set `found` belongs to (engine.markerSetKey): found marks are point indices. */
  foundKey: string | null;
}
const DEF: MapUi = {
  vis: null, names: true, hideFound: false, found: [], finders: [], view: null, tab: "live", side: true,
  look: { spacing: 8, fog: true, flame: 5, animate: true }, chestLv: [5, 50], surfaceOnly: false, enemies: false, route: null, follow: null, open: {},
  lang: null, foundKey: null,
};
const loadUi = (): MapUi => {
  try {
    return { ...DEF, ...(JSON.parse(localStorage.getItem(STORE) ?? "{}") as Partial<MapUi>) };
  } catch {
    return { ...DEF };
  }
};

// forgiving match (the Interactive Map's match.js): accents ignored, words in any order, close forms
const norm = (s: string) => (s || "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
const words = (s: string) => norm(s).split(/[^a-z0-9]+/).filter(Boolean);
const pre = (a: string, b: string) => { let i = 0; while (i < a.length && i < b.length && a[i] === b[i]) i++; return i; };
const wordHit = (q: string, w: string) => w.startsWith(q) || (q.length >= 4 && pre(q, w) >= Math.max(4, Math.min(q.length, w.length) - 2));
function fmatch(q: string, ...texts: (string | undefined | null)[]) {
  const nq = norm(q).trim();
  if (!nq) return true;
  const qw = words(q);
  for (const t of texts) {
    if (!t) continue;
    if (norm(t).includes(nq)) return true;
    const tw = words(t);
    if (qw.every((a) => tw.some((w) => wordHit(a, w)))) return true;
  }
  return false;
}
const fmtN = (n: number) => Number(n).toLocaleString(formatLocale() ?? "en-US");
const dist = (d: number) => (d >= 1000 ? tr("admin.map.km", { n: (d / 1000).toFixed(2) }) : tr("admin.map.metres", { n: Math.round(d) }));
/** The compass direction from a to b (N, NE, ... NW). */
function bearing(a: { x: number; z: number }, b: { x: number; z: number }): string {
  switch (Math.round((((Math.atan2(b.x - a.x, b.z - a.z) * 180) / Math.PI + 360) % 360) / 45) % 8) {
    case 0: return tr("admin.map.dir.n");
    case 1: return tr("admin.map.dir.ne");
    case 2: return tr("admin.map.dir.e");
    case 3: return tr("admin.map.dir.se");
    case 4: return tr("admin.map.dir.s");
    case 5: return tr("admin.map.dir.sw");
    case 6: return tr("admin.map.dir.w");
    default: return tr("admin.map.dir.nw");
  }
}
/** "{n:N0} spawn points" and the like: how many of a finder kind. */
function kindCount(kind: FinderKind, n: number): string {
  switch (kind) {
    case "spawns": return tr("admin.map.countSpawns", { n: fmtN(n) });
    case "gather": return tr("admin.map.countGather", { n: fmtN(n) });
    case "ore": return tr("admin.map.countOre", { n: fmtN(n) });
    default: return tr("admin.map.countChests", { n: fmtN(n) });
  }
}

/** Where a teleport goes, for the menu, the question and the toast. */
type Dest = { kind: "here" } | { kind: "to"; name: string } | { kind: "grave" };
const teleportItemText = (who: string, d: Dest) =>
  d.kind === "here" ? tr("admin.map.tpItemHere", { who }) : d.kind === "grave" ? tr("admin.map.tpItemGrave", { who }) : tr("admin.map.tpItemTo", { who, name: d.name });
const moveText = (who: string, d: Dest) =>
  d.kind === "here" ? tr("admin.map.moveHere", { who }) : d.kind === "grave" ? tr("admin.map.moveGrave", { who }) : tr("admin.map.moveTo", { who, name: d.name });
const moveAtText = (who: string, d: Dest, x: number, z: number, h: number) =>
  d.kind === "here" ? tr("admin.map.moveAtHere", { who, x, z, h }) : d.kind === "grave" ? tr("admin.map.moveAtGrave", { who, x, z, h }) : tr("admin.map.moveAtTo", { who, name: d.name, x, z, h });
const teleportedText = (who: string, d: Dest) =>
  d.kind === "here" ? tr("admin.map.teleportedHere", { who }) : d.kind === "grave" ? tr("admin.map.teleportedGrave", { who }) : tr("admin.map.teleportedTo", { who, name: d.name });

interface MenuItem {
  t: ReactNode;
  tag?: string;
  fn?: () => void;
  /** the menu stays open (the entry opens a form or asks for a second click) */
  keep?: boolean;
  sep?: boolean;
  disabled?: boolean;
}
interface MenuState {
  title: string;
  /** the "This spot" menu (its ground height fills in later) */
  spot?: boolean;
  sub?: string;
  point: { x: number; y: number };
  items: MenuItem[];
  form?: ReactNode;
}

export function MapView({ height }: { height: number }) {
  const t = useT();
  const id = useServerId();
  const nav = useNavigate();
  const qc = useQueryClient();
  const st = useAdminState();
  const hist = useHistory(id);
  const players = useMemo(() => (st.state?.players ?? []).filter((p) => p.pos), [st.state]);
  const [ui, setUiState] = useState<MapUi>(loadUi);
  const uiRef = useRef(ui);
  uiRef.current = ui;
  const setUi = useCallback((p: Partial<MapUi>) => setUiState((u) => ({ ...u, ...p })), []);
  useEffect(() => {
    const timer = setTimeout(() => { try { localStorage.setItem(STORE, JSON.stringify(ui)); } catch { /* full */ } }, 200);
    return () => clearTimeout(timer);
  }, [ui]);

  const box = useRef<HTMLDivElement>(null);
  const eng = useRef<MapEngine | null>(null);
  const [assets, setAssets] = useState<boolean | null>(null);
  // the launcher makes the map art from the game on its own (components/GameArt.tsx, the host's useArtStatus): probe
  // again when it has
  const artStatus = adminHost().useArtStatus();
  // a host's own words while there is no map art (Kilnwarden makes none); undefined: the launcher's
  const noArt = adminHost().useMapNoArt(artStatus);
  const probe = `${artStatus?.extracted ?? ""}|${artStatus?.map ?? ""}`;
  const [D, setD] = useState<MarkerData | null>(null);
  const [F, setF] = useState<FindData | null>(null);
  // the finder's data (spawns, plants, ore, chest loot) is not extracted from the game yet; without it the Find tab says so
  const [findMissing, setFindMissing] = useState(false);
  const [markerLang, setMarkerLang] = useState<string | null>(null);
  const [loading, setLoading] = useState("");
  const [coords, setCoords] = useState<Spot | null>(null);
  const [menu, setMenu] = useState<MenuState | null>(null);
  const [q, setQ] = useState("");
  const [findQ, setFindQ] = useState("");
  const [resultsOpen, setResultsOpen] = useState(false);
  const [enemies, setEnemies] = useState<{ entity: number; pos: AdminPos; name?: string; guid?: string }[]>([]);
  const [enemyErr, setEnemyErr] = useState("");
  const [zoneDraft, setZoneDraft] = useState<{ pos: Spot; radius: number } | null>(null);
  const zones = useAdminQuery<Zone[]>("zones_list", {}, { refetchInterval: 6000 });
  const hasZones = useHasOp("zones_add");
  const hasTp = useHasOp("teleport");
  const tp = useAdminCall("teleport");
  const tpp = useAdminCall("teleport_to_player");
  const putZones = (r: Zone[]) => qc.setQueriesData({ queryKey: ["admin", id, "zones_list"] }, r);
  const zoneAdd = useAdminCall<Zone[]>("zones_add", { onSuccess: putZones, onError: (e) => toastError(e), invalidates: ["gameplay_status"] });
  const zoneRemove = useAdminCall<Zone[]>("zones_remove", { onSuccess: putZones, onError: (e) => toastError(e), invalidates: ["gameplay_status"] });
  const closeMenu = useCallback(() => { setMenu(null); setZoneDraft(null); }, []);

  // ---------------------------------------------------------------- engine
  useEffect(() => {
    let alive = true;
    fetch(`${mapRoot()}gpu/d/0_0.webp`, { method: "HEAD" })
      .then((r) => alive && setAssets(r.ok && (r.headers.get("content-type") ?? "").startsWith("image")))
      .catch(() => alive && setAssets(false));
    return () => { alive = false; };
  }, [probe]);
  const menuRef = useRef<(mt: MenuTarget, p: { x: number; y: number }) => void>(() => undefined);
  useEffect(() => {
    if (assets === null || !box.current) return;
    const e = new MapEngine(box.current, mapRoot(), {
      menu: (mt, p) => menuRef.current(mt, p),
      closeMenu: () => setMenu(null),
      coords: setCoords,
      loading: setLoading,
      view: (v) => setUi({ view: v }),
      stopFollow: () => { setUi({ follow: null }); },
      error: (m) => toastError({ message: m }),
    }, { look: uiRef.current.look, view: uiRef.current.view, relief: assets });
    eng.current = e;
    const ro = new ResizeObserver(() => { e.map.invalidateSize(); e.game?.redraw(); });
    ro.observe(box.current);
    if (assets) {
      e.loadMarkers(uiRef.current.lang).then((d) => {
        setD(d);
        setMarkerLang(e.lang ?? null);
        // categories the saved choice does not know yet (a new marker set) start at their default
        const u = uiRef.current;
        const vis = { ...(u.vis ?? {}) };
        for (const c of d.cats) if (!(c.id in vis)) vis[c.id] = c.on;
        const key = markerSetKey(d);
        const keepFound = u.foundKey === key || (u.foundKey == null && key === "copy");
        setUi({ vis, foundKey: key, ...(keepFound ? {} : { found: [] }) });
        e.loadFind().then((f) => { setF(f); setFindMissing(false); }, () => setFindMissing(true));
      }).catch((err) => toastError({ message: tr("admin.map.mapData", { message: (err as Error).message }) }));
    }
    return () => { ro.disconnect(); e.destroy(); eng.current = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assets]);
  useEffect(() => { eng.current?.map.invalidateSize(); }, [height, ui.side]);
  const changeLang = useCallback((code: string) => {
    const e = eng.current;
    if (!e) return;
    e.setLang(code).then((ok) => {
      if (!ok) return toastError({ message: tr("admin.map.noMarkerTexts", { code }) });
      setUi({ lang: code });
      setMarkerLang(code);
      setD((d) => (d ? { ...d } : d));
    }, (err) => toastError({ message: tr("admin.map.markerTexts", { message: (err as Error).message }) }));
  }, [setUi]);
  useEffect(() => { if (D && ui.vis) eng.current?.applyMarkers(ui.vis, ui.names, ui.found, ui.hideFound, ui.chestLv); }, [D, ui.vis, ui.names, ui.found, ui.hideFound, ui.chestLv]);
  useEffect(() => { if (F) eng.current?.setFinders(ui.finders, ui.surfaceOnly); }, [F, ui.finders, ui.surfaceOnly, ui.chestLv]);
  useEffect(() => { eng.current?.setLook(ui.look); }, [ui.look]);
  useEffect(() => { eng.current?.setPlayers(players); }, [players, assets]);
  useEffect(() => { if (eng.current) eng.current.follow = ui.follow; }, [ui.follow, assets]);
  useEffect(() => { eng.current?.setWorld(hist.waypoints, hist.graves); }, [hist.waypoints, hist.graves, assets]);
  useEffect(() => { eng.current?.setZones(zones.data ?? [], zoneDraft); }, [zones.data, zoneDraft, assets]);
  useEffect(() => { eng.current?.setRoute(ui.route); }, [ui.route, assets]);
  useEffect(() => { eng.current?.setEnemies(ui.enemies ? enemies.map((e) => ({ ...e, label: hist.enemyLabels[e.guid ?? ""] || prettyTemplate(e.name) })) : []); }, [enemies, ui.enemies, hist.enemyLabels, assets]);

  // other pages open the map on a spot (zones, deaths)
  const focus = useMapFocus();
  useEffect(() => {
    if (focus.focus && eng.current) {
      eng.current.flyTo(focus.focus.x, focus.focus.z, focus.focus.zoom ?? 5.5);
      focus.set(null);
    }
  }, [focus, assets]);

  // living creatures: NetworkEnemy entities within 80 m of each player, every 5 s while switched on
  const hasEnemies = useHasOp("enemies");
  useEffect(() => {
    if (!ui.enemies || !hasEnemies) return;
    let stop = false;
    const scan = async () => {
      const seen = new Map<number, { entity: number; pos: AdminPos; name?: string; guid?: string }>();
      let err = "";
      for (const p of players) {
        try {
          const r = await callAdmin<EnemiesResult>(id, "enemies", { near: { x: p.pos!.x, y: p.pos!.y, z: p.pos!.z }, radius: 80 });
          for (const e of r.near) if (e.pos) seen.set(e.entity, e);
        } catch (e) {
          err = (e as Error).message;
        }
      }
      if (!stop) { setEnemies([...seen.values()]); setEnemyErr(err); }
    };
    scan();
    const timer = setInterval(scan, 5000);
    return () => { stop = true; clearInterval(timer); };
  }, [ui.enemies, hasEnemies, players.length, id]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---------------------------------------------------------------- actions
  const heightAt = async (pos: Spot, lift: number) => {
    if (pos.y != null) return pos.y;
    const h = await Height.exact(pos.x, pos.z);
    return h != null ? h + lift : null;
  };
  const teleport = async (p: AdminPlayer, target: Spot | { player: number }, dest: Dest) => {
    const who = playerName(p);
    if ("player" in target) {
      if (!(await confirm({ title: t("admin.map.teleportTitle", { who }), body: <p>{moveText(who, dest)}</p>, ok: t("admin.map.teleport") }))) return;
      return tpp.mutate({ entity: p.entity, target: target.player }, { onSuccess: () => toastOk(teleportedText(who, dest)), onError: (e) => toastError(e) });
    }
    const y = await heightAt(target, 3);
    if (y == null) return toastError({ message: t("admin.map.noHeightRelief") });
    const body = { x: +target.x.toFixed(2), y: +y.toFixed(2), z: +target.z.toFixed(2) };
    if (!(await confirm({ title: t("admin.map.teleportTitle", { who }), body: <p>{moveAtText(who, dest, Math.round(body.x), Math.round(body.z), Math.round(body.y))}</p>, ok: t("admin.map.teleport") }))) return;
    tp.mutate({ entity: p.entity, ...body, safe: true }, { onSuccess: () => toastOk(teleportedText(who, dest)), onError: (e) => toastError(e) });
  };
  const setRoute = (r: Route | null) => setUi({ route: r });
  const originItems = (to: Route["to"]): MenuItem[] => {
    const out: MenuItem[] = players.map((p) => ({ t: t("admin.map.routeFromHere", { from: playerName(p) }), fn: () => setRoute({ from: { kind: "player", entity: p.entity, name: p.name }, to }) }));
    const r = uiRef.current.route;
    if (r && r.from.kind === "point") out.push({ t: r.from.name ? t("admin.map.routeFromHere", { from: r.from.name }) : t("admin.map.routeFromMarked"), fn: () => setRoute({ from: r.from, to }) });
    out.push({ t: t("admin.map.measureFromHere"), fn: () => { setRoute({ from: { kind: "point", x: to.x, y: to.y ?? 0, z: to.z, name: to.name }, to }); toastOk(t("admin.map.startSet"), t("admin.map.startSetDetail")); } });
    return out;
  };
  const teleportItems = (target: Spot | { player: number }, dest: Dest, except?: number): MenuItem[] =>
    !hasTp ? [] : players.filter((p) => p.entity !== except).map((p) => ({ t: teleportItemText(playerName(p), dest), fn: () => teleport(p, target, dest) }));

  const waypointForm = (pos: Spot, name0: string, point: { x: number; y: number }) => {
    setMenu({ title: t("admin.map.saveWaypoint"), sub: posTxt(pos), point, items: [], form: <WaypointForm initial={name0} onCancel={closeMenu} onSave={async (name) => {
      const y = await heightAt(pos, 1);
      if (y == null) return toastError({ message: t("admin.map.noHeight") });
      const err = saveWaypoint(id, name, { x: pos.x, y, z: pos.z });
      if (err) return toastError({ message: err });
      toastOk(t("admin.players.savedWaypoint", { name }));
      closeMenu();
    }} /> });
  };
  const zoneForm = (pos: Spot, point: { x: number; y: number }, name0 = "") => {
    setZoneDraft({ pos, radius: 30 });
    setMenu({ title: t("admin.map.protectHere"), sub: posTxt(pos), point, items: [], form: <ZoneMapForm initial={name0} onRadius={(r) => setZoneDraft({ pos, radius: r })} onCancel={closeMenu}
      onSave={async (f) => {
        const y = await heightAt(pos, 0);
        const r = zoneArgs({ ...f, x: String(pos.x), y: String(y ?? 0), z: String(pos.z), owners: [], allowAdmins: true, editing: false });
        if (r.error) return toastError({ message: r.error });
        zoneAdd.mutate(r.args!, { onSuccess: () => { toastOk(t("admin.zones.added", { name: f.name }), t("admin.map.zoneAddedDetail")); closeMenu(); } });
      }}
      onMore={async () => { const y = await heightAt(pos, 0); useZoneDraft.getState().set({ x: pos.x, y: y ?? 0, z: pos.z }); nav("../zones"); }} /> });
  };

  menuRef.current = (mt: MenuTarget, point) => {
    if (mt.kind === "spot") {
      const pos = mt.pos;
      const to = { ...pos, name: `${Math.round(pos.x)}, ${Math.round(pos.z)}` };
      const spotSub = (y: number | null | undefined) => (y != null ? t("admin.map.spotSubGround", { x: Math.round(pos.x), z: Math.round(pos.z), h: Math.round(y) }) : t("admin.map.posXZ", { x: Math.round(pos.x), z: Math.round(pos.z) }));
      setMenu({ title: t("admin.map.thisSpot"), spot: true, sub: spotSub(pos.y), point, items: [
        { t: t("admin.map.saveWaypointHere"), keep: true, fn: () => waypointForm(pos, "", point) },
        ...(hasZones ? [{ t: t("admin.map.protectHereMenu"), keep: true, fn: () => zoneForm(pos, point) }] : []),
        ...teleportItems(pos, { kind: "here" }),
        { sep: true, t: "" },
        ...originItems(to),
        { t: t("admin.map.copyCoords"), fn: () => navigator.clipboard?.writeText(`X ${pos.x} Z ${pos.z}${pos.y != null ? ` H ${Math.round(pos.y)}` : ""}`).then(() => toastOk(t("admin.map.coordsCopied"))) },
      ] });
      Height.exact(pos.x, pos.z).then((h) => { if (h != null) setMenu((m) => (m && m.spot && m.point === point ? { ...m, sub: spotSub(h) } : m)); });
    } else if (mt.kind === "marker") {
      const pos = mt.pos;
      const items: MenuItem[] = [...originItems({ ...pos, name: mt.name }), { sep: true, t: "" }, { t: t("admin.map.saveWaypointHere"), keep: true, fn: () => waypointForm(pos, mt.name.slice(0, 40), point) },
        ...(hasZones ? [{ t: t("admin.map.protectHereMenu"), keep: true, fn: () => zoneForm(pos, point, mt.name.slice(0, 64)) }] : []), ...teleportItems(pos, { kind: "to", name: mt.name })];
      if (mt.idx != null) {
        const f = uiRef.current.found.includes(mt.idx);
        items.push({ sep: true, t: "" }, { t: f ? t("admin.map.unmarkFound") : t("admin.map.markFound"), fn: () => setUi({ found: f ? uiRef.current.found.filter((v) => v !== mt.idx) : [...uiRef.current.found, mt.idx!] }) });
      }
      setMenu({ title: mt.name, sub: `${mt.sub ? mt.sub + " · " : ""}${posTxt(pos)}`, point, items });
    } else if (mt.kind === "player") {
      const p = players.find((x) => x.entity === mt.entity);
      if (!p?.pos) return;
      const name = playerName(p);
      const others = players.filter((x) => x.entity !== mt.entity);
      setMenu({ title: name, sub: p.healthMax ? t("admin.map.playerSubHealth", { pos: posTxt(p.pos), health: Math.round(p.health ?? 0), max: Math.round(p.healthMax) }) : posTxt(p.pos), point, items: [
        { t: uiRef.current.follow === mt.entity ? t("admin.map.stopFollowing") : t("admin.map.followOnMap"), fn: () => setUi({ follow: uiRef.current.follow === mt.entity ? null : mt.entity }) },
        { t: t("admin.map.routeFromTo", { from: name }), keep: true, fn: () => { setRoute({ from: { kind: "player", entity: mt.entity, name }, to: uiRef.current.route?.to ?? { ...p.pos!, name } }); toastOk(t("admin.map.clickDestination")); } },
        ...others.map((o) => ({ t: t("admin.map.routeFromToName", { from: playerName(o), to: name }), fn: () => setRoute({ from: { kind: "player", entity: o.entity, name: o.name }, to: { ...p.pos!, name } }) })),
        { t: t("admin.map.saveWaypointAt", { name }), keep: true, fn: () => waypointForm(p.pos!, t("admin.players.spotName", { name }).slice(0, 40), point) },
        ...teleportItems({ player: mt.entity }, { kind: "to", name }, mt.entity),
        { sep: true, t: "" },
        { t: t("admin.map.openPlayers"), fn: () => nav("../players") },
      ] });
    } else if (mt.kind === "waypoint") {
      setMenu({ title: mt.name, sub: t("admin.map.waypointSub", { pos: posTxt(mt.pos) }), point, items: [
        ...originItems({ ...mt.pos, name: mt.name }),
        ...teleportItems(mt.pos, { kind: "to", name: mt.name }),
        { sep: true, t: "" },
        { t: t("admin.map.deleteWaypoint"), fn: async () => { if (await confirm({ title: t("admin.map.deleteWaypointTitle"), body: <p>{mt.name}</p>, ok: t("common.delete"), danger: true })) deleteWaypoint(id, mt.name); } },
      ] });
    } else if (mt.kind === "grave") {
      const name = mt.name != null ? t("admin.map.graveOf", { name: mt.name }) : t("admin.map.graveSomeone");
      setMenu({ title: name, sub: `${agoS(mt.at)} · ${posTxt(mt.pos)}`, point, items: [
        ...originItems({ ...mt.pos, name }),
        ...teleportItems({ ...mt.pos, y: mt.pos.y + 1 }, { kind: "grave" }),
        { t: t("admin.map.saveWaypointHere"), keep: true, fn: () => waypointForm(mt.pos, (mt.name != null ? t("admin.map.graveWaypoint", { name: mt.name }) : t("admin.map.graveWaypointNone")).slice(0, 40), point) },
      ] });
    } else if (mt.kind === "zone") {
      const z = (zones.data ?? []).find((x) => x.name === mt.name);
      if (!z) return;
      setMenu({ title: `⛉ ${z.name}`, sub: t("admin.map.zoneSub", { radius: Math.round(z.radius), deny: z.deny.map((a) => actionLabel(a)).join(", ") }), point, items: [
        { t: t("admin.map.changeRadius"), keep: true, fn: () => {
          setZoneDraft({ pos: z.pos, radius: z.radius });
          setMenu({ title: t("admin.map.radiusOf", { name: z.name }), sub: posTxt(z.pos), point, items: [], form: <RadiusForm initial={z.radius} onRadius={(r) => setZoneDraft({ pos: z.pos, radius: r })} onCancel={closeMenu}
            onSave={(r) => zoneAdd.mutate({ name: z.name, x: z.pos.x, y: z.pos.y, z: z.pos.z, radius: r, deny: z.deny, owners: z.owners, allowAdmins: z.allowAdmins }, { onSuccess: () => { toastOk(t("admin.map.zoneRadiusSet", { name: z.name, radius: r })); closeMenu(); } })} /> });
        } },
        { t: t("admin.map.editOnZones"), fn: () => nav("../zones") },
        ...originItems({ ...z.pos, name: z.name }),
        { sep: true, t: "" },
        { t: t("admin.map.removeZone"), fn: async () => { if (await confirm({ title: t("admin.map.removeZoneTitle", { name: z.name }), body: <p>{t("admin.zones.removeBody")}</p>, ok: t("common.remove"), danger: true })) zoneRemove.mutate({ name: z.name }, { onSuccess: () => toastOk(t("admin.zones.removed", { name: z.name })) }); } },
      ] });
    }
  };

  // ---------------------------------------------------------------- search
  const findEntry = (kind: FinderKind, slug: string) => (F?.[kind]?.[slug] ? { ...F[kind][slug], kind, slug } : null);
  const isOn = (kind: FinderKind, slug: string) => ui.finders.some((f) => f.kind === kind && f.slug === slug);
  const toggleFinder = (kind: FinderKind, slug: string) => setUi({ finders: isOn(kind, slug) ? ui.finders.filter((f) => !(f.kind === kind && f.slug === slug)) : [...ui.finders, { kind, slug }] });
  const addFinders = (list: [FinderKind, string][]) => setUi({ finders: [...ui.finders, ...list.filter(([k, s]) => !isOn(k, s)).map(([kind, slug]) => ({ kind, slug }))] });
  const results = useMemo(() => {
    const out: { sec: string; items: { t: string; s: string; img?: string; color?: string; on?: boolean; act: () => void }[] }[] = [];
    const s = q.trim();
    if (s.length < 2) return out;
    const push = (sec: string, items: (typeof out)[number]["items"]) => { if (items.length) out.push({ sec, items }); };
    if (F && s.length >= 3)
      push(t("admin.map.secDropped"), Object.entries(F.drops).filter(([n]) => fmatch(s, n)).slice(0, 5).map(([n, d]) => ({
        img: d.img, t: n, s: t("admin.map.droppedBy", { list: `${d.by.slice(0, 4).map((b) => F.spawns[b]?.n ?? b).join(", ")}${d.by.length > 4 ? ` +${d.by.length - 4}` : ""}` }),
        act: () => { addFinders(d.by.filter((b) => F.spawns[b]).map((b) => ["spawns", b] as [FinderKind, string])); toastOk(t("admin.map.showingDrops", { count: d.by.length, item: n })); },
      })));
    if (F) {
      const hits: (ReturnType<typeof findEntry> & object)[] = [];
      for (const kind of ["spawns", "gather", "ore", "chests"] as FinderKind[]) for (const [slug, e] of Object.entries(F[kind])) if (fmatch(s, e.n)) hits.push({ ...e, kind, slug });
      hits.sort((a, b) => Number(!!b.boss) - Number(!!a.boss) || b.c - a.c);
      push(t("admin.map.secWhere"), hits.slice(0, 8).map((e) => ({ img: e.img, color: e.color || KIND_COLOR[e.kind], t: e.n, on: isOn(e.kind, e.slug),
        s: (() => { const what = `${e.kind === "chests" ? t("admin.map.chestsHold", { n: fmtN(e.c) }) : kindCount(e.kind, e.c)}${e.boss ? t("admin.map.bossTag") : ""}`; return isOn(e.kind, e.slug) ? t("admin.map.hitOn", { what }) : t("admin.map.hitOff", { what }); })(),
        act: () => toggleFinder(e.kind, e.slug) })));
    }
    const live: (typeof out)[number]["items"] = [];
    for (const p of players) if (fmatch(s, p.name)) live.push({ t: playerName(p), s: t("admin.map.playerSub", { pos: posTxt(p.pos!) }), color: "#7dff9b", act: () => eng.current?.flyTo(p.pos!.x, p.pos!.z, 5) });
    for (const w of hist.waypoints) if (fmatch(s, w.name)) live.push({ t: w.name, s: t("admin.map.waypointSub", { pos: posTxt(w.pos) }), img: "g-mapmarker_waypoint.webp", act: () => eng.current?.flyTo(w.pos.x, w.pos.z, 5) });
    for (const z of zones.data ?? []) if (fmatch(s, z.name)) live.push({ t: z.name, s: t("admin.map.zoneSearchSub", { radius: Math.round(z.radius) }), color: "#ff8540", act: () => eng.current?.flyTo(z.pos.x, z.pos.z, 5) });
    push(t("admin.map.secLive"), live.slice(0, 8));
    if (D) {
      push(t("admin.map.secPlaces"), D.labels.filter((l) => fmatch(s, l[0])).slice(0, 6).map((l) => ({ t: l[0], s: t("admin.map.placeSub", { pos: posTxt({ x: l[1], z: l[2] }) }), color: "#d8c9a6", act: () => eng.current?.flyTo(l[1], l[2], 5) })));
      const pts: (typeof out)[number]["items"] = [];
      for (let i = 0; i < D.points.length && pts.length < 10; i++) {
        const p = D.points[i], c = D.cats[p[0]];
        if (fmatch(s, p[4], c.label)) pts.push({ img: p[7] || c.icon, t: p[4], s: `${withLevel(c.label, p[6])} · ${posTxt({ x: p[1], z: p[2] })}`,
          act: () => { if (!ui.vis?.[c.id]) setUi({ vis: { ...(ui.vis ?? {}), [c.id]: true } }); eng.current?.flyTo(p[1], p[2], 5.5); } });
      }
      push(t("admin.map.secMarkers"), pts);
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, F, D, players, hist.waypoints, zones.data, ui.finders, ui.vis, t]);

  // ---------------------------------------------------------------- render
  const route = ui.route;
  const ra = route ? (route.from.kind === "player" ? (() => { const p = players.find((x) => x.entity === (route.from as { entity: number }).entity); return p?.pos ? { ...p.pos, name: playerName(p) } : null; })() : route.from) : null;
  return (
    <div className={"ka-map-root" + (ui.side ? "" : " noside")} style={{ height }} onKeyDown={(e) => { if (e.key === "Escape") { closeMenu(); setResultsOpen(false); } }}>
      <aside className="ka-map-side">
        <div className="ka-map-search">
          <TextInput type="search" aria-label={t("admin.map.searchLabel")} placeholder={t("admin.map.searchPlaceholder")} value={q} autoComplete="off" spellCheck={false}
            onChange={(e) => { setQ(e.target.value); setResultsOpen(true); }} onFocus={() => setResultsOpen(true)}
            onKeyDown={(e) => { if (e.key === "Enter" && results[0]?.items[0]) { results[0].items[0].act(); setResultsOpen(false); } }} />
          {resultsOpen && q.trim().length >= 2 ? (
            <div className="ka-map-results" onMouseLeave={() => undefined}>
              {results.length ? results.map((r) => (
                <div key={r.sec}>
                  <h4>{r.sec}</h4>
                  {r.items.map((it, i) => (
                    <button key={i} type="button" className={it.on ? "on" : ""} onClick={() => { it.act(); setResultsOpen(false); }}>
                      {it.img ? <img src={`${mapRoot()}i/${it.img}`} alt="" /> : <i className="ka-dot" style={{ background: it.color ?? "#d8c9a6" }} />}
                      <b>{it.t}</b><small>{it.s}</small>
                    </button>
                  ))}
                </div>
              )) : <p className="ka-map-muted">{t("admin.map.nothingMatches")}</p>}
            </div>
          ) : null}
        </div>
        <div className="ka-map-tabs" role="tablist">
          {([["layers", t("admin.map.tabMarkers")], ["find", t("admin.map.tabFind")], ["live", t("admin.map.tabLive")]] as const).map(([k, l]) => <button key={k} role="tab" className={ui.tab === k ? "on" : ""} onClick={() => setUi({ tab: k })}>{l}</button>)}
        </div>
        <div className="ka-map-panel" onClick={() => setResultsOpen(false)}>
          {ui.tab === "layers" ? <LayersPanel D={D} ui={ui} setUi={setUi} assets={assets} noArt={noArt?.markers ?? noArt?.title ?? undefined} lang={markerLang} onLang={changeLang} /> : null}
          {ui.tab === "find" ? <FindPanel F={F} missing={findMissing} ui={ui} findQ={findQ} setFindQ={setFindQ} toggle={toggleFinder} addAll={addFinders} dropsBy={eng.current?.dropsBy ?? {}} clear={() => setUi({ finders: [] })} setUi={setUi} /> : null}
          {ui.tab === "live" ? (
            <LivePanel players={players} connected={st.ready} reason={st.error?.message} waypoints={hist.waypoints} graves={hist.graves} zones={zones.data ?? []} ui={ui} setUi={setUi}
              enemies={enemies.length} enemyErr={enemyErr} hasEnemies={hasEnemies} routeFrom={ra?.name ?? (route?.from.kind === "point" ? route.from.name : undefined)}
              go={(x, z) => eng.current?.flyTo(x, z, 5)} follow={(e) => { const n = ui.follow === e ? null : e; setUi({ follow: n }); const pos = n != null ? players.find((p) => p.entity === n)?.pos : null; if (pos) eng.current?.flyTo(pos.x, pos.z, 5); }} />
          ) : null}
        </div>
        <div className="ka-map-credit">{rich("admin.map.credit", { map: <b>{t("admin.map.creditMap")}</b> })}</div>
      </aside>
      <div className="ka-map-area">
        <div className="ka-map" ref={box} />
        {/* map.css places it (above the route panel, in the map area's top column); its z-index stays inline: Kilnwarden
            hides this banner by `.ka-map-area > div[style*="z-index: 1001"]` (web/src/app.css) */}
        {assets === false && noArt?.title !== null ? (
          <div className="ka-map-noart" style={{ zIndex: 1001 }}>
            {noArt ? (
              <Banner tone="info" title={noArt.title}
                actions={noArt.action ? <Button size="s" loading={noArt.action.busy} onClick={noArt.action.onClick}>{noArt.action.label}</Button> : undefined}>
                {noArt.body}
              </Banner>
            ) : <Banner tone="info" title={artStatus?.canExtract === false ? t("admin.map.artNeedsClient") : t("admin.map.artPreparing")} />}
          </div>
        ) : null}
        <button className="ka-map-sidetoggle" title={t("admin.map.toggleSide")} onClick={() => setUi({ side: !ui.side })}>☰</button>
        {route ? (
          <div className="ka-map-route">
            {ra ? (
              <>
                <span><b>{ra.name ?? t("admin.map.start")}</b> → <b>{route.to.name ?? t("admin.map.point")}</b></span>
                <span className="big">{dist(Math.hypot(route.to.x - ra.x, route.to.z - ra.z))}</span>
                <span>{bearing(ra, route.to)}</span>
                {route.to.y != null && ra.y != null ? (() => { const dy = route.to.y! - ra.y; return <span className={dy > 2 ? "up" : dy < -2 ? "down" : ""}>{dy > 2 ? "▲" : dy < -2 ? "▼" : "≈"} {t("admin.map.metres", { n: Math.abs(Math.round(dy)) })}</span>; })() : null}
              </>
            ) : <span>{t("admin.map.routeStartGone", { start: route.from.kind === "player" ? route.from.name ?? t("admin.map.player") : t("admin.map.point") })}</span>}
            <button className="ka-map-x" title={t("admin.map.clearRouteTip")} onClick={() => setRoute(null)}>✕</button>
          </div>
        ) : null}
        <div className="ka-map-zoom">
          <button title={t("admin.map.zoomIn")} onClick={() => eng.current?.map.zoomIn()}>+</button>
          <button title={t("admin.map.zoomOut")} onClick={() => eng.current?.map.zoomOut()}>−</button>
          <button title={t("admin.map.wholeWorld")} onClick={() => eng.current?.fit()}>⤢</button>
        </div>
        {coords ? <div className="ka-map-coords"><span>X <b>{Math.round(coords.x)}</b></span><span>Z <b>{Math.round(coords.z)}</b></span>{coords.y != null ? <span>{rich("admin.map.heightReadout", { h: <b>≈{Math.round(coords.y)}</b> })}</span> : null}</div> : null}
        {loading ? <div className="ka-map-loading">{loading}</div> : null}
        {menu ? <MenuBox menu={menu} close={closeMenu} /> : null}
      </div>
    </div>
  );
}

function MenuBox({ menu, close }: { menu: MenuState; close: () => void }) {
  const ref = useRef<HTMLDivElement>(null);
  const [pos, setPos] = useState({ left: menu.point.x + 6, top: menu.point.y + 6 });
  useEffect(() => {
    const el = ref.current, host = el?.parentElement;
    if (!el || !host) return;
    setPos({ left: Math.max(8, Math.min(menu.point.x + 6, host.clientWidth - el.offsetWidth - 8)), top: Math.max(8, Math.min(menu.point.y + 6, host.clientHeight - el.offsetHeight - 8)) });
  }, [menu]);
  return (
    <div ref={ref} className="ka-map-menu" style={pos} onContextMenu={(e) => e.preventDefault()} role="menu">
      <div className="ka-map-mh"><b>{menu.title}</b>{menu.sub ? <small>{menu.sub}</small> : null}</div>
      {menu.items.map((it, i) => it.sep ? <hr key={i} /> : (
        <button key={i} role="menuitem" disabled={it.disabled} onClick={() => { if (!it.keep) close(); it.fn?.(); }}>
          {it.t}{it.tag ? <span className="tag">{it.tag}</span> : null}
        </button>
      ))}
      {menu.form}
    </div>
  );
}

function WaypointForm({ initial, onSave, onCancel }: { initial: string; onSave: (name: string) => void; onCancel: () => void }) {
  const t = useT();
  const [name, setName] = useState(initial);
  return (
    <form className="ka-map-form" onSubmit={(e) => { e.preventDefault(); if (name.trim()) onSave(name.trim()); }} onKeyDown={(e) => { if (e.key === "Escape") onCancel(); e.stopPropagation(); }}>
      <div className="ka-inline"><TextInput autoFocus maxLength={40} placeholder={t("admin.map.namePlaceholder")} value={name} onChange={(e) => setName(e.target.value)} /><Button size="s" variant="primary" type="submit">{t("common.save")}</Button></div>
    </form>
  );
}

function ZoneMapForm({ initial, onSave, onCancel, onRadius, onMore }: { initial: string; onSave: (f: { name: string; radius: string; deny: string[] }) => void; onCancel: () => void; onRadius: (r: number) => void; onMore: () => void }) {
  const t = useT();
  const [name, setName] = useState(initial);
  const [radius, setRadius] = useState("30");
  const [deny, setDeny] = useState<string[]>(DEFAULT_DENY);
  return (
    <form className="ka-map-form" onSubmit={(e) => { e.preventDefault(); onSave({ name, radius, deny }); }} onKeyDown={(e) => { if (e.key === "Escape") onCancel(); e.stopPropagation(); }}>
      <TextInput autoFocus maxLength={64} placeholder={t("admin.map.zoneNamePlaceholder")} value={name} onChange={(e) => setName(e.target.value)} />
      <div className="ka-inline"><span className="k-hint">{t("admin.zones.colRadius")}</span><TextInput inputMode="numeric" value={radius} onChange={(e) => { setRadius(e.target.value); const n = Number(e.target.value); if (n >= 1 && n <= 2000) onRadius(n); }} style={{ width: 80 }} /><span className="k-hint">{t("admin.map.metreUnit")}</span></div>
      <div className="ka-flags">{ACTIONS.map((a) => <label key={a}><input type="checkbox" checked={deny.includes(a)} onChange={(e) => setDeny(e.target.checked ? [...deny, a] : deny.filter((x) => x !== a))} /> {actionLabel(a)}</label>)}</div>
      <div className="ka-inline"><Button size="s" variant="primary" type="submit">{t("admin.zones.addZone")}</Button><Button size="s" variant="ghost" onClick={onMore}>{t("admin.map.ownersMore")}</Button></div>
    </form>
  );
}

function RadiusForm({ initial, onSave, onCancel, onRadius }: { initial: number; onSave: (r: number) => void; onCancel: () => void; onRadius: (r: number) => void }) {
  const t = useT();
  const [v, setV] = useState(String(Math.round(initial)));
  const n = Number(v);
  return (
    <form className="ka-map-form" onSubmit={(e) => { e.preventDefault(); if (n >= 1 && n <= 2000) onSave(n); else toastError({ message: t("admin.zones.radiusRange") }); }} onKeyDown={(e) => { if (e.key === "Escape") onCancel(); e.stopPropagation(); }}>
      <input type="range" min={1} max={500} value={Math.min(500, n || 1)} onChange={(e) => { setV(e.target.value); onRadius(Number(e.target.value)); }} className="ka-dial" />
      <div className="ka-inline"><TextInput autoFocus inputMode="numeric" value={v} onChange={(e) => { setV(e.target.value); const x = Number(e.target.value); if (x >= 1 && x <= 2000) onRadius(x); }} style={{ width: 90 }} /><span className="k-hint">{t("admin.map.metreUnit")}</span><Button size="s" variant="primary" type="submit">{t("common.save")}</Button></div>
    </form>
  );
}

function LayersPanel({ D, ui, setUi, assets, noArt, lang, onLang }: {
  D: MarkerData | null; ui: MapUi; setUi: (p: Partial<MapUi>) => void; assets: boolean | null; noArt?: ReactNode; lang: string | null; onLang: (code: string) => void;
}) {
  const t = useT();
  // without map art: the host's line (AdminHost.useMapNoArt), else the launcher's
  if (assets === false) return <p className="ka-map-muted">{noArt ?? t("admin.map.noMapData")}</p>;
  if (!D || !ui.vis) return <p className="ka-map-muted">{t("admin.map.loadingMarkers")}</p>;
  const vis = ui.vis;
  const groups: { name: string; cats: MarkerData["cats"] }[] = [];
  for (const c of D.cats) { let g = groups.find((x) => x.name === c.group); if (!g) groups.push((g = { name: c.group, cats: [] })); g.cats.push(c); }
  const LV = [5, 10, 13, 15, 20, 27, 30, 35, 40, 45, 50];
  const shown = D.points.filter((p) => vis[D.cats[p[0]].id]).length;
  const look = (p: Partial<Look>) => setUi({ look: { ...ui.look, ...p } });
  return (
    <>
      <div className="ka-map-tools">
        <Button size="s" variant="secondary" onClick={() => setUi({ vis: Object.fromEntries(D.cats.map((c) => [c.id, true])) })}>{t("admin.map.showAll")}</Button>
        <Button size="s" variant="secondary" onClick={() => setUi({ vis: Object.fromEntries(D.cats.map((c) => [c.id, false])) })}>{t("admin.map.hideAll")}</Button>
        <label><input type="checkbox" checked={ui.names} onChange={(e) => setUi({ names: e.target.checked })} /> {t("admin.map.placeNames")}</label>
        <label><input type="checkbox" checked={ui.hideFound} onChange={(e) => setUi({ hideFound: e.target.checked })} /> {t("admin.map.hideFound")}</label>
        <label>{t("admin.map.chestLevel")} <select value={ui.chestLv[0]} onChange={(e) => { const v = +e.target.value; setUi({ chestLv: [v, Math.max(v, ui.chestLv[1])] }); }}>{LV.map((v) => <option key={v}>{v}</option>)}</select>
          {t("admin.map.chestLevelTo")} <select value={ui.chestLv[1]} onChange={(e) => { const v = +e.target.value; setUi({ chestLv: [Math.min(ui.chestLv[0], v), v] }); }}>{LV.map((v) => <option key={v}>{v}</option>)}</select></label>
      </div>
      {groups.map((g) => {
        const isOpen = ui.open[g.name] ?? true;
        const on = g.cats.filter((c) => vis[c.id]).length;
        return (
          <section key={g.name} className="ka-map-group">
            <h3><span onClick={() => { const v = g.cats.some((c) => !vis[c.id]); setUi({ vis: { ...vis, ...Object.fromEntries(g.cats.map((c) => [c.id, v])) } }); }} title={t("admin.map.toggleGroup")}>{g.name}</span>
              <small onClick={() => setUi({ open: { ...ui.open, [g.name]: !isOpen } })}>{on}/{g.cats.length} {isOpen ? "▾" : "▸"}</small></h3>
            {isOpen ? (
              <div className="ka-map-cats">
                {g.cats.map((c) => (
                  <button key={c.id} type="button" className={"ka-map-cat" + (vis[c.id] ? "" : " off")} title={c.label} onClick={() => setUi({ vis: { ...vis, [c.id]: !vis[c.id] } })}>
                    <img src={`${mapRoot()}i/${c.icon}`} alt="" /><span>{c.label}</span><small>{fmtN(c.count)}</small>
                  </button>
                ))}
              </div>
            ) : null}
          </section>
        );
      })}
      <section className="ka-map-group ka-map-look">
        <h3><span>{t("admin.map.mapLook")}</span></h3>
        <label>{t("admin.map.contourEvery")} <select value={ui.look.spacing} onChange={(e) => look({ spacing: +e.target.value })}>{[4, 8, 16, 32].map((v) => <option key={v} value={v}>{t("admin.map.metres", { n: v })}</option>)}</select></label>
        <label><input type="checkbox" checked={ui.look.fog} onChange={(e) => look({ fog: e.target.checked })} /> {t("admin.map.shroudZones")}</label>
        <label><input type="checkbox" checked={ui.look.animate} onChange={(e) => look({ animate: e.target.checked })} /> {t("admin.map.animateShroud")}</label>
        <label title={t("admin.map.flameLevelTip")}>{t("admin.map.flameLevel")} <input type="range" min={1} max={12} step={1} value={ui.look.flame} onChange={(e) => look({ flame: +e.target.value })} /> <b>{ui.look.flame}</b></label>
        {D.langs && D.langs.length > 1 ? (
          <label title={t("admin.map.namesInTip")}>{t("admin.map.namesIn")} <select value={lang ?? D.lang ?? ""} onChange={(e) => onLang(e.target.value)}>{D.langs.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}</select></label>
        ) : null}
      </section>
      <p className="ka-map-muted">{t("admin.map.markersOn", { shown: fmtN(shown), total: fmtN(D.points.length), found: fmtN(ui.found.length) })}</p>
      <p className="ka-map-muted" data-marker-source={D.source ?? "copy"}>{D.source === "game" ? (D.build ? t("admin.map.sourceGameBuild", { build: D.build }) : t("admin.map.sourceGame")) : t("admin.map.sourceCopy")}</p>
    </>
  );
}

function FindPanel({ F, missing, ui, findQ, setFindQ, toggle, addAll, dropsBy, clear, setUi }: {
  F: FindData | null; missing?: boolean; ui: MapUi; findQ: string; setFindQ: (s: string) => void; toggle: (k: FinderKind, s: string) => void; addAll: (l: [FinderKind, string][]) => void;
  dropsBy: Record<string, string[]>; clear: () => void; setUi: (p: Partial<MapUi>) => void;
}) {
  const t = useT();
  if (!F) return <p className="ka-map-muted">{missing ? t("admin.map.finderMissing") : t("admin.map.loading")}</p>;
  const isOn = (k: FinderKind, s: string) => ui.finders.some((f) => f.kind === k && f.slug === s);
  const mine = ui.finders.map((f) => (F[f.kind]?.[f.slug] ? { ...F[f.kind][f.slug], kind: f.kind, slug: f.slug } : null)).filter(Boolean) as (FindData["spawns"][string] & { kind: FinderKind; slug: string })[];
  const q = findQ.trim();
  const pick = (kind: FinderKind) => Object.entries(F[kind]).map(([slug, e]) => ({ ...e, kind, slug })).filter((e) => q.length < 2 || fmatch(q, e.n));
  const abc = (a: { n: string }, b: { n: string }) => a.n.localeCompare(b.n);
  const groups: { id: string; label: string; img?: string; list: ReturnType<typeof pick>; all?: boolean; extra?: ReactNode; more?: number }[] = [];
  if (q.length >= 3)
    for (const [item, d] of Object.entries(F.drops)) {
      if (!fmatch(q, item)) continue;
      const by = d.by.map((s) => (F.spawns[s] ? { ...F.spawns[s], kind: "spawns" as FinderKind, slug: s } : null)).filter(Boolean) as ReturnType<typeof pick>;
      groups.push({ id: `drop:${item}`, label: t("admin.map.itemDroppedBy", { item }), img: d.img, list: by, all: true });
      if (groups.length > 4) break;
    }
  const sp = pick("spawns");
  const ore = pick("ore").sort((a, b) => Number(a.more) - Number(b.more) || (a.order ?? 0) - (b.order ?? 0));
  if (ore.length) groups.push({ id: "ore", label: t("admin.map.oreGroup"), list: ore, extra: <label className="ka-map-inline"><input type="checkbox" checked={ui.surfaceOnly} onChange={(e) => setUi({ surfaceOnly: e.target.checked })} /> {t("admin.map.surfaceOnly")}</label> });
  const bo = sp.filter((e) => e.boss).sort(abc), cr = sp.filter((e) => !e.boss).sort(abc);
  if (bo.length) groups.push({ id: "bosses", label: t("admin.map.bosses"), list: bo });
  if (cr.length) groups.push({ id: "creatures", label: t("admin.map.creatures"), list: cr });
  const byG: Record<string, ReturnType<typeof pick>> = {};
  for (const e of pick("gather").sort(abc)) (byG[e.g ?? "Other"] ??= []).push(e);
  for (const [g, list] of Object.entries(byG).sort()) groups.push({ id: `g:${g}`, label: g === "Other" ? t("admin.map.otherGroup") : g, list });
  const ch = pick("chests").sort((a, b) => b.c - a.c);
  const cap = q.length < 2 ? 150 : 80;
  if (ch.length) groups.push({ id: "chests", label: t("admin.map.inChests"), list: ch.slice(0, cap), more: ch.length > cap ? ch.length : 0 });
  const qOpen = q.length >= 2;
  return (
    <>
      <section className="ka-map-mine">
        <h3><span>{t("admin.map.myFilter")}</span>{mine.length ? <Button size="s" variant="ghost" onClick={clear} style={{ marginLeft: "auto" }}>{t("admin.map.clear")}</Button> : null}</h3>
        {mine.length ? mine.map((e) => (
          <div key={`${e.kind}:${e.slug}`} className="ka-map-mrow">
            <span>{e.img ? <img src={`${mapRoot()}i/${e.img}`} alt="" /> : <i className="ka-dot" style={{ background: e.color ?? KIND_COLOR[e.kind] }} />}{e.n}</span>
            <small>{kindCount(e.kind, e.c)}</small><button className="ka-map-x" title={t("common.remove")} onClick={() => toggle(e.kind, e.slug)}>✕</button>
          </div>
        )) : <p className="ka-map-muted">{t("admin.map.filterEmpty")}</p>}
      </section>
      <TextInput type="search" aria-label={t("admin.map.filterLabel")} placeholder={t("admin.map.filterPlaceholder")} value={findQ} onChange={(e) => setFindQ(e.target.value)} />
      {groups.map((g) => {
        const isOpenG = qOpen || g.id.startsWith("drop:") || ui.open[`f:${g.id}`];
        const onN = g.list.filter((e) => isOn(e.kind, e.slug)).length;
        return (
          <section key={g.id} className="ka-map-group">
            <h3><span onClick={() => setUi({ open: { ...ui.open, [`f:${g.id}`]: !ui.open[`f:${g.id}`] } })}>{g.img ? <img src={`${mapRoot()}i/${g.img}`} alt="" style={{ width: 20, height: 20 }} /> : null}{g.label}</span>
              {g.all ? <Button size="s" variant="ghost" onClick={() => addAll(g.list.map((x) => [x.kind, x.slug]))}>{t("admin.map.showAll")}</Button> : null}
              <small onClick={() => setUi({ open: { ...ui.open, [`f:${g.id}`]: !ui.open[`f:${g.id}`] } })}>{onN ? t("admin.map.nOn", { n: onN }) : ""}{fmtN(g.list.length)} {isOpenG ? "▾" : "▸"}</small></h3>
            {isOpenG ? (
              <>
                {g.extra}
                <div className="ka-map-cats">
                  {g.list.map((e) => (
                    <button key={`${e.kind}:${e.slug}`} type="button" className={"ka-map-cat" + (isOn(e.kind, e.slug) ? "" : " off")} onClick={() => toggle(e.kind, e.slug)}
                      title={e.kind === "spawns" && dropsBy[e.slug] ? `${e.n}\n${t("admin.map.drops", { list: dropsBy[e.slug].join(", ") })}` : e.n}>
                      {e.img ? <img src={`${mapRoot()}i/${e.img}`} alt="" loading="lazy" /> : <i className="ka-dot" style={{ background: e.color ?? KIND_COLOR[e.kind] }} />}
                      <span>{e.n}</span><small>{fmtN(e.c)}</small>
                    </button>
                  ))}
                </div>
                {g.more ? <p className="ka-map-muted">{t("admin.map.moreItems", { n: fmtN(g.more) })}</p> : null}
              </>
            ) : null}
          </section>
        );
      })}
    </>
  );
}

function LivePanel({ players, connected, reason, waypoints, graves, zones, ui, setUi, enemies, enemyErr, hasEnemies, routeFrom, go, follow }: {
  players: AdminPlayer[]; connected: boolean; reason?: string; waypoints: { name: string; pos: AdminPos }[]; graves: { key: string; name: string | null; pos: AdminPos; at: number }[];
  zones: Zone[]; ui: MapUi; setUi: (p: Partial<MapUi>) => void; enemies: number; enemyErr: string; hasEnemies: boolean; routeFrom?: string;
  go: (x: number, z: number) => void; follow: (e: number) => void;
}) {
  const t = useT();
  return (
    <>
      <p className={"ka-map-status" + (connected ? " on" : "")}>{connected ? t("admin.map.liveConnected", { n: players.length }) : reason ?? t("admin.map.liveNotConnected")}</p>
      <section className="ka-map-group">
        <h3><span>{t("admin.pages.players")}</span><small>{players.length}</small></h3>
        {players.length ? players.map((p) => (
          <div key={p.entity} className="ka-map-lrow">
            <i className="ka-dot" style={{ background: "#7dff9b" }} /><b>{playerName(p)}</b>
            <small className="k-mono">{t("admin.map.playerRowPos", { x: Math.round(p.pos!.x), z: Math.round(p.pos!.z), h: Math.round(p.pos!.y) })}</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(p.pos!.x, p.pos!.z)}>{t("common.show")}</Button>
              <Button size="s" variant={ui.follow === p.entity ? "secondary" : "ghost"} onClick={() => follow(p.entity)}>{ui.follow === p.entity ? t("admin.map.following") : t("admin.map.follow")}</Button></span>
          </div>
        )) : <p className="ka-map-muted">{t("admin.players.nobody")}</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>{t("admin.zones.title")}</span><small>{zones.length}</small></h3>
        {zones.length ? zones.map((z) => (
          <div key={z.name} className="ka-map-lrow"><i className="ka-dot" style={{ background: "#ff8540" }} /><b>{z.name}</b><small>{t("admin.map.metres", { n: Math.round(z.radius) })}</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(z.pos.x, z.pos.z)}>{t("common.show")}</Button></span></div>
        )) : <p className="ka-map-muted">{t("admin.map.zonesEmpty")}</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>{t("admin.map.waypoints")}</span><small>{waypoints.length}</small></h3>
        {waypoints.length ? waypoints.map((w) => (
          <div key={w.name} className="ka-map-lrow"><img src={`${mapRoot()}i/g-mapmarker_waypoint.webp`} alt="" /><b>{w.name}</b><small className="k-mono">{Math.round(w.pos.x)}, {Math.round(w.pos.z)}</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(w.pos.x, w.pos.z)}>{t("common.show")}</Button></span></div>
        )) : <p className="ka-map-muted">{t("admin.map.waypointsEmpty")}</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>{t("admin.map.graves")}</span><small>{graves.length}</small></h3>
        {graves.length ? [...graves].reverse().slice(0, 12).map((g) => (
          <div key={g.key} className="ka-map-lrow"><i className="ka-dot" style={{ background: "#b9a7d6" }} /><b>{g.name ?? t("admin.map.aPlayer")}</b><small>{agoS(g.at)}</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(g.pos.x, g.pos.z)}>{t("common.show")}</Button></span></div>
        )) : <p className="ka-map-muted">{t("admin.map.noDeaths")}</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>{t("admin.map.livingEnemies")}</span><small>{ui.enemies ? enemies : t("admin.map.off")}</small></h3>
        <label className="ka-map-inline"><input type="checkbox" disabled={!hasEnemies} checked={ui.enemies} onChange={(e) => setUi({ enemies: e.target.checked })} /> {t("admin.map.showEnemies")}</label>
        {ui.enemies && enemyErr ? <p className="ka-map-muted" style={{ color: "var(--k-warn)" }}>{enemyErr}</p> : null}
      </section>
      <section className="ka-map-group">
        <h3><span>{t("admin.map.route")}</span></h3>
        {ui.route ? <><p>{routeFrom ?? t("admin.map.start")} → {ui.route.to.name ?? t("admin.map.point")}</p><Button size="s" onClick={() => setUi({ route: null })}>{t("admin.map.clearRoute")}</Button></>
          : <p className="ka-map-muted">{t("admin.map.routeEmpty")}</p>}
      </section>
    </>
  );
}
