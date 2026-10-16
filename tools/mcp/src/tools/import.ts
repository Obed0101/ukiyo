import { relative } from "node:path";
import { assertSpecInsideRoot, buildImporter, resolveSpecTargets, runImporter, type SpecResult } from "../importer.ts";
import { failure, text, type Tool } from "../protocol.ts";

function describe(result: SpecResult): string {
  if (result.error) return `${result.status} ${result.spec}\n  ${result.error.message}`;
  const outputs = result.outputs.map((o) => `  ${o.state} ${o.output} (${o.summary})${o.contentHash ? ` contentHash ${o.contentHash}` : ""}`);
  return [`${result.status} ${result.spec}`, ...outputs, ...result.info.map((line) => `  ${line}`)].join("\n");
}

const importTool: Tool = {
  name: "ukiyo_import",
  title: "Import assets",
  description:
    "Runs the dev-time asset importer (tools/Ukiyo.Import, docs/assets.md) on import specs (*.import.json): source art in games/<Name>/art → generated C# in Shared/gen/*.g.cs that every host compiles in. " +
    "action \"run\" writes generated files whose content changed (unchanged files are left alone); \"check\" writes nothing and reports every generated file that is missing or out of date. " +
    "Pass game (every spec under it) and/or specs (spec files or folders, inside the repository). Builds the importer once (incremental), then runs it per spec, so one bad spec does not hide the others. " +
    "Reports per spec its status (up-to-date, wrote, stale, error; ok when the importer printed no line this tool reads),the generated files with their content hash, and for stale files the regenerate command. " +
    "Importer failures come back as errors with their typed code ([IMPORT:InvalidSpec], MissingFile, InvalidPng, DuplicateName, Stale, …). " +
    "It proves the generated code matches the sources; it does not build or run the game: use ukiyo_run / ukiyo_capture for that.",
  inputSchema: {
    type: "object",
    required: ["action"],
    additionalProperties: false,
    properties: {
      action: { type: "string", enum: ["run", "check"], description: "run: regenerate stale outputs. check: report stale outputs, write nothing." },
      game: { type: "string", description: "Game folder name under games/ or samples/; every *.import.json under it, e.g. TexturedQuad." },
      specs: { type: "array", items: { type: "string", minLength: 1 }, minItems: 1, maxItems: 64, description: "Spec files or folders, repository-relative or absolute inside the repository." },
      timeoutSeconds: { type: "integer", minimum: 10, maximum: 900, default: 300, description: "For the importer build plus all specs." },
    },
  },
  // CLI: `import [--check] [specs…] [--game G]`; regenerating is the default action there.
  cli: {
    positional: ["specs"],
    switches: { check: { set: { action: "check" }, description: "report stale outputs, write nothing (without it: run, regenerating them)" } },
    defaults: { action: "run" },
  },
  async run(args, { root, log, signal }) {
    const check = args.action === "check";
    const specs = resolveSpecTargets(root, { game: args.game as string | undefined, specs: args.specs as string[] | undefined });
    for (const spec of specs) assertSpecInsideRoot(root, spec);

    const deadline = performance.now() + Number(args.timeoutSeconds) * 1000;
    const remaining = () => Math.max(1000, Math.round(deadline - performance.now()));
    log("info", "import.start", { action: args.action, specs: specs.length });
    const build = await buildImporter(root, { signal, timeoutMs: remaining() });
    if (!build.ok) return failure(build.message);

    const results: SpecResult[] = [];
    for (const spec of specs) results.push(await runImporter(root, build.dll, spec, check, { signal, timeoutMs: remaining() }));

    const stale = results.filter((r) => r.status === "stale");
    const errors = results.filter((r) => r.status === "error");
    const outcome = {
      action: args.action,
      importer: relative(root, build.dll),
      buildMs: build.ms,
      specs: results,
      stale: stale.map((r) => ({ spec: r.spec, ...r.stale, message: r.error?.message })),
      errors: errors.map((r) => ({ spec: r.spec, code: r.error?.code, message: r.error?.message })),
      wrote: results.flatMap((r) => r.outputs.filter((o) => o.state === "wrote").map((o) => o.output)),
    };
    log("info", "import.done", { action: args.action, specs: results.length, stale: stale.length, errors: errors.length });

    const failed = stale.length + errors.length;
    const headline = failed === 0
      ? `${check ? "check" : "run"}: ${results.length} spec(s) ok${outcome.wrote.length ? `, wrote ${outcome.wrote.length} file(s): rebuild the game to compile them in` : ""}`
      : `${check ? "check" : "run"}: ${stale.length} stale, ${errors.length} failed of ${results.length} spec(s)${check && stale.length ? ' (ukiyo_import {"action":"run"} regenerates)' : ""}`;
    return {
      isError: failed > 0,
      content: [text(`${headline}\n${results.map(describe).join("\n")}`)],
      structuredContent: outcome,
    };
  },
};

export default importTool;
