// The admin map's Leaflet side, built on the Enshrouded Interactive Map by Nicolas Ferreira (MIT;
// licenses/Enshrouded-Interactive-Map-LICENSE.txt): the base tiles and the WebGL relief, marker
// categories and place names, the item/creature finder, and the live overlays (players gliding between pushes,
// waypoints, graves, protection zones, enemies near players, a route line). The React page (AdminMapPage) owns the
// side panel and the menus; the engine calls back with what was clicked.
import L from "leaflet";
import "leaflet/dist/leaflet.css";
import type { AdminPlayer, AdminPos } from "../../api/admin";
import { GameMapLayer, Height, WORLD } from "./gamemap";

const NATIVE = 5;
export { WORLD, Height };

export interface MarkerCat {
  id: string;
  group: string;
  label: string;
  icon: string;
  on: boolean;
  count: number;
}
/** [catIndex, x, z, y, name, description, level, itemIcon] */
export type MarkerPoint = [number, number, number, number, string, string, number, string];
export interface MarkerData {
  world?: unknown;
  cats: MarkerCat[];
  points: MarkerPoint[];
  /** [name, x, z, tier] */
  labels: [string, number, number, number][];
  /** "game": extracted from the installed client (`game/markers.json`, kiln_manage::gameart::map::markers);
   * absent: a local copy (`data/markers.json`, the fallback). */
  source?: "game";
  /** The game data build the markers came from ("1287369"). */
  build?: string;
  /** The language of the texts in this file, and every language `game/lang/<code>.json` has. */
  lang?: string;
  langs?: { code: string; name: string }[];
}
/** `game/lang/<code>.json`: the texts of one language, index-aligned with markers.json. */
interface MarkerTexts {
  cats: string[];
  points: string[];
  labels: string[];
}
/** The marker language to show: the saved choice, else the system language's best match, else the file's own. */
export function pickLang(D: MarkerData, saved: string | null | undefined): string | undefined {
  const codes = (D.langs ?? []).map((l) => l.code);
  if (saved && codes.includes(saved)) return saved;
  const sys = typeof navigator !== "undefined" ? [...(navigator.languages ?? []), navigator.language].filter(Boolean) : [];
  for (const want of sys) {
    const exact = codes.find((c) => c.toLowerCase() === want.toLowerCase());
    if (exact) return exact;
    const base = want.split("-")[0].toLowerCase();
    const same = codes.find((c) => c.split("-")[0].toLowerCase() === base);
    if (same) return same;
  }
  return D.lang;
}
/** Identifies a marker set (the found marks are point indices, so they belong to one set). */
export const markerSetKey = (D: MarkerData) => (D.source === "game" ? `game:${D.build ?? ""}` : "copy");
export interface FindEntry {
  n: string;
  c: number;
  img?: string;
  o?: [number, number];
  boss?: boolean;
  g?: string;
  color?: string;
  order?: number;
  more?: boolean;
}
export interface FindData {
  spawns: Record<string, FindEntry>;
  gather: Record<string, FindEntry>;
  ore: Record<string, FindEntry>;
  chests: Record<string, FindEntry>;
  drops: Record<string, { by: string[]; img?: string }>;
}
export type FinderKind = "spawns" | "gather" | "ore" | "chests";
export interface Finder {
  kind: FinderKind;
  slug: string;
}
export interface Look {
  spacing: number;
  fog: boolean;
  flame: number;
  animate: boolean;
}
export interface Route {
  from: { kind: "player"; entity: number; name?: string } | ({ kind: "point"; name?: string } & AdminPos);
  to: { x: number; z: number; y?: number | null; name?: string };
}
export interface ZoneShape {
  name: string;
  pos: AdminPos;
  radius: number;
  active?: boolean;
}
export interface Spot {
  x: number;
  z: number;
  y?: number | null;
}

export type MenuTarget =
  | { kind: "spot"; pos: Spot }
  | { kind: "marker"; pos: Spot; name: string; sub: string; idx?: number }
  | { kind: "player"; entity: number }
  | { kind: "waypoint"; name: string; pos: AdminPos }
  | { kind: "grave"; key: string; name: string | null; pos: AdminPos; at: number }
  | { kind: "zone"; name: string };

export interface EngineCallbacks {
  menu: (t: MenuTarget, point: { x: number; y: number }) => void;
  closeMenu: () => void;
  coords: (p: Spot | null) => void;
  loading: (text: string) => void;
  view: (v: { x: number; z: number; zoom: number }) => void;
  stopFollow: () => void;
  error: (message: string) => void;
}

