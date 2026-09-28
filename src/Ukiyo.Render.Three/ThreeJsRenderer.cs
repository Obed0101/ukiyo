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
    private RenderPacket? _lastPacket;
    private bool _initialized;

    public RenderCapabilities Capabilities { get; private set; } = new("ThreeJsRenderer", "uninitialized", "", RenderProfile.G0Unlit, SupportsCapture: true);

    public long FramesRendered { get; private set; }

    public long BytesSent { get; private set; }

    public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken)
    {
        var info = JsonDocument.Parse(Call(() => JsInitialize(canvasId))).RootElement;
        Capabilities = new RenderCapabilities(
            "ThreeJsRenderer",
            info.GetProperty("backend").GetString() ?? "unknown",
            $"{info.GetProperty("device").GetString()} · three r{info.GetProperty("three").GetString()}",
            configuration.Profile,
            SupportsCapture: true);
        _initialized = true;
        Resize(configuration.InitialExtent);
        return ValueTask.CompletedTask;
    }

    public void ApplyResources(ResourceBatch resources)
    {
        RequireReady();
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

    public void Render(RenderPacket packet)
    {
        RequireReady();
        var bytes = PacketCodec.Encode(packet);
        BytesSent += bytes.Length;
        var presentedTick = Call(() => JsRender(bytes));
        if ((long)presentedTick != packet.Tick)
        {
            throw new RenderException(RenderErrorCode.BackendFailure, $"adapter presented tick {presentedTick}, expected {packet.Tick}");
        }

        _lastPacket = packet;
        FramesRendered++;
    }

    /// <summary>Must run right after <see cref="Render"/> in the same JS task, while the drawing buffer still holds the frame.</summary>
    public ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        RequireReady();
        if (_lastPacket?.Tick != request.Tick)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"capture for tick {request.Tick} but last frame is tick {_lastPacket?.Tick}");
        }

        var result = Call(JsCapture);
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

    [JSImport("dispose", "ukiyo-three")]
    private static partial void JsDispose();
}
