using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Ukiyo.Rendering.Three;

/// <summary>
/// C# facade of the browser renderer. Runs inside .NET WebAssembly; each call crosses into the JS module
/// "ukiyo-three" (web/three-adapter/ukiyo-three.js) with one binary packet. Payloads are marshalled as copied
/// Uint8Arrays in G0 (no zero-copy claim). JS errors come back as <see cref="RenderException"/>, never as dropped frames.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class ThreeJsRenderer(string canvasId) : IRenderer, IRenderCapture
{
    private readonly PassSequencer _passes = new();
    private RenderPacket? _lastPacket;
    private bool _initialized;

    public RenderCapabilities Capabilities { get; private set; } = new("ThreeJsRenderer", "uninitialized", "", RenderProfile.G0Unlit, SupportsCapture: true) { SupportsSprites = true, SupportsPasses = true };

    public long FramesRendered { get; private set; }

    public long BytesSent { get; private set; }

    public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken)
    {
        RenderValidation.RequireImplemented(configuration.Profile, RenderProfile.G1Lit, "ThreeJsRenderer");
        var info = JsonDocument.Parse(Call(() => JsInitialize(canvasId))).RootElement;
        Capabilities = new RenderCapabilities(
            "ThreeJsRenderer",
            info.GetProperty("backend").GetString() ?? "unknown",
            $"{info.GetProperty("device").GetString()} · three r{info.GetProperty("three").GetString()}",
            configuration.Profile,
            SupportsCapture: true)
        {
            SupportsSprites = true,
            SupportsPasses = true,
        };
        _initialized = true;
        Resize(configuration.InitialExtent);
        return ValueTask.CompletedTask;
    }

    public void ApplyResources(ResourceBatch resources)
    {
        RequireReady();
        RenderValidation.RequireProfile(Capabilities.Profile, resources);
        var bytes = PacketCodec.Encode(resources);
        BytesSent += bytes.Length;
        Call(() => JsApplyResources(bytes));
    }

    public void Resize(RenderExtent extent)
    {
        RequireReady();
        if (!extent.IsEmpty)
        {
            Call(() => JsResize(extent.Width, extent.Height));
        }
    }

    /// <summary>
    /// Sends one packet. The adapter draws a pass into its own target as it arrives and presents the canvas with the last
    /// pass (web/three-adapter/ukiyo-three.js); the frame rules are checked here and again in JS.
    /// </summary>
    public void Render(RenderPacket packet)
    {
        RequireReady();
        RenderValidation.RequireProfile(Capabilities.Profile, packet);
        var bytes = PacketCodec.Encode(packet);
        var completesFrame = _passes.Accept(packet);
        BytesSent += bytes.Length;
        double presentedTick;
        try
        {
            presentedTick = Call(() => JsRender(bytes));
        }
        catch
        {
            _passes.Abort();
            throw;
        }

        if ((long)presentedTick != packet.Tick)
        {
            throw new RenderException(RenderErrorCode.BackendFailure, $"adapter presented tick {presentedTick}, expected {packet.Tick}");
        }

        if (completesFrame)
        {
            _lastPacket = packet;
            FramesRendered++;
        }
    }

    /// <summary>
    /// The whole frame must be captured right after <see cref="Render"/> in the same JS task, while the drawing buffer
    /// still holds it. A pass capture reads the pass's own render target, which the adapter keeps until the next frame.
    /// </summary>
    public ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        RequireReady();
        _passes.RequireComplete();
        if (_lastPacket?.Tick != request.Tick)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"capture for tick {request.Tick} but last frame is tick {_lastPacket?.Tick}");
        }

        RenderValidation.RequireCapturablePass(request.Pass, _lastPacket.Pass?.Count ?? 1);
        var result = request.Pass is { } pass && _lastPacket.Pass is not null ? Call(() => JsCapturePass(pass)) : Call(JsCapture);
        var separator = result.IndexOf(';');
        var size = result[..separator].Split('x');
        return ValueTask.FromResult(new CaptureResult(CaptureStatus.Captured, Convert.FromBase64String(result[(separator + 1)..]), int.Parse(size[0]), int.Parse(size[1]), Capabilities.Backend));
    }

    public void Dispose()
    {
        if (_initialized)
        {
            JsDispose();
            _initialized = false;
        }
    }

    private void RequireReady()
    {
        if (!_initialized)
        {
            throw new RenderException(RenderErrorCode.NotInitialized, "ThreeJsRenderer used before InitializeAsync");
        }
    }

    /// <summary>Maps "[RENDER:Code]: message" errors raised by the adapter back to typed render errors.</summary>
    private static T Call<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (JSException error)
        {
            throw Translate(error);
        }
    }

    private static void Call(Action call)
    {
        try
        {
            call();
        }
        catch (JSException error)
        {
            throw Translate(error);
        }
    }

    private static RenderException Translate(JSException error)
    {
        var message = error.Message;
        var start = message.IndexOf("[RENDER:", StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = message.IndexOf(']', start);
            if (end > start && Enum.TryParse<RenderErrorCode>(message[(start + 8)..end], out var code))
            {
                return new RenderException(code, message[(end + 1)..].TrimStart(':', ' '));
            }
        }

        return new RenderException(RenderErrorCode.BackendFailure, $"three adapter: {message}");
    }

    [JSImport("initialize", "ukiyo-three")]
    private static partial string JsInitialize(string canvasId);

    [JSImport("applyResources", "ukiyo-three")]
    private static partial void JsApplyResources(byte[] bytes);

    [JSImport("resize", "ukiyo-three")]
    private static partial void JsResize(int width, int height);

    [JSImport("render", "ukiyo-three")]
    private static partial double JsRender(byte[] bytes);

    [JSImport("capture", "ukiyo-three")]
    private static partial string JsCapture();

    [JSImport("capturePass", "ukiyo-three")]
    private static partial string JsCapturePass(int pass);

    [JSImport("dispose", "ukiyo-three")]
    private static partial void JsDispose();
}
