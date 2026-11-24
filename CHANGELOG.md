# Changelog

All notable changes are documented here. Format follows [Keep a Changelog](https://keepachangelog.com/), versioning follows SemVer.

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