export const KIND_COLOR: Record<FinderKind, string> = { spawns: "#e0533a", gather: "#f2c14e", ore: "#5fd3c4", chests: "#f2c14e" };
const CHEST_IMG: Record<number, string> = { 1: "p-chest-gold.webp", 2: "p-chest-silver.webp", 3: "p-chest-wood.webp" };
export const CHEST_NAME: Record<number, string> = { 1: "Gold chest", 2: "Silver chest", 3: "Wooden chest" };
const esc = (v: unknown) => String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!);
export const posTxt = (p: { x: number; z: number; y?: number | null }) => `X ${Math.round(p.x)} · Z ${Math.round(p.z)}${p.y != null ? ` · H ${Math.round(p.y)}` : ""}`;
const tip = (title: string, sub = "", extra = "") => `<b>${esc(title)}</b>${sub ? `<span>${sub}</span>` : ""}${extra}`;
const iconPx = (z: number) => Math.max(12, Math.min(40, 6 + z * 5));

// ------------------------------------------------------------------ canvas icon markers
type Img = HTMLImageElement & { waiting: Set<L.Layer & { redraw: () => void; _map?: L.Map }> };
interface ImgMarkerOptions extends L.CircleMarkerOptions {
  img?: Img | null;
  scale?: number;
  ring?: string | null;
  item?: boolean;
  /** a plain coloured dot instead of an icon */
  dot?: string | null;
  opacity?: number;
}
// Leaflet's private renderer fields
type Internal = { _radius: number; _point: L.Point; _renderer: { _ctx: CanvasRenderingContext2D | null }; _map: L.Map; options: ImgMarkerOptions; redraw: () => void };
const ImgMarker = L.CircleMarker.extend({
  options: { scale: 1, stroke: false, fill: false, ring: null, item: false, opacity: 1, dot: null },
  _project(this: Internal) {
    this._radius = (iconPx(this._map.getZoom()) * (this.options.scale ?? 1)) / 2;
    (L.CircleMarker.prototype as unknown as { _project: () => void })._project.call(this);
  },
  _updatePath(this: Internal) {
    const o = this.options, p = this._point, ctx = this._renderer._ctx, s = this._radius * 2;
    if (!ctx) return;
    ctx.save();
    ctx.globalAlpha = o.opacity ?? 1;
    if (o.dot && !o.img) {
      ctx.beginPath();
      ctx.arc(p.x, p.y, Math.max(4, s * 0.22), 0, Math.PI * 2);
      ctx.fillStyle = o.dot;
      ctx.fill();
      ctx.lineWidth = 1.2;
      ctx.strokeStyle = "#0b0b0b";
      ctx.stroke();
      ctx.restore();
      return;
    }
    const im = o.img;
    if (!im) { ctx.restore(); return; }
    if (!im.complete || !im.naturalWidth) {
      im.waiting.add(this as never);
      ctx.restore();
      return;
    }
    if (o.item || o.ring) {
      ctx.beginPath();
      ctx.arc(p.x, p.y, s * 0.55, 0, Math.PI * 2);
      ctx.fillStyle = "#0e0d0ccc";
      ctx.fill();
      ctx.lineWidth = o.ring ? 2.2 : 1.5;
      ctx.strokeStyle = o.ring || "#c9a24a";
      if (o.ring) { ctx.shadowColor = o.ring; ctx.shadowBlur = 6; }
      ctx.stroke();
      ctx.shadowBlur = 0;
    }
    ctx.shadowColor = "#000c";
    ctx.shadowBlur = 4;
    ctx.shadowOffsetY = 1;
    const k = o.item || o.ring ? 0.82 : 1;
    ctx.drawImage(im, p.x - (s * k) / 2, p.y - (s * k) / 2, s * k, s * k);
    ctx.restore();
  },
}) as unknown as new (ll: L.LatLngExpression, o: ImgMarkerOptions) => L.CircleMarker & { options: ImgMarkerOptions; redraw: () => void };

interface PlayerMk {
  mk: L.Marker;
  from: AdminPos;
  to: AdminPos;
  cur: AdminPos;
  t0: number;
  key: string;
  p: AdminPlayer;
}

