using System.Globalization;
using System.Text;
using System.Text.Json;
using Ukiyo.Rendering;

namespace Ukiyo;

/// <summary>A scene patch as submitted: the runtime tick at submission and the patch's own JSON.</summary>
public readonly record struct PatchSubmission(long AtTick, string PatchJson);

/// <summary>The drawing-buffer size the runtime latched input with from <paramref name="Tick"/> on.</summary>
public readonly record struct ViewportChange(long Tick, RenderExtent Extent);

/// <summary>
/// Everything that reached the simulation from outside, from tick 0: latched input events, scene patch submissions and
/// the viewport input was latched with. The engine is deterministic (fixed 60 Hz ticks, no wall clock in
/// <c>Update</c>), so a fresh runtime of the same code given this log reaches the same state at the same tick; that is
/// how the Studio's controlled reload restores a game after a rebuild (<see cref="GameRuntime.RestoreFrom"/>).
/// Pointer moves that the next pointer event in the same latch overwrites are not kept (they cannot change any state).
/// The log is bounded: past <see cref="MaxEntries"/> it stops growing and <see cref="Truncated"/> says it is no longer a
/// complete history, so a restore refuses instead of replaying a different game.
/// </summary>
public sealed class ReplayLog
{
    public const int MaxEntries = 2_000_000;
    public const int Version = 1;

    private readonly List<ScriptedInput> _input = [];
    private readonly List<PatchSubmission> _patches = [];
    private readonly List<ViewportChange> _viewports = [];

    public IReadOnlyList<ScriptedInput> Input => _input;

    public IReadOnlyList<PatchSubmission> Patches => _patches;

    public IReadOnlyList<ViewportChange> Viewports => _viewports;

    public bool Truncated { get; private set; }

    public int Count => _input.Count + _patches.Count + _viewports.Count;

    internal void AddInput(long tick, InputEvent inputEvent)
    {
        if (HasRoom())
        {
            _input.Add(new ScriptedInput(tick, inputEvent));
        }
    }

    internal void AddPatch(long atTick, string patchJson)
    {
        if (HasRoom())
        {
            _patches.Add(new PatchSubmission(atTick, patchJson));
        }
    }

    internal void NoteViewport(long tick, RenderExtent extent)
    {
        if ((_viewports.Count == 0 || _viewports[^1].Extent != extent) && HasRoom())
        {
            _viewports.Add(new ViewportChange(tick, extent));
        }
    }

    private bool HasRoom()
    {
        if (Count < MaxEntries)
        {
            return true;
        }

        Truncated = true;
        return false;
    }

    /// <summary>
    /// JSON for a process that will replay to <paramref name="tick"/>. <paramref name="pending"/> are events pushed but
    /// not latched yet (they belong to <paramref name="tick"/> and are queued again after the restore).
    /// </summary>
    public string ToJson(long tick, IEnumerable<InputEvent> pending)
    {
        var events = _input.Concat(pending.Select(e => new ScriptedInput(tick, e)));
        var json = new StringBuilder();
        json.Append("{\"version\":").Append(Version).Append(",\"tick\":").Append(tick.ToString(CultureInfo.InvariantCulture))
            .Append(",\"truncated\":").Append(Truncated ? "true" : "false");
        json.Append(",\"viewports\":[");
        json.AppendJoin(',', _viewports.Select(v => string.Create(CultureInfo.InvariantCulture, $"{{\"tick\":{v.Tick},\"width\":{v.Extent.Width},\"height\":{v.Extent.Height},\"pixelRatio\":{v.Extent.PixelRatio}}}")));
        json.Append("],\"input\":").Append(new InputScript(events).ToJson().ReplaceLineEndings(string.Empty));
        json.Append(",\"patches\":[");
        json.AppendJoin(',', _patches.Select(p => string.Create(CultureInfo.InvariantCulture, $"{{\"at\":{p.AtTick},\"patch\":{p.PatchJson}}}")));
        json.Append("]}");
        return json.ToString();
    }

    /// <summary>Parses <see cref="ToJson"/> output. Returns the log and the tick it replays to.</summary>
    public static (ReplayLog Log, long Tick) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.GetInt32() != Version)
        {
            throw new FormatException($"[REPLAY]: not a replay log version {Version}");
        }

        var log = new ReplayLog { Truncated = root.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean() };
        var tick = root.GetProperty("tick").GetInt64();
        foreach (var item in root.GetProperty("viewports").EnumerateArray())
        {
            log._viewports.Add(new ViewportChange(item.GetProperty("tick").GetInt64(),
                new RenderExtent(item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32(), item.GetProperty("pixelRatio").GetSingle())));
        }

        log._input.AddRange(InputScript.Parse(root.GetProperty("input").GetRawText()).Events);
        foreach (var item in root.GetProperty("patches").EnumerateArray())
        {
            log._patches.Add(new PatchSubmission(item.GetProperty("at").GetInt64(), item.GetProperty("patch").GetRawText()));
        }

        return (log, tick);
    }
}
