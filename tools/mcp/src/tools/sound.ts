import { mkdirSync, writeFileSync } from "node:fs";
import { join, relative } from "node:path";
import { findGame, insideRoot } from "../engine.ts";
import { text, ToolInputError, type JsonSchema, type Tool } from "../protocol.ts";

// Mirrors SoundSynth.PresetNames and the parameter names SoundSynth.FromJson accepts (src/Ukiyo.Audio/SoundSynth.cs).
const PRESETS = ["blip", "coin", "jump", "hit", "explosion", "powerup", "laser"] as const;
const number = (description: string, minimum: number, maximum: number): JsonSchema => ({ type: "number", minimum, maximum, description });

const parameters: Record<string, JsonSchema> = {
  wave: { type: "string", enum: ["square", "saw", "sine", "triangle", "noise"] },
  frequency: number("Start frequency, Hz.", 20, 20_000),
  slide: number("Octaves per second; negative falls.", -24, 24),
  minFrequency: number("The sound stops when the slide reaches this, Hz.", 20, 20_000),
  vibratoDepth: number("Fraction of the frequency.", 0, 1),
  vibratoSpeed: number("Hz.", 0, 60),
  arpeggio: number("Frequency multiplier applied once (coin ≈ 1.335).", 0.1, 8),
  arpeggioTime: number("Seconds before the arpeggio jump.", 0, 10),
  duty: number("Square duty cycle.", 0.05, 0.95),
  attack: number("Seconds.", 0, 5),
  sustain: number("Seconds.", 0, 5),
  punch: number("Extra level at the start of sustain.", 0, 2),
  decay: number("Seconds.", 0, 5),
  lowPass: number("Low-pass cutoff, Hz; 0 = off.", 0, 22_000),
  volume: number("0..1.", 0, 1),
};

const sound: Tool = {
  name: "ukiyo_sound_generate",
  title: "Create a sound effect",
  description:
    "Writes a synthesized sound effect for a game: games/<Game>/Shared/assets/sounds/<name>.json with a preset (blip, coin, jump, hit, explosion, powerup, laser), a seed for variation and optional parameter overrides. The engine renders it at load time (no audio files): `var jump = context.LoadSound(\"sounds/jump\")` in Initialize, `tick.Audio.Play(jump)` in Update. Check it with ukiyo_run {\"audio\":true}: the report gives sound count, peak and RMS of the mixed run.",
  inputSchema: {
    type: "object",
    required: ["name", "preset"],
    additionalProperties: false,
    properties: {
      name: { type: "string", pattern: "^[a-z][a-z0-9_]{0,47}$" },
      preset: { type: "string", enum: PRESETS },
      seed: { type: "integer", minimum: 0, maximum: 2_147_483_647, default: 1 },
      overrides: { type: "object", properties: parameters, additionalProperties: false },
      game: { type: "string" },
      outDir: { type: "string" },
    },
  },
  async run(args, { root, log }) {
    const dir = typeof args.game === "string"
      ? join(findGame(root, args.game).dir, "Shared", "assets", "sounds")
      : typeof args.outDir === "string" ? insideRoot(root, args.outDir) : null;
    if (!dir) throw new ToolInputError("pass game (a game name) or outDir (a folder inside the repository)");

    const description = { preset: args.preset, seed: args.seed, ...(args.overrides as Record<string, unknown> | undefined) };
    mkdirSync(dir, { recursive: true });
    const path = join(dir, `${args.name}.json`);
    writeFileSync(path, `${JSON.stringify(description, null, 2)}\n`);
    const rel = relative(root, path);
    log("info", "sound.written", { path: rel, preset: args.preset });
    const assetName = rel.slice(rel.indexOf("/assets/") + "/assets/".length).replace(/\.json$/, "");
    return {
      content: [text(`${rel}\nIn C#: var ${args.name} = context.LoadSound("${assetName}"); … tick.Audio.Play(${args.name});\nVary it with another seed; the same preset and seed always sound the same.`)],
      structuredContent: { path: rel, description },
    };
  },
};

export default sound;
