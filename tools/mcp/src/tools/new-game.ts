import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { text, ToolInputError, type Tool } from "../protocol.ts";

type Template = "2d" | "3d";
type Host = "Headless" | "Desktop" | "Browser";

const gameSource = (name: string, template: Template) =>
  template === "2d"
    ? `using System.Numerics;
using Ukiyo.Audio;
using Ukiyo.Physics;
using Ukiyo.Rendering;
using Ukiyo.UI;

namespace Ukiyo.Games.${name};

/// <summary>${name}: a 2D starting point with a sprite, a physics body, input and a HUD. Replace freely.</summary>
public sealed class ${name}Game : IGame
{
    private readonly PhysicsWorld2D _world = new() { Gravity = new Vector2(0, -30f) };
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

    private EntityPose Pose => new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, _yaw), Vector3.One);
}
`;

const programSource = (name: string) => `// The only entry point of ${name}. Every host compiles this exact file; each host assembly provides its own
// Ukiyo.Hosting.PlatformBootstrap. No #if, no platform branches.
using Ukiyo;
using Ukiyo.Hosting;
using Ukiyo.Games.${name};

return await GameApplication.RunAsync(new ${name}Game(), PlatformBootstrap.Create(args));
`;

const extraReferences = (template: Template) =>
  template === "2d"
    ? `
    <ProjectReference Include="../../../src/Ukiyo.Physics/Ukiyo.Physics.csproj" />
    <ProjectReference Include="../../../src/Ukiyo.UI/Ukiyo.UI.csproj" />
    <ProjectReference Include="../../../src/Ukiyo.Audio/Ukiyo.Audio.csproj" />`
    : "";

const shared = `
    <UkiyoSharedSource Include="../Shared/*.cs" />
    <Compile Include="@(UkiyoSharedSource)" Link="Shared/%(Filename)%(Extension)" />`;

function hostProject(name: string, host: Host, template: Template): string {
  const kebab = name.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
  if (host === "Headless") {
    return `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>${kebab}-headless</AssemblyName>
    <RootNamespace>Ukiyo.Games.${name}</RootNamespace>
  </PropertyGroup>
  <ItemGroup>${shared}
    <ProjectReference Include="../../../src/Ukiyo.Platform.Headless/Ukiyo.Platform.Headless.csproj" />${extraReferences(template)}
  </ItemGroup>
</Project>
`;
  }
  if (host === "Desktop") {
    return `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>${kebab}-desktop</AssemblyName>
    <RootNamespace>Ukiyo.Games.${name}</RootNamespace>
    <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
    <SelfContained>false</SelfContained>
    <UkiyoDevBridge>true</UkiyoDevBridge>
  </PropertyGroup>
  <ItemGroup>${shared}
    <ProjectReference Include="../../../src/Ukiyo.Platform.Desktop/Ukiyo.Platform.Desktop.csproj" />${extraReferences(template)}
    <None Include="$(UkiyoNativeDir)lib/libwgpu_native.dylib;$(UkiyoNativeDir)lib/libSDL3.dylib" CopyToOutputDirectory="PreserveNewest" Visible="false" />
  </ItemGroup>
  <Target Name="UkiyoRequireNative" BeforeTargets="Build" Condition="!Exists('$(UkiyoNativeDir)lib/libwgpu_native.dylib')">
    <Error Text="Native dependencies missing. Run scripts/fetch-native.sh first." />
  </Target>
</Project>
`;
  }
  return `<Project Sdk="Microsoft.NET.Sdk.WebAssembly">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>${kebab}-web</AssemblyName>
    <RootNamespace>Ukiyo.Games.${name}</RootNamespace>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <UkiyoBrowserHost>true</UkiyoBrowserHost>
  </PropertyGroup>
  <ItemGroup>${shared}
    <ProjectReference Include="../../../src/Ukiyo.Platform.Browser/Ukiyo.Platform.Browser.csproj" />${extraReferences(template)}
    <SupportedPlatform Include="browser" />
    <AssemblyAttribute Include="System.Runtime.Versioning.SupportedOSPlatformAttribute">
      <_Parameter1>browser</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>
</Project>
`;
}