export class MapEngine {
  map: L.Map;
  root: string;
  cb: EngineCallbacks;
  game: GameMapLayer | null = null;
  D: MarkerData | null = null;
  F: FindData | null = null;
  dropsBy: Record<string, string[]> = {};
  private PTS: Uint16Array | null = null;
  private CH: { items: Record<string, number[]>; chests: [number, number, number, number, number][] } | null = null;
  private ORE: Record<string, [number, number, number, number, number][]> | null = null;
  private imgs = new Map<string, Img>();
  private icons: L.Canvas;
  private decor: L.SVG;
  private catLayers: Record<string, L.LayerGroup> = {};
  private pointMk: (L.CircleMarker & { options: ImgMarkerOptions })[] = [];
  private labelLayer: L.LayerGroup | null = null;
  private finderLayer: L.LayerGroup | null = null;
  private live: L.LayerGroup;
  private enemyLayer: L.LayerGroup;
  private zoneLayer: L.LayerGroup;
  private worldLayer: L.LayerGroup;
  private players = new Map<number, PlayerMk>();
  private raf = 0;
  private routeLine: L.Polyline | null = null;
  private routeEnd: L.CircleMarker | null = null;
  private flash: L.CircleMarker | null = null;
  private finderGen = 0;
  follow: number | null = null;
  route: Route | null = null;
  active = true;
  chestLv: [number, number] = [5, 50];
  surfaceOnly = false;

  constructor(el: HTMLElement, root: string, cb: EngineCallbacks, opts: { look: Look; view: { x: number; z: number; zoom: number } | null; relief: boolean }) {
    this.root = root;
    this.cb = cb;
    const map = (this.map = L.map(el, {
      crs: L.CRS.Simple, minZoom: 1, maxZoom: 9, zoomSnap: 0.25, zoomDelta: 0.5, wheelPxPerZoomLevel: 90,
      zoomControl: false, attributionControl: false, preferCanvas: true, maxBoundsViscosity: 0.8,
      zoomAnimation: false, fadeAnimation: false, doubleClickZoom: true,
    }));
    const bounds = this.bounds();
    map.setMaxBounds(bounds.pad(0.15));
    L.tileLayer(`${root}tiles/{z}/{x}_{y}.webp`, { tileSize: 256, minNativeZoom: 0, maxNativeZoom: 3, noWrap: true, bounds, keepBuffer: 4 }).addTo(map);
    // the world's outline, so the plane reads as a map even without tiles
    L.rectangle(bounds, { color: "#4d413a", weight: 1, fill: false, interactive: false }).addTo(map);
    const probe = opts.relief && document.createElement("canvas").getContext("webgl2");
    if (probe) {
      cb.loading("Loading the game map relief…");
      this.game = new GameMapLayer({
        root, isolineSpacing: opts.look.spacing, playerFogLevel: opts.look.flame, showFog: opts.look.fog, animate: opts.look.animate,
        onProgress: (a, b) => cb.loading(`Loading the game map relief… ${Math.round((a / b) * 100)}%`),
        onReady: () => { cb.loading(""); el.dataset.relief = "ready"; },
        onError: (m) => { cb.loading(""); el.dataset.relief = "error"; cb.error(`Map relief unavailable (${m}); showing the low-detail map.`); },
      });
      this.game.addTo(map);
    }
    map.createPane("decor");
    map.getPane("decor")!.style.zIndex = "450";
    map.getPane("decor")!.style.pointerEvents = "none";
    map.createPane("live");
    map.getPane("live")!.style.zIndex = "650";
    this.decor = L.svg({ pane: "decor" });
    this.icons = L.canvas({ padding: 0.3, tolerance: 2 });
    this.zoneLayer = L.layerGroup().addTo(map);
    this.worldLayer = L.layerGroup().addTo(map);
    this.live = L.layerGroup().addTo(map);
    this.enemyLayer = L.layerGroup().addTo(map);
    if (opts.view) map.setView(this.toLL(opts.view.x, opts.view.z), opts.view.zoom);
    else map.fitBounds(bounds);
    map.on("moveend", () => { const c = this.fromLL(map.getCenter()); cb.view({ x: Math.round(c.x), z: Math.round(c.z), zoom: map.getZoom() }); });
    map.on("dragstart", () => { if (this.follow != null) cb.stopFollow(); cb.closeMenu(); });
    map.on("zoomstart movestart", () => cb.closeMenu());
    map.on("mousemove", (e: L.LeafletMouseEvent) => this.showCoords(this.fromLL(e.latlng)));
    map.on("mouseout", () => cb.coords(null));
    map.on("contextmenu", (e: L.LeafletMouseEvent) => this.spot(e));
    map.on("click", (e: L.LeafletMouseEvent) => this.spot(e));
    const onZoom = () => {
      const z = map.getZoom();
      el.dataset.ltier = String(z >= 5.08 ? 3 : z >= 4.28 ? 2 : z >= 3.28 ? 1 : 0);
      this.layoutLabels();
    };
    map.on("zoomend", onZoom);
    onZoom();
  }

  destroy() {
    this.active = false;
    cancelAnimationFrame(this.raf);
    this.game?.pause();
    this.map.remove();
  }

