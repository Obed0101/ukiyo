namespace Ukiyo.Import;

public enum ImportErrorCode
{
    /// <summary>The import spec is missing, not JSON, or has a field of the wrong type or range.</summary>
    InvalidSpec = 1,

    /// <summary>A source file named by the spec does not exist.</summary>
    MissingFile = 2,

    /// <summary>A source image is not a PNG the engine's decoder accepts.</summary>
    InvalidPng = 3,

    /// <summary>An atlas JSON next to a PNG is not valid JSON or lacks a required field.</summary>
    InvalidAtlas = 4,

    /// <summary>A frame rectangle is empty or reaches outside its image.</summary>
    FrameOutOfBounds = 5,

    /// <summary>Two frames or two animations of one atlas share a name.</summary>
    DuplicateName = 6,

    /// <summary>A class, namespace, frame or animation name cannot be used in generated code.</summary>
    InvalidName = 7,

    /// <summary>The packed atlas would exceed the texture size limit or the spec's maxWidth.</summary>
    AtlasTooLarge = 8,

    /// <summary>An animation references a frame that is not in the atlas.</summary>
    UnknownFrame = 9,

    /// <summary><c>--check</c>: the generated file on disk differs from what the sources produce now.</summary>
    Stale = 10,
}

/// <summary>Typed importer failure, prefixed <c>[IMPORT:Code]</c> like the engine's render errors.</summary>
public sealed class ImportException(ImportErrorCode code, string message) : Exception($"[IMPORT:{code}]: {message}")
{
    public ImportErrorCode Code { get; } = code;
}
