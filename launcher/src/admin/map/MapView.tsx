// MapView: the admin map: a Leaflet map built on the Enshrouded Interactive Map (MIT, Nicolas Ferreira;
// licenses/Enshrouded-Interactive-Map-LICENSE.txt) with the side panel (markers, find & filter, live), the search box, the context menus and the live overlays: players
// from the state pushes, waypoints and graves (launcher history), protection zones (zones_list, editable from the map
// with zones_add / zones_remove), enemies near players, routes. Assets: assetsRoot() + "map/" (../catalog.ts).
import { useQueryClient } from "@tanstack/react-query";
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { callAdmin, useAdminCall, useAdminQuery, useAdminState, useHasOp, type AdminPlayer, type AdminPos } from "../../api/admin";
import { useAssetsStatus } from "../../components/GameArt";
import { assetsRoot, prettyTemplate } from "../catalog";
import { playerName, useServerId } from "../common";
import { deleteWaypoint, saveWaypoint, useHistory } from "../history";
import type { EnemiesResult, Zone } from "../types";
import { Banner, Button, TextInput, confirm, toastError, toastOk, agoS } from "../ui";
import { ACTIONS, ACTION_LABEL, DEFAULT_DENY, useMapFocus, useZoneDraft, zoneArgs } from "../zones";
import { Height, KIND_COLOR, MapEngine, markerSetKey, posTxt, type FindData, type Finder, type FinderKind, type Look, type MarkerData, type MenuTarget, type Route, type Spot } from "./engine";
import "./map.css";

export const MAP_ROOT = `${assetsRoot()}map/`;
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
const fmtN = (n: number) => Number(n).toLocaleString("en-US");
const dist = (d: number) => (d >= 1000 ? `${(d / 1000).toFixed(2)} km` : `${Math.round(d)} m`);
const DIRS = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
const bearing = (a: { x: number; z: number }, b: { x: number; z: number }) => DIRS[Math.round((((Math.atan2(b.x - a.x, b.z - a.z) * 180) / Math.PI + 360) % 360) / 45) % 8];
const KIND_NOUN: Record<FinderKind, string> = { spawns: "spawn points", gather: "locations", ore: "deposits", chests: "chests" };

interface MenuItem {
  t: ReactNode;
  tag?: string;
  fn?: () => void;
  sep?: boolean;
  disabled?: boolean;
}
interface MenuState {
  title: string;
  sub?: string;
  point: { x: number; y: number };
  items: MenuItem[];
  form?: ReactNode;
}

