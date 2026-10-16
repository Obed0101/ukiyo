using System.Numerics;
using Ukiyo.Rendering;

namespace Ukiyo.Import;

/// <summary>
/// One glTF 2.0 model: a <c>.gltf</c> (JSON) or <c>.glb</c> (binary) file and the path recorded in the generated header.
/// <paramref name="ReadResource"/> returns the bytes of a buffer the model references by relative URI (a <c>.bin</c>
/// next to a <c>.gltf</c>), or null when it does not exist; every resource read is hashed into the header too.
/// </summary>
public sealed record MeshSource(string Path, byte[] Bytes, Func<string, byte[]?>? ReadResource = null);

/// <summary>Where vertex normals come from. Lit materials need normals; unlit ones ignore them.</summary>
public enum NormalsMode
{
    /// <summary>Every primitive must have a NORMAL attribute; otherwise <see cref="ImportErrorCode.MissingNormals"/>.</summary>
    Required = 1,

    /// <summary>Ignore source normals and give every triangle its own three vertices with the face normal (faceted look).</summary>
    Flat = 2,

    /// <summary>Drop normals: the mesh is G0 data for unlit materials (a lit material on it is <c>InvalidPacket</c>).</summary>
    None = 3,
}

/// <summary>What to do with glTF materials marked <c>doubleSided</c>, which the engine (back faces culled) cannot draw.</summary>
public enum DoubleSidedMode
{
    /// <summary>Fail with <see cref="ImportErrorCode.UnsupportedFeature"/>, so a surface never silently disappears from behind.</summary>
    Reject = 1,

    /// <summary>Import the faces once; their back sides are culled like every other mesh.</summary>
    Cull = 2,
}

/// <summary>Which models to import and how to name the generated class.</summary>
public sealed record MeshImportRequest(string ClassName, string Namespace, IReadOnlyList<MeshSource> Sources)
{
    public NormalsMode Normals { get; init; } = NormalsMode.Required;

    public DoubleSidedMode DoubleSided { get; init; } = DoubleSidedMode.Reject;

    /// <summary>The command that regenerates the file, written into its header. Must not depend on the machine.</summary>
    public string? RegenerateCommand { get; init; }
}

/// <summary>
/// One imported mesh in engine conventions, exactly the data the generated code rebuilds: name (the model's file name),
/// the C# member that exposes it, the <see cref="MeshData"/>, its bounds and its content hash.
/// </summary>
public sealed record ImportedMesh(string Name, string MemberName, MeshData Data, Vector3 BoundsMin, Vector3 BoundsMax, string ContentHash)
{
    public int VertexCount => Data.Vertices.Length;

    public int TriangleCount => Data.Indices.Length / 3;
}

/// <summary>Generated source plus the meshes it encodes (sorted by name), so callers and tests can check one against the other.</summary>
public sealed record MeshImportResult(IReadOnlyList<ImportedMesh> Meshes, string ContentHash, IReadOnlyList<(string Path, string Sha256)> SourceHashes, string Code);
