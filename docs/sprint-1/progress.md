# Sprint 1 — Progress (Phases 1–5)

Branch: `feature/sprint-1`. Dev team: Nova (app), Sage (systems), Kira (UX copy).
Phase 0 (CI, templates, labels, gitignore) was merged separately (PR #1/#6) — not repeated here.

Update this file after every phase (PROJECT_BRIEF §12).

---

## Phase 1 — Foundation (Tasks 1–4) ✅

**Built:**
- Solution `get-and-see.sln` with four projects:
  - `src/GetAndSee.Core` — class library (net10.0), `GenerateDocumentationFile` on.
  - `src/GetAndSee.Cli` — exe (net10.0), `AssemblyName=get-and-see`, `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` so the native AFC DLLs land next to the app (decision doc §5.2). Placeholder `Program.cs`; real CLI wiring is Phase 4.
  - `tests/GetAndSee.Tests` — xUnit v3 + Shouldly + NSubstitute.
  - `tests/GetAndSee.SafetyTests` — xUnit v3 + Shouldly + Mono.Cecil.
- `Directory.Build.props`: `LangVersion 14`, `Nullable enable`, `ImplicitUsings enable`, `TreatWarningsAsErrors true`, `InvariantGlobalization true`, and `NoWarn=NETSDK1206` (cosmetic stale-RID warning from the 2021 library — R20).
- `.editorconfig` (file-scoped namespaces, System-first usings, 4-space C#).
- **AFC integration (Task 2):** `iMobileDevice-net 1.3.17` referenced in Core. Bound **only** the read path:
  `idevice_get_device_list`, `idevice_new`, `lockdownd_client_new_with_handshake`,
  `lockdownd_get_value` (read), `lockdownd_start_service`, `afc_client_new`,
  `afc_read_directory`, `afc_get_file_info`, `afc_file_open` (FopenRdonly **only**),
  `afc_file_read`, `afc_file_close`, `plist_get_string_val`.
- **`IPhoneClient` (Task 3):** read-only interface — `ConnectAsync`, `ListDirectoryAsync`,
  `GetFileInfoAsync`, `OpenReadAsync` — plus `AfcIPhoneClient` implementation and a forward-only
  `AfcReadStream`. XML doc comments carry the read-only contract. No write/delete/rename/truncate
  symbol referenced anywhere in Core.
- **`ReadOnlyContractTests` (Task 4, Ivy):** Mono.Cecil IL scan of the compiled `GetAndSee.Core`.
  Two facts: (1) no reference to any blocklisted mutator (decision doc §5.5), and (2) `afc_file_open`
  is never called in a write/append mode (caught via IL constant analysis, since enum constants are
  inlined and invisible to plain reflection). **Fails the build on violation.**

**Verified locally:** `dotnet build -c Release` → 0 errors / 0 warnings. `dotnet test` → 3 passed
(1 unit + 2 safety). `dotnet format --verify-no-changes` → clean (CI format gate).

**Decisions / notes:**
- `IPhoneClient` is modelled as an **interface** (not a concrete class) so unit tests can mock it
  with NSubstitute; `AfcIPhoneClient` is the imobiledevice-net implementation. The read-only contract
  lives on the interface and is enforced against the whole Core assembly.
- `afc_file_open` handle and `afc_file_read` bytesRead are **`ref`** params in this binding (not
  `out`) — discovered by reflecting over the real 1.3.17 assembly before writing code.
- AFC `st_mtime` is nanoseconds since epoch (÷1e6 → ms), per decision doc §3.
- Device name / product type read best-effort via `lockdownd_get_value` for the summary line; never fatal.

## Phase 2 — Enumerate & Plan (Tasks 5–7, 10) ⬜

## Phase 3 — Core Copy (Tasks 8–9) ⬜

## Phase 4 — CLI & Output (Tasks 11–13) ⬜

## Phase 5 — Tests & Handoff (Tasks 14, 14b, 20) ⬜

---

## Bugs / Issues Found

_None yet._
