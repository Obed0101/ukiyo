// Discovers image providers from this folder: adding a provider means adding a file with a default ImageProvider.
import { readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import type { ImageProvider } from "./types.ts";

const here = dirname(fileURLToPath(import.meta.url));
const SKIP = new Set(["index.ts", "types.ts"]);

let cache: Map<string, ImageProvider> | null = null;

export async function providers(): Promise<Map<string, ImageProvider>> {
  if (cache) return cache;
  const found = new Map<string, ImageProvider>();
  for (const file of readdirSync(here).filter((f) => f.endsWith(".ts") && !SKIP.has(f)).sort()) {
    const provider = ((await import(pathToFileURL(join(here, file)).href)) as { default: ImageProvider }).default;
    found.set(provider.name, provider);
  }
  cache = found;
  return found;
}

/** "auto" picks the first configured network provider (openai, then gemini), else the offline procedural one. */
export async function pickProvider(name: string): Promise<ImageProvider> {
  const all = await providers();
  if (name !== "auto") {
    const provider = all.get(name);
    if (!provider) throw new RangeError(`[IMAGE]: unknown provider ${name}. Known: ${[...all.keys()].join(", ")}`);
    if (!provider.available()) throw new RangeError(`[IMAGE]: provider ${name} is not configured (its API key env var is not set)`);
    return provider;
  }
  for (const preferred of ["openai", "gemini"]) {
    const provider = all.get(preferred);
    if (provider?.available()) return provider;
  }
  return all.get("procedural")!;
}
