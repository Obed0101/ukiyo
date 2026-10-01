// PNG codec with no dependencies (node:zlib). Mirrors src/Ukiyo.Render/PngDecoder.cs: non-interlaced, 8-bit for every
// color type plus 1/2/4-bit palettes and grayscale, tRNS transparency. Output is always RGBA8.
import { deflateSync, inflateSync } from "node:zlib";

export type Image = { width: number; height: number; data: Uint8Array };

const SIGNATURE = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
})();

export function createImage(width: number, height: number): Image {
  if (!(width > 0 && height > 0 && Number.isInteger(width) && Number.isInteger(height))) throw new RangeError(`[PNG]: bad size ${width}x${height}`);
  return { width, height, data: new Uint8Array(width * height * 4) };
}

export function isPng(bytes: Uint8Array): boolean {
  return bytes.length > 8 && SIGNATURE.every((b, i) => bytes[i] === b);
}

export function decodePng(bytes: Uint8Array): Image {
  if (!isPng(bytes)) throw new TypeError("[PNG]: not a PNG file (bad signature)");
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let offset = 8;
  let width = 0;
  let height = 0;
  let depth = 0;
  let colorType = -1;
  let palette: Uint8Array | null = null;
  let transparency: Uint8Array | null = null;
  const chunks: Uint8Array[] = [];
  while (offset + 12 <= bytes.length) {
    const length = view.getUint32(offset);
    const type = String.fromCharCode(...bytes.subarray(offset + 4, offset + 8));
    const data = bytes.subarray(offset + 8, offset + 8 + length);
    if (type === "IHDR") {
      width = view.getUint32(offset + 8);
      height = view.getUint32(offset + 12);
      depth = data[8] ?? 0;
      colorType = data[9] ?? -1;
      if (data[12] !== 0) throw new TypeError("[PNG]: interlaced images are not supported");
    } else if (type === "PLTE") palette = data;
    else if (type === "tRNS") transparency = data;
    else if (type === "IDAT") chunks.push(data);
    else if (type === "IEND") break;
    offset += 12 + length;
  }

  const channels = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[colorType as 0 | 2 | 3 | 4 | 6];
  if (!channels) throw new TypeError(`[PNG]: unknown color type ${colorType}`);
  if (!(depth === 8 || ((colorType === 0 || colorType === 3) && (depth === 1 || depth === 2 || depth === 4)))) {
    throw new TypeError(`[PNG]: bit depth ${depth} with color type ${colorType} is not supported`);
  }
  if (colorType === 3 && !palette) throw new TypeError("[PNG]: palette image without PLTE");

  const raw = inflateSync(Buffer.concat(chunks));
  const bitsPerPixel = channels * depth;
  const stride = Math.ceil((width * bitsPerPixel) / 8);
  const bpp = Math.max(1, bitsPerPixel >> 3);
  if (raw.length < (stride + 1) * height) throw new TypeError(`[PNG]: image data has ${raw.length} bytes, expected ${(stride + 1) * height}`);

  const image = createImage(width, height);
  let previous = new Uint8Array(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const row = Uint8Array.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));
    unfilter(filter ?? 0, row, previous, bpp);
    for (let x = 0; x < width; x++) {
      const out = (y * width + x) * 4;
      const sample = (index: number) => {
        if (depth === 8) return row[index] ?? 0;
        const perByte = 8 / depth;
        const shift = 8 - depth * ((index % perByte) + 1);
        return ((row[Math.floor(index / perByte)] ?? 0) >> shift) & ((1 << depth) - 1);
      };
      let r: number, g: number, b: number, a = 255;
      if (colorType === 0) {
        const v = sample(x);
        r = g = b = Math.round((v * 255) / ((1 << depth) - 1));
        if (transparency && transparency.length >= 2 && ((transparency[0]! << 8) | transparency[1]!) === v) a = 0;
      } else if (colorType === 2) {
        r = row[x * 3]!; g = row[x * 3 + 1]!; b = row[x * 3 + 2]!;
        if (transparency && transparency.length >= 6 && ((transparency[0]! << 8) | transparency[1]!) === r && ((transparency[2]! << 8) | transparency[3]!) === g && ((transparency[4]! << 8) | transparency[5]!) === b) a = 0;
      } else if (colorType === 3) {
        const i = sample(x);
        r = palette![i * 3] ?? 0; g = palette![i * 3 + 1] ?? 0; b = palette![i * 3 + 2] ?? 0;
        if (transparency && i < transparency.length) a = transparency[i]!;
      } else if (colorType === 4) {
        r = g = b = row[x * 2]!; a = row[x * 2 + 1]!;
      } else {
        r = row[x * 4]!; g = row[x * 4 + 1]!; b = row[x * 4 + 2]!; a = row[x * 4 + 3]!;
      }
      image.data[out] = r; image.data[out + 1] = g; image.data[out + 2] = b; image.data[out + 3] = a;
    }
    previous = row;
  }
  return image;
}

function unfilter(filter: number, row: Uint8Array, previous: Uint8Array, bpp: number) {
  for (let i = 0; i < row.length; i++) {
    const left = i >= bpp ? row[i - bpp]! : 0;
    const up = previous[i]!;
    const upLeft = i >= bpp ? previous[i - bpp]! : 0;
    let add: number;
    switch (filter) {
      case 0: add = 0; break;
      case 1: add = left; break;
      case 2: add = up; break;
      case 3: add = (left + up) >> 1; break;
      case 4: {
        const p = left + up - upLeft;
        const pa = Math.abs(p - left);
        const pb = Math.abs(p - up);
        const pc = Math.abs(p - upLeft);
        add = pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft;
        break;
      }
      default: throw new TypeError(`[PNG]: unknown row filter ${filter}`);
    }
    row[i] = (row[i]! + add) & 0xff;
  }
}

export function encodePng(image: Image): Uint8Array {
  const { width, height, data } = image;
  const raw = new Uint8Array((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (width * 4 + 1)] = 0;
    raw.set(data.subarray(y * width * 4, (y + 1) * width * 4), y * (width * 4 + 1) + 1);
  }
  const header = new Uint8Array(13);
  const hv = new DataView(header.buffer);
  hv.setUint32(0, width);
  hv.setUint32(4, height);
  header[8] = 8;
  header[9] = 6;
  return Buffer.concat([Uint8Array.from(SIGNATURE), chunk("IHDR", header), chunk("IDAT", deflateSync(raw, { level: 9 })), chunk("IEND", new Uint8Array(0))]);
}

function chunk(type: string, data: Uint8Array): Uint8Array {
  const out = new Uint8Array(12 + data.length);
  const view = new DataView(out.buffer);
  view.setUint32(0, data.length);
  for (let i = 0; i < 4; i++) out[4 + i] = type.charCodeAt(i);
  out.set(data, 8);
  let crc = 0xffffffff;
  for (let i = 4; i < 8 + data.length; i++) crc = CRC_TABLE[(crc ^ out[i]!) & 0xff]! ^ (crc >>> 8);
  view.setUint32(8 + data.length, (crc ^ 0xffffffff) >>> 0);
  return out;
}
