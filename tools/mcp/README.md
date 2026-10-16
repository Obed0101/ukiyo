# ukiyo MCP server

Dependency-free MCP server (JSON-RPC 2.0 over stdio, Bun) that gives coding agents the engine. stdout is the protocol;
logs are JSON lines on stderr. Tools are discovered from `src/tools/*.ts` (adding a tool is adding a file); the
module contract, error rules and truth rules are in `skills/ukiyo-tooling/SKILL.md`.

```sh
claude mcp add ukiyo -- bun /path/to/ukiyo/tools/mcp/src/server.ts   # UKIYO_ROOT overrides the repository
(cd tools/mcp && bun test)
```

`UKIYO_ROOT` must be a real path (`realpath`). On macOS `/tmp` and the default temp dir (`/var/folders/…`) are
symlinks, and building a repository reached through one is a known problem; use `/private/tmp/…` instead. The CLI
resolves `UKIYO_ROOT` exactly like the server.

## CLI

`bun tools/mcp/src/cli.ts` runs the same tool modules without an MCP client. It finds them the way the server does
(`src/tool-registry.ts`), so every tool module is a command: `ukiyo_new_game` is `new-game`. `--help` lists every
command and its options, generated from the input schemas; `<command> --help` shows one.

```sh
bun tools/mcp/src/cli.ts status
bun tools/mcp/src/cli.ts new-game SkyLantern --template 2d --hosts Headless,Desktop
bun tools/mcp/src/cli.ts capture SkyLantern --checkpoints 0,60 --size 640x360 --out frames
bun tools/mcp/src/cli.ts test --filter FullyQualifiedName~Physics
bun tools/mcp/src/cli.ts import --check samples/TexturedQuad      # without --check: run (regenerate)
bun tools/mcp/src/cli.ts skills install --preset engine --target claude
```

- Mapping (`src/cli-args.ts`): required string and number properties are positional, in schema order; every other
  property is `--kebab-case` (`timeoutSeconds` → `--timeout-seconds`). Lists are comma-separated; booleans are flags
  (`--audio`, or `--audio false`); objects and arrays of objects are a JSON file whose content is passed inline
  (`--patches log.json`, `--input script.json`). Path values go to the tool unchanged, so they are
  repository-relative or absolute inside the repository, as in MCP. The input is then checked by the tool's own schema.
- A tool can refine its spelling with optional `cli` hints on its module (`CliHints` in protocol.ts): `ukiyo_import`
  takes its specs as positional paths, `--check` for `action: "check"` and `run` by default; `ukiyo_capture` lists
  `captures` and `passes` as output files, which `--out DIR` copies into DIR (every path under those keys, each file
  once; a single-camera result gets no `passes` key) and points the result at. A file name already in DIR
  is exit 1, with the result (still pointing at the temp files) on stdout; nothing is overwritten.
- stdout is the tool's `structuredContent` as one JSON line; logs and `[UKIYO]: …` errors go to stderr. Exit 2 is a
  usage error (unknown command or option, missing argument, unreadable FILE, or input the schema or the tool refuses
  with `ToolInputError`); exit 1 is a tool failure (`isError`, whose structured result still goes to stdout when it
  has one, or a thrown error).
- `run <Game>` and `live <action>` keep their own parsing in cli.ts (`--help` shows them) and exit 1 on every error.
- Not every tool needs a target: `import` with no specs and no `--game` is refused by the tool (exit 2), as
  `ukiyo_import {"action":"check"}` is.

### Golden transcript

`bun scripts/cli-flow.ts` drives only the CLI through `--help`, `status`, `new-game CiProbe --hosts Headless`,
`run CiProbe --checkpoints 0,60`, `capture CiProbe --checkpoints 0 --out DIR`, `import --check samples`, the removal of
the game and a `run CiProbe` that no longer finds it. It works in a temporary copy of the repository (tracked and
untracked-but-not-ignored files, without `games/`, so no `bin/`, `obj/`, `native/` or `node_modules`) and deletes it
afterwards; it fails if `git status --short` of the checkout changed. CI runs it on Linux and macOS.

