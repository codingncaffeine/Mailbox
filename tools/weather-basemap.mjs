// Builds assets/weather/map/basemap.bin — the Weather map's own base map — from public data:
// Natural Earth (public domain) for land, lakes, borders, states and provinces, cities and major
// highways, and us-atlas (ISC, drawn from the US Census Bureau's cartographic boundaries) for the
// counties.
//
//   node tools/weather-basemap.mjs <folder holding the downloaded sources>
//
// The sources are the GeoJSON files of github.com/nvkelso/natural-earth-vector (ne_50m_land,
// ne_10m_land, ne_50m_lakes, ne_10m_lakes, ne_50m_admin_0_boundary_lines_land,
// ne_10m_admin_0_boundary_lines_land, ne_50m_admin_1_states_provinces_lines,
// ne_10m_admin_1_states_provinces_lines, ne_10m_populated_places_simple, ne_10m_roads) and
// us-atlas@3's counties-10m.json.
//
// Why a map of our own: the map is drawn in the reader's theme — dark in Black, light elsewhere —
// and needs no tile server, so it has no usage policy to meet and nothing to fetch. What the
// weather services provide is drawn over it.
//
// Every coordinate is projected to Web Mercator on [0,1), simplified (Douglas-Peucker) for the
// zoom band it is drawn at, quantized to 2^26 steps per world and written as zig-zag varint
// deltas. Format, little-endian where it matters:
//   "MBXMAP01", u8 layers; per layer: name (u8 length + UTF-8), u8 kind (0 area, 1 line, 2 place),
//   u8 lods; per lod: f32 minimum zoom, varuint features; per area/line feature: varuint parts,
//   per part: varuint points, then x, y as varuint and each later point as zig-zag varint deltas;
//   per place: varuint x, y, f32 minimum label zoom, name (varuint length + UTF-8).
import fs from "node:fs";
import path from "node:path";

const [, , sourceArg, outArg] = process.argv;
if (!sourceArg) {
  console.error("usage: node tools/weather-basemap.mjs <sources> [out.bin]");
  process.exit(2);
}
const SRC = sourceArg;
const OUT = outArg ?? path.join(path.dirname(new URL(import.meta.url).pathname), "..", "assets", "weather", "map", "basemap.bin");
const Q = 2 ** 26;
const MAX_LAT = 85.05112878;

const read = (name) => JSON.parse(fs.readFileSync(path.join(SRC, name), "utf8"));

function project([lon, lat]) {
  const clamped = Math.max(-MAX_LAT, Math.min(MAX_LAT, lat));
  const s = Math.sin((clamped * Math.PI) / 180);
  const x = (lon + 180) / 360;
  const y = 0.5 - Math.log((1 + s) / (1 - s)) / (4 * Math.PI);
  return [Math.min(Math.max(x, 0), 1 - 1e-12), Math.min(Math.max(y, 0), 1 - 1e-12)];
}

/** Half a pixel at a zoom, in world units: the most a simplification may move a line. */
const tolerance = (zoom) => 0.5 / (256 * 2 ** zoom);

function simplify(points, tol) {
  if (points.length <= 2) return points;
  const keep = new Uint8Array(points.length);
  keep[0] = keep[points.length - 1] = 1;
  const stack = [[0, points.length - 1]];
  const tol2 = tol * tol;
  while (stack.length) {
    const [a, b] = stack.pop();
    const [ax, ay] = points[a];
    const [bx, by] = points[b];
    const dx = bx - ax, dy = by - ay;
    const len2 = dx * dx + dy * dy;
    let worst = -1, index = -1;
    for (let i = a + 1; i < b; i++) {
      const [px, py] = points[i];
      let d2;
      if (len2 === 0) d2 = (px - ax) ** 2 + (py - ay) ** 2;
      else {
        const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / len2));
        d2 = (px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2;
      }
      if (d2 > worst) { worst = d2; index = i; }
    }
    if (worst > tol2 && index > 0) {
      keep[index] = 1;
      stack.push([a, index], [index, b]);
    }
  }
  return points.filter((_, i) => keep[i]);
}

