namespace Ukiyo.Rendering.Null;

/// <summary>
/// Full renderer lifecycle with no device, window or graphics library. Validates every batch and packet and
/// tracks logical resources. It has no images, so it does not implement <see cref="IRenderCapture"/>. It implements
/// every profile up to G1Lit: lit data is checked exactly like a drawing renderer checks it (profile, normals,
/// lighting section) and counted, never drawn.
/// </summary>
public sealed class NullRenderer : IRenderer
{
    private const RenderProfile HighestProfile = RenderProfile.G1Lit;

    private readonly ResourceTable<MeshData> _meshes = new();
    private readonly ResourceTable<MaterialData> _materials = new();
    private readonly ResourceTable<TextureData> _textures = new();
    private readonly PassSequencer _passes = new();
    private bool _initialized;
    private bool _disposed;

    public RenderCapabilities Capabilities { get; private set; } = new("NullRenderer", "None", "none", RenderProfile.G0Unlit, SupportsCapture: false) { SupportsSprites = true, SupportsPasses = true };

    public long FramesRendered { get; private set; }

    public long LightsSubmitted { get; private set; }

    /// <summary>Pass packets of multi-camera frames. <see cref="FramesRendered"/> counts complete frames, so a two-pass frame adds one there and two here.</summary>
    public long PassesRendered { get; private set; }

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
        RenderValidation.RequireImplemented(configuration.Profile, HighestProfile, "NullRenderer");
        Capabilities = Capabilities with { Profile = configuration.Profile };
        Extent = configuration.InitialExtent;
        _initialized = true;
        return ValueTask.CompletedTask;
    }

    public void ApplyResources(ResourceBatch resources)
    {
        RequireReady();
        RenderValidation.Validate(resources);
        RenderValidation.RequireProfile(Capabilities.Profile, resources);
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
        RenderValidation.RequireProfile(Capabilities.Profile, packet);
        var completesFrame = _passes.Accept(packet);
        try
        {
            if (packet.Pass is { } pass && !Extent.IsEmpty)
            {
                RenderValidation.RequirePassInside(pass, Extent);
            }

            foreach (var instance in packet.Instances)
            {
                RenderValidation.RequireLitInputs(_meshes.Get(instance.Mesh).Normals is not null, _materials.Get(instance.Material), packet, instance);
            }

            foreach (var sprite in packet.Sprites)
            {
                _textures.Get(sprite.Texture);
            }
        }
        catch
        {
            _passes.Abort();
            throw;
        }

        if (completesFrame)
        {
            FramesRendered++;
        }

        PassesRendered += packet.Pass is null ? 0 : 1;
        InstancesSubmitted += packet.Instances.Count;
        SpritesSubmitted += packet.Sprites.Count;
        LightsSubmitted += packet.Lighting?.Lights.Count ?? 0;
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
