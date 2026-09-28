using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static Ukiyo.Rendering.Wgpu.WgpuNative;

namespace Ukiyo.Rendering.Wgpu;

/// <summary>
/// Native renderer: C# → wgpu-native (C ABI) → Metal. The host provides a CAMetalLayer and keeps every call on
/// the thread that owns the window. Metal is required: any other effective backend fails initialization.
/// </summary>
public sealed unsafe class WgpuRenderer : IRenderer, IRenderCapture
{
    private const int UniformStride = 256;       // >= minUniformBufferOffsetAlignment on every Metal device
    private const int UniformSize = 96;          // mat4 mvp + vec4 color + vec4 params
    private const int MaxInstancesPerFrame = 1024;

    private const string Shader = """
        struct Instance {
            mvp: mat4x4<f32>,
            color: vec4<f32>,
            params: vec4<f32>,
        };
        @group(0) @binding(0) var<uniform> inst: Instance;

        struct VsOut {
            @builtin(position) position: vec4<f32>,
            @location(0) color: vec3<f32>,
        };

        @vertex
        fn vs_main(@location(0) position: vec3<f32>, @location(1) color: vec3<f32>) -> VsOut {
            var out: VsOut;
            out.position = inst.mvp * vec4<f32>(position, 1.0);
            let vertexColor = mix(vec3<f32>(1.0), color, inst.params.x);
            out.color = vertexColor * inst.color.rgb;
            return out;
        }

        @fragment
        fn fs_main(input: VsOut) -> @location(0) vec4<f32> {
            return vec4<f32>(input.color, 1.0);
        }
        """;

    private readonly nint _metalLayer;
    private readonly ResourceTable<GpuMesh> _meshes = new();
    private readonly ResourceTable<MaterialData> _materials = new();
    private readonly List<string> _deviceErrors = [];
    private GCHandle _self;
    private nint _instance, _surface, _adapter, _device, _queue;
    private nint _shader, _bindGroupLayout, _pipelineLayout, _pipeline, _uniformBuffer, _bindGroup;
    private nint _depthTexture, _depthView;
    private uint _surfaceFormat;
    private RenderExtent _extent;
    private RenderPacket? _lastPacket;
    private bool _disposed;

    public WgpuRenderer(nint metalLayer)
    {
        if (metalLayer == 0)
        {
            throw new ArgumentException("[WGPU]: a CAMetalLayer is required", nameof(metalLayer));
        }

        _metalLayer = metalLayer;
    }

    public RenderCapabilities Capabilities { get; private set; } = new("WgpuRenderer", "uninitialized", "", RenderProfile.G0Unlit, SupportsCapture: true);

    public long FramesPresented { get; private set; }

    public long SkippedFrames { get; private set; }

    public static string NativeVersion
    {
        get
        {
            var v = wgpuGetVersion();
            return $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
        }
    }

    /// <summary>Completes synchronously: wgpu-native fires adapter/device callbacks spontaneously, so the host stays on its thread.</summary>
    public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _self = GCHandle.Alloc(this);
        _extent = configuration.InitialExtent;
        _instance = Require(wgpuCreateInstance(null), "instance");

        var metal = new SurfaceSourceMetalLayer { Chain = new ChainedStruct { SType = WgpuConst.STypeSurfaceSourceMetalLayer }, Layer = (void*)_metalLayer };
        var surfaceDescriptor = new SurfaceDescriptor { NextInChain = (ChainedStruct*)&metal };
        _surface = Require(wgpuInstanceCreateSurface(_instance, &surfaceDescriptor), "surface");

        _adapter = RequestAdapter();
        var info = default(AdapterInfo);
        if (wgpuAdapterGetInfo(_adapter, &info) != WgpuConst.StatusSuccess)
        {
            throw new RenderException(RenderErrorCode.BackendFailure, "wgpuAdapterGetInfo failed");
        }

