# Shan.ImageCompressor

Compress JPEG, PNG, GIF, BMP, TIFF, TGA, PNM and QOI images toward a target file size. **No dependencies** — the image codecs are part of the package, so there is no third-party imaging library (and no third-party licence) to worry about.

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
| PNG | yes | yes — every colour type and bit depth, Adam7 interlacing, tRNS transparency, ICC profile and EXIF |
| JPEG | yes | baseline, extended-sequential and progressive, 8-bit; grayscale, YCbCr/RGB and **CMYK/YCCK** (Adobe); any chroma subsampling; restart intervals; ICC profile and EXIF |
| GIF | yes | the first frame, with interlacing and the transparent colour; an **animated** GIF is not flattened (see below) |
| BMP | yes | 1/4/8-bit palette, 16/24/32-bit, bit masks (incl. alpha), RLE4/RLE8, top-down or bottom-up |
| TIFF | yes | classic TIFF, first image: 1/2/4/8/16-bit gray, palette, RGB(A) and CMYK; uncompressed, PackBits, LZW or Deflate; predictor 2; either byte order; orientation tag; ICC profile. Tiled, planar-separate, float, JPEG/CCITT-compressed and BigTIFF files are rejected |
| TGA | yes | colour-mapped, true-colour and gray, 8/15/16/24/32-bit, uncompressed or RLE, any origin. TGA has no magic number, so it is recognised by a strict header and size check |
| PNM | yes | PBM, PGM and PPM (P1–P6), ASCII or binary, 8 or 16-bit |
| QOI | yes | 3 and 4 channel |
| WebP | yes | no |

An image that is already small enough is returned as-is whatever its format. One that needs compressing but isn't a supported kind throws `UnsupportedImageException` (**WebP, animated GIF, arithmetic-coded JPEG, JPEG-compressed TIFF**); a corrupt or non-image stream throws `InvalidImageException` — and nothing else, however damaged the bytes are. Both derive from `ImageCompressionException` and carry the detected `Format`. An animated GIF is rejected rather than squashed into a still, so a caller can keep the original, as with any other unsupported file.

## Output, metadata and colour

```csharp
new ImageCompressionOptions
{
    Output = OutputFormat.Auto,   // Auto: JPEG, or PNG when there is real transparency. Or force Jpeg (flattened on white) / Png
    KeepMetadata = false,         // true: carry EXIF (orientation reset) and, if not converted, the ICC profile to the output
    ConvertToSrgb = true,         // convert pixels from an embedded matrix/TRC RGB profile (Adobe RGB, Display P3, ...) to sRGB
    MaxDegreeOfParallelism = 0,   // 0: use every core for resizing, colour conversion and JPEG encoding; 1 keeps a call on one core
}
```

By default all metadata is dropped — that removes GPS locations from user uploads — and an embedded RGB profile is applied to the pixels first, so wide-gamut photos keep their colours after the profile is gone. Profiles built from lookup tables (CMYK, some printer profiles) are not converted.

Output is always JPEG (baseline, 4:2:0 chroma below quality 90, 4:4:4 from 90 up) or PNG (8-bit RGB/RGBA). Compare against libjpeg at the same quality: file sizes land within about 2%.

## Notes

- Compression is CPU-bound and synchronous after the input is read; a 12-megapixel photo takes a few seconds. Run it off a request thread if that matters, and pass a `CancellationToken` to stop between passes.
- A decoded image takes 4 bytes per pixel. `MaxPixels` (default 50 million) caps that; lower it if you run in a small container.
- Not a general-purpose imaging library: no editing, no colour management (ICC profiles are dropped, pixels are assumed sRGB).
