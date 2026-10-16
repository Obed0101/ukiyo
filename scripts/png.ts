// Minimal PNG reader for evidence scripts: 8-bit RGB or RGBA, non-interlaced (what every ukiyo host writes).
import { readFileSync } from "node:fs";
import { inflateSync } from "node:zlib";

export type Image = { width: number; height: number; channels: number; pixels: Uint8Array };

/** Decodes to raw rows without the filter bytes. Throws "[PNG]: …" on any other layout. */
export function decodePng(path: string): Image {
  const file = readFileSync(path);
  const width = file.readUInt32BE(16), height = file.readUInt32BE(20);
  const [depth, colorType, , , interlace] = file.subarray(24, 29);
  if (depth !== 8 || (colorType !== 2 && colorType !== 6) || interlace !== 0) throw new Error(`[PNG]: unsupported layout in ${path}`);
  const idat: Buffer[] = [];
  for (let at = 8; at < file.length;) {
    const length = file.readUInt32BE(at);
    if (file.toString("latin1", at + 4, at + 8) === "IDAT") idat.push(file.subarray(at + 8, at + 8 + length));
    at += 12 + length;
  }
  const raw = inflateSync(Buffer.concat(idat));
  const channels = colorType === 6 ? 4 : 3, stride = width * channels;
  const pixels = new Uint8Array(stride * height);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)]!, row = y * stride, source = y * (stride + 1) + 1;
    for (let i = 0; i < stride; i++) {
      const left = i >= channels ? pixels[row + i - channels]! : 0;
      const up = y > 0 ? pixels[row - stride + i]! : 0;
      const corner = y > 0 && i >= channels ? pixels[row - stride + i - channels]! : 0;
      const p = left + up - corner, pa = Math.abs(p - left), pb = Math.abs(p - up), pc = Math.abs(p - corner);
      const predicted = [0, left, up, (left + up) >> 1, pa <= pb && pa <= pc ? left : pb <= pc ? up : corner][filter];
      if (predicted === undefined) throw new Error(`[PNG]: bad filter ${filter} in ${path}`);
      pixels[row + i] = (raw[source + i]! + predicted) & 255;
    }
  }
  return { width, height, channels, pixels };
}
