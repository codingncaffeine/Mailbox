// Reads the weather map's legend colours off the services' own legends, so the stops in
// src/Mailbox.Core/Weather/MapLayers.cs can be made again — and checked when a service changes a
// style — with nothing but Node.
//
//   node tools/weather-legends.mjs [--check | --write] [--map] [--from <folder>] [--save <folder>]
//
// Prints each legend's stops as MapLayers.cs writes them. --check compares them with MapLayers.cs
// instead and exits 1 on any difference; --write puts them into MapLayers.cs in place of what is
// there. --map also asks each layer for one picture of the weather
// now and says how far its colours lie from the legend's — a legend is only right if the map is
// drawn in its colours — exiting 1 when one in a hundred lies further than MAP_TOLERANCE.
// --save keeps what the services answered; --from reads such a folder rather than asking them.
//
// GeoMet describes a style only as a picture: a Matplotlib colour bar whose tick labels are the
// values. The bar is found by its outline, its ticks by the marks just right of it, and its colour
// is read down its middle; the numbers at the ticks are listed below, read off the labels by eye,
// and a picture whose tick count differs from its list stops the run. A bar whose every span is
// one colour from tick to tick is stepped, and is read block by block, each from its tick up to
// the next, the block above the top tick being the bar's arrow. Any other bar is smooth and is
// read at its ticks; where the middle of a span strays from a straight blend of its ends, `sub`
// splits it into that many steps. nowCOAST answers its legend as JSON — the style's colour map
// itself — which is read at every `step` from its first entry to its last one that shows: the
// legend spaces its stops evenly, so they must be evenly spaced in value, and every entry must
// fall on a step, so the blend between two steps is the map's own.
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";

const GEOMET = "https://geo.weather.gc.ca/geomet";
const NOWCOAST = "https://nowcoast.noaa.gov/geoserver/ows";
const AGENT = "Mailbox weather-legends (https://github.com/codingncaffeine/Mailbox)";
const LAYERS = path.join(path.dirname(new URL(import.meta.url).pathname), "..", "src", "Mailbox.Core", "Weather", "MapLayers.cs");

// Where --map looks: the radars over the continent they cover, the global model over the world
// (latitude, longitude in EPSG:4326's order).
const NORTH_AMERICA = "20,-130,60,-60";
const WORLD = "-80,-180,80,180";
const MAP_TOLERANCE = 12;

const LEGENDS = [
  {
    name: "ReflectivityLegend", source: "nowcoast", layer: "weather_radar:base_reflectivity_mosaic",
    style: "weather_radar_base_reflectivity", step: 1, area: NORTH_AMERICA,
  },
  {
    // Its colours bend through a control colour between each pair of ticks.
    name: "RainRateLegend", source: "geomet", layer: "RADAR_1KM_RRAI", style: "Radar-Rain_14colors", sub: 4, area: NORTH_AMERICA,
    ticks: [200, 125, 100, 64, 50, 32, 24, 16, 12, 8, 4, 2, 1, 0.1],
  },
  {
    name: "TemperatureLegend", source: "geomet", layer: "GDPS_15km_AirTemp_2m", style: "TEMPERATURE-LINEAR", area: WORLD,
    ticks: [40, 35, 30, 25, 20, 15, 10, 5, 0, -5, -10, -15, -20, -25, -30, -35, -40],
  },
  {
    name: "WindLegend", source: "geomet", layer: "GDPS_15km_WindSpeed_10m", style: "WINDSPEEDKNOTS-LINEAR", sub: 4, area: WORLD,
    ticks: [55, 50, 45, 40, 35, 30, 25, 20, 15, 10, 5, 0],
  },
  {
    name: "PrecipitationLegend", source: "geomet", layer: "GDPS_15km_PrecipRate", style: "PRECIPPRTMMH-LINEAR", area: WORLD,
    ticks: [50, 20, 10, 5, 1, 0.5, 0.1, 0.05, 0.01],
  },
  {
    name: "CloudLegend", source: "geomet", layer: "GDPS_15km_TotalCloudCover", style: "CLOUD", area: WORLD,
    ticks: [100, 0],
  },
];

