namespace Shan.ImageCompressor;

/// <summary>The result of a compression call. The caller owns and must dispose <see cref="Content"/>.</summary>
/// <param name="Content">The image bytes, positioned at 0.</param>
/// <param name="ContentType">MIME type of the content, e.g. <c>image/jpeg</c>.</param>
/// <param name="Extension">File extension including the dot, e.g. <c>.jpg</c>.</param>
/// <param name="WasCompressed">False when the input was already within the target size and is returned as-is.</param>
public sealed record CompressedImage(Stream Content, string ContentType, string Extension, bool WasCompressed);