  bounds() {
    return L.latLngBounds(this.map.unproject([0, WORLD], NATIVE), this.map.unproject([WORLD, 0], NATIVE));
  }
  toLL(x: number, z: number) {
    return this.map.unproject([x, WORLD - z], NATIVE);
  }
  fromLL(ll: L.LatLng) {
    const p = this.map.project(ll, NATIVE);
    return { x: p.x, z: WORLD - p.y };
  }
  private stop(e: L.LeafletEvent) {
    L.DomEvent.stop(e as unknown as Event);
    const oe = (e as L.LeafletMouseEvent).originalEvent;
    if (oe) L.DomEvent.stop(oe);
  }
  private point(e: L.LeafletMouseEvent) {
    const cp = e.containerPoint ?? this.map.latLngToContainerPoint(e.latlng);
    return { x: cp.x, y: cp.y };
  }

  private coordReq = 0;
  private showCoords(p: { x: number; z: number }) {
    if (p.x < 0 || p.z < 0 || p.x > WORLD || p.z > WORLD) return this.cb.coords(null);
    this.cb.coords({ ...p, y: Height.quick(p.x, p.z) });
    const id = ++this.coordReq;
    if (Height.base) setTimeout(() => { if (id === this.coordReq) Height.exact(p.x, p.z); }, 250);
  }

  private spot(e: L.LeafletMouseEvent) {
    const p = this.fromLL(e.latlng);
    if (p.x < 0 || p.z < 0 || p.x > WORLD || p.z > WORLD) return this.cb.closeMenu();
    this.cb.menu({ kind: "spot", pos: { x: Math.round(p.x * 10) / 10, z: Math.round(p.z * 10) / 10, y: Height.quick(p.x, p.z) } }, this.point(e));
  }

  setLook(look: Look) {
    this.game?.setLook({ isolineSpacing: look.spacing, playerFogLevel: look.flame, showFog: look.fog, animate: look.animate });
  }
  pause() {
    this.active = false;
    this.game?.pause();
  }
  resume() {
    this.active = true;
    this.game?.resume();
    setTimeout(() => { this.map.invalidateSize(); this.game?.redraw(); }, 0);
    this.startAnim();
  }

  // ------------------------------------------------------------------ marker categories
  img(name: string): Img {
    let im = this.imgs.get(name);
    if (!im) {
      im = new Image() as Img;
      im.waiting = new Set();
      const me = im;
      im.onload = () => { for (const l of me.waiting) if (l._map) l.redraw(); me.waiting.clear(); };
      im.src = `${this.root}i/${name}`;
      this.imgs.set(name, im);
    }
    return im;
  }

  private async getJson<T>(path: string): Promise<T | null> {
    const r = await fetch(this.root + path).catch(() => null);
    if (!r || !r.ok || !(r.headers.get("content-type") ?? "").includes("json")) return null;
    return (await r.json()) as T;
  }

  /** The markers: the game's own (`game/markers.json`, extracted by the launcher) when there, else a local copy
   * (`data/markers.json`). Texts in `lang` when the game set has it (see setLang). */
  async loadMarkers(lang?: string | null): Promise<MarkerData> {
    const D = (await this.getJson<MarkerData>("game/markers.json")) ?? (await this.getJson<MarkerData>("data/markers.json"));
    if (!D || !Array.isArray(D.points)) throw new Error("no marker data (game/markers.json, data/markers.json)");
    this.D = D;
    this.lang = D.lang;
    this.english = { cats: D.cats.map((c) => c.label), points: D.points.map((p) => p[4]), labels: D.labels.map((l) => l[0]) };
    for (const c of D.cats) this.catLayers[c.id] = L.layerGroup();
    D.points.forEach((p, i) => {
      const [ci, x, z, y, , , , img] = p;
      const c = D.cats[ci];
      const mk = new ImgMarker(this.toLL(x, z), { renderer: this.icons, img: this.img(img || c.icon), item: !!img });
      // texts read at show time: setLang replaces them in place
      mk.bindTooltip(() => {
        const [, , , , n, d, lv] = D.points[i];
        return tip(n, `${esc(D.cats[ci].label)}${lv ? ` · level ${lv}` : ""}${d ? ` · ${esc(d)}` : ""}`, `<em>${posTxt({ x, z, y })}</em>`);
      }, { direction: "top", offset: [0, -8], className: "ka-tip" });
      const open = (e: L.LeafletMouseEvent) => { this.stop(e); this.cb.menu({ kind: "marker", pos: { x, z, y }, name: D.points[i][4], sub: D.cats[ci].label, idx: i }, this.point(e)); };
      mk.on("click", open);
      mk.on("contextmenu", open);
      this.pointMk[i] = mk;
    });
    this.buildLabels();
    const want = pickLang(D, lang);
    if (want && want !== D.lang) await this.setLang(want);
    return D;
  }

