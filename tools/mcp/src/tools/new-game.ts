import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { text, ToolInputError, type Tool } from "../protocol.ts";

type Template = "2d" | "3d";
type Host = "Headless" | "Desktop" | "Browser" | "Export.Desktop";
const HOSTS: Host[] = ["Headless", "Desktop", "Browser", "Export.Desktop"];

/** The game layout every scaffold copies (docs/game-layout.md). Its identity tokens are MyGame and my-game. */
export const TEMPLATE_DIR = join("templates", "game");

const gameSource = (name: string, template: Template) =>
  template === "2d"
    ? `using System.Numerics;
using Ukiyo.Audio;
using Ukiyo.Physics;
using Ukiyo.Rendering;
using Ukiyo.UI;

namespace Ukiyo.Games.${name};

/// <summary>${name}: a 2D starting point with a sprite, a physics body, input, a HUD and one scene. Replace freely.</summary>
public sealed class ${name}Game : IGame
{
    private readonly PhysicsWorld2D _world = new() { Gravity = new Vector2(0, -30f) };
    private SceneRegistry<Body2D> _scenes = null!;
    private GameUi _ui = null!;
    private SpriteFrame _hero;
    private Body2D _player = null!;
    private SoundHandle _jump;
    private long _tick;

    public string Name => "${name}";

    public void Initialize(GameContext context)
    {
        var art = PixelArt.FromRows(
            [
                "..kkkk..",
                ".kwwwwk.",
                "kwkwwkwk",
                "kwwwwwwk",
                "kwkkkkwk",
                "kwwwwwwk",
                ".kwwwwk.",
                "..k..k..",
            ],
            new Dictionary<char, uint> { ['k'] = PixelArt.Rgb(0x111214), ['w'] = PixelArt.Rgb(0xF2F0EA) });
        _hero = SpriteFrame.Whole(context.CreateTexture(art), art.Width, art.Height);
        _ui = new GameUi(context, 320, 180);
        _jump = context.CreateSound(SoundSynth.Preset("jump").Render());
        _world.AddBox("ground", BodyType.Static, new Vector2(0, -0.5f), new Vector2(40, 1));
        _player = _world.AddBox("player", BodyType.Dynamic, new Vector2(0, 2), new Vector2(0.8f, 0.9f));
        _player.FixedRotation = true;
        _player.Friction = 0;

        // Scenes: a prefab is a function that builds an entity; a scene is data (here in code, or JSON from
        // Shared/assets via RegisterJson). Loads and patches apply between ticks (skill ukiyo-core).
        _scenes = new SceneRegistry<Body2D>(context);
        _scenes.Register(new Prefab<Body2D>("crate", args => _world.AddBox(args.Id, BodyType.Dynamic, new Vector2(args.Position.X, args.Position.Y), new Vector2(args.Float("size"))))
        {
            Parameters = [new PrefabParameter("size", 0.25, 2, 0.6)],
            Dispose = body => _world.Remove(body),
        });
        _scenes.Register(new SceneData("start", [new EntityData("crate1", "crate", new Vector3(3, 1, 0))]));
        _scenes.LoadScene("start");
    }

    public void Update(in TickInfo tick)
    {
        _tick = tick.Tick;
        var input = tick.Input;
        var velocity = _player.Velocity with { X = input.Axis(Key.Left, Key.Right, Key.A, Key.D) * 6f };
        if ((input.WasPressed(Key.Space) || input.WasPressed(Key.Up)) && _player.IsGrounded)
        {
            velocity.Y = 12f;
            tick.Audio.Play(_jump);
        }

        _player.Velocity = velocity;
        _world.Step(Simulation.TickSecondsF);

        _ui.Begin(tick);
        _ui.Label(new Vector2(8, 8), $"${name.toUpperCase()}  TICK {_tick}");
        _ui.End();
    }

    public void Extract(FrameBuilder frame)
    {
        frame.ClearColor = new Vector4(0.01f, 0.012f, 0.02f, 1f);
        frame.Camera2D = new Camera2D(new Vector2(_player.Position.X, 3f), 10f);
        frame.DrawSprite(_hero, _player.Position, Vector2.One);
        foreach (var crate in _scenes.Entities)
        {
            frame.DrawSprite(_hero, crate.Instance.Position, new Vector2(crate.Args.Float("size")), tint: new Vector4(0.85f, 0.26f, 0.18f, 1f));
        }

        _ui.Draw(frame);
    }

    public GameSnapshot Snapshot(long tick) => new(tick, _world.Poses())
    {
        Values = new Dictionary<string, double> { ["grounded"] = _player.IsGrounded ? 1 : 0 },
    };
}
`
    : `using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Games.${name};

/// <summary>${name}: a 3D starting point with one vertex-colored quad that turns with the arrow keys. Replace freely.</summary>
public sealed class ${name}Game : IGame
{
    private ResourceHandle _mesh;
    private ResourceHandle _material;
    private float _yaw;

    public string Name => "${name}";

    public void Initialize(GameContext context)
    {
        VertexPositionColor[] vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0), new Vector3(0.85f, 0.26f, 0.18f)),
            new(new Vector3(0.5f, -0.5f, 0), new Vector3(0.95f, 0.94f, 0.92f)),
            new(new Vector3(0.5f, 0.5f, 0), new Vector3(0.85f, 0.26f, 0.18f)),
            new(new Vector3(-0.5f, 0.5f, 0), new Vector3(0.07f, 0.07f, 0.08f)),
        ];
        _mesh = context.CreateMesh(new MeshData(vertices, [0, 1, 2, 0, 2, 3]));
        _material = context.CreateMaterial(new MaterialData(Vector4.One, UseVertexColors: true));
    }

    public void Update(in TickInfo tick)
    {
        _yaw += tick.Input.Axis(Key.Left, Key.Right) * 2f * Simulation.TickSecondsF;
    }

    public void Extract(FrameBuilder frame)
    {
        frame.Camera = FrameBuilder.LookAt(new Vector3(0, 0.4f, 2.4f), Vector3.Zero, MathF.PI / 3f, 0.1f, 100f);
        frame.Draw(_mesh, _material, Pose.ToMatrix());
    }

    public GameSnapshot Snapshot(long tick) => new(tick, new Dictionary<string, EntityPose> { ["quad"] = Pose });

    // DetMath, not Quaternion.CreateFromAxisAngle: libm sin/cos differ per OS, and the pose is in every snapshot.
    private EntityPose Pose => new(Vector3.Zero, DetMath.AxisAngle(Vector3.UnitY, _yaw), Vector3.One);
}
`;