export function MapView({ height }: { height: number }) {
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
    const t = setTimeout(() => { try { localStorage.setItem(STORE, JSON.stringify(ui)); } catch { /* full */ } }, 200);
    return () => clearTimeout(t);
  }, [ui]);

  const box = useRef<HTMLDivElement>(null);
  const eng = useRef<MapEngine | null>(null);
  const [assets, setAssets] = useState<boolean | null>(null);
  // the launcher makes the map art from the game on its own (components/GameArt.tsx): probe again when it has
  const artStatus = useAssetsStatus().data?.admin;
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
    fetch(`${MAP_ROOT}gpu/d/0_0.webp`, { method: "HEAD" })
      .then((r) => alive && setAssets(r.ok && (r.headers.get("content-type") ?? "").startsWith("image")))
      .catch(() => alive && setAssets(false));
    return () => { alive = false; };
  }, [probe]);
  const menuRef = useRef<(t: MenuTarget, p: { x: number; y: number }) => void>(() => undefined);
  useEffect(() => {
    if (assets === null || !box.current) return;
    const e = new MapEngine(box.current, MAP_ROOT, {
      menu: (t, p) => menuRef.current(t, p),
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
      }).catch((err) => toastError({ message: `Map data: ${(err as Error).message}` }));
    }
    return () => { ro.disconnect(); e.destroy(); eng.current = null; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assets]);
  useEffect(() => { eng.current?.map.invalidateSize(); }, [height, ui.side]);
  const changeLang = useCallback((code: string) => {
    const e = eng.current;
    if (!e) return;
    e.setLang(code).then((ok) => {
      if (!ok) return toastError({ message: `No marker texts for ${code}.` });
      setUi({ lang: code });
      setMarkerLang(code);
      setD((d) => (d ? { ...d } : d));
    }, (err) => toastError({ message: `Marker texts: ${(err as Error).message}` }));
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
    const t = setInterval(scan, 5000);
    return () => { stop = true; clearInterval(t); };
  }, [ui.enemies, hasEnemies, players.length, id]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---------------------------------------------------------------- actions
  const heightAt = async (pos: Spot, lift: number) => {
    if (pos.y != null) return pos.y;
    const h = await Height.exact(pos.x, pos.z);
    return h != null ? h + lift : null;
  };
  const teleport = async (p: AdminPlayer, target: Spot | { player: number }, label: string) => {
    const who = playerName(p);
    if ("player" in target) {
      if (!(await confirm({ title: `Teleport ${who}?`, body: <p>Move {who} {label}.</p>, ok: "Teleport" }))) return;
      return tpp.mutate({ entity: p.entity, target: target.player }, { onSuccess: () => toastOk(`Teleported ${who} ${label}`), onError: (e) => toastError(e) });
    }
    const y = await heightAt(target, 3);
    if (y == null) return toastError({ message: "No height known for that spot yet (the map relief is still loading, or there is no relief)." });
    const body = { x: +target.x.toFixed(2), y: +y.toFixed(2), z: +target.z.toFixed(2) };
    if (!(await confirm({ title: `Teleport ${who}?`, body: <p>Move {who} {label} (X {Math.round(body.x)}, Z {Math.round(body.z)}, height {Math.round(body.y)}). The server looks for a safe spot near the target.</p>, ok: "Teleport" }))) return;
    tp.mutate({ entity: p.entity, ...body, safe: true }, { onSuccess: () => toastOk(`Teleported ${who} ${label}`), onError: (e) => toastError(e) });
  };
  const setRoute = (r: Route | null) => setUi({ route: r });
  const originItems = (to: Route["to"]): MenuItem[] => {
    const out: MenuItem[] = players.map((p) => ({ t: `Route from ${playerName(p)} to here`, fn: () => setRoute({ from: { kind: "player", entity: p.entity, name: p.name }, to }) }));
    const r = uiRef.current.route;
    if (r && r.from.kind === "point") out.push({ t: `Route from ${r.from.name || "the marked point"} to here`, fn: () => setRoute({ from: r.from, to }) });
    out.push({ t: "Measure from here", fn: () => { setRoute({ from: { kind: "point", x: to.x, y: to.y ?? 0, z: to.z, name: to.name }, to }); toastOk("Start point set", "Click another spot and pick “Route from … to here”."); } });
    return out;
  };
  const teleportItems = (target: Spot | { player: number }, label: string, except?: number): MenuItem[] =>
    !hasTp ? [] : players.filter((p) => p.entity !== except).map((p) => ({ t: `Teleport ${playerName(p)} ${label}`, fn: () => teleport(p, target, label) }));

  const waypointForm = (pos: Spot, name0: string, point: { x: number; y: number }) => {
    setMenu({ title: "Save waypoint", sub: posTxt(pos), point, items: [], form: <WaypointForm initial={name0} onCancel={closeMenu} onSave={async (name) => {
      const y = await heightAt(pos, 1);
      if (y == null) return toastError({ message: "No height known for that spot yet." });
      const err = saveWaypoint(id, name, { x: pos.x, y, z: pos.z });
      if (err) return toastError({ message: err });
      toastOk(`Saved waypoint ${name}`);
      closeMenu();
    }} /> });
  };
  const zoneForm = (pos: Spot, point: { x: number; y: number }, name0 = "") => {
    setZoneDraft({ pos, radius: 30 });
    setMenu({ title: "Protect an area here", sub: posTxt(pos), point, items: [], form: <ZoneMapForm initial={name0} onRadius={(r) => setZoneDraft({ pos, radius: r })} onCancel={closeMenu}
      onSave={async (f) => {
        const y = await heightAt(pos, 0);
        const r = zoneArgs({ ...f, x: String(pos.x), y: String(y ?? 0), z: String(pos.z), owners: [], allowAdmins: true, editing: false });
        if (r.error) return toastError({ message: r.error });
        zoneAdd.mutate(r.args!, { onSuccess: () => { toastOk(`Added ${f.name}`, "Owners and details on the Zones page."); closeMenu(); } });
      }}
      onMore={async () => { const y = await heightAt(pos, 0); useZoneDraft.getState().set({ x: pos.x, y: y ?? 0, z: pos.z }); nav("../zones"); }} /> });
  };

  menuRef.current = (t: MenuTarget, point) => {
    if (t.kind === "spot") {
      const pos = t.pos;
      const to = { ...pos, name: `${Math.round(pos.x)}, ${Math.round(pos.z)}` };
      setMenu({ title: "This spot", sub: `X ${Math.round(pos.x)} · Z ${Math.round(pos.z)}${pos.y != null ? ` · ground ≈${Math.round(pos.y)} m` : ""}`, point, items: [
        { t: "Save waypoint here…", fn: () => waypointForm(pos, "", point) },
        ...(hasZones ? [{ t: "Protect an area here…", fn: () => zoneForm(pos, point) }] : []),
        ...teleportItems(pos, "here"),
        { sep: true, t: "" },
        ...originItems(to),
        { t: "Copy coordinates", fn: () => navigator.clipboard?.writeText(`X ${pos.x} Z ${pos.z}${pos.y != null ? ` H ${Math.round(pos.y)}` : ""}`).then(() => toastOk("Coordinates copied")) },
      ] });
      Height.exact(pos.x, pos.z).then((h) => { if (h != null) setMenu((m) => (m && m.title === "This spot" && m.point === point ? { ...m, sub: `X ${Math.round(pos.x)} · Z ${Math.round(pos.z)} · ground ≈${Math.round(h)} m` } : m)); });
    } else if (t.kind === "marker") {
      const pos = t.pos;
      const items: MenuItem[] = [...originItems({ ...pos, name: t.name }), { sep: true, t: "" }, { t: "Save waypoint here…", fn: () => waypointForm(pos, t.name.slice(0, 40), point) },
        ...(hasZones ? [{ t: "Protect an area here…", fn: () => zoneForm(pos, point, t.name.slice(0, 64)) }] : []), ...teleportItems(pos, `to ${t.name}`)];
      if (t.idx != null) {
        const f = uiRef.current.found.includes(t.idx);
        items.push({ sep: true, t: "" }, { t: f ? "Unmark found" : "Mark as found", fn: () => setUi({ found: f ? uiRef.current.found.filter((v) => v !== t.idx) : [...uiRef.current.found, t.idx!] }) });
      }
      setMenu({ title: t.name, sub: `${t.sub ? t.sub + " · " : ""}${posTxt(pos)}`, point, items });
    } else if (t.kind === "player") {
      const p = players.find((x) => x.entity === t.entity);
      if (!p?.pos) return;
      const name = playerName(p);
      const others = players.filter((x) => x.entity !== t.entity);
      setMenu({ title: name, sub: `${posTxt(p.pos)}${p.healthMax ? ` · health ${Math.round(p.health ?? 0)}/${Math.round(p.healthMax)}` : ""}`, point, items: [
        { t: uiRef.current.follow === t.entity ? "Stop following" : "Follow on the map", fn: () => setUi({ follow: uiRef.current.follow === t.entity ? null : t.entity }) },
        { t: `Route from ${name} to …`, fn: () => { setRoute({ from: { kind: "player", entity: t.entity, name }, to: uiRef.current.route?.to ?? { ...p.pos!, name } }); toastOk("Now click the destination and pick the route entry."); } },
        ...others.map((o) => ({ t: `Route from ${playerName(o)} to ${name}`, fn: () => setRoute({ from: { kind: "player", entity: o.entity, name: o.name }, to: { ...p.pos!, name } }) })),
        { t: `Save waypoint at ${name}…`, fn: () => waypointForm(p.pos!, `${name} spot`.slice(0, 40), point) },
        ...teleportItems({ player: t.entity }, `to ${name}`, t.entity),
        { sep: true, t: "" },
        { t: "Open on the Players page", fn: () => nav("../players") },
      ] });
    } else if (t.kind === "waypoint") {
      setMenu({ title: t.name, sub: `Waypoint · ${posTxt(t.pos)}`, point, items: [
        ...originItems({ ...t.pos, name: t.name }),
        ...teleportItems(t.pos, `to ${t.name}`),
        { sep: true, t: "" },
        { t: "Delete waypoint", fn: async () => { if (await confirm({ title: "Delete waypoint?", body: <p>{t.name}</p>, ok: "Delete", danger: true })) deleteWaypoint(id, t.name); } },
      ] });
    } else if (t.kind === "grave") {
      const name = `${t.name ?? "A player"}'s grave`;
      setMenu({ title: name, sub: `${agoS(t.at)} · ${posTxt(t.pos)}`, point, items: [
        ...originItems({ ...t.pos, name }),
        ...teleportItems({ ...t.pos, y: t.pos.y + 1 }, "to this grave"),
        { t: "Save waypoint here…", fn: () => waypointForm(t.pos, `${t.name ?? "Grave"} grave`.slice(0, 40), point) },
      ] });
    } else if (t.kind === "zone") {
      const z = (zones.data ?? []).find((x) => x.name === t.name);
      if (!z) return;
      setMenu({ title: `⛉ ${z.name}`, sub: `${Math.round(z.radius)} m · refuses ${z.deny.map((a) => ACTION_LABEL[a] ?? a).join(", ")}`, point, items: [
        { t: "Change the radius…", fn: () => {
          setZoneDraft({ pos: z.pos, radius: z.radius });
          setMenu({ title: `Radius of ${z.name}`, sub: posTxt(z.pos), point, items: [], form: <RadiusForm initial={z.radius} onRadius={(r) => setZoneDraft({ pos: z.pos, radius: r })} onCancel={closeMenu}
            onSave={(r) => zoneAdd.mutate({ name: z.name, x: z.pos.x, y: z.pos.y, z: z.pos.z, radius: r, deny: z.deny, owners: z.owners, allowAdmins: z.allowAdmins }, { onSuccess: () => { toastOk(`${z.name}: ${r} m`); closeMenu(); } })} /> });
        } },
        { t: "Edit on the Zones page", fn: () => nav("../zones") },
        ...originItems({ ...z.pos, name: z.name }),
        { sep: true, t: "" },
        { t: "Remove zone", fn: async () => { if (await confirm({ title: `Remove the zone ${z.name}?`, body: <p>Its protection ends at once.</p>, ok: "Remove", danger: true })) zoneRemove.mutate({ name: z.name }, { onSuccess: () => toastOk(`Removed ${z.name}`) }); } },
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
      push("Dropped by creatures", Object.entries(F.drops).filter(([n]) => fmatch(s, n)).slice(0, 5).map(([n, d]) => ({
        img: d.img, t: n, s: `Dropped by ${d.by.slice(0, 4).map((b) => F.spawns[b]?.n ?? b).join(", ")}${d.by.length > 4 ? ` +${d.by.length - 4}` : ""} · show all on the map`,
        act: () => { addFinders(d.by.filter((b) => F.spawns[b]).map((b) => ["spawns", b] as [FinderKind, string])); toastOk(`Showing where ${d.by.length} creature types that drop ${n} spawn.`); },
      })));
    if (F) {
      const hits: (ReturnType<typeof findEntry> & object)[] = [];
      for (const kind of ["spawns", "gather", "ore", "chests"] as FinderKind[]) for (const [slug, e] of Object.entries(F[kind])) if (fmatch(s, e.n)) hits.push({ ...e, kind, slug });
      hits.sort((a, b) => Number(!!b.boss) - Number(!!a.boss) || b.c - a.c);
      push("Where to find", hits.slice(0, 8).map((e) => ({ img: e.img, color: e.color || KIND_COLOR[e.kind], t: e.n, on: isOn(e.kind, e.slug),
        s: `${fmtN(e.c)} ${e.kind === "chests" ? "chests can hold it" : KIND_NOUN[e.kind]}${e.boss ? " · boss" : ""} · ${isOn(e.kind, e.slug) ? "on the map (click to remove)" : "show on the map"}`,
        act: () => toggleFinder(e.kind, e.slug) })));
    }
    const live: (typeof out)[number]["items"] = [];
    for (const p of players) if (fmatch(s, p.name)) live.push({ t: playerName(p), s: `Player · ${posTxt(p.pos!)}`, color: "#7dff9b", act: () => eng.current?.flyTo(p.pos!.x, p.pos!.z, 5) });
    for (const w of hist.waypoints) if (fmatch(s, w.name)) live.push({ t: w.name, s: `Waypoint · ${posTxt(w.pos)}`, img: "g-mapmarker_waypoint.webp", act: () => eng.current?.flyTo(w.pos.x, w.pos.z, 5) });
    for (const z of zones.data ?? []) if (fmatch(s, z.name)) live.push({ t: z.name, s: `Protection zone · ${Math.round(z.radius)} m`, color: "#ff8540", act: () => eng.current?.flyTo(z.pos.x, z.pos.z, 5) });
    push("Live", live.slice(0, 8));
    if (D) {
      push("Places", D.labels.filter((l) => fmatch(s, l[0])).slice(0, 6).map((l) => ({ t: l[0], s: `Place · ${posTxt({ x: l[1], z: l[2] })}`, color: "#d8c9a6", act: () => eng.current?.flyTo(l[1], l[2], 5) })));
      const pts: (typeof out)[number]["items"] = [];
      for (let i = 0; i < D.points.length && pts.length < 10; i++) {
        const p = D.points[i], c = D.cats[p[0]];
        if (fmatch(s, p[4], c.label)) pts.push({ img: p[7] || c.icon, t: p[4], s: `${c.label}${p[6] ? ` · level ${p[6]}` : ""} · ${posTxt({ x: p[1], z: p[2] })}`,
          act: () => { if (!ui.vis?.[c.id]) setUi({ vis: { ...(ui.vis ?? {}), [c.id]: true } }); eng.current?.flyTo(p[1], p[2], 5.5); } });
      }
      push("Markers", pts);
    }
    return out;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q, F, D, players, hist.waypoints, zones.data, ui.finders, ui.vis]);

  // ---------------------------------------------------------------- render
  const route = ui.route;
  const ra = route ? (route.from.kind === "player" ? (() => { const p = players.find((x) => x.entity === (route.from as { entity: number }).entity); return p?.pos ? { ...p.pos, name: playerName(p) } : null; })() : route.from) : null;
  return (
    <div className={"ka-map-root" + (ui.side ? "" : " noside")} style={{ height }} onKeyDown={(e) => { if (e.key === "Escape") { closeMenu(); setResultsOpen(false); } }}>
      <aside className="ka-map-side">
        <div className="ka-map-search">
          <TextInput type="search" aria-label="Search the map" placeholder="Search items, creatures, places, players…" value={q} autoComplete="off" spellCheck={false}
            onChange={(e) => { setQ(e.target.value); setResultsOpen(true); }} onFocus={() => setResultsOpen(true)}
            onKeyDown={(e) => { if (e.key === "Enter" && results[0]?.items[0]) { results[0].items[0].act(); setResultsOpen(false); } }} />
          {resultsOpen && q.trim().length >= 2 ? (
            <div className="ka-map-results" onMouseLeave={() => undefined}>
              {results.length ? results.map((r) => (
                <div key={r.sec}>
                  <h4>{r.sec}</h4>
                  {r.items.map((it, i) => (
                    <button key={i} type="button" className={it.on ? "on" : ""} onClick={() => { it.act(); setResultsOpen(false); }}>
                      {it.img ? <img src={`${MAP_ROOT}i/${it.img}`} alt="" /> : <i className="ka-dot" style={{ background: it.color ?? "#d8c9a6" }} />}
                      <b>{it.t}</b><small>{it.s}</small>
                    </button>
                  ))}
                </div>
              )) : <p className="ka-map-muted">Nothing matches.</p>}
            </div>
          ) : null}
        </div>
        <div className="ka-map-tabs" role="tablist">
          {([["layers", "Markers"], ["find", "Find & filter"], ["live", "Live"]] as const).map(([k, l]) => <button key={k} role="tab" className={ui.tab === k ? "on" : ""} onClick={() => setUi({ tab: k })}>{l}</button>)}
        </div>
        <div className="ka-map-panel" onClick={() => setResultsOpen(false)}>
          {ui.tab === "layers" ? <LayersPanel D={D} ui={ui} setUi={setUi} assets={assets} lang={markerLang} onLang={changeLang} /> : null}
          {ui.tab === "find" ? <FindPanel F={F} missing={findMissing} ui={ui} findQ={findQ} setFindQ={setFindQ} toggle={toggleFinder} addAll={addFinders} dropsBy={eng.current?.dropsBy ?? {}} clear={() => setUi({ finders: [] })} setUi={setUi} /> : null}
          {ui.tab === "live" ? (
            <LivePanel players={players} connected={st.ready} reason={st.error?.message} waypoints={hist.waypoints} graves={hist.graves} zones={zones.data ?? []} ui={ui} setUi={setUi}
              enemies={enemies.length} enemyErr={enemyErr} hasEnemies={hasEnemies} routeFrom={ra?.name ?? (route?.from.kind === "point" ? route.from.name : undefined)}
              go={(x, z) => eng.current?.flyTo(x, z, 5)} follow={(e) => { const n = ui.follow === e ? null : e; setUi({ follow: n }); const pos = n != null ? players.find((p) => p.entity === n)?.pos : null; if (pos) eng.current?.flyTo(pos.x, pos.z, 5); }} />
          ) : null}
        </div>
        <div className="ka-map-credit">Map, names and icons: game data © Keen Games. Map and search built on the <b>Enshrouded Interactive Map</b> by Nicolas Ferreira (MIT). Leaflet (BSD-2).</div>
      </aside>
      <div className="ka-map-area">
        <div className="ka-map" ref={box} />
        {assets === false ? (
          <div style={{ position: "absolute", left: 56, right: 56, top: 12, zIndex: 1001 }}>
            <Banner tone="info" title={artStatus?.canExtract === false ? "Map art needs the Enshrouded client" : "Preparing map art…"} />
          </div>
        ) : null}
        <button className="ka-map-sidetoggle" title="Show or hide the side panel" onClick={() => setUi({ side: !ui.side })}>☰</button>
        {route ? (
          <div className="ka-map-route">
            {ra ? (
              <>
                <span><b>{ra.name ?? "start"}</b> → <b>{route.to.name ?? "point"}</b></span>
                <span className="big">{dist(Math.hypot(route.to.x - ra.x, route.to.z - ra.z))}</span>
                <span>{bearing(ra, route.to)}</span>
                {route.to.y != null && ra.y != null ? (() => { const dy = route.to.y! - ra.y; return <span className={dy > 2 ? "up" : dy < -2 ? "down" : ""}>{dy > 2 ? "▲" : dy < -2 ? "▼" : "≈"} {Math.abs(Math.round(dy))} m</span>; })() : null}
              </>
            ) : <span>Route start ({route.from.kind === "player" ? route.from.name ?? "player" : "point"}) is not in the world</span>}
            <button className="ka-map-x" title="Clear the route" onClick={() => setRoute(null)}>✕</button>
          </div>
        ) : null}
        <div className="ka-map-zoom">
          <button title="Zoom in" onClick={() => eng.current?.map.zoomIn()}>+</button>
          <button title="Zoom out" onClick={() => eng.current?.map.zoomOut()}>−</button>
          <button title="Whole world" onClick={() => eng.current?.fit()}>⤢</button>
        </div>
        {coords ? <div className="ka-map-coords"><span>X <b>{Math.round(coords.x)}</b></span><span>Z <b>{Math.round(coords.z)}</b></span>{coords.y != null ? <span>Height <b>≈{Math.round(coords.y)}</b> m</span> : null}</div> : null}
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
        <button key={i} role="menuitem" disabled={it.disabled} onClick={() => { const keep = /…$/.test(String(it.t)); if (!keep) close(); it.fn?.(); }}>
          {it.t}{it.tag ? <span className="tag">{it.tag}</span> : null}
        </button>
      ))}
      {menu.form}
    </div>
  );
}