function option(name) {
  const i = process.argv.indexOf(name);
  return i > 0 ? process.argv[i + 1] : undefined;
}

const check = process.argv.includes("--check");
const write = process.argv.includes("--write");
const map = process.argv.includes("--map");
const from = option("--from");
const save = option("--save");

// ---- PNG, as far as legends need it: 8-bit, not interlaced, grey/RGB/palette with or without alpha.
function decodePng(bytes) {
  const signature = [137, 80, 78, 71, 13, 10, 26, 10];
  if (!signature.every((b, i) => bytes[i] === b)) throw new Error("not a PNG");
  let offset = 8, width = 0, height = 0, depth = 0, type = 0, interlace = 0, palette = null, transparency = null;
  const data = [];
  while (offset < bytes.length) {
    const length = bytes.readUInt32BE(offset);
    const kind = bytes.toString("latin1", offset + 4, offset + 8);
    const body = bytes.subarray(offset + 8, offset + 8 + length);
    if (kind === "IHDR") {
      width = body.readUInt32BE(0);
      height = body.readUInt32BE(4);
      [depth, type, interlace] = [body[8], body[9], body[12]];
    } else if (kind === "PLTE") palette = body;
    else if (kind === "tRNS") transparency = body;
    else if (kind === "IDAT") data.push(body);
    else if (kind === "IEND") break;
    offset += 12 + length;
  }
  const channels = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[type];
  if (depth !== 8 || interlace !== 0 || !channels) throw new Error(`unsupported PNG: depth ${depth}, colour type ${type}, interlace ${interlace}`);

  const raw = zlib.inflateSync(Buffer.concat(data));
  const stride = width * channels;
  const rgba = Buffer.alloc(width * height * 4);
  let previous = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? line[x - channels] : 0;
      const up = previous[x];
      const corner = x >= channels ? previous[x - channels] : 0;
      let add;
      switch (filter) {
        case 0: add = 0; break;
        case 1: add = left; break;
        case 2: add = up; break;
        case 3: add = (left + up) >> 1; break;
        case 4: {
          const p = left + up - corner;
          const [pa, pb, pc] = [Math.abs(p - left), Math.abs(p - up), Math.abs(p - corner)];
          add = pa <= pb && pa <= pc ? left : pb <= pc ? up : corner;
          break;
        }
        default: throw new Error(`bad PNG filter ${filter}`);
      }
      line[x] = (line[x] + add) & 255;
    }
    for (let x = 0; x < width; x++) {
      const i = x * channels, o = (y * width + x) * 4;
      if (type === 3) {
        const p = line[i];
        rgba[o] = palette[p * 3]; rgba[o + 1] = palette[p * 3 + 1]; rgba[o + 2] = palette[p * 3 + 2];
        rgba[o + 3] = transparency && p < transparency.length ? transparency[p] : 255;
      } else if (type === 0 || type === 4) {
        rgba[o] = rgba[o + 1] = rgba[o + 2] = line[i];
        rgba[o + 3] = type === 4 ? line[i + 1] : 255;
      } else {
        rgba[o] = line[i]; rgba[o + 1] = line[i + 1]; rgba[o + 2] = line[i + 2];
        rgba[o + 3] = type === 6 ? line[i + 3] : 255;
      }
    }
    previous = line;
  }
  return { width, height, rgba };
}