const programSource = (name: string) => `// The only entry point of ${name}. Desktop, browser and headless hosts compile this exact file; each host
// assembly provides its own Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Games.${name};

return await GameApplication.RunAsync(new ${name}Game(), PlatformBootstrap.Create(args));
`;

/** The 2d template uses optional engine systems; their references go right after the host's platform reference. */
const EXTRA_REFERENCES: Record<Template, string[]> = { "2d": ["Ukiyo.Physics", "Ukiyo.UI", "Ukiyo.Audio"], "3d": [] };
const PLATFORM_REFERENCE = /^( *)<ProjectReference Include="\$\(UkiyoRoot\)src\/Ukiyo\.Platform\.\w+\/Ukiyo\.Platform\.\w+\.csproj" \/>$/m;

const kebabCase = (name: string) => name.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();

/** Files of the template, relative and with "/" separators, without build output and the synced web adapter. */
export function templateFiles(templateDir: string, prefix = ""): string[] {
  const files: string[] = [];
  for (const entry of readdirSync(join(templateDir, prefix), { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1))) {
    const path = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.isDirectory()) {
      if (entry.name === "bin" || entry.name === "obj" || path === "Www/lib") continue;
      files.push(...templateFiles(templateDir, path));
    } else if (entry.name !== ".DS_Store") {
      files.push(path);
    }
  }
  return files;
}

