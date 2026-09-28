// Builds assets/weather/places/us-zip.tsv.gz — the United States ZIP codes the Weather module's
// place search finds on its own — from GeoNames' postal code list for the country.
//
//   node tools/weather-zipcodes.mjs <folder holding US.txt> [out.tsv.gz]
//
// The source is US.txt from https://download.geonames.org/export/zip/US.zip, unzipped, which
// GeoNames publishes under CC BY 4.0 (credited in packaging/NOTICES.txt). Why a list of our own:
// the forecast service's search knows most codes only through the city they belong to — every
// code in Phoenix finds the middle of Phoenix — and a few not at all, while this list puts each
// code at its own point, costs no request and works offline.
//
// Kept: the fifty states and the District of Columbia. Left out: the military APO and FPO codes,
// which name no place and sit at bases abroad, and the Marshall Islands' two, a country of its
// own. One line per code, sorted by code — code, place, state, latitude, longitude, separated by
// tabs — after a comment line naming the source. The file is gzip'd without a timestamp, so the
// same source always builds the same bytes.
import fs from "node:fs";
import path from "node:path";
import zlib from "node:zlib";

const [, , sourceArg, outArg] = process.argv;
if (!sourceArg) {
  console.error("usage: node tools/weather-zipcodes.mjs <folder holding US.txt> [out.tsv.gz]");
  process.exit(2);
}
const OUT = outArg ?? path.join(path.dirname(new URL(import.meta.url).pathname), "..", "assets", "weather", "places", "us-zip.tsv.gz");

const STATES = new Set([
  "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "DC", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS",
  "KY", "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC",
  "ND", "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY",
]);
const coordinate = (text, limit) => /^-?\d+(\.\d+)?$/.test(text) && Math.abs(Number(text)) <= limit;

const rows = new Map();
let skipped = 0;
for (const line of fs.readFileSync(path.join(sourceArg, "US.txt"), "utf8").split("\n")) {
  const field = line.replace(/\r$/, "").split("\t");
  if (field.length < 11 || field[0] !== "US") continue;
  const [, zip, place, state, code] = field;
  const [latitude, longitude] = [field[9], field[10]];
  if (!STATES.has(code)) {
    skipped++;
    continue;
  }
  if (!/^\d{5}$/.test(zip) || !place || !state || !coordinate(latitude, 90) || !coordinate(longitude, 180) || /\t/.test(place)) {
    throw new Error(`unreadable row: ${line}`);
  }
  if (rows.has(zip)) throw new Error(`${zip} appears twice among the states`);
  rows.set(zip, [zip, place, state, latitude, longitude].join("\t"));
}

const lines = [...rows.keys()].sort().map(zip => rows.get(zip));
const text = `# United States ZIP codes from GeoNames (www.geonames.org), CC BY 4.0 — tools/weather-zipcodes.mjs\n${lines.join("\n")}\n`;
const gzip = zlib.gzipSync(Buffer.from(text, "utf8"), { level: 9 });
gzip[9] = 255; // the header's operating system byte: "unknown", so the bytes do not depend on where it was built
fs.mkdirSync(path.dirname(OUT), { recursive: true });
fs.writeFileSync(OUT, gzip);
console.log(`${lines.length} codes (${skipped} left out) → ${OUT}, ${gzip.length} bytes`);