// ---- A Matplotlib colour bar: its outline, the ticks right of it, the colour down its middle.
function readBar(png) {
  const { width, height, rgba } = png;
  // Over white, as the legend is seen.
  const at = (x, y) => {
    const o = (y * width + x) * 4, a = rgba[o + 3] / 255;
    return [0, 1, 2].map(c => Math.round(rgba[o + c] * a + 255 * (1 - a)));
  };
  const dark = ([r, g, b]) => r < 70 && g < 70 && b < 70;

  // The outline's right side, where the ticks are, is the last column dark for over half the
  // picture; its left side can be drawn too light to find that way, so the bar's left edge is
  // where its middle row meets the white margin.
  const sides = [];
  for (let x = 0; x < width; x++) {
    let best = null;
    for (let y = 0, start = -1; y <= height; y++) {
      const d = y < height && dark(at(x, y));
      if (d && start < 0) start = y;
      if (!d && start >= 0) {
        if (!best || y - start > best.bottom - best.top + 1) best = { top: start, bottom: y - 1 };
        start = -1;
      }
    }
    if (best && best.bottom - best.top > height / 2) sides.push({ x, ...best });
  }
  if (sides.length === 0) throw new Error("no colour bar found");
  const right = sides[sides.length - 1];
  const row = Math.round((right.top + right.bottom) / 2);
  let left = right.x - 1;
  while (left > 0 && !at(left - 1, row).every(c => c > 245)) left--;

  const ticks = [];
  for (let y = 0, run = -1; y <= height; y++) {
    const d = y < height && dark(at(right.x + 2, y));
    if (d && run < 0) run = y;
    if (!d && run >= 0) {
      ticks.push((run + y - 1) / 2);
      run = -1;
    }
  }

  // Read inside the outline, two pixels clear of its ends; above the top is the arrow, if any.
  const middle = Math.floor((left + right.x) / 2);
  const colour = (y, arrow = false) => at(middle, Math.round(arrow ? y : Math.min(right.bottom - 2, Math.max(right.top + 2, y))));
  return { ticks, colour };
}

function geometStops(legend, png) {
  const { ticks, colour } = readBar(png);
  if (ticks.length !== legend.ticks.length) throw new Error(`${legend.style}: ${ticks.length} ticks, but ${legend.ticks.length} values are listed`);
  const out = [];
  let departure = 0;
  const near = (a, b) => a.every((v, c) => Math.abs(v - b[c]) <= 6);
  const stepped = ticks.slice(1).every((y, i) => near(colour(ticks[i] + 3), colour(y - 3)));
  if (stepped) {
    for (let i = ticks.length - 1; i >= 0; i--) {
      const arrow = i === 0;
      out.push([legend.ticks[i], colour(arrow ? ticks[0] - 6 : (ticks[i] + ticks[i - 1]) / 2, arrow)]);
    }
  } else {
    const sub = legend.sub ?? 1;
    for (let i = ticks.length - 1; i > 0; i--) {
      const [y0, y1, v0, v1] = [ticks[i], ticks[i - 1], legend.ticks[i], legend.ticks[i - 1]];
      for (let k = 0; k < sub; k++) {
        const [ya, yb] = [y0 + (y1 - y0) * k / sub, y0 + (y1 - y0) * (k + 1) / sub];
        out.push([+(v0 + (v1 - v0) * k / sub).toFixed(4), colour(ya)]);
        const [a, b, m] = [colour(ya), colour(yb), colour((ya + yb) / 2)];
        departure = Math.max(departure, ...m.map((v, c) => Math.abs(v - (a[c] + b[c]) / 2)));
      }
    }
    out.push([legend.ticks[0], colour(ticks[0])]);
  }
  return { stops: out.map(([value, [r, g, b]]) => [value, (0xFF << 24 | r << 16 | g << 8 | b) >>> 0]), departure, stepped };
}

function nowcoastStops(legend, json) {
  const ramp = JSON.parse(json).Legend[0].rules[0].symbolizers[0].Raster.colormap;
  if (ramp.type !== "ramp") throw new Error(`${legend.style}: a ${ramp.type} colour map, not a ramp`);
  const entries = ramp.entries.map(e => ({
    value: Number(e.quantity),
    rgb: [1, 3, 5].map(i => parseInt(e.color.slice(i, i + 2), 16)),
    alpha: e.opacity === undefined ? 1 : Number(e.opacity),
  })).filter(e => e.alpha > 0);
  const [first, last] = [entries[0].value, entries[entries.length - 1].value];
  for (const e of entries) {
    if (Math.abs((e.value - first) / legend.step - Math.round((e.value - first) / legend.step)) > 1e-9) {
      throw new Error(`${legend.style}: an entry at ${e.value} falls between steps of ${legend.step}`);
    }
  }
  const stops = [];
  for (let v = first; v <= last + 1e-9; v += legend.step) {
    const upper = entries.findIndex(e => e.value >= v - 1e-9);
    const [a, b] = [entries[Math.max(0, upper - 1)], entries[upper]];
    const f = b.value === a.value ? 1 : (v - a.value) / (b.value - a.value);
    const [r, g, bl] = a.rgb.map((c, i) => Math.round(c + (b.rgb[i] - c) * f));
    const alpha = Math.round(255 * (a.alpha + (b.alpha - a.alpha) * f));
    stops.push([+v.toFixed(4), (alpha << 24 | r << 16 | g << 8 | bl) >>> 0]);
  }
  return { stops, departure: 0, stepped: false };
}