/** Which host a template file belongs to, or null for files every game gets (Shared/, Platform/). */
function fileHost(path: string): Host | "web" | null {
  const host = path.match(/^Hosts\/([^/]+)\//)?.[1];
  if (host) {
    if (!HOSTS.includes(host as Host)) throw new Error(`[NEW_GAME]: unknown host folder in ${TEMPLATE_DIR}: ${host}`);
    return host as Host;
  }
  return path.startsWith("Www/") ? "web" : null;
}

/**
 * The template with the game's names filled in: MyGame → Name, my-game → kebab-name, and its 2d/3d starting code in
 * Shared/Program.cs and Shared/Game.cs. Paths are relative to the new game folder.
 */
export function scaffoldFiles(templateDir: string, name: string, template: Template, hosts: Host[]): Record<string, string> {
  const kebab = kebabCase(name);
  const files: Record<string, string> = {};
  for (const path of templateFiles(templateDir)) {
    const host = fileHost(path);
    if (host === "web" ? !hosts.includes("Browser") : host !== null && !hosts.includes(host)) continue;
    let content = readFileSync(join(templateDir, path), "utf8").replaceAll("MyGame", name).replaceAll("my-game", kebab);
    if (path.endsWith(".csproj") && EXTRA_REFERENCES[template].length > 0) {
      if (!PLATFORM_REFERENCE.test(content)) throw new Error(`[NEW_GAME]: ${TEMPLATE_DIR}/${path} has no Ukiyo.Platform.* reference to extend`);
      content = content.replace(PLATFORM_REFERENCE, (line, indent: string) =>
        [line, ...EXTRA_REFERENCES[template].map((p) => `${indent}<ProjectReference Include="$(UkiyoRoot)src/${p}/${p}.csproj" />`)].join("\n"));
    }
    files[path.replaceAll("MyGame", name)] = content;
  }
  for (const required of ["Shared/Program.cs", "Shared/Game.cs"]) {
    if (!(required in files)) throw new Error(`[NEW_GAME]: ${TEMPLATE_DIR}/${required} is missing`);
  }
  files["Shared/Program.cs"] = programSource(name);
  files["Shared/Game.cs"] = gameSource(name, template);
  return files;
}

function addToSolution(root: string, projects: string[]) {
  const path = join(root, "Ukiyo.slnx");
  let solution = readFileSync(path, "utf8");
  const lines = projects.filter((p) => !solution.includes(p)).map((p) => `    <Project Path="${p}" />`);
  if (lines.length === 0) return;
  if (!solution.includes('<Folder Name="/games/">')) {
    solution = solution.replace("</Solution>", `  <Folder Name="/games/">\n  </Folder>\n</Solution>`);
  }
  solution = solution.replace(/(<Folder Name="\/games\/">\n)/, `$1${lines.join("\n")}\n`);
  writeFileSync(path, solution);
}

const newGame: Tool = {
  name: "ukiyo_new_game",
  title: "Create a game",
  description:
    "Scaffolds games/<Name> from templates/game (docs/game-layout.md): Shared/ (Program.cs, Game.cs, Scenes/ Entities/ Systems/ Lib/ gen/ assets/), Platform/{Desktop,Browser}/, Hosts/<Host>/ with one csproj per host you ask for (Headless, Desktop with the dev bridge, Browser, Export.Desktop NativeAOT) and Www/ for the browser page, and adds the hosts to Ukiyo.slnx. Template 2d: sprite + physics + input + HUD + a scene registry; 3d: a mesh turned by input. Never overwrites an existing game.",
  inputSchema: {
    type: "object",
    required: ["name"],
    additionalProperties: false,
    properties: {
      name: { type: "string", pattern: "^[A-Z][A-Za-z0-9]{1,39}$", description: "PascalCase, e.g. SkyLantern." },
      template: { type: "string", enum: ["2d", "3d"], default: "2d" },
      hosts: { type: "array", items: { type: "string", enum: HOSTS }, minItems: 1, default: HOSTS },
    },
  },
  async run(args, { root, log }) {
    const name = String(args.name);
    const template = args.template as Template;
    const hosts = [...new Set(args.hosts as Host[])];
    const dir = join(root, "games", name);
    if (existsSync(dir)) throw new ToolInputError(`games/${name} already exists`);
    const templateDir = join(root, TEMPLATE_DIR);
    if (!existsSync(templateDir)) throw new Error(`[NEW_GAME]: ${TEMPLATE_DIR} not found in ${root}; the scaffold copies it`);

    const files = scaffoldFiles(templateDir, name, template, hosts);
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(dirname(join(dir, path)), { recursive: true });
      writeFileSync(join(dir, path), content);
    }
    addToSolution(root, hosts.map((h) => `games/${name}/Hosts/${h}/${name}.${h}.csproj`));
    log("info", "game.created", { name, template, hosts });

    const created = Object.keys(files).map((f) => relative(root, join(dir, f)));
    return {
      content: [
        text(`Created games/${name} (${template}).\n${created.join("\n")}\n\nNext: ukiyo_run {"game":"${name}"} to prove it ticks, ukiyo_capture to see it, then dotnet run --project games/${name}/Hosts/Desktop for a live window (the dev bridge attaches automatically).`),
      ],
      structuredContent: { name, template, hosts, files: created },
    };
  },
};

export default newGame;
