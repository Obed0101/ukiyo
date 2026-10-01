# ukiyo skills

Per-area guides that teach a coding agent to use the engine: the exact APIs, the rules that keep games deterministic,
and how to prove each kind of change. Each folder is a standard skill (`SKILL.md` with `name` and `description`
frontmatter) that Claude Code, Codex-style agents and other skill-aware tools load on demand.

| Skill | Area |
|---|---|
| `ukiyo-core` | game loop, layout, assets, determinism, how to prove a change — load first |
| `ukiyo-sprites` | 2D drawing, textures, sprite sheets, animation, 2D camera |
| `ukiyo-sprite-generation` | art from image models (OpenAI, Gemini) or procedural, cleaned into pixel art |
| `ukiyo-physics` | 2D rigid bodies, platformer control, sensors, raycasts, layers |
| `ukiyo-input` | keys, pointer, axes, input scripts, recording and replay |
| `ukiyo-ui` | HUDs and menus with `GameUi`, focus, themes |
| `ukiyo-audio` | synthesized sound effects, WAV assets, playing sounds, proving them headless |
| `ukiyo-testing` | tests, snapshots, CPU captures, parity — what each proves |
| `ukiyo-live` | pause, step, capture and inject input into a running build |
| `ukiyo-new-game` | from idea to a playable, tested game on every target |
| `ukiyo-rendering` | 3D drawing, conventions, renderers, the binary protocol |
| `ukiyo-studio` | the person's Studio: panels, layouts, themes as files; what they are looking at; notices |

For working on ukiyo itself:

| Skill | Area |
|---|---|
| `ukiyo-engine` | architecture, boundaries, build rules, where a change goes, evidence — load first |
| `ukiyo-renderer-dev` | renderer contract, protocol on both sides, wgpu and three.js internals, cross-renderer parity |
| `ukiyo-platform-dev` | hosts (headless, SDL3, WebAssembly), native interop, flags, export profiles, dev bridge commands |
| `ukiyo-tooling` | MCP tools (module contract, schemas, errors, tests) and writing skills and presets |

## Presets

`presets.json` groups them the way the engine is assembled: **simple**, **balanced**, **complete** for making games,
and **engine** for working on the engine, its tools and the Studio.

```bash
# through the MCP server (registered in .mcp.json at the repository root)
ukiyo_skills {"action":"install","preset":"balanced","target":"both"}

# or by hand
mkdir -p .claude/skills && cp -R skills/ukiyo-core skills/ukiyo-sprites .claude/skills/
```

Skills describe the code as it is. When an API changes, update the skill in the same change.
