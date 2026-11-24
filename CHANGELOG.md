# Changelog

All notable changes are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/), versioning follows SemVer.

## [2.0.0] - 2026-11-24

### Breaking

- `ImageCompressor` now throws `InvalidImageException` (corrupt, truncated, over `MaxPixels`, not an image) and `UnsupportedImageException` (recognised but not decodable), both deriving from the new `ImageCompressionException` and carrying the detected `Format`. They replace `InvalidDataException` and `NotSupportedException`, which `InvalidDataException` being sealed made impossible to subclass. Catch `ImageCompressionException` (or the two new types) instead.

### Fixed

- `MinWidth` is now a true floor: the last shrink step lands exactly on it instead of stepping past it (1600 → 1280 → 1024 → 819 with `MinWidth = 1000`).

### Added

- Input formats: **TIFF** (classic, first image: 1–16-bit gray/palette/RGB(A)/CMYK; uncompressed, PackBits, LZW, Deflate; predictor; both byte orders; orientation; ICC), **TGA**, **PNM** (PBM/PGM/PPM, ASCII and binary), **QOI**, and **CMYK/YCCK JPEG**.
- `ImageCompressionOptions.Output` (`Auto`/`Jpeg`/`Png`) to choose the output format; forcing JPEG flattens transparency onto white.
- `ImageCompressionOptions.KeepMetadata` (default false) carries EXIF — orientation reset, since the pixels are rotated upright — and the ICC profile to JPEG and PNG output.
- `ImageCompressionOptions.ConvertToSrgb` (default true): pixels from an embedded RGB matrix/TRC ICC profile are converted to sRGB before the profile is dropped, so Adobe RGB / Display P3 photos no longer look dull. Verified against LittleCMS.
- `ImageCompressionOptions.MaxDegreeOfParallelism` (default 0 = all cores): resizing, colour conversion, JPEG colour conversion and DCT/quantisation now run across cores, about 1.8× faster on a 12-megapixel photo. The output is byte-identical whatever the setting; use 1 on a busy server.

## [1.1.0] - 2026-11-24

### Added

- Progressive JPEG decoding (spectral selection and successive approximation, restart intervals), checked against libjpeg.
- GIF input: the first frame with interlacing and the transparent colour index. Animated GIFs are rejected with `NotSupportedException` instead of being flattened to a still.
- BMP input: 1/4/8-bit palette, 16/24/32-bit, `BI_BITFIELDS` (including alpha masks and V4/V5 headers), RLE4/RLE8, top-down and bottom-up.
- Synchronous `ImageCompressor.Compress(Stream, ...)` and `Compress(byte[], ...)`.
- Option validation: unusable values throw `ArgumentOutOfRangeException` naming the property; a null input throws `ArgumentNullException`.
- A transparent GIF or BMP stays PNG, like a transparent PNG.
- Tests: reference fixtures for every new format, a corruption/truncation test over every fixture, and sync/async equivalence.

### Fixed

- A PNG whose compressed data is damaged threw a raw `ZLibException`; it now throws `InvalidDataException` like every other corrupt input.

## [1.0.1] - 2026-11-24

### Changed

- Package metadata: added release notes, repository URL, project URL and copyright.
- Releases are now published from GitHub Actions through nuget.org Trusted Publishing (no stored API key).
- No code or behaviour changes from 1.0.0.

## [1.0.0] - 2026-11-24

### Added

- `ImageCompressor.CompressAsync`, `DetectFormatAsync` and `DetectFormat`.
- `ImageCompressionOptions` (target size, quality, min width, shrink factor, max passes, `MaxPixels`).
- Self-contained PNG codec (all colour types and depths, Adam7, tRNS) and baseline JPEG codec (any subsampling, restart intervals, EXIF orientation, interpolated chroma upsampling). No package dependencies.
- Alpha-aware output: opaque images become JPEG, transparent images stay PNG.
