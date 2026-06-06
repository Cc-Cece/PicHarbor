# Ideas Backlog — get-and-see

Ideas deferred from brainstorm. Revisit after v1 ships.

## v2 Candidates
- [ ] `--convert-heic` flag — convert HEIC to JPEG alongside originals (ImageMagick.NET or libheif binding)
- [ ] **XMP sidecars per file** (Lightroom / digiKam-compatible) — EXIF + reverse-geocoded location keywords. See brainstorm Session 3 for the trade-off discussion (doubles inodes; mitigated by being opt-in).
- [ ] **Reverse-geocoding** GPS → city/country/place keywords. Offline DB (~100 MB) preferred over network calls (no-network policy).
- [ ] **Smart-folder symlink views** (`by-location/Tokyo/`, `by-camera/iPhone-12-Pro/`) layered over the canonical date folders. Windows needs Developer Mode for symlinks.
- [ ] **Live Photo / Burst / Portrait / Cinematic awareness** — detect and tag in manifest (`live_photo_pair_id`, `burst_id`, `is_portrait`, `is_cinematic`)
- [ ] **Apple Photos export via `house_arrest`** — separate tool, NOT part of get-and-see (different safety profile, would write to a sandboxed area). Recovers albums, keywords, favorites, People.
- [ ] Album / smart-album organization (requires PhotoDB access via `house_arrest`, harder than AFC/DCIM)
- [ ] GUI wrapper (WinUI 3 or Avalonia — keep CLI as the engine underneath)
- [ ] Hash-based dedup across re-organizations (xxHash for speed; integrates with journal)
- [ ] Thumbnail preview before copy (read-only thumbnail stream)
- [ ] Selective copy by date range (`--after 2024-01-01 --before 2025-01-01`)
- [ ] `--verify-hash` reverify mode against existing destination (no re-copy, just hash check)
- [ ] Export to external drive with double-write verification
- [ ] macOS support (NetiMobileDevice is cross-platform — should "just work", needs testing)
- [ ] Video transcoding options (ProRes → H.265) — optional post-step
- [ ] Copy from multiple devices in one session
- [ ] Parallel file transfers — only after telemetry confirms it's safe at scale

## Infra / CI (advanced — v1 ships basic CI + release pipelines in Sprints 1 and 3)
- [ ] Code-signing the release EXE (Authenticode) — requires a code-signing certificate
- [ ] Cross-platform CI matrix (win-arm64, linux-x64, osx-arm64) — only after `pymobiledevice3`-style cross-platform support is verified
- [ ] Code coverage reporting (coverlet + Codecov or similar)
- [ ] Dependabot config for NuGet package updates
- [ ] SBOM generation on release
- [ ] Smoke-test workflow that runs the published EXE in `--help` mode on a clean Windows runner

## Docs
- [ ] README with screenshots of the live dashboard
- [ ] Troubleshooting guide ("iPhone not detected", "Trust dialog never appeared", etc.)
- [ ] Comparison table vs. iTunes / iCloud / iMazing / Explorer
