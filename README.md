# get-and-see

A single-file C# / .NET 10 CLI that copies the full media archive (photos, videos, Live Photos) from an iPhone to a Windows PC over USB. It uses Apple's native AFC protocol (the same protocol Finder, iTunes, and iMazing rely on) and is **read-only by architectural design** — no write or delete code paths against the device exist in the codebase.

Documentation is in [`docs/`](docs/). Full README written in Sprint 3 by Quill.