function WaypointForm({ initial, onSave, onCancel }: { initial: string; onSave: (name: string) => void; onCancel: () => void }) {
  const [name, setName] = useState(initial);
  return (
    <form className="ka-map-form" onSubmit={(e) => { e.preventDefault(); if (name.trim()) onSave(name.trim()); }} onKeyDown={(e) => { if (e.key === "Escape") onCancel(); e.stopPropagation(); }}>
      <div className="ka-inline"><TextInput autoFocus maxLength={40} placeholder="Name" value={name} onChange={(e) => setName(e.target.value)} /><Button size="s" variant="primary" type="submit">Save</Button></div>
    </form>
  );
}

function ZoneMapForm({ initial, onSave, onCancel, onRadius, onMore }: { initial: string; onSave: (f: { name: string; radius: string; deny: string[] }) => void; onCancel: () => void; onRadius: (r: number) => void; onMore: () => void }) {
  const [name, setName] = useState(initial);
  const [radius, setRadius] = useState("30");
  const [deny, setDeny] = useState<string[]>(DEFAULT_DENY);
  return (
    <form className="ka-map-form" onSubmit={(e) => { e.preventDefault(); onSave({ name, radius, deny }); }} onKeyDown={(e) => { if (e.key === "Escape") onCancel(); e.stopPropagation(); }}>
      <TextInput autoFocus maxLength={64} placeholder="Zone name" value={name} onChange={(e) => setName(e.target.value)} />
      <div className="ka-inline"><span className="k-hint">Radius</span><TextInput inputMode="numeric" value={radius} onChange={(e) => { setRadius(e.target.value); const n = Number(e.target.value); if (n >= 1 && n <= 2000) onRadius(n); }} style={{ width: 80 }} /><span className="k-hint">m</span></div>
      <div className="ka-flags">{ACTIONS.map((a) => <label key={a}><input type="checkbox" checked={deny.includes(a)} onChange={(e) => setDeny(e.target.checked ? [...deny, a] : deny.filter((x) => x !== a))} /> {ACTION_LABEL[a]}</label>)}</div>
      <div className="ka-inline"><Button size="s" variant="primary" type="submit">Add zone</Button><Button size="s" variant="ghost" onClick={onMore}>Owners…</Button></div>
    </form>
  );
}

