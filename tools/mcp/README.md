# ukiyo MCP server

Dependency-free MCP server (JSON-RPC 2.0 over stdio, Bun) that gives coding agents the engine. stdout is the protocol;
logs are JSON lines on stderr. Tools are discovered from `src/tools/*.ts` (adding a tool is adding a file); the
module contract, error rules and truth rules are in `skills/ukiyo-tooling/SKILL.md`.

```sh
claude mcp add ukiyo -- bun /path/to/ukiyo/tools/mcp/src/server.ts   # UKIYO_ROOT overrides the repository
(cd tools/mcp && bun test)
```

## Tools

| Tool | What it does |
|---|---|
| `ukiyo_status` | Orientation: engine version, toolchain, games and hosts, skills, live sessions; per game its render profile, scene registry and import specs (below) |
| `ukiyo_run` | Headless build + run with scripted input; JSON snapshots at checkpoint ticks |
| `ukiyo_capture` | `ukiyo_run` plus CPU-renderer PNG frames at each checkpoint |
| `ukiyo_test` | C# tests (xunit) and/or the three.js adapter tests |
| `ukiyo_import` | Dev-time asset importer on `*.import.json` specs: `run` regenerates, `check` reports stale files (below) |
| `ukiyo_live` | Dev bridge of a running desktop build: pause, step, snapshot, GPU capture, input, recording |
| `ukiyo_new_game` | Scaffolds `games/<Name>` with the hosts you ask for |
| `ukiyo_sprite_generate` · `_process` · `_sheet` | Image model or existing PNG → pixel-art PNG + atlas JSON; pack atlases |
| `ukiyo_sound_generate` | Seeded synth sound effect as a JSON asset |
| `ukiyo_studio` | What the person sees in the Studio; notify them; compose layouts, themes, panels |
| `ukiyo_skills` | List or install skills and presets |

## `ukiyo_import`

Wraps `tools/Ukiyo.Import` ([docs/assets.md](../../docs/assets.md)). Input: `action` (`run` | `check`), `game`
and/or `specs` (spec files or folders, inside the repository), `timeoutSeconds` (default 300).

- Spec paths, and the `output` / `input` / `inputs` fields inside each spec, must resolve inside the repository;
  anything else is an input error before the importer runs. Specs are otherwise passed through unchanged, so new
  spec kinds the importer learns need no change here.
- Builds the importer once (`dotnet build tools/Ukiyo.Import/Ukiyo.Import.csproj -c Debug -t:Build
  -getProperty:TargetPath`, incremental), then runs the built assembly once per spec from the repository root, so
  every stale or broken spec is reported, not only the first.
- Per spec: `status` (`up-to-date`, `wrote`, `stale`, `error`; `ok` if the importer exited 0 without an `[import]`
  line this tool reads), the generated `outputs` with their summary and content hash (the full `ContentHash` from the
  generated file when it has one, else the 12-character prefix the importer prints), and for stale files the output,
  reason (`missing` / `out of date`) and regenerate command.
- Any stale or failed spec makes the result `isError`. Importer failures keep their typed code (`[IMPORT:InvalidSpec]`,
  `MissingFile`, `InvalidPng`, `DuplicateName`, `Stale`, …); exit 2 is `Usage`, a timeout `Timeout`, any other exit
  `Crash`, each with the importer's own message.
- It proves the generated code matches the sources. It does not build or run the game.

## `ukiyo_status` per game

| Field | Source |
|---|---|
| `profile` | The game's `Shared/**/*.cs`: the `RenderProfile Profile` declaration (`G1Lit` = lit 3D) with file:line, `G0Unlit` when there is none (the `IGame` default), `unknown` when it is not a literal. Read from source because the headless `[ukiyo] renderer=… profile=…` line is the renderer's profile, not the game's |
| `scenes` | Whether the shared source uses `SceneRegistry<…>` (snapshots then carry the `scene` key), with file:line |
| `imports.specs` | Every `*.import.json` under the game folder |
| `imports.check` | `ran: true` with an importer `--check` per spec when the existing importer build is newer than every file of the importer, its project references and the repository build files; otherwise `ran: false` and the reason. Status never builds; `ukiyo_import {"action":"check"}` does |
