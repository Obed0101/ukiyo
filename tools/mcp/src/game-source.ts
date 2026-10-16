// Facts about a game read from its shared C# source, without building it: the render profile it declares
// (IGame.Profile, docs/rendering-lighting.md) and whether it creates a SceneRegistry (docs/scenes-patches.md).
// The headless host cannot answer these: its "[ukiyo] renderer=… profile=…" line is the renderer's profile, not the
// game's, and the "scene" snapshot key only appears once a run reaches a checkpoint.
import { existsSync, readFileSync, readdirSync } from "node:fs";
import { join, relative } from "node:path";

const SKIPPED_DIRS = new Set(["bin", "obj", "node_modules"]);
export const DEFAULT_PROFILE = "G0Unlit";

type SourceLine = { file: string; line: number; text: string };

/** Every non-comment line of the game's Shared/**\/*.cs, with repository-relative locations. */
export function sharedSourceLines(root: string, gameDir: string): SourceLine[] {
  const lines: SourceLine[] = [];
  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1))) {
      const path = join(dir, entry.name);
      if (entry.isDirectory()) {
        if (!SKIPPED_DIRS.has(entry.name)) walk(path);
      } else if (entry.name.endsWith(".cs")) {
        readFileSync(path, "utf8").split("\n").forEach((text, i) => {
          const trimmed = text.trimStart();
          if (!trimmed.startsWith("//") && !trimmed.startsWith("*") && !trimmed.startsWith("/*")) lines.push({ file: relative(root, path), line: i + 1, text });
        });
      }
    }
  };
  const shared = join(gameDir, "Shared");
  if (existsSync(shared)) walk(shared);
  return lines;
}

export type ProfileFact = { profile: string; basis: "source"; declaredAt: string | null; note?: string };

/**
 * The profile the game declares: `RenderProfile Profile => RenderProfile.G1Lit;` (or `IGame.Profile`, or a getter on
 * the following lines). No declaration means the IGame default, G0Unlit. A declaration without a literal is reported
 * as unknown rather than guessed.
 */
export function declaredProfile(lines: SourceLine[]): ProfileFact {
  for (const [index, line] of lines.entries()) {
    const declaration = line.text.match(/\bRenderProfile\s+(?:IGame\.)?Profile\b(.*)$/);
    if (!declaration) continue;
    const where = `${line.file}:${line.line}`;
    const body = [declaration[1] ?? "", ...lines.slice(index + 1, index + 4).filter((l) => l.file === line.file).map((l) => l.text)].join(" ");
    const literal = body.match(/RenderProfile\.(\w+)/)?.[1];
    if (literal) return { profile: literal, basis: "source", declaredAt: where };
    return { profile: "unknown", basis: "source", declaredAt: where, note: "Profile is not a RenderProfile literal; read the declaration" };
  }
  return { profile: DEFAULT_PROFILE, basis: "source", declaredAt: null, note: "no Profile override in Shared/: the IGame default" };
}

export type SceneFact = { registry: boolean; at: string | null };

/**
 * Whether the game uses a SceneRegistry (its snapshots then carry the "scene" key). Points at the `new SceneRegistry<…>`
 * line when there is one, otherwise at the first line that names the type (a target-typed `new(context)`).
 */
export function sceneRegistry(lines: SourceLine[]): SceneFact {
  const line = lines.find((l) => /\bnew\s+SceneRegistry\s*</.test(l.text)) ?? lines.find((l) => /\bSceneRegistry\s*</.test(l.text));
  return { registry: Boolean(line), at: line ? `${line.file}:${line.line}` : null };
}
