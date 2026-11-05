// Kiln's own WebGL2 shaders for the admin map's relief layer (gamemap.ts). How the picture is made:
//
//  ground   The true height lives on a 1 m lattice: 8 m coarse nodes, bilinear between them, plus an 8-bit offset per
//           metre. A quadratic B-spline over that lattice gives a height and an exact slope at every pixel, so hills
//           stay round at any zoom. Zoomed out, the same kernel runs on a coarser level of the lattice; zoomed in, it
//           hides the storage steps of the 8-bit data (withoutStairs).
//  colour   A hypsometric ramp written here, on the launcher's dark ember palette, under a hillshade lit from three
//           directions so that no ridge disappears because it runs along the light.
//  contours Each pixel works out how far away the nearest level is, in pixels, from height and slope alone. Line classes
//           (the chosen interval, x5, x25) fade out wherever their neighbours would come too close together, which
//           keeps cliffs clean and keeps the lines one width at every zoom; up close, hairlines at a quarter of the
//           interval fade in where the regular lines have moved far apart.
//  Shroud   Zones come as a distance from the border and a level. The distance gives the soft edge; the level picks
//           the look: the outer Shroud is a dark veil, zones the player's Flame handles are a cold mist, and zones
//           above it glow ember red with a hatch, so they read without relying on colour alone. The mist is
//           procedural and follows the zoom.

/** One triangle over the whole canvas; it needs no vertex data. */
export const RELIEF_VERTEX = `#version 300 es
void main() {
  gl_Position = vec4(gl_VertexID == 1 ? 3.0 : -1.0, gl_VertexID == 2 ? 3.0 : -1.0, 0.0, 1.0);
}`;