async function fetchLegend(legend) {
  const file = legend.source === "nowcoast" ? `${legend.style}.json` : `${legend.style}.png`;
  const url = legend.source === "nowcoast"
    ? `${NOWCOAST}?service=WMS&version=1.3.0&request=GetLegendGraphic&format=application/json&layer=${encodeURIComponent(legend.layer)}&style=${encodeURIComponent(legend.style)}`
    : `${GEOMET}?service=WMS&version=1.3.0&request=GetLegendGraphic&sld_version=1.1.0&format=image/png&layer=${encodeURIComponent(legend.layer)}&style=${encodeURIComponent(legend.style)}`;
  return fetchOrRead(file, url);
}

async function fetchMap(legend) {
  const url = `${legend.source === "nowcoast" ? NOWCOAST : GEOMET}?service=WMS&version=1.3.0&request=GetMap`
    + `&layers=${encodeURIComponent(legend.layer)}&styles=${encodeURIComponent(legend.style)}`
    + `&crs=EPSG:4326&bbox=${legend.area}&width=1400&height=700&format=image/png&transparent=true`;
  return fetchOrRead(`map-${legend.style}.png`, url);
}

async function fetchOrRead(file, url) {
  if (from) return fs.readFileSync(path.join(from, file));
  let response;
  try {
    response = await fetch(url, { headers: { "User-Agent": AGENT }, signal: AbortSignal.timeout(60_000) });
  } catch (error) {
    throw new Error(`${error.name === "TimeoutError" ? "no answer within a minute" : error.message} from ${url}`);
  }
  if (!response.ok) throw new Error(`HTTP ${response.status} from ${url}`);
  const bytes = Buffer.from(await response.arrayBuffer());
  if (save) {
    fs.mkdirSync(save, { recursive: true });
    fs.writeFileSync(path.join(save, file), bytes);
  }
  return bytes;
}

// ---- MapLayers.cs as written: each legend's stops, and whether it is stepped.
function committed() {
  const source = fs.readFileSync(LAYERS, "utf8");
  const found = new Map();
  const block = /MapLegend (\w+) = new\(MapQuantity\.\w+,\s*\[([\s\S]*?)\](, Stepped: true)?\);/g;
  for (let m; (m = block.exec(source));) {
    const stops = [...m[2].matchAll(/new\((-?[\d.]+), 0x([0-9A-Fa-f]{8})\)/g)].map(s => [Number(s[1]), parseInt(s[2], 16)]);
    found.set(m[1], { stops, stepped: Boolean(m[3]) });
  }
  return found;
}

