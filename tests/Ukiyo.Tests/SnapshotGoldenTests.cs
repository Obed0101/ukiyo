using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ukiyo.Hosting;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Null;
using Xunit;

namespace Ukiyo.Tests;

/// <summary>
/// Every sample's headless output at fixed checkpoints, with its scripted input or patch log, pinned byte for byte in
/// tests/Ukiyo.Tests/golden/snapshots/&lt;Sample&gt;.txt. CI runs this on macOS arm64 and ubuntu x64, so a golden
/// recorded on one machine has to come out of the other: an operation that rounds differently per CPU or libm (a SIMD
/// reduction, a fused multiply-add, MathF.Sin) fails here instead of drifting silently. Each file has the headless
/// host's own snapshot lines (source hashes left out: they change with every edit) and a SHA-256 per resource batch
/// and per frame packet, so camera and matrix bits are covered too, not only poses.
/// Regenerate only for an intended change: UKIYO_UPDATE_GOLDEN=1 dotnet test tests/Ukiyo.Tests --filter SnapshotGoldenTests
/// </summary>
public sealed class SnapshotGoldenTests
{
    private static readonly RenderExtent Extent = new(1280, 720, 1f);
    private static readonly long[] DefaultCheckpoints = [0, 1, 60, 180, 600];

    private sealed record GoldenCase(Func<IGame> Create, long[] Checkpoints, string? Input = null, string? Patches = null);

    private static readonly Dictionary<string, GoldenCase> Cases = new(StringComparer.Ordinal)
    {
        ["CameraRig"] = new(() => new Samples.CameraRig.CameraRigGame(), [0, 1, 60, 120, 180, 300, 600]),
        ["ImportedMesh"] = new(() => new Samples.ImportedMesh.ImportedMeshGame(), DefaultCheckpoints),
        ["LanternRun"] = new(() => new Samples.LanternRun.LanternRunGame(), [0, 30, 60, 120, 180, 240, 300], Input: "samples/LanternRun/parity/input.json"),
        ["LitCube"] = new(() => new Samples.LitCube.LitCubeGame(), DefaultCheckpoints),
        ["Minimap"] = new(() => new Samples.Minimap.MinimapGame(), DefaultCheckpoints),
        ["MyGameTemplate"] = new(() => new Games.MyGame.CubeGame(), DefaultCheckpoints),
        ["Patrol"] = new(() => new Samples.Patrol.PatrolGame(), [0, 60, 250, 330, 360, 400, 450, 640], Input: "samples/Patrol/demo-input.json"),
        ["RotatingCube"] = new(() => new Samples.RotatingCube.CubeGame(), DefaultCheckpoints),
        ["SceneHop"] = new(() => new Samples.SceneHop.SceneHopGame(), [0, 12, 40, 60, 75, 80, 120, 300], Patches: "tests/Ukiyo.Tests/golden/snapshots/SceneHop.patches.json"),
        ["Sparks"] = new(() => new Samples.Sparks.SparksGame(), [0, 1, 60, 120, 300, 600]),
        ["TexturedQuad"] = new(() => new Samples.TexturedQuad.TexturedQuadGame(), DefaultCheckpoints),
    };

