# Shan.ImageCompressor

Compress JPEG, PNG, GIF and BMP images toward a target file size. **No dependencies** — the image codecs are part of the package, so there is no third-party imaging library (and no third-party licence) to worry about.

- Detects the real format from the bytes, not the client-claimed content type
- Opaque images become JPEG (quality stepped down, then dimensions); images with real transparency stay PNG
- Applies EXIF orientation, and drops all metadata (EXIF/GPS/XMP/IPTC) — a decoded-and-re-encoded image carries none
- Images already within the target size are returned untouched
- Refuses oversized images (`MaxPixels`) before allocating pixel memory, so a small file can't expand into gigabytes

```csharp
using Shan.ImageCompressor;

await using var file = File.OpenRead("photo.jpg");
var result = await ImageCompressor.CompressAsync(file, new ImageCompressionOptions { TargetSizeBytes = 500 * 1024 });

await using var output = File.Create("photo" + result.Extension);
await result.Content.CopyToAsync(output);
```

There is a synchronous version too, for code that isn't async: `ImageCompressor.Compress(stream, options)` or `ImageCompressor.Compress(bytes, options)`. Unusable options (a `ShrinkFactor` of 1, a `MinWidth` of 0, …) throw `ArgumentOutOfRangeException` before any work is done.

## What it can read

| Format | Detect | Compress |
|---|---|---|
| PNG | yes | yes — every colour type and bit depth, Adam7 interlacing, tRNS transparency |
| JPEG | yes | baseline, extended-sequential and **progressive**, 8-bit, grayscale or YCbCr/RGB, any chroma subsampling, restart intervals |
| GIF | yes | yes — the first frame, with interlacing and the transparent colour; an **animated** GIF is not flattened (see below) |
| BMP | yes | yes — 1/4/8-bit palette, 16/24/32-bit, bit masks (incl. alpha), RLE4/RLE8, top-down or bottom-up |
| WebP | yes | no |

An image that is already small enough is returned as-is whatever its format. One that needs compressing but isn't a supported kind throws `NotSupportedException` (**WebP, animated GIF, CMYK or arithmetic-coded JPEG**); a corrupt or non-image stream throws `InvalidDataException` — and nothing else, however damaged the bytes are. An animated GIF is rejected rather than squashed into a still, so a caller can keep the original, as with any other unsupported file.

Output is always JPEG (baseline, 4:2:0 chroma below quality 90, 4:4:4 from 90 up) or PNG (8-bit RGB/RGBA). Compare against libjpeg at the same quality: file sizes land within about 2%.

## Notes

- Compression is CPU-bound and synchronous after the input is read; a 12-megapixel photo takes a few seconds. Run it off a request thread if that matters, and pass a `CancellationToken` to stop between passes.
- A decoded image takes 4 bytes per pixel. `MaxPixels` (default 50 million) caps that; lower it if you run in a small container.
- Not a general-purpose imaging library: no editing, no colour management (ICC profiles are dropped, pixels are assumed sRGB).