        var backendType = info.BackendType;
        var device = $"{info.Device} ({info.Description})".Trim();
        wgpuAdapterInfoFreeMembers(info);
        if (backendType != WgpuConst.BackendMetal)
        {
            throw new RenderException(RenderErrorCode.BackendFailure, $"effective backend type {backendType} is not Metal; no fallback is accepted");
        }

        _device = RequestDevice();
        _queue = Require(wgpuDeviceGetQueue(_device), "queue");
        _surfaceFormat = PickSurfaceFormat();
        ConfigureSurface();
        CreatePipeline();
        Capabilities = new RenderCapabilities("WgpuRenderer", "Metal", $"{device} · wgpu-native {NativeVersion}", configuration.Profile, SupportsCapture: true);
        ThrowOnDeviceErrors("initialize");
        return ValueTask.CompletedTask;
    }

    public void ApplyResources(ResourceBatch resources)
    {
        RequireDevice();
        RenderValidation.Validate(resources);
        foreach (var command in resources.Commands)
        {
            switch (command.Kind)
            {
                case ResourceCommandKind.CreateMesh:
                    _meshes.Add(command.Handle, UploadMesh(command.Mesh!));
                    break;
                case ResourceCommandKind.CreateMaterial:
                    _materials.Add(command.Handle, command.Material!);
                    break;
                case ResourceCommandKind.Destroy when command.Handle.Kind == ResourceKind.Mesh:
                    _meshes.Remove(command.Handle).Release();
                    break;
                case ResourceCommandKind.Destroy:
                    _materials.Remove(command.Handle);
                    break;
            }
        }

        ThrowOnDeviceErrors("apply resources");
    }

    public void Resize(RenderExtent extent)
    {
        RequireDevice();
        if (extent.IsEmpty || extent == _extent)
        {
            _extent = extent;
            return;
        }

        _extent = extent;
        ConfigureSurface();
    }

    public void Render(RenderPacket packet)
    {
        RequireDevice();
        RenderValidation.Validate(packet);
        _lastPacket = packet;
        if (_extent.IsEmpty)
        {
            SkippedFrames++;
            return;
        }

        var surfaceTexture = default(SurfaceTexture);
        wgpuSurfaceGetCurrentTexture(_surface, &surfaceTexture);
        switch (surfaceTexture.Status)
        {
            case WgpuConst.SurfaceTextureSuccessOptimal:
            case WgpuConst.SurfaceTextureSuccessSuboptimal:
                break;
            case WgpuConst.SurfaceTextureOutdated:
            case WgpuConst.SurfaceTextureLost:
                if (surfaceTexture.Texture != 0)
                {
                    wgpuTextureRelease(surfaceTexture.Texture);
                }

                SkippedFrames++;
                Console.Error.WriteLine($"[wgpu] surface status {surfaceTexture.Status}; reconfiguring (frame {packet.Sequence} skipped)");
                ConfigureSurface();
                return;
            case WgpuConst.SurfaceTextureOccluded:
            case WgpuConst.SurfaceTextureTimeout:
                // Window not visible yet (or presentation timed out): nothing to present, counted instead of hidden.
                SkippedFrames++;
                return;
            default:
                throw new RenderException(RenderErrorCode.BackendFailure, $"surface texture status {surfaceTexture.Status}");
        }

        var view = Require(wgpuTextureCreateView(surfaceTexture.Texture, null), "surface view");
        try
        {
            Encode(packet, view, _depthView, (uint)_extent.Width, (uint)_extent.Height, copy: null);
            if (wgpuSurfacePresent(_surface) != WgpuConst.StatusSuccess)
            {
                throw new RenderException(RenderErrorCode.BackendFailure, "wgpuSurfacePresent failed");
            }
        }
        finally
        {
            wgpuTextureViewRelease(view);
            wgpuTextureRelease(surfaceTexture.Texture);
        }

        wgpuDevicePoll(_device, 0, null);
        FramesPresented++;
        ThrowOnDeviceErrors($"frame {packet.Sequence}");
    }

    /// <summary>Re-renders the last packet into an offscreen texture of the same size and reads it back from the GPU.</summary>
    public ValueTask<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        RequireDevice();
        var packet = _lastPacket ?? throw new RenderException(RenderErrorCode.NotInitialized, "capture requested before any frame");
        if (packet.Tick != request.Tick)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"capture for tick {request.Tick} but last frame is tick {packet.Tick}");
        }

        var width = (uint)_extent.Width;
        var height = (uint)_extent.Height;
        var bytesPerRow = (width * 4 + 255) & ~255u;
        var target = CreateTexture(width, height, _surfaceFormat, WgpuConst.TextureUsageRenderAttachment | WgpuConst.TextureUsageCopySrc, "capture color");
        var targetView = Require(wgpuTextureCreateView(target, null), "capture view");
        var readback = CreateBuffer((ulong)bytesPerRow * height, WgpuConst.BufferUsageMapRead | WgpuConst.BufferUsageCopyDst, "capture readback");
        try
        {
            Encode(packet, targetView, _depthView, width, height, copy: (target, readback, bytesPerRow));
            var status = WgpuConst.MapAsyncSuccess + 1;
            var callback = new BufferMapCallbackInfo
            {
                Mode = WgpuConst.CallbackModeAllowSpontaneous,
                Callback = &OnBufferMapped,
                Userdata1 = &status,
            };
            wgpuBufferMapAsync(readback, WgpuConst.MapModeRead, 0, (nuint)(bytesPerRow * height), callback);
            for (var i = 0; i < 1000 && status == WgpuConst.MapAsyncSuccess + 1; i++)
            {
                wgpuDevicePoll(_device, 1, null);
            }

            if (status != WgpuConst.MapAsyncSuccess)
            {
                throw new RenderException(RenderErrorCode.BackendFailure, $"readback map status {status}");
            }

            var mapped = (byte*)wgpuBufferGetConstMappedRange(readback, 0, (nuint)(bytesPerRow * height));
            var rgba = new byte[width * height * 4];
            var bgra = _surfaceFormat is WgpuConst.FormatBgra8Unorm or WgpuConst.FormatBgra8UnormSrgb;
            for (var y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<byte>(mapped + y * bytesPerRow, (int)width * 4);
                var dst = rgba.AsSpan((int)(y * width * 4), (int)width * 4);
                for (var x = 0; x < width * 4; x += 4)
                {
                    dst[x] = bgra ? row[x + 2] : row[x];
                    dst[x + 1] = row[x + 1];
                    dst[x + 2] = bgra ? row[x] : row[x + 2];
                    dst[x + 3] = 255;
                }
            }

            wgpuBufferUnmap(readback);
            ThrowOnDeviceErrors("capture");
            return ValueTask.FromResult(new CaptureResult(CaptureStatus.Captured, PngEncoder.EncodeRgba(rgba, (int)width, (int)height), (int)width, (int)height, "Metal"));
        }
        finally
        {
            wgpuBufferDestroy(readback);
            wgpuBufferRelease(readback);
            wgpuTextureViewRelease(targetView);
            wgpuTextureDestroy(target);
            wgpuTextureRelease(target);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var mesh in _meshes.Values)
        {
            mesh.Release();
        }

        ReleaseDepth();
        Release(ref _bindGroup, wgpuBindGroupRelease);
        if (_uniformBuffer != 0)
        {
            wgpuBufferDestroy(_uniformBuffer);
        }

        Release(ref _uniformBuffer, wgpuBufferRelease);
        Release(ref _pipeline, wgpuRenderPipelineRelease);
        Release(ref _pipelineLayout, wgpuPipelineLayoutRelease);
        Release(ref _bindGroupLayout, wgpuBindGroupLayoutRelease);
        Release(ref _shader, wgpuShaderModuleRelease);
        if (_surface != 0 && _device != 0)
        {
            wgpuSurfaceUnconfigure(_surface);
        }

        Release(ref _queue, wgpuQueueRelease);
        Release(ref _device, wgpuDeviceRelease);
        Release(ref _adapter, wgpuAdapterRelease);
        Release(ref _surface, wgpuSurfaceRelease);
        Release(ref _instance, wgpuInstanceRelease);
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void Encode(RenderPacket packet, nint colorView, nint depthView, uint width, uint height, (nint Texture, nint Buffer, uint BytesPerRow)? copy)
    {
        if (packet.Instances.Count > MaxInstancesPerFrame)
        {
            throw new RenderException(RenderErrorCode.OutOfRange, $"{packet.Instances.Count} instances exceed {MaxInstancesPerFrame} per frame");
        }

        var viewProjection = ViewProjection(packet.Camera, width / (float)height);
        var uniforms = new float[packet.Instances.Count * (UniformStride / sizeof(float))];
        var draws = new (GpuMesh Mesh, uint Offset)[packet.Instances.Count];
        for (var i = 0; i < packet.Instances.Count; i++)
        {
            var instance = packet.Instances[i];
            var mesh = _meshes.Get(instance.Mesh);
            var material = _materials.Get(instance.Material);
            var slot = uniforms.AsSpan(i * (UniformStride / sizeof(float)));
            WriteMatrix(slot, instance.World * viewProjection);
            slot[16] = material.BaseColor.X;
            slot[17] = material.BaseColor.Y;
            slot[18] = material.BaseColor.Z;
            slot[19] = material.BaseColor.W;
            slot[20] = material.UseVertexColors ? 1f : 0f;
            draws[i] = (mesh, (uint)(i * UniformStride));
        }

        if (uniforms.Length > 0)
        {
            fixed (float* data = uniforms)
            {
                wgpuQueueWriteBuffer(_queue, _uniformBuffer, 0, data, (nuint)(uniforms.Length * sizeof(float)));
            }
        }

        var encoder = Require(wgpuDeviceCreateCommandEncoder(_device, null), "command encoder");
        try
        {
            var color = new RenderPassColorAttachment
            {
                View = colorView,
                DepthSlice = WgpuConst.DepthSliceUndefined,
                LoadOp = WgpuConst.LoadOpClear,
                StoreOp = WgpuConst.StoreOpStore,
                ClearValue = new Color { R = packet.ClearColor.X, G = packet.ClearColor.Y, B = packet.ClearColor.Z, A = packet.ClearColor.W },
            };
            var depth = new RenderPassDepthStencilAttachment
            {
                View = depthView,
                DepthLoadOp = WgpuConst.LoadOpClear,
                DepthStoreOp = WgpuConst.StoreOpDiscard,
                DepthClearValue = 1f,
            };
            var passDescriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &color, DepthStencilAttachment = &depth };
            var pass = Require(wgpuCommandEncoderBeginRenderPass(encoder, &passDescriptor), "render pass");
            wgpuRenderPassEncoderSetPipeline(pass, _pipeline);
            foreach (var (mesh, offset) in draws)
            {
                var dynamicOffset = offset;
                wgpuRenderPassEncoderSetBindGroup(pass, 0, _bindGroup, 1, &dynamicOffset);
                wgpuRenderPassEncoderSetVertexBuffer(pass, 0, mesh.Vertices, 0, WgpuConst.WholeSize);
                wgpuRenderPassEncoderSetIndexBuffer(pass, mesh.Indices, WgpuConst.IndexFormatUint32, 0, WgpuConst.WholeSize);
                wgpuRenderPassEncoderDrawIndexed(pass, mesh.IndexCount, 1, 0, 0, 0);
            }

            wgpuRenderPassEncoderEnd(pass);
            wgpuRenderPassEncoderRelease(pass);
            if (copy is { } c)
            {
                var source = new TexelCopyTextureInfo { Texture = c.Texture, Aspect = WgpuConst.TextureAspectAll };
                var destination = new TexelCopyBufferInfo { Buffer = c.Buffer, Layout = new TexelCopyBufferLayout { BytesPerRow = c.BytesPerRow, RowsPerImage = height } };
                var size = new Extent3D { Width = width, Height = height, DepthOrArrayLayers = 1 };
                wgpuCommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &size);
            }

            var commands = Require(wgpuCommandEncoderFinish(encoder, null), "command buffer");
            wgpuQueueSubmit(_queue, 1, &commands);
            wgpuCommandBufferRelease(commands);
        }
        finally
        {
            wgpuCommandEncoderRelease(encoder);
        }
    }

    /// <summary>Right-handed view (inverse of the camera's world transform) and a 0..1 depth perspective, as WebGPU expects.</summary>
    private static Matrix4x4 ViewProjection(CameraState camera, float aspect)
    {
        var cameraWorld = Matrix4x4.CreateFromQuaternion(camera.Rotation) * Matrix4x4.CreateTranslation(camera.Position);
        if (!Matrix4x4.Invert(cameraWorld, out var view))
        {
            throw new RenderException(RenderErrorCode.OutOfRange, "camera transform is not invertible");
        }

        return view * Matrix4x4.CreatePerspectiveFieldOfView(camera.FieldOfViewY, aspect, camera.NearPlane, camera.FarPlane);
    }

    /// <summary>Field order M11..M44 = column-major array of the column-vector matrix WGSL multiplies as mvp * v.</summary>
    private static void WriteMatrix(Span<float> destination, Matrix4x4 m)
    {
        destination[0] = m.M11; destination[1] = m.M12; destination[2] = m.M13; destination[3] = m.M14;
        destination[4] = m.M21; destination[5] = m.M22; destination[6] = m.M23; destination[7] = m.M24;
        destination[8] = m.M31; destination[9] = m.M32; destination[10] = m.M33; destination[11] = m.M34;
        destination[12] = m.M41; destination[13] = m.M42; destination[14] = m.M43; destination[15] = m.M44;
    }

    private nint RequestAdapter()
    {
        var result = new RequestResult();
        var options = new RequestAdapterOptions
        {
            FeatureLevel = WgpuConst.FeatureLevelCore,
            PowerPreference = WgpuConst.PowerHighPerformance,
            BackendType = WgpuConst.BackendMetal,
            CompatibleSurface = _surface,
        };
        var callback = new RequestAdapterCallbackInfo { Mode = WgpuConst.CallbackModeAllowSpontaneous, Callback = &OnRequestCompleted, Userdata1 = &result };
        wgpuInstanceRequestAdapter(_instance, &options, callback);
        return AwaitRequest(ref result, "adapter");
    }

    private nint RequestDevice()
    {
        var result = new RequestResult();
        var descriptor = new DeviceDescriptor
        {
            DeviceLostCallbackInfo = new DeviceLostCallbackInfo { Mode = WgpuConst.CallbackModeAllowSpontaneous, Callback = &OnDeviceLost, Userdata1 = (void*)GCHandle.ToIntPtr(_self) },
            UncapturedErrorCallbackInfo = new UncapturedErrorCallbackInfo { Callback = &OnUncapturedError, Userdata1 = (void*)GCHandle.ToIntPtr(_self) },
        };
        var callback = new RequestDeviceCallbackInfo { Mode = WgpuConst.CallbackModeAllowSpontaneous, Callback = &OnRequestCompleted, Userdata1 = &result };
        wgpuAdapterRequestDevice(_adapter, &descriptor, callback);
        return AwaitRequest(ref result, "device");
    }

    private nint AwaitRequest(ref RequestResult result, string what)
    {
        for (var i = 0; i < 1000 && !result.Done; i++)
        {
            wgpuInstanceProcessEvents(_instance);
        }

        if (!result.Done || result.Status != WgpuConst.RequestStatusSuccess || result.Handle == 0)
        {
            throw new RenderException(RenderErrorCode.BackendFailure, $"request {what} failed (status {result.Status}): {s_requestMessage}");
        }

        return result.Handle;
    }

    private uint PickSurfaceFormat()
    {
        var capabilities = default(SurfaceCapabilities);
        if (wgpuSurfaceGetCapabilities(_surface, _adapter, &capabilities) != WgpuConst.StatusSuccess || capabilities.FormatCount == 0)
        {
            throw new RenderException(RenderErrorCode.BackendFailure, "surface reports no formats");
        }

        try
        {
            var formats = new ReadOnlySpan<uint>(capabilities.Formats, (int)capabilities.FormatCount);
            foreach (var preferred in (ReadOnlySpan<uint>)[WgpuConst.FormatBgra8UnormSrgb, WgpuConst.FormatRgba8UnormSrgb])
            {
                if (formats.Contains(preferred))
                {
                    return preferred;
                }
            }

            throw new RenderException(RenderErrorCode.BackendFailure, $"no sRGB 8-bit surface format among [{string.Join(", ", formats.ToArray())}]");
        }
        finally
        {
            wgpuSurfaceCapabilitiesFreeMembers(capabilities);
        }
    }

    private void ConfigureSurface()
    {
        if (_extent.IsEmpty)
        {
            return;
        }

        var configuration = new SurfaceConfiguration
        {
            Device = _device,
            Format = _surfaceFormat,
            Usage = WgpuConst.TextureUsageRenderAttachment,
            Width = (uint)_extent.Width,
            Height = (uint)_extent.Height,
            AlphaMode = WgpuConst.CompositeAlphaAuto,
            PresentMode = WgpuConst.PresentModeFifo,
        };
        wgpuSurfaceConfigure(_surface, &configuration);
        ReleaseDepth();
        _depthTexture = CreateTexture((uint)_extent.Width, (uint)_extent.Height, WgpuConst.FormatDepth24Plus, WgpuConst.TextureUsageRenderAttachment, "depth");
        _depthView = Require(wgpuTextureCreateView(_depthTexture, null), "depth view");
    }

    private void CreatePipeline()
    {
        var code = Utf8(Shader);
        fixed (byte* codePtr = code)
        {
            var wgsl = new ShaderSourceWgsl { Chain = new ChainedStruct { SType = WgpuConst.STypeShaderSourceWgsl }, Code = new StringView { Data = codePtr, Length = (nuint)code.Length } };
            var moduleDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            _shader = Require(wgpuDeviceCreateShaderModule(_device, &moduleDescriptor), "shader module");
        }

        var entry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = WgpuConst.ShaderStageVertex | WgpuConst.ShaderStageFragment,
            Buffer = new BufferBindingLayout { Type = WgpuConst.BufferBindingUniform, HasDynamicOffset = 1, MinBindingSize = UniformSize },
        };
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &entry };
        _bindGroupLayout = Require(wgpuDeviceCreateBindGroupLayout(_device, &layoutDescriptor), "bind group layout");
        var bindGroupLayout = _bindGroupLayout;
        var pipelineLayoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &bindGroupLayout };
        _pipelineLayout = Require(wgpuDeviceCreatePipelineLayout(_device, &pipelineLayoutDescriptor), "pipeline layout");

        _uniformBuffer = CreateBuffer((ulong)(UniformStride * MaxInstancesPerFrame), WgpuConst.BufferUsageUniform | WgpuConst.BufferUsageCopyDst, "instance uniforms");
        var bindEntry = new BindGroupEntry { Binding = 0, Buffer = _uniformBuffer, Offset = 0, Size = UniformSize };
        var bindGroupDescriptor = new BindGroupDescriptor { Layout = _bindGroupLayout, EntryCount = 1, Entries = &bindEntry };
        _bindGroup = Require(wgpuDeviceCreateBindGroup(_device, &bindGroupDescriptor), "bind group");

        var vsEntry = Utf8("vs_main");
        var fsEntry = Utf8("fs_main");
        var attributes = stackalloc VertexAttribute[2];
        attributes[0] = new VertexAttribute { Format = WgpuConst.VertexFormatFloat32x3, Offset = 0, ShaderLocation = 0 };
        attributes[1] = new VertexAttribute { Format = WgpuConst.VertexFormatFloat32x3, Offset = 12, ShaderLocation = 1 };
        var vertexLayout = new VertexBufferLayout { StepMode = WgpuConst.VertexStepModeVertex, ArrayStride = VertexPositionColor.SizeInBytes, AttributeCount = 2, Attributes = attributes };
        var colorTarget = new ColorTargetState { Format = _surfaceFormat, WriteMask = WgpuConst.ColorWriteAll };
        var keep = new StencilFaceState { Compare = WgpuConst.CompareAlways, FailOp = WgpuConst.StencilKeep, DepthFailOp = WgpuConst.StencilKeep, PassOp = WgpuConst.StencilKeep };
        var depthStencil = new DepthStencilState
        {
            Format = WgpuConst.FormatDepth24Plus,
            DepthWriteEnabled = WgpuConst.OptionalBoolTrue,
            DepthCompare = WgpuConst.CompareLess,
            StencilFront = keep,
            StencilBack = keep,
        };
        fixed (byte* vs = vsEntry)
        fixed (byte* fs = fsEntry)
        {
            var fragment = new FragmentState { Module = _shader, EntryPoint = new StringView { Data = fs, Length = (nuint)fsEntry.Length }, TargetCount = 1, Targets = &colorTarget };
            var descriptor = new RenderPipelineDescriptor
            {
                Layout = _pipelineLayout,
                Vertex = new VertexState { Module = _shader, EntryPoint = new StringView { Data = vs, Length = (nuint)vsEntry.Length }, BufferCount = 1, Buffers = &vertexLayout },
                Primitive = new PrimitiveState { Topology = WgpuConst.TopologyTriangleList, FrontFace = WgpuConst.FrontFaceCcw, CullMode = WgpuConst.CullModeBack },
                DepthStencil = &depthStencil,
                Multisample = new MultisampleState { Count = 1, Mask = uint.MaxValue },
                Fragment = &fragment,
            };
            _pipeline = Require(wgpuDeviceCreateRenderPipeline(_device, &descriptor), "render pipeline");
        }
    }

    private GpuMesh UploadMesh(MeshData mesh)
    {
        var vertexBytes = (ulong)(mesh.Vertices.Length * VertexPositionColor.SizeInBytes);
        var indexBytes = (ulong)(mesh.Indices.Length * sizeof(uint));
        var vertexBuffer = CreateBuffer(vertexBytes, WgpuConst.BufferUsageVertex | WgpuConst.BufferUsageCopyDst, "mesh vertices");
        var indexBuffer = CreateBuffer(indexBytes, WgpuConst.BufferUsageIndex | WgpuConst.BufferUsageCopyDst, "mesh indices");
        var floats = new float[mesh.Vertices.Length * 6];
        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            var v = mesh.Vertices[i];
            floats[i * 6] = v.Position.X;
            floats[i * 6 + 1] = v.Position.Y;
            floats[i * 6 + 2] = v.Position.Z;
            floats[i * 6 + 3] = v.Color.X;
            floats[i * 6 + 4] = v.Color.Y;
            floats[i * 6 + 5] = v.Color.Z;
        }

        fixed (float* vertices = floats)
        fixed (uint* indices = mesh.Indices)
        {
            wgpuQueueWriteBuffer(_queue, vertexBuffer, 0, vertices, (nuint)vertexBytes);
            wgpuQueueWriteBuffer(_queue, indexBuffer, 0, indices, (nuint)indexBytes);
        }

        return new GpuMesh(vertexBuffer, indexBuffer, (uint)mesh.Indices.Length);
    }

    private nint CreateBuffer(ulong size, ulong usage, string label)
    {
        var descriptor = new BufferDescriptor { Usage = usage, Size = (size + 3) & ~3ul };
        return Require(wgpuDeviceCreateBuffer(_device, &descriptor), label);
    }

    private nint CreateTexture(uint width, uint height, uint format, ulong usage, string label)
    {
        var descriptor = new TextureDescriptor
        {
            Usage = usage,
            Dimension = WgpuConst.TextureDimension2D,
            Size = new Extent3D { Width = width, Height = height, DepthOrArrayLayers = 1 },
            Format = format,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        return Require(wgpuDeviceCreateTexture(_device, &descriptor), label);
    }

    private void ReleaseDepth()
    {
        Release(ref _depthView, wgpuTextureViewRelease);
        if (_depthTexture != 0)
        {
            wgpuTextureDestroy(_depthTexture);
        }

        Release(ref _depthTexture, wgpuTextureRelease);
    }

    private void RequireDevice()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_device == 0)
        {
            throw new RenderException(RenderErrorCode.NotInitialized, "WgpuRenderer used before InitializeAsync");
        }
    }

    private void ThrowOnDeviceErrors(string stage)
    {
        if (_deviceErrors.Count == 0)
        {
            return;
        }

        var message = string.Join(" | ", _deviceErrors);
        _deviceErrors.Clear();
        throw new RenderException(RenderErrorCode.BackendFailure, $"{stage}: {message}");
    }

    private static nint Require(nint handle, string what) =>
        handle != 0 ? handle : throw new RenderException(RenderErrorCode.BackendFailure, $"wgpu returned null {what}");

    private static void Release(ref nint handle, Action<nint> release)
    {
        if (handle != 0)
        {
            release(handle);
            handle = 0;
        }
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRequestCompleted(uint status, nint handle, StringView message, void* userdata1, void* userdata2)
    {
        var result = (RequestResult*)userdata1;
        result->Status = status;
        result->Handle = handle;
        s_requestMessage = message.ToString();
        result->Done = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBufferMapped(uint status, StringView message, void* userdata1, void* userdata2) => *(uint*)userdata1 = status;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnUncapturedError(nint* device, uint type, StringView message, void* userdata1, void* userdata2)
    {
        var text = $"uncaptured error type {type}: {message}";
        Console.Error.WriteLine($"[wgpu] {text}");
        if (GCHandle.FromIntPtr((nint)userdata1).Target is WgpuRenderer renderer)
        {
            renderer._deviceErrors.Add(text);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDeviceLost(nint* device, uint reason, StringView message, void* userdata1, void* userdata2)
    {
        if (userdata1 == null || !GCHandle.FromIntPtr((nint)userdata1).IsAllocated)
        {
            return;
        }

        if (GCHandle.FromIntPtr((nint)userdata1).Target is WgpuRenderer { _disposed: false } renderer)
        {
            renderer._deviceErrors.Add($"device lost (reason {reason}): {message}");
            Console.Error.WriteLine($"[wgpu] device lost (reason {reason}): {message}");
        }
    }

    [ThreadStatic]
    private static string? s_requestMessage;

    private struct RequestResult
    {
        public bool Done;
        public uint Status;
        public nint Handle;
    }

    private sealed class GpuMesh(nint vertices, nint indices, uint indexCount)
    {
        public nint Vertices { get; private set; } = vertices;
        public nint Indices { get; private set; } = indices;
        public uint IndexCount { get; } = indexCount;

        public void Release()
        {
            if (Vertices != 0) { wgpuBufferDestroy(Vertices); wgpuBufferRelease(Vertices); Vertices = 0; }
            if (Indices != 0) { wgpuBufferDestroy(Indices); wgpuBufferRelease(Indices); Indices = 0; }
        }
    }
}
