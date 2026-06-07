# get-and-see

A single-file C# / .NET 10 CLI that copies the full media archive (photos, videos, Live Photos) from an iPhone to a Windows PC over USB. It uses Apple's native AFC protocol (the same protocol Finder, iTunes, and iMazing rely on) and is **read-only by architectural design** — no write or delete code paths against the device exist in the codebase.

## Commands (preview — full README in Sprint 3)

- `get-and-see copy --dest <folder>` — copy all `/DCIM/` media into date-organized folders. A **live dashboard** (current/avg speed, ETA, counts) is shown by default; it falls back to plain per-file text when output is piped/redirected or when `--no-dashboard` is given. `--dry-run` plans without copying. `--read-timeout <seconds>` (default 30) stops a stalled run cleanly so it can be resumed.
- `get-and-see status --dest <folder>` — print archive totals and the last-run summary from `get-and-see.db`, with **no device attached**.

Re-running `copy` resumes: completed files are skipped, and an interrupted or stalled run continues from where it stopped.

Documentation is in [`docs/`](docs/). Full README written in Sprint 3 by Quill.