  private english: MarkerTexts | null = null;
  /** The language the marker texts are in now. */
  lang: string | undefined;

  /** Switches the marker, category and place-name texts to another game language (game markers only). */
  async setLang(code: string): Promise<boolean> {
    const D = this.D, en = this.english;
    if (!D || !en || D.source !== "game") return false;
    const t = code === D.lang ? en : await this.getJson<MarkerTexts>(`game/lang/${encodeURIComponent(code)}.json`);
    if (!t || t.points.length !== D.points.length || t.cats.length !== D.cats.length || t.labels.length !== D.labels.length) return false;
    D.cats.forEach((c, i) => { c.label = t.cats[i] || en.cats[i]; });
    D.points.forEach((p, i) => { p[4] = t.points[i] || en.points[i]; });
    D.labels.forEach((l, i) => { l[0] = t.labels[i] || en.labels[i]; });
    this.lang = code;
    const shown = !!this.labelLayer && this.map.hasLayer(this.labelLayer);
    this.labelLayer?.remove();
    this.buildLabels();
    if (shown) this.labelLayer?.addTo(this.map);
    return true;
  }

  private buildLabels() {
    const D = this.D;
    if (!D) return;
    this.labelLayer = L.layerGroup();
    for (const [n, x, z, t] of D.labels)
      L.marker(this.toLL(x, z), { interactive: false, keyboard: false, icon: L.divIcon({ className: `ka-place t${t || 1}`, html: `<span>${esc(n)}</span>`, iconSize: undefined }) }).addTo(this.labelLayer);
  }

  applyMarkers(vis: Record<string, boolean>, names: boolean, found: number[], hideFound: boolean, chestLv: [number, number]) {
    const D = this.D;
    if (!D) return;
    this.chestLv = chestLv;
    const f = new Set(found);
    for (const g of Object.values(this.catLayers)) g.clearLayers();
    this.pointMk.forEach((mk, i) => {
      const p = D.points[i];
      const isF = f.has(i);
      mk.options.opacity = isF ? 0.35 : 1;
      if ((hideFound && isF) || this.lvOut(p[6])) return;
      this.catLayers[D.cats[p[0]].id].addLayer(mk);
    });
    for (const c of D.cats) {
      const g = this.catLayers[c.id];
      if (vis[c.id]) g.addTo(this.map);
      else g.remove();
    }
    if (names) this.labelLayer?.addTo(this.map);
    else this.labelLayer?.remove();
  }
  private lvOut(lv: number) {
    return !!lv && (lv < this.chestLv[0] || lv > this.chestLv[1]);
  }

  // ------------------------------------------------------------------ finder ("Find & filter")
  async loadFind(): Promise<FindData> {
    const r = await fetch(`${this.root}data/find.json`);
    if (!r.ok) throw new Error(`data/find.json: ${r.status}`);
    const F = (this.F = (await r.json()) as FindData);
    this.dropsBy = {};
    for (const [item, d] of Object.entries(F.drops)) for (const s of d.by) (this.dropsBy[s] ??= []).push(item);
    return F;
  }

  private async needFinderData(finders: Finder[]) {
    const get = async (p: string) => {
      const r = await fetch(this.root + p);
      if (!r.ok) throw new Error(`${p}: ${r.status}`);
      return r;
    };
    const kinds = new Set(finders.map((f) => f.kind));
    if ((kinds.has("spawns") || kinds.has("gather")) && !this.PTS) this.PTS = new Uint16Array(await (await get("data/pts.bin")).arrayBuffer());
    if (kinds.has("chests") && !this.CH) {
      const ch = (await (await get("data/chests.json")).json()) as NonNullable<MapEngine["CH"]>;
      for (const k of Object.keys(ch.items)) { const a = ch.items[k]; for (let i = 1; i < a.length; i++) a[i] += a[i - 1]; }
      this.CH = ch;
    }
    if (kinds.has("ore") && !this.ORE) this.ORE = (await (await get("data/ore.json")).json()) as NonNullable<MapEngine["ORE"]>;
  }