function RadiusForm({ initial, onSave, onCancel, onRadius }: { initial: number; onSave: (r: number) => void; onCancel: () => void; onRadius: (r: number) => void }) {
  const [v, setV] = useState(String(Math.round(initial)));
  const n = Number(v);
  return (
    <form className="ka-map-form" onSubmit={(e) => { e.preventDefault(); if (n >= 1 && n <= 2000) onSave(n); else toastError({ message: "Radius: 1 to 2000 m." }); }} onKeyDown={(e) => { if (e.key === "Escape") onCancel(); e.stopPropagation(); }}>
      <input type="range" min={1} max={500} value={Math.min(500, n || 1)} onChange={(e) => { setV(e.target.value); onRadius(Number(e.target.value)); }} className="ka-dial" />
      <div className="ka-inline"><TextInput autoFocus inputMode="numeric" value={v} onChange={(e) => { setV(e.target.value); const x = Number(e.target.value); if (x >= 1 && x <= 2000) onRadius(x); }} style={{ width: 90 }} /><span className="k-hint">m</span><Button size="s" variant="primary" type="submit">Save</Button></div>
    </form>
  );
}

function LayersPanel({ D, ui, setUi, assets, lang, onLang }: { D: MarkerData | null; ui: MapUi; setUi: (p: Partial<MapUi>) => void; assets: boolean | null; lang: string | null; onLang: (code: string) => void }) {
  if (assets === false) return <p className="ka-map-muted">No map data yet: the launcher prepares it from your installed game (Preparing game art in the top bar).</p>;
  if (!D || !ui.vis) return <p className="ka-map-muted">Loading the markers…</p>;
  const vis = ui.vis;
  const groups: { name: string; cats: MarkerData["cats"] }[] = [];
  for (const c of D.cats) { let g = groups.find((x) => x.name === c.group); if (!g) groups.push((g = { name: c.group, cats: [] })); g.cats.push(c); }
  const LV = [5, 10, 13, 15, 20, 27, 30, 35, 40, 45, 50];
  const shown = D.points.filter((p) => vis[D.cats[p[0]].id]).length;
  const look = (p: Partial<Look>) => setUi({ look: { ...ui.look, ...p } });
  return (
    <>
      <div className="ka-map-tools">
        <Button size="s" variant="secondary" onClick={() => setUi({ vis: Object.fromEntries(D.cats.map((c) => [c.id, true])) })}>Show all</Button>
        <Button size="s" variant="secondary" onClick={() => setUi({ vis: Object.fromEntries(D.cats.map((c) => [c.id, false])) })}>Hide all</Button>
        <label><input type="checkbox" checked={ui.names} onChange={(e) => setUi({ names: e.target.checked })} /> Place names</label>
        <label><input type="checkbox" checked={ui.hideFound} onChange={(e) => setUi({ hideFound: e.target.checked })} /> Hide found</label>
        <label>Chest level <select value={ui.chestLv[0]} onChange={(e) => { const v = +e.target.value; setUi({ chestLv: [v, Math.max(v, ui.chestLv[1])] }); }}>{LV.map((v) => <option key={v}>{v}</option>)}</select>
          to <select value={ui.chestLv[1]} onChange={(e) => { const v = +e.target.value; setUi({ chestLv: [Math.min(ui.chestLv[0], v), v] }); }}>{LV.map((v) => <option key={v}>{v}</option>)}</select></label>
      </div>
      {groups.map((g) => {
        const isOpen = ui.open[g.name] ?? true;
        const on = g.cats.filter((c) => vis[c.id]).length;
        return (
          <section key={g.name} className="ka-map-group">
            <h3><span onClick={() => { const v = g.cats.some((c) => !vis[c.id]); setUi({ vis: { ...vis, ...Object.fromEntries(g.cats.map((c) => [c.id, v])) } }); }} title="Show or hide the whole group">{g.name}</span>
              <small onClick={() => setUi({ open: { ...ui.open, [g.name]: !isOpen } })}>{on}/{g.cats.length} {isOpen ? "▾" : "▸"}</small></h3>
            {isOpen ? (
              <div className="ka-map-cats">
                {g.cats.map((c) => (
                  <button key={c.id} type="button" className={"ka-map-cat" + (vis[c.id] ? "" : " off")} title={c.label} onClick={() => setUi({ vis: { ...vis, [c.id]: !vis[c.id] } })}>
                    <img src={`${MAP_ROOT}i/${c.icon}`} alt="" /><span>{c.label}</span><small>{fmtN(c.count)}</small>
                  </button>
                ))}
              </div>
            ) : null}
          </section>
        );
      })}
      <section className="ka-map-group ka-map-look">
        <h3><span>Map look</span></h3>
        <label>Contour lines every <select value={ui.look.spacing} onChange={(e) => look({ spacing: +e.target.value })}>{[4, 8, 16, 32].map((v) => <option key={v} value={v}>{v} m</option>)}</select></label>
        <label><input type="checkbox" checked={ui.look.fog} onChange={(e) => look({ fog: e.target.checked })} /> Shroud zones</label>
        <label><input type="checkbox" checked={ui.look.animate} onChange={(e) => look({ animate: e.target.checked })} /> Animate the Shroud</label>
        <label title="Shroud zones above this Flame level are drawn as dangerous (red, hatched)">Flame level <input type="range" min={1} max={12} step={1} value={ui.look.flame} onChange={(e) => look({ flame: +e.target.value })} /> <b>{ui.look.flame}</b></label>
        {D.langs && D.langs.length > 1 ? (
          <label title="The game's own texts for marker names, categories and place names">Names in <select value={lang ?? D.lang ?? ""} onChange={(e) => onLang(e.target.value)}>{D.langs.map((l) => <option key={l.code} value={l.code}>{l.name}</option>)}</select></label>
        ) : null}
      </section>
      <p className="ka-map-muted">{fmtN(shown)} of {fmtN(D.points.length)} markers on · {fmtN(ui.found.length)} marked found. Click a marker to mark it found.</p>
      <p className="ka-map-muted" data-marker-source={D.source ?? "copy"}>{D.source === "game" ? `Markers from your game's data${D.build ? ` (build ${D.build})` : ""}.` : "Markers from a local copy (the game's own are made when the launcher prepares the map art)."}</p>
    </>
  );
}