export const RELIEF_FRAGMENT = `#version 300 es
precision highp float;
precision highp int;
precision highp sampler2D;
precision highp sampler2DArray;

uniform sampler2D u_nodes;        // coarse height, a node every 8 m, 0..1 = 0..4096 m
uniform sampler2D u_cells;        // means over coarse cells: level 0 = 8 m, level 1 = 16 m
uniform sampler2DArray u_fine;    // height offset, 1 m lattice, one layer per 1024 m tile, row by row from the north-west
uniform sampler2DArray u_zones;   // R depth inside a zone, G zone level; 2 m lattice, same layers; see buildZonePyramid

uniform vec2 u_origin;            // world position of the canvas's top-left corner: metres east and south of the north-west corner
uniform float u_metresPerPixel;
uniform float u_canvasHeight;
uniform float u_pixelRatio;
uniform int u_smoothLevel;        // lattice level for height, contours and broad shading (cell = 2^level m)
uniform int u_fineLevel;          // lattice level for the shading's small features; at most u_smoothLevel
uniform int u_zoneLevel;          // lattice level for the zones (cell = 2 * 2^level m)
uniform float u_reliefGain;       // keeps the hillshade's contrast when a coarser level flattens the slopes
uniform float u_contourStep;      // metres between the finest contour lines
uniform float u_flame;            // zones above this level are dangerous
uniform bool u_showZones;
uniform float u_clock;            // seconds

out vec4 o_colour;

const float WORLD_METRES = 10240.0;
const float NODE_METRES = 8.0;
const float NODE_RANGE = 4096.0;
const float FINE_RANGE = 255.0;
const float OUTER_LEVEL = 15.5;   // the Shroud beyond the playable land is level 16
const float STAIR_SLOPE = 0.44;   // how steep one 8-bit stair still looks on the 1 m lattice (half that a level up)

// ---- lattice access -----------------------------------------------------------------------------------------------

// 10 x 10 tiles of 2^bits samples: the texel and layer of one sample of the whole lattice, clamped to the world.
ivec3 inTile(ivec2 p, int bits) {
  ivec2 q = clamp(p, ivec2(0), ivec2((10 << bits) - 1));
  ivec2 tile = q >> bits;
  return ivec3(q - (tile << bits), tile.y * 10 + tile.x);
}

// A quadratic B-spline along one axis at lattice position t: the first of its three samples, their weights and the
// weights of the slope (per lattice cell). The result has a continuous slope.
struct Spline { int first; vec3 value; vec3 slope; };

Spline splineAt(float t) {
  float centre = floor(t + 0.5), f = t - centre;
  float a = 0.5 - f, b = 0.5 + f;
  return Spline(int(centre) - 1, vec3(0.5 * a * a, 0.75 - f * f, 0.5 * b * b), vec3(-a, -2.0 * f, b));
}

// Rewrites a spline over lattice samples as one over coarse nodes: each sample is bilinear between two nodes. A cell of
// up to 4 m spans at most three nodes. nudge lines the axis up: a node sits on lattice sample 8i across, 8i - 1 down.
Spline onNodes(Spline s, float cell, float nudge) {
  Spline n = Spline(0, vec3(0.0), vec3(0.0));
  for (int i = 0; i < 3; i++) {
    float at = ((float(s.first + i) + 0.5) * cell - 0.5 + nudge) / NODE_METRES;
    float whole = floor(at), part = at - whole;
    if (i == 0) n.first = int(whole);
    int k = int(whole) - n.first;
    n.value[k] += s.value[i] * (1.0 - part);
    n.value[k + 1] += s.value[i] * part;
    n.slope[k] += s.slope[i] * (1.0 - part);
    n.slope[k + 1] += s.slope[i] * part;
  }
  return n;
}

// ---- ground ---------------------------------------------------------------------------------------------------------

struct Ground { float metres; vec2 slope; };

Ground groundAt(vec2 world, int level) {
  float cell = float(1 << level);
  vec2 t = world / cell - 0.5;
  Spline sx = splineAt(t.x), sy = splineAt(t.y);
  float metres = 0.0;
  vec2 rise = vec2(0.0);
  bool coarse = level >= 3;
  int cellLevel = level - 3;
  ivec2 cellLast = ivec2((1280 >> max(cellLevel, 0)) - 1);
  for (int j = 0; j < 3; j++) {
    for (int i = 0; i < 3; i++) {
      ivec2 p = ivec2(sx.first + i, sy.first + j);
      float h = (texelFetch(u_fine, inTile(p, 10 - level), level).r - 0.5) * FINE_RANGE;
      if (coarse) h += texelFetch(u_cells, clamp(p, ivec2(0), cellLast), cellLevel).r * NODE_RANGE;
      metres += sx.value[i] * sy.value[j] * h;
      rise += vec2(sx.slope[i] * sy.value[j], sx.value[i] * sy.slope[j]) * h;
    }
  }
  if (!coarse) {
    Spline nx = onNodes(sx, cell, 0.0), ny = onNodes(sy, cell, 1.0);
    for (int j = 0; j < 3; j++) {
      for (int i = 0; i < 3; i++) {
        float h = texelFetch(u_nodes, clamp(ivec2(nx.first + i, ny.first + j), ivec2(0), ivec2(1279)), 0).r * NODE_RANGE;
        metres += nx.value[i] * ny.value[j] * h;
        rise += vec2(nx.slope[i] * ny.value[j], nx.value[i] * ny.slope[j]) * h;
      }
    }
  }
  return Ground(metres, rise / cell);
}

// The fine offsets are 8-bit, a metre per step, so a gentle slope is stored as a staircase, and the texture compression
// adds blocks on top. Up close that would be all you see. So close-ups keep a smooth ground from a coarser level for the
// height and the contours, and the shading adds back only the part of the fine slope that stands out from the smooth one
// by more than a stair could: walls and cliffs stay, the stairs go.
vec2 withoutStairs(vec2 fine, vec2 smoothed) {
  vec2 extra = fine - smoothed;
  float size = length(extra);
  float stair = STAIR_SLOPE / float(1 << u_fineLevel);
  return smoothed + extra * (max(0.0, size - stair) / max(size, 1e-4));
}

// ---- colour ---------------------------------------------------------------------------------------------------------

const int RAMP_STOPS = 9;
const float RAMP_METRES[RAMP_STOPS] = float[RAMP_STOPS](530.0, 610.0, 720.0, 860.0, 1040.0, 1300.0, 1650.0, 2050.0, 2420.0);
const vec3 RAMP_COLOUR[RAMP_STOPS] = vec3[RAMP_STOPS](
  vec3(0.098, 0.094, 0.096), vec3(0.150, 0.134, 0.118), vec3(0.238, 0.203, 0.154), vec3(0.340, 0.272, 0.192),
  vec3(0.425, 0.315, 0.208), vec3(0.505, 0.362, 0.245), vec3(0.625, 0.508, 0.398), vec3(0.792, 0.742, 0.668),
  vec3(0.930, 0.908, 0.858));

vec3 landColour(float metres) {
  for (int i = 1; i < RAMP_STOPS; i++) {
    if (metres < RAMP_METRES[i]) {
      float a = clamp((metres - RAMP_METRES[i - 1]) / (RAMP_METRES[i] - RAMP_METRES[i - 1]), 0.0, 1.0);
      return mix(RAMP_COLOUR[i - 1], RAMP_COLOUR[i], a);
    }
  }
  return RAMP_COLOUR[RAMP_STOPS - 1];
}

// Light on a slope, 1 on flat ground: a sun in the north-west and a weaker fill on either side of it. The light wraps
// past the point where a slope turns away from the sun, so steep shaded ground keeps its form instead of going flat.
float sunlight(vec2 slope) {
  vec3 normal = normalize(vec3(-slope, 1.0));
  float key = dot(normal, vec3(-0.5417, -0.5417, 0.6428));
  float west = dot(normal, vec3(-0.7077, 0.2932, 0.6428));
  float north = dot(normal, vec3(0.2932, -0.7077, 0.6428));
  float raw = 0.62 * key + 0.19 * west + 0.19 * north;
  return max(0.0, raw + 0.42) / (0.6428 + 0.42);
}

vec3 shadeLand(vec3 colour, vec2 slope) {
  float light = sunlight(slope * u_reliefGain);
  vec3 lit = light < 1.0 ? mix(vec3(0.30, 0.26, 0.32), vec3(1.0), light) : mix(vec3(1.0), vec3(1.36, 1.28, 1.14), min(1.0, (light - 1.0) * 2.0));
  return colour * lit;
}

// ---- contours -------------------------------------------------------------------------------------------------------

// Coverage of the nearest line of one class. climb = metres of height per pixel, so metres / climb = pixels. minApart
// and fullApart: how far apart, in CSS pixels, the class's lines must be to start showing and to show fully.
float contourClass(float metres, float climb, float interval, float halfWidth, float minApart, float fullApart) {
  float nearest = abs(metres / interval - floor(metres / interval + 0.5)) * interval;
  float line = 1.0 - smoothstep(halfWidth - 0.6, halfWidth + 0.6, nearest / climb);
  return line * smoothstep(minApart, fullApart, interval / climb / u_pixelRatio);
}

vec3 drawContours(vec3 colour, Ground g) {
  float grade = length(g.slope);
  float climb = max(grade * u_metresPerPixel, 1e-4);
  float visible = smoothstep(0.003, 0.012, grade);
  float hair = contourClass(g.metres, climb, u_contourStep * 0.25, 0.26 * u_pixelRatio, 26.0, 60.0);
  float fine = contourClass(g.metres, climb, u_contourStep, 0.30 * u_pixelRatio, 5.0, 11.0);
  float major = contourClass(g.metres, climb, u_contourStep * 5.0, 0.50 * u_pixelRatio, 5.0, 11.0);
  float index = contourClass(g.metres, climb, u_contourStep * 25.0, 0.68 * u_pixelRatio, 5.0, 11.0);
  colour = mix(colour, vec3(1.00, 0.94, 0.86), 0.10 * hair * visible);
  colour = mix(colour, vec3(1.00, 0.93, 0.82), 0.20 * fine * visible);
  colour = mix(colour, vec3(1.00, 0.90, 0.76), 0.36 * major * visible);
  colour = mix(colour, vec3(1.00, 0.70, 0.42), 0.50 * index * visible);
  return colour;
}

// ---- Shroud ---------------------------------------------------------------------------------------------------------

struct Veil { float depth; float danger; float outer; };

// depth = mean distance signal; danger and outer = shares of the zone samples under this pixel. Samples outside every
// zone do not vote, so a zone keeps its class right up to its border.
Veil veilAt(vec2 world) {
  float cell = 2.0 * float(1 << u_zoneLevel);
  vec2 t = world / cell - 0.5;
  Spline sx = splineAt(t.x), sy = splineAt(t.y);
  Veil v = Veil(0.0, 0.0, 0.0);
  float votes = 0.0;
  for (int j = 0; j < 3; j++) {
    for (int i = 0; i < 3; i++) {
      vec2 z = texelFetch(u_zones, inTile(ivec2(sx.first + i, sy.first + j), 9 - u_zoneLevel), u_zoneLevel).rg;
      float w = sx.value[i] * sy.value[j];
      v.depth += w * z.r;
      if (z.r > 0.004) {
        float level = z.g * 255.0;
        votes += w;
        if (level > OUTER_LEVEL) v.outer += w;
        else if (level > u_flame + 0.5) v.danger += w;
      }
    }
  }
  float total = max(votes, 1e-4);
  v.danger /= total;
  v.outer /= total;
  return v;
}

float scatter(ivec2 p) {
  uvec2 q = uvec2(p) * uvec2(1597334677u, 3812015801u);
  uint h = (q.x ^ q.y) * 1597334677u;
  return float(h >> 8) / 16777215.0;
}

float softNoise(vec2 q) {
  vec2 whole = floor(q), f = q - whole;
  f = f * f * (3.0 - 2.0 * f);
  ivec2 p = ivec2(whole);
  return mix(mix(scatter(p), scatter(p + ivec2(1, 0)), f.x), mix(scatter(p + ivec2(0, 1)), scatter(p + ivec2(1, 1)), f.x), f.y);
}

// Drifting cloud, about 300 pixels across at every zoom: two neighbouring scales cross-fade as the zoom changes.
float mist(vec2 world) {
  float scale = log2(u_metresPerPixel * 180.0), whole = floor(scale), between = scale - whole;
  float size = exp2(whole);
  vec2 wind = u_clock * vec2(0.016, 0.009);
  float near = softNoise(world / size + wind) * 0.65 + softNoise(world / (size * 0.5) - wind * 1.7) * 0.35;
  float far = softNoise(world / (size * 2.0) + wind * 0.6) * 0.65 + softNoise(world / size + wind) * 0.35;
  return mix(near, far, between);
}

// Diagonal lines about 14-28 pixels apart. They stay on fixed world positions: every doubling of the zoom fades in a
// line between each pair, so nothing jumps.
float hatch(vec2 world) {
  float scale = log2(u_metresPerPixel * 14.0 * u_pixelRatio), whole = ceil(scale);
  float apart = exp2(whole), wide = smoothstep(0.55, 1.0, whole - scale);
  float along = (world.x + world.y) * 0.7071 / apart;
  float halfWidth = 0.7 * u_pixelRatio;
  float toLine = abs(along - floor(along + 0.5)) * apart / u_metresPerPixel;
  float toMiddle = (0.5 - abs(along - floor(along + 0.5))) * apart / u_metresPerPixel;
  return max(1.0 - smoothstep(halfWidth - 0.6, halfWidth + 0.6, toLine), wide * (1.0 - smoothstep(halfWidth - 0.6, halfWidth + 0.6, toMiddle)));
}

vec3 drawVeil(vec3 colour, vec2 world, Veil v) {
  float cover = smoothstep(0.0, 0.5, v.depth);
  if (cover <= 0.0) return colour;
  float edge = cover * (1.0 - smoothstep(0.12, 0.62, v.depth));
  float cloud = mist(world);

  float mistAlpha = cover * (0.20 + 0.26 * cloud);
  vec3 cold = mix(colour, vec3(0.40, 0.55, 0.78), mistAlpha);
  cold = mix(cold, vec3(0.62, 0.78, 0.95), 0.32 * edge);

  float breath = 0.94 + 0.06 * sin(u_clock * 0.9);
  float redAlpha = cover * (0.26 + 0.20 * cloud) * breath;
  vec3 hot = mix(colour, vec3(0.86, 0.20, 0.08), redAlpha);
  hot = mix(hot, vec3(1.00, 0.46, 0.16), cover * 0.34 * hatch(world));
  hot = mix(hot, vec3(1.00, 0.52, 0.18), 0.62 * edge);

  float darkAlpha = cover * (0.84 + 0.05 * cloud);
  vec3 dark = mix(colour, vec3(0.040, 0.034, 0.032), darkAlpha);
  dark = mix(dark, vec3(0.20, 0.17, 0.15), 0.30 * edge);

  vec3 inside = mix(cold, hot, v.danger);
  return mix(inside, dark, v.outer);
}

// ---- the picture ----------------------------------------------------------------------------------------------------

void main() {
  vec2 world = u_origin + vec2(gl_FragCoord.x, u_canvasHeight - gl_FragCoord.y) * u_metresPerPixel;
  if (any(lessThan(world, vec2(0.0))) || any(greaterThanEqual(world, vec2(WORLD_METRES)))) {
    o_colour = vec4(0.0);
    return;
  }
  Ground ground = groundAt(world, u_smoothLevel);
  vec2 slope = u_fineLevel < u_smoothLevel ? withoutStairs(groundAt(world, u_fineLevel).slope, ground.slope) : ground.slope;
  vec3 colour = shadeLand(landColour(ground.metres), slope);
  colour = drawContours(colour, ground);
  if (u_showZones) colour = drawVeil(colour, world, veilAt(world));
  // half a step of 8-bit dither keeps the dark gradients from banding
  colour += (scatter(ivec2(gl_FragCoord.xy)) - 0.5) / 255.0;
  o_colour = vec4(colour, 1.0);
}`;

/** One level of the zone pyramid from the level above it: the mean depth and the highest level. A mean of levels would
 *  invent zones along every border (level 16 next to no zone would read as level 8). */
export const ZONE_PYRAMID_FRAGMENT = `#version 300 es
precision highp float;
precision highp int;
precision highp sampler2DArray;
uniform sampler2DArray u_source;
uniform int u_layer;
out vec4 o_colour;
void main() {
  ivec3 p = ivec3(ivec2(gl_FragCoord.xy) * 2, u_layer);
  vec2 a = texelFetch(u_source, p, 0).rg;
  vec2 b = texelFetch(u_source, p + ivec3(1, 0, 0), 0).rg;
  vec2 c = texelFetch(u_source, p + ivec3(0, 1, 0), 0).rg;
  vec2 d = texelFetch(u_source, p + ivec3(1, 1, 0), 0).rg;
  o_colour = vec4((a.r + b.r + c.r + d.r) * 0.25, max(max(a.g, b.g), max(c.g, d.g)), 0.0, 1.0);
}`;