/** Lines that cross the antimeridian are cut there, so no segment spans the whole world. */
function splitAtDateLine(points) {
  const parts = [[points[0]]];
  for (let i = 1; i < points.length; i++) {
    if (Math.abs(points[i][0] - points[i - 1][0]) > 0.5) parts.push([]);
    parts.at(-1).push(points[i]);
  }
  return parts.filter((p) => p.length >= 2);
}

function areaFeatures(geojson, zoom) {
  const tol = tolerance(zoom);
  const out = [];
  for (const f of geojson.features) {
    const g = f.geometry;
    if (!g) continue;
    const polygons = g.type === "Polygon" ? [g.coordinates] : g.type === "MultiPolygon" ? g.coordinates : [];
    for (const polygon of polygons) {
      const rings = [];
      for (const ring of polygon) {
        const simple = simplify(ring.map(project), tol);
        if (simple.length < 4) continue;
        const [minX, maxX] = [Math.min(...simple.map((p) => p[0])), Math.max(...simple.map((p) => p[0]))];
        const [minY, maxY] = [Math.min(...simple.map((p) => p[1])), Math.max(...simple.map((p) => p[1]))];
        if ((maxX - minX) < tol * 3 && (maxY - minY) < tol * 3) continue;
        rings.push(simple);
      }
      if (rings.length) out.push(rings);
    }
  }
  return out;
}

/** Lines, simplified for a zoom; a piece shorter than `shortest` pixels there is too small to see and is left out. */
function lineFeatures(lines, zoom, shortest = 0) {
  const tol = tolerance(zoom);
  const floor = shortest / (256 * 2 ** zoom);
  const out = [];
  for (const line of lines) {
    for (const part of splitAtDateLine(line.map(project))) {
      const simple = simplify(part, tol);
      if (simple.length < 2) continue;
      let length = 0;
      for (let i = 1; i < simple.length; i++) length += Math.hypot(simple[i][0] - simple[i - 1][0], simple[i][1] - simple[i - 1][1]);
      if (length >= floor) out.push([simple]);
    }
  }
  return out;
}

function geojsonLines(geojson, keep = () => true) {
  const lines = [];
  for (const f of geojson.features) {
    if (!f.geometry || !keep(f.properties ?? {})) continue;
    const g = f.geometry;
    if (g.type === "LineString") lines.push(g.coordinates);
    else if (g.type === "MultiLineString") lines.push(...g.coordinates);
  }
  return lines;
}

/** The county boundaries: the TopoJSON arcs two different counties share, decoded to lon/lat. */
function countyLines(topology) {
  const { scale, translate } = topology.transform;
  const arcs = topology.arcs.map((arc) => {
    let x = 0, y = 0;
    return arc.map(([dx, dy]) => { x += dx; y += dy; return [x * scale[0] + translate[0], y * scale[1] + translate[1]]; });
  });
  const users = new Map();
  topology.objects.counties.geometries.forEach((geometry, owner) => {
    const polygons = geometry.type === "Polygon" ? [geometry.arcs] : geometry.type === "MultiPolygon" ? geometry.arcs : [];
    for (const polygon of polygons) for (const ring of polygon) for (const index of ring) {
      const arc = index < 0 ? ~index : index;
      if (!users.has(arc)) users.set(arc, new Set());
      users.get(arc).add(owner);
    }
  });
  return [...users.entries()].filter(([, owners]) => owners.size > 1).map(([arc]) => arcs[arc]);
}

// ---- Writing ----------------------------------------------------------------------------------
const bytes = [];
const u8 = (v) => bytes.push(v & 0xff);
const varuint = (v) => { v = Math.floor(v); while (v >= 0x80) { u8((v % 0x80) | 0x80); v = Math.floor(v / 0x80); } u8(v); };
const varint = (v) => varuint(v < 0 ? -2 * v - 1 : 2 * v);
const f32 = (v) => { const b = Buffer.alloc(4); b.writeFloatLE(v); for (const x of b) u8(x); };
const text = (s, long = false) => { const b = Buffer.from(s, "utf8"); if (long) varuint(b.length); else u8(Math.min(b.length, 255)); for (const x of b.subarray(0, long ? b.length : 255)) u8(x); };
const quant = (v) => Math.round(v * Q);