    public static TheoryData<string> Samples() => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Headless_output_matches_the_committed_golden_byte_for_byte(string sample)
    {
        var actual = await GoldenAsync(sample, Cases[sample]);
        var path = Path.Combine(CoreTests.RepoRoot(), "tests", "Ukiyo.Tests", "golden", "snapshots", $"{sample}.txt");
        if (Environment.GetEnvironmentVariable("UKIYO_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        var expected = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        if (expected == actual)
        {
            return;
        }

        // CI uploads this directory when the job fails, so both machines' outputs can be diffed.
        var actualPath = Path.Combine(CoreTests.RepoRoot(), "artifacts", "golden-snapshots", $"{sample}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        await File.WriteAllTextAsync(actualPath, actual);
        Assert.Fail($"{sample}: headless output differs from {Path.GetRelativePath(CoreTests.RepoRoot(), path)} at {FirstDifference(expected, actual)}; actual output in {actualPath}");
    }

    [Fact]
    public void Every_sample_has_a_golden()
    {
        var samples = Directory.GetDirectories(Path.Combine(CoreTests.RepoRoot(), "samples")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.All(samples, sample => Assert.True(Cases.ContainsKey(sample!), $"samples/{sample} has no SnapshotGoldenTests case"));
    }

    private static async Task<string> GoldenAsync(string sample, GoldenCase golden)
    {
        var root = CoreTests.RepoRoot();
        var args = new List<string> { "--checkpoints", string.Join(',', golden.Checkpoints) };
        if (golden.Input is { } input)
        {
            args.AddRange(["--input", Path.Combine(root, input)]);
        }

        if (golden.Patches is { } patches)
        {
            args.AddRange(["--patches", Path.Combine(root, patches)]);
        }

        var source = new SourceIdentity(new Dictionary<string, string>());
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await new HeadlessHost(HeadlessOptions.Parse([.. args]), stdout, stderr).RunAsync(new GameRuntime(golden.Create(), source));
        Assert.True(code == 0, $"{sample}: headless exit {code}: {stderr}");

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"# {sample}: headless snapshots at {string.Join(',', golden.Checkpoints)}");
        text.Append(golden.Input is null ? "" : $", input {golden.Input}").Append(golden.Patches is null ? "" : $", patches {golden.Patches}").Append('\n');
        text.Append("# Pinned on every CI machine. Regenerate only for an intended change: UKIYO_UPDATE_GOLDEN=1 dotnet test tests/Ukiyo.Tests --filter SnapshotGoldenTests\n");
        foreach (var line in stdout.ToString().Split('\n', StringSplitOptions.TrimEntries))
        {
            // The input-script and patch-log lines carry this machine's absolute path; the snapshots and counts do not.
            if (line.StartsWith("snapshot ", StringComparison.Ordinal) || line.StartsWith("[ukiyo] null ", StringComparison.Ordinal))
            {
                text.Append(line).Append('\n');
            }
        }

        foreach (var line in await PacketDigestsAsync(golden, source))
        {
            text.Append(line).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The same run through GameRuntime, keeping the bytes the codec writes for every resource batch and frame packet.</summary>
    private static async Task<List<string>> PacketDigestsAsync(GoldenCase golden, SourceIdentity source)
    {
        var root = CoreTests.RepoRoot();
        var renderer = new DigestRenderer();
        var runtime = new GameRuntime(golden.Create(), source)
        {
            Script = golden.Input is { } input ? InputScript.Parse(await File.ReadAllTextAsync(Path.Combine(root, input))) : null,
        };
        await runtime.StartAsync(renderer, new RenderConfiguration("golden", Extent));
        if (golden.Patches is { } patches)
        {
            foreach (var patch in ScenePatch.ParseLog(await File.ReadAllTextAsync(Path.Combine(root, patches))))
            {
                runtime.SubmitPatch(patch);
            }
        }

        var lines = new List<string>();
        foreach (var checkpoint in golden.Checkpoints)
        {
            runtime.StepTo(checkpoint);
            runtime.RenderFrame(Extent);
            lines.AddRange(renderer.TakeResourceDigests().Select(digest => $"resources {digest}"));
            lines.Add($"frame {checkpoint} {string.Join(' ', runtime.LastFrame.Select(packet => Digest(PacketCodec.Encode(packet))))}");
        }

        return lines;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var left = i < e.Length ? e[i] : "<end>";
            var right = i < a.Length ? a[i] : "<end>";
            if (left != right)
            {
                return $"line {i + 1}:\n  expected: {left}\n  actual:   {right}";
            }
        }

        return "line endings";
    }

    /// <summary>Null renderer that hashes the encoded bytes of each resource batch it receives.</summary>
    private sealed class DigestRenderer : IRenderer
    {
        private readonly NullRenderer _inner = new();
        private readonly List<string> _resources = [];

        public RenderCapabilities Capabilities => _inner.Capabilities;

        public List<string> TakeResourceDigests()
        {
            var digests = _resources.ToList();
            _resources.Clear();
            return digests;
        }

        public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken) => _inner.InitializeAsync(configuration, cancellationToken);

        public void ApplyResources(ResourceBatch resources)
        {
            _resources.Add(Digest(PacketCodec.Encode(resources)));
            _inner.ApplyResources(resources);
        }

        public void Resize(RenderExtent extent) => _inner.Resize(extent);

        public void Render(RenderPacket packet) => _inner.Render(packet);

        public void Dispose() => _inner.Dispose();
    }
}