const browserIndex = (name: string) => `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>ukiyo · ${name} · web</title>
  <style>
    html, body { margin: 0; height: 100%; background: #000; color: #a8a8a8; font: 12px ui-monospace, monospace; }
    canvas { display: block; width: 100vw; height: 100vh; touch-action: none; }
    canvas:focus { outline: none; }
    #status { position: fixed; left: 12px; bottom: 10px; }
  </style>
  <script type="importmap">{ "imports": { "three": "./lib/three/three.module.js" } }</script>
</head>
<body>
  <canvas id="ukiyo-canvas" tabindex="0" aria-label="${name}"></canvas>
  <div id="status">loading .NET WebAssembly…</div>
  <script type="module" src="./main.js"></script>
</body>
</html>
`;

const browserMain = `import { dotnet } from "./_framework/dotnet.js";
import { boot } from "./lib/ukiyo-host.js";

const status = document.getElementById("status");
const canvas = document.getElementById("ukiyo-canvas");
canvas.focus();
boot({ dotnet, canvas, status }).catch((error) => {
  status.textContent = \`error: \${error.message}\`;
  console.error(error);
});
`;

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
    "Scaffolds games/<Name> with one shared Program.cs and game class plus the hosts you ask for (Headless, Desktop with the dev bridge, Browser), and adds them to Ukiyo.slnx. Template 2d: sprite + physics + input + HUD; 3d: a mesh turned by input. Never overwrites an existing game.",
  inputSchema: {
    type: "object",
    required: ["name"],
    additionalProperties: false,
    properties: {
      name: { type: "string", pattern: "^[A-Z][A-Za-z0-9]{1,39}$", description: "PascalCase, e.g. SkyLantern." },
      template: { type: "string", enum: ["2d", "3d"], default: "2d" },
      hosts: { type: "array", items: { type: "string", enum: ["Headless", "Desktop", "Browser"] }, minItems: 1, default: ["Headless", "Desktop", "Browser"] },
    },
  },
  async run(args, { root, log }) {
    const name = String(args.name);
    const template = args.template as Template;
    const hosts = [...new Set(args.hosts as Host[])];
    const dir = join(root, "games", name);
    if (existsSync(dir)) throw new ToolInputError(`games/${name} already exists`);

    const files: Record<string, string> = {
      [join(dir, "Shared", "Program.cs")]: programSource(name),
      [join(dir, "Shared", `${name}Game.cs`)]: gameSource(name, template),
      [join(dir, "Shared", "assets", ".gitkeep")]: "",
    };
    for (const host of hosts) files[join(dir, host, `${name}.${host}.csproj`)] = hostProject(name, host, template);
    if (hosts.includes("Browser")) {
      files[join(dir, "Browser", "wwwroot", "index.html")] = browserIndex(name);
      files[join(dir, "Browser", "wwwroot", "main.js")] = browserMain;
    }
    for (const [path, content] of Object.entries(files)) {
      mkdirSync(dirname(path), { recursive: true });
      writeFileSync(path, content);
    }
    addToSolution(root, hosts.map((h) => `games/${name}/${h}/${name}.${h}.csproj`));
    log("info", "game.created", { name, template, hosts });

    const created = Object.keys(files).map((f) => relative(root, f));
    return {
      content: [
        text(`Created games/${name} (${template}).\n${created.join("\n")}\n\nNext: ukiyo_run {"game":"${name}"} to prove it ticks, ukiyo_capture to see it, then dotnet run --project games/${name}/Desktop for a live window (the dev bridge attaches automatically).`),
      ],
      structuredContent: { name, template, hosts, files: created },
    };
  },
};

export default newGame;