  async setFinders(finders: Finder[], surfaceOnly: boolean) {
    const gen = ++this.finderGen;
    this.surfaceOnly = surfaceOnly;
    const F = this.F;
    if (!F) return;
    try {
      await this.needFinderData(finders);
    } catch (e) {
      this.cb.error(`Finder data: ${(e as Error).message}`);
      return;
    }
    if (gen !== this.finderGen) return;
    const g = L.layerGroup();
    for (const f of finders) {
      const e = F[f.kind]?.[f.slug];
      if (!e) continue;
      const img = e.img ? this.img(e.img) : null;
      const color = e.color || KIND_COLOR[f.kind];
      const add = (x: number, z: number, opts: ImgMarkerOptions & { sub?: string }, t: string | (() => string)) => {
        const mk = new ImgMarker(this.toLL(x, z), { renderer: this.icons, img, dot: img ? null : color, ...opts });
        mk.bindTooltip(t, { direction: "top", offset: [0, -8], className: "ka-tip" });
        const open = (ev: L.LeafletMouseEvent) => { this.stop(ev); this.cb.menu({ kind: "marker", pos: { x, z, y: null }, name: e.n, sub: opts.sub ?? "" }, this.point(ev)); };
        mk.on("click", open);
        mk.on("contextmenu", open);
        g.addLayer(mk);
      };
      if ((f.kind === "spawns" || f.kind === "gather") && this.PTS && e.o) {
        const [o, n] = e.o;
        const drops = f.kind === "spawns" ? this.dropsBy[f.slug] ?? [] : [];
        const sub = f.kind === "spawns" ? (e.boss ? "Boss" : "Creature spawn") : e.g ?? "";
        const t = () => tip(e.n, esc(sub), drops.length ? `<em>Drops: ${esc(drops.slice(0, 8).join(", "))}${drops.length > 8 ? "…" : ""}</em>` : "");
        for (let i = o; i < o + n; i++) add(this.PTS[2 * i] / 6, this.PTS[2 * i + 1] / 6, { item: f.kind === "gather", scale: e.boss ? 1.2 : 0.9, sub }, t);
      } else if (f.kind === "chests" && this.CH) {
        for (const ci of this.CH.items[f.slug] ?? []) {
          const [x, z, y, type, lv] = this.CH.chests[ci];
          if (this.lvOut(lv)) continue;
          const mk = new ImgMarker(this.toLL(x, z), { renderer: this.icons, img: this.img(CHEST_IMG[type] ?? "p-chest-wood.webp"), scale: 0.9 });
          mk.bindTooltip(() => `${e.img ? `<img class="ka-tipimg" src="${this.root}i/${e.img}" alt="">` : ""}${tip(e.n, `${CHEST_NAME[type] ?? "Chest"}${lv ? ` · level ${lv}` : ""}`, `<em>${posTxt({ x, z, y })}</em>`)}`, { direction: "top", offset: [0, -8], className: "ka-tip" });
          const open = (ev: L.LeafletMouseEvent) => { this.stop(ev); this.cb.menu({ kind: "marker", pos: { x, z, y }, name: `${CHEST_NAME[type] ?? "Chest"} (${e.n})`, sub: "" }, this.point(ev)); };
          mk.on("click", open);
          mk.on("contextmenu", open);
          g.addLayer(mk);
        }
      } else if (f.kind === "ore" && this.ORE) {
        for (const [x, z, vol, surf, depth] of this.ORE[f.slug] ?? []) {
          if (surfaceOnly && !surf) continue;
          const scale = (16 + 10 * Math.min(1, Math.log10(Math.max(1, vol)) / 4)) / 24;
          add(x, z, { scale, opacity: surf ? 1 : 0.45, ring: f.slug.startsWith("archaic-essence") ? "#ff5ad8" : null, sub: "Ore" },
            () => tip(e.n, `Volume ${vol.toLocaleString("en-US")}${surf ? "" : " · buried"}${depth > 0 ? ` · ${depth} m below the surface` : ""}`));
        }
      }
    }
    this.finderLayer?.remove();
    this.finderLayer = g.addTo(this.map);
  }

  flyTo(x: number, z: number, zoom: number) {
    if (this.follow != null) this.cb.stopFollow();
    this.map.setView(this.toLL(x, z), Math.max(this.map.getZoom(), zoom), { animate: false });
    this.flash?.remove();
    this.flash = L.circleMarker(this.toLL(x, z), { radius: 26, color: "#f9c74f", weight: 3, fill: false, className: "ka-flash", interactive: false, pane: "decor", renderer: this.decor }).addTo(this.map);
    setTimeout(() => { this.flash?.remove(); this.flash = null; }, 4000);
  }
  fit() {
    this.map.fitBounds(this.bounds());
  }

  // ------------------------------------------------------------------ live overlays
  private playerIcon(p: AdminPlayer) {
    const hp = p.healthMax ? Math.max(0, Math.min(1, (p.health ?? 0) / p.healthMax)) : null;
    return L.divIcon({
      className: "ka-player", iconSize: [16, 16], iconAnchor: [8, 8],
      html: `<i></i><span>${esc(p.name || `Player ${p.entity}`)}${hp != null ? `<em style="--hp:${Math.round(hp * 100)}%"></em>` : ""}</span>`,
    });
  }