function FindPanel({ F, missing, ui, findQ, setFindQ, toggle, addAll, dropsBy, clear, setUi }: {
  F: FindData | null; missing?: boolean; ui: MapUi; findQ: string; setFindQ: (s: string) => void; toggle: (k: FinderKind, s: string) => void; addAll: (l: [FinderKind, string][]) => void;
  dropsBy: Record<string, string[]>; clear: () => void; setUi: (p: Partial<MapUi>) => void;
}) {
  if (!F) return <p className="ka-map-muted">{missing ? "The finder's data (creature spawns, plants, ore, chest loot) is not extracted from the game yet; without a local copy of it, the Find tab stays empty." : "Loading…"}</p>;
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
      groups.push({ id: `drop:${item}`, label: `${item}: dropped by`, img: d.img, list: by, all: true });
      if (groups.length > 4) break;
    }
  const sp = pick("spawns");
  const ore = pick("ore").sort((a, b) => Number(a.more) - Number(b.more) || (a.order ?? 0) - (b.order ?? 0));
  if (ore.length) groups.push({ id: "ore", label: "Ore and deposits", list: ore, extra: <label className="ka-map-inline"><input type="checkbox" checked={ui.surfaceOnly} onChange={(e) => setUi({ surfaceOnly: e.target.checked })} /> Surface only</label> });
  const bo = sp.filter((e) => e.boss).sort(abc), cr = sp.filter((e) => !e.boss).sort(abc);
  if (bo.length) groups.push({ id: "bosses", label: "Bosses", list: bo });
  if (cr.length) groups.push({ id: "creatures", label: "Creatures", list: cr });
  const byG: Record<string, ReturnType<typeof pick>> = {};
  for (const e of pick("gather").sort(abc)) (byG[e.g ?? "Other"] ??= []).push(e);
  for (const [g, list] of Object.entries(byG).sort()) groups.push({ id: `g:${g}`, label: g, list });
  const ch = pick("chests").sort((a, b) => b.c - a.c);
  const cap = q.length < 2 ? 150 : 80;
  if (ch.length) groups.push({ id: "chests", label: "Found in chests", list: ch.slice(0, cap), more: ch.length > cap ? ch.length : 0 });
  const qOpen = q.length >= 2;
  return (
    <>
      <section className="ka-map-mine">
        <h3><span>My filter</span>{mine.length ? <Button size="s" variant="ghost" onClick={clear} style={{ marginLeft: "auto" }}>Clear</Button> : null}</h3>
        {mine.length ? mine.map((e) => (
          <div key={`${e.kind}:${e.slug}`} className="ka-map-mrow">
            <span>{e.img ? <img src={`${MAP_ROOT}i/${e.img}`} alt="" /> : <i className="ka-dot" style={{ background: e.color ?? KIND_COLOR[e.kind] }} />}{e.n}</span>
            <small>{fmtN(e.c)} {KIND_NOUN[e.kind]}</small><button className="ka-map-x" title="Remove" onClick={() => toggle(e.kind, e.slug)}>✕</button>
          </div>
        )) : <p className="ka-map-muted">Pick creatures, plants, ore or chest items below (or search an item at the top) to show where they are.</p>}
      </section>
      <TextInput type="search" aria-label="Filter the lists" placeholder="Filter the lists below…" value={findQ} onChange={(e) => setFindQ(e.target.value)} />
      {groups.map((g) => {
        const isOpenG = qOpen || g.id.startsWith("drop:") || ui.open[`f:${g.id}`];
        const onN = g.list.filter((e) => isOn(e.kind, e.slug)).length;
        return (
          <section key={g.id} className="ka-map-group">
            <h3><span onClick={() => setUi({ open: { ...ui.open, [`f:${g.id}`]: !ui.open[`f:${g.id}`] } })}>{g.img ? <img src={`${MAP_ROOT}i/${g.img}`} alt="" style={{ width: 20, height: 20 }} /> : null}{g.label}</span>
              {g.all ? <Button size="s" variant="ghost" onClick={() => addAll(g.list.map((x) => [x.kind, x.slug]))}>Show all</Button> : null}
              <small onClick={() => setUi({ open: { ...ui.open, [`f:${g.id}`]: !ui.open[`f:${g.id}`] } })}>{onN ? `${onN} on · ` : ""}{fmtN(g.list.length)} {isOpenG ? "▾" : "▸"}</small></h3>
            {isOpenG ? (
              <>
                {g.extra}
                <div className="ka-map-cats">
                  {g.list.map((e) => (
                    <button key={`${e.kind}:${e.slug}`} type="button" className={"ka-map-cat" + (isOn(e.kind, e.slug) ? "" : " off")} onClick={() => toggle(e.kind, e.slug)}
                      title={`${e.n}${e.kind === "spawns" && dropsBy[e.slug] ? `\nDrops: ${dropsBy[e.slug].join(", ")}` : ""}`}>
                      {e.img ? <img src={`${MAP_ROOT}i/${e.img}`} alt="" loading="lazy" /> : <i className="ka-dot" style={{ background: e.color ?? KIND_COLOR[e.kind] }} />}
                      <span>{e.n}</span><small>{fmtN(e.c)}</small>
                    </button>
                  ))}
                </div>
                {g.more ? <p className="ka-map-muted">{fmtN(g.more)} items in total; type to narrow the list.</p> : null}
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
  return (
    <>
      <p className={"ka-map-status" + (connected ? " on" : "")}>{connected ? `Kiln.Admin connected · ${players.length} in the world` : reason ?? "Live tools not connected."}</p>
      <section className="ka-map-group">
        <h3><span>Players</span><small>{players.length}</small></h3>
        {players.length ? players.map((p) => (
          <div key={p.entity} className="ka-map-lrow">
            <i className="ka-dot" style={{ background: "#7dff9b" }} /><b>{playerName(p)}</b>
            <small className="k-mono">{Math.round(p.pos!.x)}, {Math.round(p.pos!.z)} · H {Math.round(p.pos!.y)}</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(p.pos!.x, p.pos!.z)}>Show</Button>
              <Button size="s" variant={ui.follow === p.entity ? "secondary" : "ghost"} onClick={() => follow(p.entity)}>{ui.follow === p.entity ? "Following" : "Follow"}</Button></span>
          </div>
        )) : <p className="ka-map-muted">Nobody is in the world.</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>Protection zones</span><small>{zones.length}</small></h3>
        {zones.length ? zones.map((z) => (
          <div key={z.name} className="ka-map-lrow"><i className="ka-dot" style={{ background: "#ff8540" }} /><b>{z.name}</b><small>{Math.round(z.radius)} m</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(z.pos.x, z.pos.z)}>Show</Button></span></div>
        )) : <p className="ka-map-muted">Click the map and pick “Protect an area here…”.</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>Waypoints</span><small>{waypoints.length}</small></h3>
        {waypoints.length ? waypoints.map((w) => (
          <div key={w.name} className="ka-map-lrow"><img src={`${MAP_ROOT}i/g-mapmarker_waypoint.webp`} alt="" /><b>{w.name}</b><small className="k-mono">{Math.round(w.pos.x)}, {Math.round(w.pos.z)}</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(w.pos.x, w.pos.z)}>Show</Button></span></div>
        )) : <p className="ka-map-muted">Click the map and pick “Save waypoint here”.</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>Graves</span><small>{graves.length}</small></h3>
        {graves.length ? [...graves].reverse().slice(0, 12).map((g) => (
          <div key={g.key} className="ka-map-lrow"><i className="ka-dot" style={{ background: "#b9a7d6" }} /><b>{g.name ?? "A player"}</b><small>{agoS(g.at)}</small>
            <span className="ka-map-btns"><Button size="s" variant="ghost" onClick={() => go(g.pos.x, g.pos.z)}>Show</Button></span></div>
        )) : <p className="ka-map-muted">No deaths recorded.</p>}
      </section>
      <section className="ka-map-group">
        <h3><span>Living enemies</span><small>{ui.enemies ? enemies : "off"}</small></h3>
        <label className="ka-map-inline"><input type="checkbox" disabled={!hasEnemies} checked={ui.enemies} onChange={(e) => setUi({ enemies: e.target.checked })} /> Show enemies within 80 m of each player (asks the server every 5 s)</label>
        {ui.enemies && enemyErr ? <p className="ka-map-muted" style={{ color: "var(--k-warn)" }}>{enemyErr}</p> : null}
      </section>
      <section className="ka-map-group">
        <h3><span>Route</span></h3>
        {ui.route ? <><p>{routeFrom ?? "start"} → {ui.route.to.name ?? "point"}</p><Button size="s" onClick={() => setUi({ route: null })}>Clear route</Button></>
          : <p className="ka-map-muted">Click a marker or the map and pick “Route …” to measure the distance from a player or a point.</p>}
      </section>
    </>
  );
}