The normalized transcript must equal `test/golden/cli-flow.txt`: on any difference it prints a unified diff, writes
the actual transcript to `artifacts/cli-flow/actual.txt` and exits 1. After an intended change, run
`bun scripts/cli-flow.ts --update`, read the diff of the golden file and commit it with the change.

- Normalized: the copy's root (`<repo>`), the `--out` dir (`<out>`), the CLI's `TMPDIR` (`<tmp>`), mkdtemp suffixes
  (`<id>`), every `ms` / `…Ms` duration, the dotnet patch version (`10.0.<patch>`) and the `live` sessions of status.
- Kept as is: snapshot JSON, source hashes of the scaffolded `Game.cs` / `Program.cs`, file lists, import content
  hashes, the captured PNG's size and SHA-256, `[UKIYO]:` error lines and every exit code. Other stderr (logs) is
  not recorded.

## Tools

| Tool | What it does |
|---|---|
| `ukiyo_status` | Orientation: engine version, toolchain, games and hosts, skills, live sessions; per game its render profile, scene registry and import specs (below) |
| `ukiyo_run` | Headless build + run with scripted input; JSON snapshots at checkpoint ticks |
| `ukiyo_capture` | `ukiyo_run` plus CPU-renderer PNG frames at each checkpoint; multi-camera games also each pass alone (below) |
| `ukiyo_test` | C# tests (xunit) and/or the three.js adapter tests |
| `ukiyo_import` | Dev-time asset importer on `*.import.json` specs: `run` regenerates, `check` reports stale files (below) |
| `ukiyo_live` | Dev bridge of a running desktop build: pause, step, snapshot, GPU capture (whole frame or one `pass`), input, recording, patches |
| `ukiyo_new_game` | Scaffolds `games/<Name>` from `templates/game` (docs/game-layout.md) with the hosts you ask for under `Hosts/` |
| `ukiyo_sprite_generate` · `_process` · `_sheet` | Image model or existing PNG → pixel-art PNG + atlas JSON; pack atlases |
| `ukiyo_sound_generate` | Seeded synth sound effect as a JSON asset |
| `ukiyo_studio` | What the person sees in the Studio; notify them; compose layouts, themes, panels |
| `ukiyo_skills` | List or install skills and presets |

## Multi-camera passes

A game that adds passes (minimap, split screen; [docs/rendering-passes.md](../../docs/rendering-passes.md)) is drawn
by several cameras. The tools show the composed frame as before and each camera alone at its rect size.

- `ukiyo_capture`: the headless host prints `capture-pass K primary=true|false <path>` after each checkpoint's
  `capture <path>` line, only for multi-camera frames. The result then has `passes` next to `captures`, one entry per
  checkpoint: `{"tick":60,"frame":"…/headless-tick-000060.png","passes":[{"pass":0,"primary":true,"path":"…-pass-0.png"},{"pass":1,"primary":false,"path":"…-pass-1.png"}]}`.
  The MCP content adds the pass images of the last checkpoint. Single-camera games print no such lines and their
  result has no `passes` key, byte for byte what it was before. CLI: `capture Minimap --checkpoints 0,60 --out DIR`
  copies the frames and the pass PNGs.
- `ukiyo_live {"action":"capture","pass":K}` (CLI `live capture --pass K --out FILE`) asks the dev bridge for pass K
  alone (GPU readback); the result adds `pass`, `passes` and `primaryPass`. Without `pass` it is the composed frame,
  unchanged. A pass the frame does not have is refused by the engine: an `isError` result whose text carries
  `[RENDER:OutOfRange]` and whose structured result is `{"error": …, "code": "RENDER:OutOfRange"}`; the CLI exits 1
  with that message. Every bridge refusal comes back this way (`code` is null when the message has no typed code).

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
