/**
 * Draws the tray icons into electron/assets/. The PNGs are checked in; run this only to change them.
 *
 * Usage: node scripts/make-tray-icons.js
 *
 * The icon is three books on a shelf, the last one leaning. It is drawn from shapes rather than
 * shipped as a design file so it stays crisp at 16 px and can be regenerated without any tooling
 * beyond Node.
 *
 * - trayTemplate.png / @2x: black on transparent. macOS treats a name ending in "Template" as a
 *   template image and recolours it for a light or dark menu bar.
 * - tray.png / @2x: the app's accent blue, which reads on both light and dark taskbars (Windows
 *   and Linux do not recolour tray icons).
 */
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const OUTPUT_DIR = path.resolve(__dirname, '../electron/assets');

/** The drawing's coordinate space: one unit is one pixel of the 16 px icon. */
const DESIGN_SIZE = 16;

/** Samples per pixel along each axis, for anti-aliased edges. */
const SUPERSAMPLING = 8;

const BLACK = [0, 0, 0];
// --color-accent in src/index.css.
const ACCENT = [88, 143, 255];

/** Convex polygons, points listed clockwise, in design units. */
const SHAPES = [
  // Shelf.
  [[1, 14], [15, 14], [15, 15.5], [1, 15.5]],
  // Short book.
  [[2, 4], [5, 4], [5, 13], [2, 13]],
  // Tall book.
  [[6, 1.5], [9, 1.5], [9, 13], [6, 13]],
  // Leaning book: resting on its bottom-right corner, tipped about 15 degrees to the right.
  [[12.3, 4.2], [15.2, 5.0], [12.8, 13.8], [9.9, 13.0]],
];

function isInsideConvexPolygon(x, y, polygon) {
  for (let i = 0; i < polygon.length; i++) {
    const [ax, ay] = polygon[i];
    const [bx, by] = polygon[(i + 1) % polygon.length];
    // With y pointing down, a clockwise polygon keeps its inside to the right of every edge.
    if ((bx - ax) * (y - ay) - (by - ay) * (x - ax) < 0) return false;
  }
  return true;
}

/** Fraction of the pixel at (px, py) that the shapes cover, 0..1. */
function coverage(px, py, scale) {
  let hits = 0;
  for (let sy = 0; sy < SUPERSAMPLING; sy++) {
    for (let sx = 0; sx < SUPERSAMPLING; sx++) {
      const x = (px + (sx + 0.5) / SUPERSAMPLING) / scale;
      const y = (py + (sy + 0.5) / SUPERSAMPLING) / scale;
      if (SHAPES.some((shape) => isInsideConvexPolygon(x, y, shape))) hits++;
    }
  }
  return hits / (SUPERSAMPLING * SUPERSAMPLING);
}

/** RGBA scanlines, each prefixed with PNG filter type 0 (none). */
function render(scale, [r, g, b]) {
  const size = DESIGN_SIZE * scale;
  const rowLength = 1 + size * 4;
  const pixels = Buffer.alloc(rowLength * size);

  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      const offset = py * rowLength + 1 + px * 4;
      pixels[offset] = r;
      pixels[offset + 1] = g;
      pixels[offset + 2] = b;
      pixels[offset + 3] = Math.round(coverage(px, py, scale) * 255);
    }
  }
  return { size, pixels };
}

const CRC_TABLE = Array.from({ length: 256 }, (_, n) => {
  let c = n;
  for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
  return c >>> 0;
});

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) crc = CRC_TABLE[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body));
  return Buffer.concat([length, body, crc]);
}

function encodePng({ size, pixels }) {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header[8] = 8; // bits per channel
  header[9] = 6; // RGBA
  // Bytes 10-12: default compression, filtering and no interlacing.

  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', zlib.deflateSync(pixels, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

const ICONS = [
  { name: 'trayTemplate', colour: BLACK },
  { name: 'tray', colour: ACCENT },
];

fs.mkdirSync(OUTPUT_DIR, { recursive: true });
for (const { name, colour } of ICONS) {
  for (const [scale, suffix] of [[1, ''], [2, '@2x']]) {
    const file = path.join(OUTPUT_DIR, `${name}${suffix}.png`);
    fs.writeFileSync(file, encodePng(render(scale, colour)));
    console.log(`Wrote ${path.relative(process.cwd(), file)}`);
  }
}
