namespace Ukiyo.Rendering.Null;

/// <summary>
/// Full renderer lifecycle with no device, window or graphics library. Validates every batch and packet and
/// tracks logical resources. It has no images, so it does not implement <see cref="IRenderCapture"/>.
/// </summary>
public sealed class NullRenderer : IRenderer
{
    private readonly ResourceTable<MeshData> _meshes = new();
    private readonly ResourceTable<MaterialData> _materials = new();
    private readonly ResourceTable<TextureData> _textures = new();
    private bool _initialized;
    private bool _disposed;

    public RenderCapabilities Capabilities { get; } = new("NullRenderer", "None", "none", RenderProfile.G0Unlit, SupportsCapture: false) { SupportsSprites = true };

    public long FramesRendered { get; private set; }

    public long InstancesSubmitted { get; private set; }

    public long SpritesSubmitted { get; private set; }

    public RenderExtent Extent { get; private set; }

    public int LiveMeshes => _meshes.Count;

    public int LiveMaterials => _materials.Count;

    public int LiveTextures => _textures.Count;

    public RenderPacket? LastPacket { get; private set; }

    public ValueTask InitializeAsync(RenderConfiguration configuration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Extent = configuration.InitialExtent;
        _initialized = true;
        return ValueTask.CompletedTask;
    }

    public void ApplyResources(ResourceBatch resources)
    {
        RequireReady();
        RenderValidation.Validate(resources);
        foreach (var command in resources.Commands)
        {
            switch (command.Kind)
            {
                case ResourceCommandKind.CreateMesh:
                    _meshes.Add(command.Handle, command.Mesh!);
                    break;
                case ResourceCommandKind.CreateMaterial:
                    _materials.Add(command.Handle, command.Material!);
                    break;
                case ResourceCommandKind.CreateTexture:
                    _textures.Add(command.Handle, command.Texture!);
                    break;
                case ResourceCommandKind.Destroy when command.Handle.Kind == ResourceKind.Mesh:
                    _meshes.Remove(command.Handle);
                    break;
                case ResourceCommandKind.Destroy when command.Handle.Kind == ResourceKind.Texture:
                    _textures.Remove(command.Handle);
                    break;
                case ResourceCommandKind.Destroy:
                    _materials.Remove(command.Handle);
                    break;
            }
        }
    }

    public void Resize(RenderExtent extent)
    {
        RequireReady();
        Extent = extent;
    }

    public void Render(RenderPacket packet)
    {
        RequireReady();
        RenderValidation.Validate(packet);
        foreach (var instance in packet.Instances)
        {
            _meshes.Get(instance.Mesh);
            _materials.Get(instance.Material);
        }

        foreach (var sprite in packet.Sprites)
        {
            _textures.Get(sprite.Texture);
        }

        FramesRendered++;
        InstancesSubmitted += packet.Instances.Count;
        SpritesSubmitted += packet.Sprites.Count;
        LastPacket = packet;
    }

    public void Dispose()
    {
        _disposed = true;
        _initialized = false;
    }

    private void RequireReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new RenderException(RenderErrorCode.NotInitialized, "NullRenderer used before InitializeAsync");
        }
    }
}