let points = 0;
function writeShapes(features) {
  varuint(features.length);
  for (const parts of features) {
    varuint(parts.length);
    for (const part of parts) {
      varuint(part.length);
      let px = quant(part[0][0]), py = quant(part[0][1]);
      varuint(px); varuint(py);
      for (let i = 1; i < part.length; i++) {
        const x = quant(part[i][0]), y = quant(part[i][1]);
        varint(x - px); varint(y - py);
        px = x; py = y;
      }
      points += part.length;
    }
  }
}

const layers = [];
const land50 = read("ne_50m_land.geojson"), land10 = read("ne_10m_land.geojson");
const lakes50 = read("ne_50m_lakes.geojson"), lakes10 = read("ne_10m_lakes.geojson");
const borders50 = read("ne_50m_admin_0_boundary_lines_land.geojson"), borders10 = read("ne_10m_admin_0_boundary_lines_land.geojson");
const states50 = read("ne_50m_admin_1_states_provinces_lines.geojson"), states10 = read("ne_10m_admin_1_states_provinces_lines.geojson");

// Two levels of detail per layer: the 1:50m data up to zoom 5, and the 1:10m data simplified to
// half a pixel at zoom 8 from there on — within a pixel or so even at zoom 10, which is closer
// than a weather map needs to go, at a third of the size a third level would cost.
layers.push({ name: "land", kind: 0, lods: [[0, areaFeatures(land50, 4)], [5, areaFeatures(land10, 8)]] });
layers.push({ name: "lakes", kind: 0, lods: [[0, areaFeatures(lakes50, 4)], [5, areaFeatures(lakes10, 7)]] });
layers.push({ name: "counties", kind: 1, lods: [[6, lineFeatures(countyLines(read("us-counties-10m.json")), 9)]] });
layers.push({ name: "states", kind: 1, lods: [[0, lineFeatures(geojsonLines(states50), 4)], [5, lineFeatures(geojsonLines(states10), 7, 12)]] });
layers.push({ name: "borders", kind: 1, lods: [[0, lineFeatures(geojsonLines(borders50), 4)], [5, lineFeatures(geojsonLines(borders10), 8)]] });
const roads = read("ne_10m_roads.geojson");
const major = (p) => p.type === "Major Highway";
layers.push({ name: "roads", kind: 1, lods: [[5, lineFeatures(geojsonLines(roads, (p) => major(p) && p.scalerank <= 5), 7)], [7, lineFeatures(geojsonLines(roads, major), 9)]] });

const places = read("ne_10m_populated_places_simple.geojson").features
  .filter((f) => f.geometry?.type === "Point")
  .map((f) => ({ at: project(f.geometry.coordinates), zoom: Number(f.properties.min_zoom ?? 10), name: f.properties.name ?? f.properties.nameascii ?? "" }))
  .filter((p) => p.name.length > 0)
  .sort((a, b) => a.zoom - b.zoom);

bytes.push(...Buffer.from("MBXMAP01", "ascii"));
u8(layers.length + 1);
for (const layer of layers) {
  text(layer.name); u8(layer.kind); u8(layer.lods.length);
  for (const [minZoom, features] of layer.lods) {
    const before = points;
    f32(minZoom);
    writeShapes(features);
    console.log(`${layer.name.padEnd(9)} from z${minZoom}: ${String(features.length).padStart(6)} features, ${String(points - before).padStart(7)} points`);
  }
}
text("places"); u8(2); u8(1); f32(0); varuint(places.length);
for (const p of places) { varuint(quant(p.at[0])); varuint(quant(p.at[1])); f32(p.zoom); text(p.name, true); }
console.log(`places: ${places.length}`);

fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, Buffer.from(bytes));
console.log(`${OUT}: ${(bytes.length / 1024 / 1024).toFixed(2)} MB, ${points} points`);
