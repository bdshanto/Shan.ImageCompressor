# Shan.ImageCompressor

Compress JPEG, PNG, GIF (animated too), WebP, BMP, TIFF, TGA, PNM and QOI images toward a target file size, and resize, crop, rotate and mirror them on the way. **No dependencies** — the image codecs are part of the package, so there is no third-party imaging library (and no third-party licence) to worry about.

- Detects the real format from the bytes, not the client-claimed content type
- Opaque images become JPEG (quality stepped down, then dimensions); images with real transparency stay PNG
- Applies EXIF orientation, and drops all metadata (EXIF/GPS/XMP/IPTC) — a decoded-and-re-encoded image carries none
- **Animated GIFs stay animated**: every frame, its timing and the loop count are kept, and the palette and pixels are reduced until the file fits
- **WebP** input, lossy and lossless (including transparency), decoded bit-for-bit like libwebp
- Resize to a box (`MaxWidth`/`MaxHeight`), crop, rotate, mirror, and pick the resize filter (box, bilinear, bicubic, Lanczos)
- Images already within the target size are returned untouched (unless you asked for an edit that changes the pixels)
- Refuses oversized images (`MaxPixels`) before allocating pixel memory, so a small file can't expand into gigabytes

```csharp
using Shan.ImageCompressor;

await using var file = File.OpenRead("photo.jpg");
var result = await ImageCompressor.CompressAsync(file, new ImageCompressionOptions { TargetSizeBytes = 500 * 1024 });

await using var output = File.Create("photo" + result.Extension);
await result.Content.CopyToAsync(output);
```

There is a synchronous version too, for code that isn't async: `ImageCompressor.Compress(stream, options)` or `ImageCompressor.Compress(bytes, options)`. Unusable options (a `ShrinkFactor` of 1, a `MinWidth` of 0, …) throw `ArgumentOutOfRangeException` before any work is done.

## Why compress?

Phone and camera photos are far bigger than a web page, an API upload or a database row needs. Measured with the defaults (768 KB target) and with a 300 KB target on real photos:

| Input | Original | Target 768 KB | Saved | Target 300 KB | Saved |
|---|---:|---:|---:|---:|---:|
| Photo, JPEG | 4,906 KB | 748 KB | **84.7 %** (6.6× smaller) | 295 KB | **94.0 %** (16.6× smaller) |
| 12 MP photo, JPEG | 2,296 KB | 667 KB | **70.9 %** (3.4× smaller) | 275 KB | **88.0 %** (8.3× smaller) |
| 12 MP photo, progressive JPEG | 2,137 KB | 667 KB | **68.8 %** (3.2× smaller) | 275 KB | **87.1 %** (7.8× smaller) |
| Photo, PNG | 12,054 KB | 745 KB | **93.8 %** (16.2× smaller) | 294 KB | **97.6 %** (40.9× smaller) |
| 12 MP photo, PNG | 10,786 KB | 644 KB | **94.0 %** (16.7× smaller) | 272 KB | **97.5 %** (39.5× smaller) |

What that means in practice:

- Upload less: a 4.9 MB photo becomes 0.7 MB, so uploads finish about 6× sooner and the same storage holds about 6× more images.
- A photo saved as PNG is the biggest win, because it is lossless and the library converts it to JPEG. If it has real transparency it stays PNG and is only resized.
- The result always lands at or under the target, instead of whatever size the camera produced.
- A file already at or under the target is returned untouched, so small images are never made worse.

The other formats, with a 300 KB target:

| Input | Original | Result | Saved |
|---|---:|---:|---:|
| 12 MP photo, lossless WebP | 5,395 KB | 275 KB (JPEG) | **94.9 %** (19.6× smaller) |
| 12 MP photo, lossy WebP | 732 KB | 258 KB (JPEG) | **64.7 %** (2.8× smaller) |
| Animated GIF, 30 frames of 320×240 | 709 KB | 396 KB (GIF) | **44.1 %** (1.8× smaller) |

The animated GIF stopped at 396 KB because it is already narrower than the default `MinWidth` of 400, so only its palette was reduced (to 48 colours); lower `MinWidth` to let it shrink further.