  setPlayers(list: AdminPlayer[]) {
    const players = list.filter((p) => p.pos && Number.isFinite(p.pos.x));
    const seen = new Set<number>();
    const now = performance.now();
    for (const p of players) {
      seen.add(p.entity);
      let r = this.players.get(p.entity);
      if (!r) {
        const mk = L.marker(this.toLL(p.pos!.x, p.pos!.z), { pane: "live", zIndexOffset: 9000, icon: this.playerIcon(p), keyboard: false })
          .bindTooltip(() => { const q = this.players.get(p.entity)?.p ?? p; return tip(q.name || `Player ${q.entity}`, posTxt(q.pos!), q.healthMax ? `<em>Health ${Math.round(q.health ?? 0)} / ${Math.round(q.healthMax)}</em>` : ""); }, { direction: "top", offset: [0, -10], className: "ka-tip" })
          .addTo(this.live);
        const open = (e: L.LeafletMouseEvent) => { this.stop(e); this.cb.menu({ kind: "player", entity: p.entity }, this.point(e)); };
        mk.on("click", open);
        mk.on("contextmenu", open);
        r = { mk, from: { ...p.pos! }, to: { ...p.pos! }, cur: { ...p.pos! }, t0: now, key: "", p };
        this.players.set(p.entity, r);
      }
      r.p = p;
      const key = `${p.name}|${p.healthMax ? Math.round(((p.health ?? 0) / p.healthMax) * 20) : ""}`;
      if (key !== r.key) { r.key = key; r.mk.setIcon(this.playerIcon(p)); }
      if (p.pos!.x !== r.to.x || p.pos!.z !== r.to.z) { r.from = { ...r.cur }; r.to = { ...p.pos! }; r.t0 = now; }
    }
    for (const [e, r] of this.players) if (!seen.has(e)) { r.mk.remove(); this.players.delete(e); }
    this.startAnim();
    this.drawRoute();
    this.layoutLabels();
  }

  playerPos(entity: number): AdminPos | null {
    return this.players.get(entity)?.cur ?? null;
  }

  /** glide the player dots between the ~1 s state pushes */
  private startAnim() {
    if (this.raf || !this.active) return;
    const step = () => {
      this.raf = 0;
      if (!this.active) return;
      const now = performance.now();
      let moving = false;
      for (const [e, r] of this.players) {
        const k = Math.min(1, (now - r.t0) / 950);
        r.cur = { x: r.from.x + (r.to.x - r.from.x) * k, y: r.to.y, z: r.from.z + (r.to.z - r.from.z) * k };
        r.mk.setLatLng(this.toLL(r.cur.x, r.cur.z));
        if (k < 1) moving = true;
        if (this.follow === e) this.map.panTo(this.toLL(r.cur.x, r.cur.z), { animate: false, noMoveStart: true });
      }
      if (this.route) this.drawRoute();
      this.layoutLabels();
      if (moving) this.raf = requestAnimationFrame(step);
    };
    this.raf = requestAnimationFrame(step);
  }

  /** players standing together: stack their name labels */
  private layoutLabels() {
    const pts: L.Point[] = [];
    for (const r of this.players.values()) {
      const el = r.mk.getElement();
      if (!el) continue;
      const pt = this.map.latLngToContainerPoint(r.mk.getLatLng());
      const row = pts.filter((q) => Math.abs(q.x - pt.x) < 90 && Math.abs(q.y - pt.y) < 16).length;
      pts.push(pt);
      el.style.setProperty("--row", String(row));
    }
  }

