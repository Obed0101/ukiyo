import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

export type ProviderConfig = { model: string; endpoint: string; keyEnv: string; size?: string };

export type SpriteConfig = {
  background: string;
  templates: Record<"single" | "strip" | "tile", string>;
  styles: Record<string, string>;
  palettes: Record<string, string[]>;
  providers: Record<"openai" | "gemini", ProviderConfig>;
};

const CONFIG_PATH = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "config", "sprite-prompts.json");

export function spriteConfig(): SpriteConfig {
  return JSON.parse(readFileSync(CONFIG_PATH, "utf8")) as SpriteConfig;
}

export function fillTemplate(template: string, values: Record<string, string>): string {
  return template.replace(/\{(\w+)\}/g, (match, key: string) => values[key] ?? match).replace(/\s+/g, " ").trim();
}
