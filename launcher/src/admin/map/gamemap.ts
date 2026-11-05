// The admin map's relief layer and height read-out. The Leaflet layer and `Height` build on gamemap.js from the
// Enshrouded Interactive Map (https://github.com/nicolasferreiraxn/Enshrouded-Interactive-Map, MIT, Nicolas Ferreira);
// the picture itself is Kiln's own, see reliefShader.ts.
// Inputs (Keen Games data under assetsRoot() + "map/", made from the installed game; never committed):
//   gpu/base.bin   1280x1280 uint16 base height (gzip)        height = base * 4096 + (detail - 0.5) * 255  [metres]
//   gpu/d/x_y.webp 10x10 tiles of 1024x1024 detail height (R8)  tile x = west->east, y = south->north
//   gpu/f/x_y.webp 10x10 tiles of 512x512 Shroud zones: R = depth inside the zone (saturates about 34 m in), G = zone level
// World: 10240 m square; map pixel (x, y) at Leaflet zoom 5 = world (x, 10240 - z).
import L from "leaflet";
import { RELIEF_FRAGMENT, RELIEF_VERTEX, ZONE_PYRAMID_FRAGMENT } from "./reliefShader";

const N = 10;
export const WORLD = 10240;
/** Lattice levels the shader reads: detail 1, 2, 4, 8, 16 m; zones 2, 4, 8, 16 m. */
const FINE_LEVELS = 5;
const ZONE_SIZE = 512;
const ZONE_LEVELS = 4;

function compile(gl: WebGL2RenderingContext, type: number, src: string) {
  const s = gl.createShader(type)!;
  gl.shaderSource(s, src);
  gl.compileShader(s);
  const log = gl.getShaderInfoLog(s)?.trim();
  if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(log || "shader");
  if (log) console.warn(`relief shader: ${log}`);
  return s;
}

function link(gl: WebGL2RenderingContext, fragment: string) {
  const prog = gl.createProgram()!;
  gl.attachShader(prog, compile(gl, gl.VERTEX_SHADER, RELIEF_VERTEX));
  gl.attachShader(prog, compile(gl, gl.FRAGMENT_SHADER, fragment));
  gl.linkProgram(prog);
  const log = gl.getProgramInfoLog(prog)?.trim();
  if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) throw new Error(log || "link");
  if (log) console.warn(`relief shader: ${log}`);
  return prog;
}

/** Means over the coarse height's cells, for the zoomed-out lattice levels: level 0 = 8 m, level 1 = 16 m. The mean of
 *  a bilinear cell is the mean of its corners, so this is the exact counterpart of the detail texture's mips. */
function coarseCells(gl: WebGL2RenderingContext, nodes: Float32Array, size: number) {
  const tex = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, tex);
  gl.texStorage2D(gl.TEXTURE_2D, 2, gl.R32F, size, size);
  const last = size - 1;
  const eight = new Float32Array(size * size);
  for (let y = 0; y < size; y++) {
    const below = Math.min(y + 1, last) * size;
    for (let x = 0; x < size; x++) {
      const right = Math.min(x + 1, last);
      eight[y * size + x] = (nodes[y * size + x] + nodes[y * size + right] + nodes[below + x] + nodes[below + right]) / 4;
    }
  }
  const half = size >> 1, sixteen = new Float32Array(half * half);
  for (let y = 0; y < half; y++) {
    for (let x = 0; x < half; x++) {
      const a = 2 * y * size + 2 * x;
      sixteen[y * half + x] = (eight[a] + eight[a + 1] + eight[a + size] + eight[a + size + 1]) / 4;
    }
  }
  gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, size, size, gl.RED, gl.FLOAT, eight);
  gl.texSubImage2D(gl.TEXTURE_2D, 1, 0, 0, half, half, gl.RED, gl.FLOAT, sixteen);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST_MIPMAP_NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
}

/** Fills the lower levels of the zone array bound to `unit` (mean depth, highest level): one draw per layer and level. */
function buildZonePyramid(gl: WebGL2RenderingContext, tex: WebGLTexture | null, unit: number) {
  const prog = link(gl, ZONE_PYRAMID_FRAGMENT);
  const fb = gl.createFramebuffer();
  gl.useProgram(prog);
  gl.uniform1i(gl.getUniformLocation(prog, "u_source"), unit);
  const layerAt = gl.getUniformLocation(prog, "u_layer");
  gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
  for (let level = 1; level < ZONE_LEVELS; level++) {
    gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_BASE_LEVEL, level - 1);
    gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MAX_LEVEL, level - 1);
    gl.viewport(0, 0, ZONE_SIZE >> level, ZONE_SIZE >> level);
    for (let layer = 0; layer < N * N; layer++) {
      gl.framebufferTextureLayer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, tex, level, layer);
      gl.uniform1i(layerAt, layer);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    }
  }
  gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_BASE_LEVEL, 0);
  gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MAX_LEVEL, ZONE_LEVELS - 1);
  gl.bindFramebuffer(gl.FRAMEBUFFER, null);
  gl.deleteFramebuffer(fb);
  gl.deleteProgram(prog);
}