  setWorld(waypoints: { name: string; pos: AdminPos }[], graves: { key: string; name: string | null; pos: AdminPos; at: number }[]) {
    this.worldLayer.clearLayers();
    for (const w of waypoints) {
      const mk = L.marker(this.toLL(w.pos.x, w.pos.z), {
        pane: "live", zIndexOffset: 8000, keyboard: false,
        icon: L.divIcon({ className: "ka-wp", iconSize: [26, 26], iconAnchor: [13, 24], html: `<img src="${this.root}i/g-mapmarker_waypoint.webp" alt=""><span>${esc(w.name)}</span>` }),
      }).bindTooltip(tip(w.name, "Waypoint", `<em>${posTxt(w.pos)}</em>`), { direction: "top", offset: [0, -24], className: "ka-tip" }).addTo(this.worldLayer);
      const open = (e: L.LeafletMouseEvent) => { this.stop(e); this.cb.menu({ kind: "waypoint", name: w.name, pos: w.pos }, this.point(e)); };
      mk.on("click", open);
      mk.on("contextmenu", open);
    }
    for (const g of graves) {
      const mk = L.marker(this.toLL(g.pos.x, g.pos.z), {
        pane: "live", zIndexOffset: 7000, keyboard: false,
        icon: L.divIcon({ className: "ka-grave", iconSize: [22, 22], iconAnchor: [11, 11], html: '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 21V9a5 5 0 0 1 10 0v12z" /><path d="M12 9v6M9.5 11.5h5" /></svg>' }),
      }).bindTooltip(tip(`${g.name ?? "A player"}'s grave`, "", `<em>${posTxt(g.pos)}</em>`), { direction: "top", offset: [0, -10], className: "ka-tip" }).addTo(this.worldLayer);
      const open = (e: L.LeafletMouseEvent) => { this.stop(e); this.cb.menu({ kind: "grave", ...g }, this.point(e)); };
      mk.on("click", open);
      mk.on("contextmenu", open);
    }
  }

  /** Kiln protection zones: dashed circles with their name, under the live markers; click one to edit. */
  setZones(zones: ZoneShape[], draft?: { pos: Spot; radius: number } | null) {
    this.zoneLayer.clearLayers();
    for (const z of zones) {
      if (!z.pos) continue;
      const c = L.circle(this.toLL(z.pos.x, z.pos.z), { radius: z.radius / 2 ** NATIVE, color: z.active === false ? "#94857a" : "#ff8540", weight: 2, dashArray: "6 4", fillColor: "#ff6a1a", fillOpacity: 0.08, pane: "decor", renderer: this.decor, interactive: false });
      c.addTo(this.zoneLayer);
      const label = L.marker(this.toLL(z.pos.x, z.pos.z), {
        pane: "live", keyboard: false,
        icon: L.divIcon({ className: "ka-zone", iconSize: [0, 0], html: `<span>⛉ ${esc(z.name)}</span>` }),
      }).addTo(this.zoneLayer);
      const open = (e: L.LeafletMouseEvent) => { this.stop(e); this.cb.menu({ kind: "zone", name: z.name }, this.point(e)); };
      label.on("click", open);
      label.on("contextmenu", open);
    }
    if (draft) L.circle(this.toLL(draft.pos.x, draft.pos.z), { radius: draft.radius / 2 ** NATIVE, color: "#f9c74f", weight: 2, dashArray: "2 4", fillColor: "#f9c74f", fillOpacity: 0.1, pane: "decor", renderer: this.decor, interactive: false }).addTo(this.zoneLayer);
  }

  setEnemies(list: { entity: number; pos: AdminPos; name?: string; label?: string }[]) {
    this.enemyLayer.clearLayers();
    for (const e of list)
      L.circleMarker(this.toLL(e.pos.x, e.pos.z), { renderer: this.icons, radius: 5, color: "#1a0805", weight: 1.5, fillColor: "#e0533a", fillOpacity: 0.95 })
        .bindTooltip(tip(e.label ?? e.name ?? "Enemy", `Entity ${e.entity}`, `<em>${posTxt(e.pos)}</em>`), { direction: "top", className: "ka-tip" })
        .addTo(this.enemyLayer);
  }

  // ------------------------------------------------------------------ route / distance
  routeFrom(): (AdminPos & { name?: string }) | null {
    const r = this.route;
    if (!r) return null;
    if (r.from.kind === "player") {
      const q = this.players.get(r.from.entity);
      return q ? { ...q.cur, name: q.p.name } : null;
    }
    return r.from;
  }
  setRoute(route: Route | null) {
    this.route = route;
    if (!route) { this.routeLine?.remove(); this.routeEnd?.remove(); this.routeLine = this.routeEnd = null; }
    this.drawRoute();
  }
  private drawRoute() {
    const r = this.route;
    if (!r) return;
    const a = this.routeFrom();
    if (!a) { this.routeLine?.remove(); this.routeLine = null; return; }
    const lls = [this.toLL(a.x, a.z), this.toLL(r.to.x, r.to.z)];
    if (!this.routeLine) this.routeLine = L.polyline(lls, { color: "#f9c74f", weight: 3, dashArray: "8 8", opacity: 0.95, interactive: false, pane: "decor", renderer: this.decor }).addTo(this.map);
    else this.routeLine.setLatLngs(lls);
    if (!this.routeEnd) this.routeEnd = L.circleMarker(lls[1], { radius: 9, color: "#f9c74f", weight: 2.5, fill: false, interactive: false, pane: "decor", renderer: this.decor }).addTo(this.map);
    else this.routeEnd.setLatLng(lls[1]);
  }
}
