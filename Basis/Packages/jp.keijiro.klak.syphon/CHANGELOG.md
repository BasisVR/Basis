# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.0.4] - 2025-11-16

### Added

- Added `AGENTS.md` with workflow instructions for AI agents.
- Added `CHANGELOG.md` to track project changes.
- Added package signing for Unity 6.3 compatibility.

### Changed

- Replaced `ShaderUtil` (obsolete) with newer Shader methods for Unity 6.3
  compatibility.

## [1.0.3] - 2025-11-14

### Fixed

- Cleared the active render target before screenshot capture to stop Game View
  senders from grabbing stale frames.

## [1.0.2] - 2025-11-13

### Changed

- Updated the embedded Syphon framework and native plugin to the latest
  ARC-based implementation for better stability.
- Refreshed the sample project, ignore rules, and package metadata to align
  with the 2022.3 Unity baseline.
- Rewrote the README (package and root) with clearer setup instructions plus
  new inspector screenshots for the server and client components.
- Tweaked the GitHub Actions workflow to match the current project layout.

### Fixed

- Resolved the Game View capture conflict that occurred when another server
  used a render-texture camera, and allowed the sender to follow Game View size
  changes on the fly.