// How far a map's colours lie from a legend's, in the largest channel's steps of 255: from the
// nearest point on the blend between neighbouring stops, or from the nearest block when stepped.
function mapDistances(png, stops, stepped) {
  const colours = stops.map(([, c]) => [(c >> 16) & 255, (c >> 8) & 255, c & 255]);
  const toSegment = (p, a, b) => {
    const d = a.map((v, i) => b[i] - v);
    const length = d.reduce((s, v) => s + v * v, 0);
    const t = length ? Math.max(0, Math.min(1, d.reduce((s, v, i) => s + (p[i] - a[i]) * v, 0) / length)) : 0;
    return Math.max(...p.map((v, i) => Math.abs(a[i] + d[i] * t - v)));
  };
  const counts = new Map();
  for (let o = 0; o < png.rgba.length; o += 4) {
    if (png.rgba[o + 3] === 0) continue;
    const key = (png.rgba[o] << 16) | (png.rgba[o + 1] << 8) | png.rgba[o + 2];
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  const measured = [];
  for (const [key, count] of counts) {
    const p = [(key >> 16) & 255, (key >> 8) & 255, key & 255];
    let best = Infinity;
    if (stepped) for (const c of colours) best = Math.min(best, Math.max(...p.map((v, i) => Math.abs(v - c[i]))));
    else for (let i = 0; i + 1 < colours.length; i++) best = Math.min(best, toSegment(p, colours[i], colours[i + 1]));
    measured.push([best, count]);
  }
  measured.sort((a, b) => a[0] - b[0]);
  const total = measured.reduce((s, [, n]) => s + n, 0);
  const share = q => {
    let seen = 0;
    for (const [d, n] of measured) if ((seen += n) >= q * total) return d;
    return 0;
  };
  return { pixels: total, median: share(0.5), p99: share(0.99), max: measured.length ? measured[measured.length - 1][0] : 0 };
}

const hex = colour => `0x${colour.toString(16).toUpperCase().padStart(8, "0")}`;

// A legend's stops as MapLayers.cs lays them out: five to a line inside the list.
function written(stops) {
  const words = stops.map(([v, c]) => `new(${v}, ${hex(c)})`);
  const lines = [];
  for (let i = 0; i < words.length; i += 5) lines.push(`        ${words.slice(i, i + 5).join(", ")},`);
  return lines.join("\n");
}

function rewrite(results) {
  let source = fs.readFileSync(LAYERS, "utf8");
  for (const { legend, stops, stepped } of results) {
    const block = new RegExp(`(MapLegend ${legend.name} = new\\(MapQuantity\\.\\w+,\\s*\\[)[\\s\\S]*?\\](, Stepped: true)?\\);`);
    if (!block.test(source)) throw new Error(`${legend.name} is not in MapLayers.cs`);
    source = source.replace(block, (_, head) => `${head}\n${written(stops)}\n    ]${stepped ? ", Stepped: true" : ""});`);
  }
  fs.writeFileSync(LAYERS, source);
}

let differences = 0;
const inCode = check ? committed() : null;
const results = [];
for (const legend of LEGENDS) {
  const bytes = await fetchLegend(legend);
  const { stops, departure, stepped } = legend.source === "nowcoast" ? nowcoastStops(legend, bytes.toString("utf8")) : geometStops(legend, decodePng(bytes));
  results.push({ legend, stops, stepped });
  if (map) {
    const seen = mapDistances(decodePng(await fetchMap(legend)), stops, stepped);
    const far = seen.pixels > 0 && seen.p99 > MAP_TOLERANCE;
    if (far) differences++;
    console.log(seen.pixels === 0
      ? `// ${legend.name}: the map has no weather in view to compare`
      : `// ${legend.name}: ${far ? "FAR FROM" : "on"} the map's colours — ${seen.pixels} pixels, off the legend by a median ${seen.median.toFixed(1)}, 1 in 100 by ${seen.p99.toFixed(1)}, at most ${seen.max.toFixed(1)}`);
  }

  if (!check) {
    const note = legend.source === "geomet" && !stepped ? ` — largest departure of a step's middle from a straight blend: ${departure.toFixed(0)}/255` : "";
    console.log(`// ${legend.name}: ${legend.style}${stepped ? ", stepped" : ""}${note}`);
    if (!write) console.log(written(stops));
    continue;
  }

  const code = inCode.get(legend.name);
  const problems = [];
  if (!code) problems.push("not found in MapLayers.cs");
  else {
    if (code.stepped !== stepped) problems.push(`stepped is ${code.stepped} in the code, ${stepped} in the legend`);
    if (code.stops.length !== stops.length) problems.push(`${code.stops.length} stops in the code, ${stops.length} in the legend`);
    stops.forEach(([v, c], i) => {
      const [cv, cc] = code.stops[i] ?? [];
      if (cv !== v || cc !== c) problems.push(`stop ${i}: code new(${cv}, ${cc === undefined ? "-" : hex(cc)}), legend new(${v}, ${hex(c)})`);
    });
  }
  differences += problems.length;
  console.log(`${problems.length ? "DIFFERS" : "matches"}  ${legend.name} (${legend.style})`);
  for (const p of problems) console.log(`    ${p}`);
}
if (write) rewrite(results);
if (differences) process.exit(1);
