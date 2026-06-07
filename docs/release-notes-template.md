# Release notes — vX.Y.Z

> Template for a get-and-see release. Copy this file to `docs/release-notes/vX.Y.Z.md`, fill it in, then
> cut the tag (`git tag vX.Y.Z && git push origin vX.Y.Z`). The release workflow
> (`.github/workflows/release.yml`) builds the single-file EXE, attaches it plus its `.sha256`, and
> publishes the GitHub Release. **Do not cut a tag until QA has signed off on the build.**

**Release date:** YYYY-MM-DD
**Tag:** `vX.Y.Z`

## Highlights

- One or two sentences on the headline change(s).

## What's new

- Bullet per user-visible feature or flag (link the issue/PR: `(#NN)`).

## Fixes

- Bullet per user-visible fix `(#NN)`.

## Known limitations

- Carry forward anything users should know the tool does **not** do (see the README's
  "What this tool does NOT do" section), plus any open caveats for this release.

## Safety

- Confirm the read-only device contract still holds (`ReadOnlyContractTests` green) and note any
  safety-relevant change.

## Install & verify

Download `get-and-see.exe` from the release and verify it:

```pwsh
(Get-FileHash .\get-and-see.exe -Algorithm SHA256).Hash
# compare to get-and-see.exe.sha256 attached to the release
```

No install, no admin rights, no .NET runtime required (single self-contained win-x64 file).
