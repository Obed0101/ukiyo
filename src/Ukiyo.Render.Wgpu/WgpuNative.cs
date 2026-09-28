using System.Runtime.InteropServices;

namespace Ukiyo.Rendering.Wgpu;

// Minimal, AOT-safe subset of the C ABI of wgpu-native v29.0.1.1 (webgpu.h + wgpu.h pinned in
// scripts/fetch-native.sh). Struct layouts are transcribed field by field from those headers; any
// version bump must re-check them. Handles are opaque pointers (nint). Enums are 32-bit, flags 64-bit.
// Dev builds load libwgpu_native.dylib; NativeAOT exports link libwgpu_native.a via DirectPInvoke.

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StringView
{
    public byte* Data;
    public nuint Length;

    public static readonly StringView Empty = default;

    public override readonly string ToString() => Data == null ? "" : Length == nuint.MaxValue
        ? Marshal.PtrToStringUTF8((nint)Data) ?? ""
        : System.Text.Encoding.UTF8.GetString(Data, (int)Length);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ChainedStruct
{
    public ChainedStruct* Next;
    public uint SType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SurfaceDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SurfaceSourceMetalLayer
{
    public ChainedStruct Chain;
    public void* Layer;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RequestAdapterOptions
{
    public ChainedStruct* NextInChain;
    public uint FeatureLevel;
    public uint PowerPreference;
    public uint ForceFallbackAdapter;
    public uint BackendType;
    public nint CompatibleSurface;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RequestAdapterCallbackInfo
{
    public ChainedStruct* NextInChain;
    public uint Mode;
    public delegate* unmanaged[Cdecl]<uint, nint, StringView, void*, void*, void> Callback;
    public void* Userdata1;
    public void* Userdata2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RequestDeviceCallbackInfo
{
    public ChainedStruct* NextInChain;
    public uint Mode;
    public delegate* unmanaged[Cdecl]<uint, nint, StringView, void*, void*, void> Callback;
    public void* Userdata1;
    public void* Userdata2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct QueueDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DeviceLostCallbackInfo
{
    public ChainedStruct* NextInChain;
    public uint Mode;
    public delegate* unmanaged[Cdecl]<nint*, uint, StringView, void*, void*, void> Callback;
    public void* Userdata1;
    public void* Userdata2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UncapturedErrorCallbackInfo
{
    public ChainedStruct* NextInChain;
    public delegate* unmanaged[Cdecl]<nint*, uint, StringView, void*, void*, void> Callback;
    public void* Userdata1;
    public void* Userdata2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DeviceDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public nuint RequiredFeatureCount;
    public uint* RequiredFeatures;
    public void* RequiredLimits;
    public QueueDescriptor DefaultQueue;
    public DeviceLostCallbackInfo DeviceLostCallbackInfo;
    public UncapturedErrorCallbackInfo UncapturedErrorCallbackInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AdapterInfo
{
    public ChainedStruct* NextInChain;
    public StringView Vendor;
    public StringView Architecture;
    public StringView Device;
    public StringView Description;
    public uint BackendType;
    public uint AdapterType;
    public uint VendorId;
    public uint DeviceId;
    public uint SubgroupMinSize;
    public uint SubgroupMaxSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SurfaceCapabilities
{
    public ChainedStruct* NextInChain;
    public ulong Usages;
    public nuint FormatCount;
    public uint* Formats;
    public nuint PresentModeCount;
    public uint* PresentModes;
    public nuint AlphaModeCount;
    public uint* AlphaModes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SurfaceConfiguration
{
    public ChainedStruct* NextInChain;
    public nint Device;
    public uint Format;
    public ulong Usage;
    public uint Width;
    public uint Height;
    public nuint ViewFormatCount;
    public uint* ViewFormats;
    public uint AlphaMode;
    public uint PresentMode;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SurfaceTexture
{
    public ChainedStruct* NextInChain;
    public nint Texture;
    public uint Status;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BufferDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public ulong Usage;
    public ulong Size;
    public uint MappedAtCreation;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ShaderModuleDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ShaderSourceWgsl
{
    public ChainedStruct Chain;
    public StringView Code;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BufferBindingLayout
{
    public ChainedStruct* NextInChain;
    public uint Type;
    public uint HasDynamicOffset;
    public ulong MinBindingSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SamplerBindingLayout
{
    public ChainedStruct* NextInChain;
    public uint Type;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TextureBindingLayout
{
    public ChainedStruct* NextInChain;
    public uint SampleType;
    public uint ViewDimension;
    public uint Multisampled;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct StorageTextureBindingLayout
{
    public ChainedStruct* NextInChain;
    public uint Access;
    public uint Format;
    public uint ViewDimension;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BindGroupLayoutEntry
{
    public ChainedStruct* NextInChain;
    public uint Binding;
    public ulong Visibility;
    public uint BindingArraySize;
    public BufferBindingLayout Buffer;
    public SamplerBindingLayout Sampler;
    public TextureBindingLayout Texture;
    public StorageTextureBindingLayout StorageTexture;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BindGroupLayoutDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public nuint EntryCount;
    public BindGroupLayoutEntry* Entries;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PipelineLayoutDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public nuint BindGroupLayoutCount;
    public nint* BindGroupLayouts;
    public uint ImmediateSize;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VertexAttribute
{
    public ChainedStruct* NextInChain;
    public uint Format;
    public ulong Offset;
    public uint ShaderLocation;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VertexBufferLayout
{
    public ChainedStruct* NextInChain;
    public uint StepMode;
    public ulong ArrayStride;
    public nuint AttributeCount;
    public VertexAttribute* Attributes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VertexState
{
    public ChainedStruct* NextInChain;
    public nint Module;
    public StringView EntryPoint;
    public nuint ConstantCount;
    public void* Constants;
    public nuint BufferCount;
    public VertexBufferLayout* Buffers;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PrimitiveState
{
    public ChainedStruct* NextInChain;
    public uint Topology;
    public uint StripIndexFormat;
    public uint FrontFace;
    public uint CullMode;
    public uint UnclippedDepth;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StencilFaceState
{
    public uint Compare;
    public uint FailOp;
    public uint DepthFailOp;
    public uint PassOp;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DepthStencilState
{
    public ChainedStruct* NextInChain;
    public uint Format;
    public uint DepthWriteEnabled;
    public uint DepthCompare;
    public StencilFaceState StencilFront;
    public StencilFaceState StencilBack;
    public uint StencilReadMask;
    public uint StencilWriteMask;
    public int DepthBias;
    public float DepthBiasSlopeScale;
    public float DepthBiasClamp;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MultisampleState
{
    public ChainedStruct* NextInChain;
    public uint Count;
    public uint Mask;
    public uint AlphaToCoverageEnabled;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ColorTargetState
{
    public ChainedStruct* NextInChain;
    public uint Format;
    public void* Blend;
    public ulong WriteMask;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FragmentState
{
    public ChainedStruct* NextInChain;
    public nint Module;
    public StringView EntryPoint;
    public nuint ConstantCount;
    public void* Constants;
    public nuint TargetCount;
    public ColorTargetState* Targets;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RenderPipelineDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public nint Layout;
    public VertexState Vertex;
    public PrimitiveState Primitive;
    public DepthStencilState* DepthStencil;
    public MultisampleState Multisample;
    public FragmentState* Fragment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Extent3D
{
    public uint Width;
    public uint Height;
    public uint DepthOrArrayLayers;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TextureDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public ulong Usage;
    public uint Dimension;
    public Extent3D Size;
    public uint Format;
    public uint MipLevelCount;
    public uint SampleCount;
    public nuint ViewFormatCount;
    public uint* ViewFormats;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BindGroupEntry
{
    public ChainedStruct* NextInChain;
    public uint Binding;
    public nint Buffer;
    public ulong Offset;
    public ulong Size;
    public nint Sampler;
    public nint TextureView;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BindGroupDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public nint Layout;
    public nuint EntryCount;
    public BindGroupEntry* Entries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Color
{
    public double R;
    public double G;
    public double B;
    public double A;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RenderPassColorAttachment
{
    public ChainedStruct* NextInChain;
    public nint View;
    public uint DepthSlice;
    public nint ResolveTarget;
    public uint LoadOp;
    public uint StoreOp;
    public Color ClearValue;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RenderPassDepthStencilAttachment
{
    public ChainedStruct* NextInChain;
    public nint View;
    public uint DepthLoadOp;
    public uint DepthStoreOp;
    public float DepthClearValue;
    public uint DepthReadOnly;
    public uint StencilLoadOp;
    public uint StencilStoreOp;
    public uint StencilClearValue;
    public uint StencilReadOnly;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RenderPassDescriptor
{
    public ChainedStruct* NextInChain;
    public StringView Label;
    public nuint ColorAttachmentCount;
    public RenderPassColorAttachment* ColorAttachments;
    public RenderPassDepthStencilAttachment* DepthStencilAttachment;
    public nint OcclusionQuerySet;
    public void* TimestampWrites;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Origin3D
{
    public uint X;
    public uint Y;
    public uint Z;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TexelCopyTextureInfo
{
    public nint Texture;
    public uint MipLevel;
    public Origin3D Origin;
    public uint Aspect;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TexelCopyBufferLayout
{
    public ulong Offset;
    public uint BytesPerRow;
    public uint RowsPerImage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TexelCopyBufferInfo
{
    public TexelCopyBufferLayout Layout;
    public nint Buffer;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BufferMapCallbackInfo
{
    public ChainedStruct* NextInChain;
    public uint Mode;
    public delegate* unmanaged[Cdecl]<uint, StringView, void*, void*, void> Callback;
    public void* Userdata1;
    public void* Userdata2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Future
{
    public ulong Id;
}

internal static class WgpuConst
{
    public const uint STypeShaderSourceWgsl = 0x02;
    public const uint STypeSurfaceSourceMetalLayer = 0x04;
    public const uint FormatBgra8Unorm = 0x1B;
    public const uint FormatBgra8UnormSrgb = 0x1C;
    public const uint FormatRgba8Unorm = 0x16;
    public const uint FormatRgba8UnormSrgb = 0x17;
    public const uint FormatDepth24Plus = 0x2E;
    public const ulong TextureUsageCopySrc = 0x01;
    public const ulong TextureUsageRenderAttachment = 0x10;
    public const ulong BufferUsageMapRead = 0x01;
    public const ulong BufferUsageCopyDst = 0x08;
    public const ulong BufferUsageIndex = 0x10;
    public const ulong BufferUsageVertex = 0x20;
    public const ulong BufferUsageUniform = 0x40;
    public const ulong ShaderStageVertex = 0x01;
    public const ulong ShaderStageFragment = 0x02;
    public const uint BufferBindingUniform = 0x02;
    public const uint CallbackModeAllowSpontaneous = 0x03;
    public const uint RequestStatusSuccess = 0x01;
    public const uint BackendMetal = 0x05;
    public const uint PresentModeFifo = 0x01;
    public const uint CompositeAlphaOpaque = 0x01;
    public const uint CompositeAlphaAuto = 0x00;
    public const uint VertexFormatFloat32x3 = 0x1E;
    public const uint VertexStepModeVertex = 0x01;
    public const uint TopologyTriangleList = 0x04;
    public const uint FrontFaceCcw = 0x01;
    public const uint CullModeBack = 0x03;
    public const uint OptionalBoolTrue = 0x01;
    public const uint CompareLess = 0x02;
    public const uint CompareAlways = 0x08;
    public const uint StencilKeep = 0x01;
    public const uint LoadOpClear = 0x02;
    public const uint StoreOpStore = 0x01;
    public const uint StoreOpDiscard = 0x02;
    public const uint IndexFormatUint32 = 0x02;
    public const uint TextureDimension2D = 0x02;
    public const uint TextureAspectAll = 0x01;
    public const uint SurfaceTextureSuccessOptimal = 0x01;
    public const uint SurfaceTextureSuccessSuboptimal = 0x02;
    public const uint SurfaceTextureOutdated = 0x04;
    public const uint SurfaceTextureLost = 0x05;
    public const uint SurfaceTextureTimeout = 0x03;
    public const uint SurfaceTextureOccluded = 0x00030001; // wgpu.h native extension
    public const ulong ColorWriteAll = 0x0F;
    public const uint FeatureLevelCore = 0x02;
    public const uint PowerHighPerformance = 0x02;
    public const uint StatusSuccess = 0x01;
    public const ulong MapModeRead = 0x01;
    public const uint MapAsyncSuccess = 0x01;
    public const uint DepthSliceUndefined = uint.MaxValue;
    public const ulong WholeSize = ulong.MaxValue;
}

internal static unsafe partial class WgpuNative
{
    private const string Library = "wgpu_native";

    [LibraryImport(Library)] public static partial nint wgpuCreateInstance(void* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuInstanceCreateSurface(nint instance, SurfaceDescriptor* descriptor);
    [LibraryImport(Library)] public static partial Future wgpuInstanceRequestAdapter(nint instance, RequestAdapterOptions* options, RequestAdapterCallbackInfo callbackInfo);
    [LibraryImport(Library)] public static partial void wgpuInstanceProcessEvents(nint instance);
    [LibraryImport(Library)] public static partial Future wgpuAdapterRequestDevice(nint adapter, DeviceDescriptor* descriptor, RequestDeviceCallbackInfo callbackInfo);
    [LibraryImport(Library)] public static partial uint wgpuAdapterGetInfo(nint adapter, AdapterInfo* info);
    [LibraryImport(Library)] public static partial void wgpuAdapterInfoFreeMembers(AdapterInfo info);
    [LibraryImport(Library)] public static partial nint wgpuDeviceGetQueue(nint device);
    [LibraryImport(Library)] public static partial uint wgpuSurfaceGetCapabilities(nint surface, nint adapter, SurfaceCapabilities* capabilities);
    [LibraryImport(Library)] public static partial void wgpuSurfaceCapabilitiesFreeMembers(SurfaceCapabilities capabilities);
    [LibraryImport(Library)] public static partial void wgpuSurfaceConfigure(nint surface, SurfaceConfiguration* configuration);
    [LibraryImport(Library)] public static partial void wgpuSurfaceUnconfigure(nint surface);
    [LibraryImport(Library)] public static partial void wgpuSurfaceGetCurrentTexture(nint surface, SurfaceTexture* surfaceTexture);
    [LibraryImport(Library)] public static partial uint wgpuSurfacePresent(nint surface);
    [LibraryImport(Library)] public static partial nint wgpuTextureCreateView(nint texture, void* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreateBuffer(nint device, BufferDescriptor* descriptor);
    [LibraryImport(Library)] public static partial void wgpuQueueWriteBuffer(nint queue, nint buffer, ulong offset, void* data, nuint size);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreateShaderModule(nint device, ShaderModuleDescriptor* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreateBindGroupLayout(nint device, BindGroupLayoutDescriptor* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreatePipelineLayout(nint device, PipelineLayoutDescriptor* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreateRenderPipeline(nint device, RenderPipelineDescriptor* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreateTexture(nint device, TextureDescriptor* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreateBindGroup(nint device, BindGroupDescriptor* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuDeviceCreateCommandEncoder(nint device, void* descriptor);
    [LibraryImport(Library)] public static partial nint wgpuCommandEncoderBeginRenderPass(nint encoder, RenderPassDescriptor* descriptor);
    [LibraryImport(Library)] public static partial void wgpuCommandEncoderCopyTextureToBuffer(nint encoder, TexelCopyTextureInfo* source, TexelCopyBufferInfo* destination, Extent3D* copySize);
    [LibraryImport(Library)] public static partial void wgpuRenderPassEncoderSetPipeline(nint pass, nint pipeline);
    [LibraryImport(Library)] public static partial void wgpuRenderPassEncoderSetBindGroup(nint pass, uint groupIndex, nint group, nuint dynamicOffsetCount, uint* dynamicOffsets);
    [LibraryImport(Library)] public static partial void wgpuRenderPassEncoderSetVertexBuffer(nint pass, uint slot, nint buffer, ulong offset, ulong size);
    [LibraryImport(Library)] public static partial void wgpuRenderPassEncoderSetIndexBuffer(nint pass, nint buffer, uint format, ulong offset, ulong size);
    [LibraryImport(Library)] public static partial void wgpuRenderPassEncoderDrawIndexed(nint pass, uint indexCount, uint instanceCount, uint firstIndex, int baseVertex, uint firstInstance);
    [LibraryImport(Library)] public static partial void wgpuRenderPassEncoderEnd(nint pass);
    [LibraryImport(Library)] public static partial nint wgpuCommandEncoderFinish(nint encoder, void* descriptor);
    [LibraryImport(Library)] public static partial void wgpuQueueSubmit(nint queue, nuint commandCount, nint* commands);
    [LibraryImport(Library)] public static partial Future wgpuBufferMapAsync(nint buffer, ulong mode, nuint offset, nuint size, BufferMapCallbackInfo callbackInfo);
    [LibraryImport(Library)] public static partial void* wgpuBufferGetConstMappedRange(nint buffer, nuint offset, nuint size);
    [LibraryImport(Library)] public static partial void wgpuBufferUnmap(nint buffer);
    [LibraryImport(Library)] public static partial uint wgpuDevicePoll(nint device, uint wait, ulong* submissionIndex);
    [LibraryImport(Library)] public static partial uint wgpuGetVersion();

    [LibraryImport(Library)] public static partial void wgpuInstanceRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuSurfaceRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuAdapterRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuDeviceRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuQueueRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuBufferRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuBufferDestroy(nint handle);
    [LibraryImport(Library)] public static partial void wgpuTextureRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuTextureDestroy(nint handle);
    [LibraryImport(Library)] public static partial void wgpuTextureViewRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuShaderModuleRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuBindGroupLayoutRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuBindGroupRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuPipelineLayoutRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuRenderPipelineRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuCommandEncoderRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuCommandBufferRelease(nint handle);
    [LibraryImport(Library)] public static partial void wgpuRenderPassEncoderRelease(nint handle);
}