Your numbers will differ with the picture: noisy, detailed photos shrink less than smooth ones, and a JPEG that was already heavily compressed has less left to give. A 12 MP image takes roughly 1–2 s on a multi-core machine. Run the [examples](#examples) on your own file to see your figure.

## Examples

A runnable tour of every feature lives in [`samples/Shan.ImageCompressor.Examples`](samples/Shan.ImageCompressor.Examples/Program.cs):

```
dotnet run --project samples/Shan.ImageCompressor.Examples            # builds a test picture in memory
dotnet run --project samples/Shan.ImageCompressor.Examples photo.jpg  # or use your own (shows metadata and colour too)
```

It covers compression, resizing, editing (box, crop, rotate, mirror, filters), output formats, metadata and colour, an animated GIF, a WebP, detection, async and cancellation, and error handling. The animated GIF and the WebP it uses are small samples built into the project, so nothing needs to be on disk.

### Resizing to fit a size

By default the library shrinks an image only as far as it needs to for the file to fit `TargetSizeBytes`, and you steer how:

```csharp
// Fit in 150 KB, but never go below 1000 px wide, and shrink gently (5 % per step) so detail is lost slowly.
var result = ImageCompressor.Compress(bytes, new ImageCompressionOptions
{
    TargetSizeBytes = 150 * 1024,
    MinWidth = 1000,      // the width lands exactly on this, never under it
    ShrinkFactor = 0.95,  // each step multiplies both dimensions by this
    MinQuality = 75,      // JPEG quality stops stepping down here; after that, only pixels are removed
});
```

Order of events per pass: JPEG quality steps down by 10 from `InitialQuality` to `MinQuality`, and only then do dimensions shrink by `ShrinkFactor`. Set `MinQuality = InitialQuality` to keep quality fixed and resize instead. Transparent (PNG) output can only resize, since PNG is lossless.

### Resizing to a box, cropping, rotating

To get an exact size rather than "small enough", say so. These options are applied **even when the file is already under the target**; the image is only re-encoded when something really changes, so a picture that already fits the box comes back untouched.

```csharp
var result = ImageCompressor.Compress(bytes, new ImageCompressionOptions
{
    MaxWidth = 1200,                              // fit inside 1200 x 800, keeping the proportions; never enlarges
    MaxHeight = 800,
    Crop = new CropRegion(X: 100, Y: 50, Width: 2000, Height: 1500),  // of the upright image, applied first
    Rotate = Rotation.Rotate90,                   // clockwise, after the crop
    FlipHorizontal = true,                        // mirror left-right, after the rotation
    Filter = ResizeFilter.Lanczos3,               // how pixels are blended when shrinking
});
```

Order of events: EXIF orientation, then `Crop`, then `Rotate`, then `FlipHorizontal`/`FlipVertical`, then the `MaxWidth`/`MaxHeight` box, then the size-driven passes above. A crop that reaches past the edge is clipped; one that starts outside the image throws `ArgumentOutOfRangeException`.

| `ResizeFilter` | Character |
|---|---|
| `Box` (default) | Exact area averaging. Fast, never rings or overshoots |
| `Triangle` | Bilinear. A little smoother at modest reductions |
| `CatmullRom` | Bicubic. Noticeably sharper, with a faint halo on hard edges |
| `Lanczos3` | The sharpest and slowest; best for photographs reduced a lot |

All filters weight colour by alpha, so transparent pixels never bleed into edges, and give identical output on one core or many.

### Animated GIF

An animated GIF that needs compressing stays an animated GIF. Every frame is composed the way a viewer shows it (disposal methods and transparency respected), then re-encoded with one shared palette, and each frame stores only the rectangle that changed. Delays and the loop count are kept.

```csharp
var result = ImageCompressor.Compress(gifBytes, new ImageCompressionOptions { TargetSizeBytes = 300 * 1024 });
// result.ContentType == "image/gif", result.Extension == ".gif"
```

The palette plays the part JPEG quality does: it steps down from 256 colours (256, 192, 128, 96, 64, 48, ...) before pixels are dropped, and `MinQuality = InitialQuality` keeps all 256 and resizes instead. `MaxWidth`, `MaxHeight`, `Crop`, `Rotate` and the flips apply to every frame. `MaxPixels` bounds the pixels across **all** frames, so a long animation counts for more than a single picture. There is no dithering. Ask for `Output = OutputFormat.Jpeg` or `Png` and you get the first frame as a still instead.

### WebP

Lossy (VP8) and lossless (VP8L) still WebP files are read, including the alpha plane, ICC profile and EXIF; the pixels match libwebp's exactly. A WebP is compressed to JPEG, or to PNG when it has real transparency, like any other input. Writing WebP is not supported, and an **animated** WebP is rejected with `UnsupportedImageException`.

### The other features in a few lines

```csharp
using Shan.ImageCompressor;

// Stream, byte[] or async - same behaviour
var a = ImageCompressor.Compress(bytes);
var b = await ImageCompressor.CompressAsync(stream, options, cancellationToken);

// What is this really? (from the bytes, not the file name or content type)
ImageFormat format = ImageCompressor.DetectFormat(bytes);

// Output and metadata
new ImageCompressionOptions { Output = OutputFormat.Png, KeepMetadata = true, ConvertToSrgb = false };

// One core per call on a busy server (output is identical)
new ImageCompressionOptions { MaxDegreeOfParallelism = 1 };

// Errors: a result or one of two exceptions
try { ImageCompressor.Compress(bytes); }
catch (InvalidImageException e)     { /* corrupt, truncated, not an image, over MaxPixels; e.Format says what it looked like */ }
catch (UnsupportedImageException e) { /* animated WebP, arithmetic-coded JPEG, ... - keep the original */ }
```

## What it can read

| Format | Detect | Compress |
|---|---|---|
| PNG | yes | yes — every colour type and bit depth, Adam7 interlacing, tRNS transparency, ICC profile and EXIF |
| JPEG | yes | baseline, extended-sequential and progressive, 8-bit; grayscale, YCbCr/RGB and **CMYK/YCCK** (Adobe); any chroma subsampling; restart intervals; ICC profile and EXIF |
| GIF | yes | every frame with interlacing, disposal methods, delays, the loop count and the transparent colour; an animated GIF is compressed as an animation ([see above](#animated-gif)) |
| BMP | yes | 1/4/8-bit palette, 16/24/32-bit, bit masks (incl. alpha), RLE4/RLE8, top-down or bottom-up |
| TIFF | yes | classic TIFF, first image: 1/2/4/8/16-bit gray, palette, RGB(A) and CMYK; uncompressed, PackBits, LZW or Deflate; predictor 2; either byte order; orientation tag; ICC profile. Tiled, planar-separate, float, JPEG/CCITT-compressed and BigTIFF files are rejected |
| TGA | yes | colour-mapped, true-colour and gray, 8/15/16/24/32-bit, uncompressed or RLE, any origin. TGA has no magic number, so it is recognised by a strict header and size check |
| PNM | yes | PBM, PGM and PPM (P1–P6), ASCII or binary, 8 or 16-bit |
| QOI | yes | 3 and 4 channel |
| WebP | yes | lossy (VP8) and lossless (VP8L) stills, with alpha, ICC profile and EXIF; **animated WebP is rejected** |

An image that is already small enough is returned as-is whatever its format. One that needs compressing but isn't a supported kind throws `UnsupportedImageException` (**animated WebP, arithmetic-coded JPEG, JPEG-compressed TIFF**); a corrupt or non-image stream throws `InvalidImageException` — and nothing else, however damaged the bytes are. Both derive from `ImageCompressionException` and carry the detected `Format`, so a caller can keep the original, as with any other unsupported file.

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

- Compression is CPU-bound and synchronous after the input is read; a 12-megapixel photo takes about 1–2 seconds on a multi-core machine. Run it off a request thread if that matters, and pass a `CancellationToken` to stop between passes.
- Runs on .NET 5 through .NET 11: the package carries a build for each of `net5.0` to `net11.0`.
- A decoded image takes 4 bytes per pixel. `MaxPixels` (default 50 million) caps that; lower it if you run in a small container.
- Not a general-purpose imaging library: the edits are the ones above (resize, crop, rotate, mirror), there are no filters or drawing, and colour management is limited to converting matrix/TRC RGB profiles to sRGB.