async function bitmap(url: string) {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`${url}: ${r.status}`);
  return createImageBitmap(await r.blob(), { premultiplyAlpha: "none", colorSpaceConversion: "none" });
}

type Tile = Promise<Uint8Array> & { data?: Uint8Array };

/** CPU copy of the height data: the hover read-out and teleport / waypoint / zone heights. */
export const Height = {
  base: null as Float32Array | null,
  tiles: new Map<string, Tile>(),
  root: "",
  tileKey(x: number, z: number) {
    const u = x / WORLD, v = (WORLD - z) / WORLD;
    const tx = Math.min(N - 1, Math.max(0, Math.floor(u * N))), ty = Math.min(N - 1, Math.max(0, Math.floor(v * N)));
    return { key: `${tx}_${N - 1 - ty}`, lx: u * N - tx, ly: v * N - ty };
  },
  baseAt(u: number, v: number) {
    const b = this.base;
    if (!b) return null;
    const sx = u * 1280 - 0.5, sy = v * 1280 - 0.5, ix = Math.floor(sx), iy = Math.floor(sy), fx = sx - ix, fy = sy - iy;
    const g = (x: number, y: number) => b[Math.min(1279, Math.max(0, y)) * 1280 + Math.min(1279, Math.max(0, x))];
    const a = g(ix, iy), c = g(ix + 1, iy), d = g(ix, iy + 1), e = g(ix + 1, iy + 1);
    return ((a * (1 - fx) + c * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy) * 4096;
  },
  /** terrain height in metres; detail only when that tile is already decoded (sync) */
  quick(x: number, z: number): number | null {
    if (!this.base || x < 0 || z < 0 || x > WORLD || z > WORLD) return null;
    const u = x / WORLD, v = (WORLD - z) / WORLD;
    let h = this.baseAt(u + 0.00390625 / N, v + 0.00390625 / N)!;
    const t = this.tileKey(x, z), d = this.tiles.get(t.key);
    if (d?.data) h += (d.data[Math.min(1023, Math.floor(t.ly * 1024)) * 1024 + Math.min(1023, Math.floor(t.lx * 1024))] / 255 - 0.5) * 255;
    return h;
  },
  async exact(x: number, z: number): Promise<number | null> {
    if (!this.base) return null;
    const t = this.tileKey(x, z);
    let p = this.tiles.get(t.key);
    if (!p) {
      // eslint-disable-next-line prefer-const
      let tile!: Tile;
      tile = (async () => {
        const img = await bitmap(`${this.root}gpu/d/${t.key}.webp`);
        const cv = new OffscreenCanvas(1024, 1024), cx = cv.getContext("2d", { willReadFrequently: true })!;
        cx.drawImage(img, 0, 0);
        const px = cx.getImageData(0, 0, 1024, 1024).data, out = new Uint8Array(1024 * 1024);
        for (let i = 0, j = 0; i < px.length; i += 4) out[j++] = px[i];
        tile.data = out;
        if (this.tiles.size > 6) this.tiles.delete(this.tiles.keys().next().value!);
        return out;
      })() as Tile;
      p = tile;
      this.tiles.set(t.key, p);
    }
    try {
      await p;
    } catch {
      this.tiles.delete(t.key);
    }
    return this.quick(x, z);
  },
};

export interface GameMapOptions extends L.LayerOptions {
  root: string;
  isolineSpacing: number;
  playerFogLevel: number;
  showFog: boolean;
  animate: boolean;
  onProgress?: (done: number, total: number) => void;
  onReady?: () => void;
  onError?: (message: string) => void;
}

/** The relief as a Leaflet layer: one full-viewport canvas in the tile pane, redrawn on move/zoom. */
export class GameMapLayer extends L.Layer {
  declare options: GameMapOptions;
  private canvas!: HTMLCanvasElement;
  private gl!: WebGL2RenderingContext;
  private prog!: WebGLProgram;
  private u!: (n: string) => WebGLUniformLocation | null;
  private ready = false;
  private timer: ReturnType<typeof setInterval> | null = null;
  private frame = 0;
  private repeats = 0;
  private fence: WebGLSync | null = null;
  private t = 0;
  private lmap: L.Map | null = null;

  constructor(options: GameMapOptions) {
    super(options);
    L.setOptions(this, options);
  }

  onAdd(map: L.Map) {
    this.lmap = map;
    const c = (this.canvas = L.DomUtil.create("canvas", "ka-gamemap"));
    c.style.visibility = "hidden";
    map.getPane("tilePane")!.appendChild(c);
    const gl = c.getContext("webgl2", { antialias: false, premultipliedAlpha: false, preserveDrawingBuffer: false });
    if (!gl) {
      this.options.onError?.("WebGL2 is not available");
      return this;
    }
    this.gl = gl;
    c.addEventListener("webglcontextlost", (e) => {
      e.preventDefault();
      this.ready = false;
      this.options.onError?.("The graphics context was lost; reopen the page.");
    });
    let prog: WebGLProgram;
    try {
      prog = link(gl, RELIEF_FRAGMENT);
    } catch (e) {
      console.error(e);
      this.options.onError?.(String((e as Error).message ?? e));
      return this;
    }
    this.prog = prog;
    gl.useProgram(prog);
    const uc = new Map<string, WebGLUniformLocation | null>();
    this.u = (n) => {
      if (!uc.has(n)) uc.set(n, gl.getUniformLocation(prog, n));
      return uc.get(n)!;
    };
    Height.root = this.options.root;
    this.load()
      .then(() => {
        this.ready = true;
        c.style.visibility = "";
        this.redraw();
        this.options.onReady?.();
      })
      .catch((e) => {
        console.error(e);
        this.options.onError?.(String(e?.message ?? e));
      });
    map.on("move zoom viewreset resize", this.redraw, this);
    this.resume();
    return this;
  }

  onRemove(map: L.Map) {
    this.pause();
    map.off("move zoom viewreset resize", this.redraw, this);
    this.canvas?.remove();
    this.lmap = null;
    return this;
  }

  /** Shroud animation: the mist drifts slowly, so ~8 redraws a second look the same as 60 and cost far less. The same
   *  timer repeats a still picture a few times: some WebKitGTK setups show a canvas one draw late. */
  resume() {
    this.pause();
    this.timer = setInterval(() => {
      if (document.hidden || !this.ready) return;
      const animating = this.options.animate && this.options.showFog;
      if (!animating && !this.repeats) return;
      const gl = this.gl;
      if (this.fence) {
        if (gl.clientWaitSync(this.fence, 0, 0) === gl.TIMEOUT_EXPIRED) return;
        gl.deleteSync(this.fence);
        this.fence = null;
      }
      if (animating) this.t = performance.now();
      if (this.repeats) this.repeats--;
      this.schedule();
    }, 125);
  }

  pause() {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    cancelAnimationFrame(this.frame);
    this.frame = 0;
  }

  setLook(o: Partial<Pick<GameMapOptions, "isolineSpacing" | "playerFogLevel" | "showFog" | "animate">>) {
    L.setOptions(this, o);
    this.redraw();
  }

  private async load() {
    const gl = this.gl, root = this.options.root;
    let done = 0;
    const total = 2 + N * N * 2 + 2;
    const tick = () => this.options.onProgress?.(++done, total);
    gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
    gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE);
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
    const r = await fetch(`${root}gpu/base.bin`);
    if (!r.ok || !r.body) throw new Error(`gpu/base.bin: ${r.status}`);
    tick();
    const raw = new Uint16Array(await new Response(r.body.pipeThrough(new DecompressionStream("gzip"))).arrayBuffer());
    const f = new Float32Array(raw.length);
    for (let i = 0; i < raw.length; i++) f[i] = raw[i] / 65535;
    Height.base = f;
    gl.activeTexture(gl.TEXTURE0);
    gl.bindTexture(gl.TEXTURE_2D, gl.createTexture());
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.R32F, 1280, 1280, 0, gl.RED, gl.FLOAT, f);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.activeTexture(gl.TEXTURE1);
    coarseCells(gl, f, 1280);
    tick();
    /** A tile array read with texelFetch only: layer = row from the north * N + column from the west. */
    const tiles = async (unit: number, dir: string, size: number, levels: number, internal: number, fmt: number) => {
      gl.activeTexture(gl.TEXTURE0 + unit);
      const t = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D_ARRAY, t);
      gl.texStorage3D(gl.TEXTURE_2D_ARRAY, levels, internal, size, size, N * N);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MIN_FILTER, gl.NEAREST_MIPMAP_NEAREST);
      gl.texParameteri(gl.TEXTURE_2D_ARRAY, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      const jobs: [number, number][] = [];
      for (let x = 0; x < N; x++) for (let y = 0; y < N; y++) jobs.push([x, y]);
      let next = 0;
      // a few at a time: 100 decoded 1024² bitmaps at once would spike memory
      const worker = async () => {
        while (next < jobs.length) {
          const [x, y] = jobs[next++];
          const img = await bitmap(`${root}gpu/${dir}/${x}_${y}.webp`);
          gl.activeTexture(gl.TEXTURE0 + unit);
          gl.bindTexture(gl.TEXTURE_2D_ARRAY, t);
          gl.texSubImage3D(gl.TEXTURE_2D_ARRAY, 0, 0, 0, (N - 1 - y) * N + x, size, size, 1, fmt, gl.UNSIGNED_BYTE, img);
          img.close?.();
          tick();
        }
      };
      await Promise.all([worker(), worker(), worker(), worker(), worker(), worker()]);
      return t;
    };
    await tiles(2, "d", 1024, FINE_LEVELS, gl.R8, gl.RED);
    gl.activeTexture(gl.TEXTURE2);
    gl.generateMipmap(gl.TEXTURE_2D_ARRAY);
    tick();
    const zones = await tiles(3, "f", ZONE_SIZE, ZONE_LEVELS, gl.RG8, gl.RG);
    buildZonePyramid(gl, zones, 3);
    tick();
    gl.useProgram(this.prog);
    ["u_nodes", "u_cells", "u_fine", "u_zones"].forEach((n, i) => gl.uniform1i(this.u(n), i));
  }

  redraw() {
    this.repeats = 2;
    this.schedule();
  }

  /** at most one draw per animation frame, however many move / zoom events arrive */
  private schedule() {
    if (this.frame || !this.ready) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.draw();
    });
  }

  private draw() {
    const map = this.lmap;
    if (!this.ready || !map) return;
    const c = this.canvas, gl = this.gl;
    const size = map.getSize(), dpr = window.devicePixelRatio || 1;
    if (!size.x || !size.y) return;
    if (c.width !== Math.round(size.x * dpr) || c.height !== Math.round(size.y * dpr)) {
      c.width = Math.round(size.x * dpr);
      c.height = Math.round(size.y * dpr);
      c.style.width = size.x + "px";
      c.style.height = size.y + "px";
    }
    L.DomUtil.setPosition(c, map.containerPointToLayerPoint([0, 0]));
    const topLeft = map.project(map.containerPointToLatLng([0, 0]), 5);
    const metresPerPixel = map.getZoomScale(5, map.getZoom()) / dpr;
    const zoomedOut = Math.log2(metresPerPixel);
    const smoothLevel = Math.max(2, Math.min(FINE_LEVELS - 1, Math.round(zoomedOut)));
    // past about 1.5 pixels per metre the 1 m level only adds its storage steps
    const fineLevel = zoomedOut < -0.6 ? 1 : Math.max(0, Math.min(smoothLevel, Math.round(zoomedOut)));
    gl.viewport(0, 0, c.width, c.height);
    const u = this.u;
    gl.uniform2f(u("u_origin"), topLeft.x, topLeft.y);
    gl.uniform1f(u("u_metresPerPixel"), metresPerPixel);
    gl.uniform1f(u("u_canvasHeight"), c.height);
    gl.uniform1f(u("u_pixelRatio"), dpr);
    gl.uniform1i(u("u_smoothLevel"), smoothLevel);
    gl.uniform1i(u("u_fineLevel"), fineLevel);
    gl.uniform1i(u("u_zoneLevel"), Math.max(0, Math.min(ZONE_LEVELS - 1, Math.round(zoomedOut - 1))));
    gl.uniform1f(u("u_reliefGain"), Math.pow(1.22, smoothLevel - 2) * 1.5);
    gl.uniform1f(u("u_contourStep"), this.options.isolineSpacing);
    gl.uniform1f(u("u_flame"), this.options.playerFogLevel);
    gl.uniform1i(u("u_showZones"), this.options.showFog ? 1 : 0);
    gl.uniform1f(u("u_clock"), this.t / 1000);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    if (!this.fence) {
      this.fence = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
      gl.flush();
    }
  }
}
