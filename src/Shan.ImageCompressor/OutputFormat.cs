namespace Shan.ImageCompressor;

/// <summary>The format <see cref="ImageCompressor"/> writes a compressed image in.</summary>
public enum OutputFormat
{
    /// <summary>JPEG for opaque images, PNG for images with real transparency (JPEG would lose it).</summary>
    Auto = 0,

    /// <summary>Always JPEG. Transparent areas are flattened onto white.</summary>
    Jpeg,

    /// <summary>Always PNG, lossless; the file shrinks by reducing dimensions only.</summary>
    Png
}
