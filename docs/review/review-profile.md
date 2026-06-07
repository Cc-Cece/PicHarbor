# Code Review Profile — get-and-see

> Auto-maintained by the **`code-review` skill**. Bootstrapped 2026-06-07 from
> `PROJECT_BRIEF.md` + the Sprint 1/2 history. Updated after every review.
> Edit by hand freely — the skill respects manual edits.
>
> Used as the merge-gate review profile: the Producer runs independent reviewers
> (4 gate lenses + 1 advisory) calibrated against this file before QA + merge.

## Project goal (1–2 sentences)

A C#/.NET 10 CLI that copies an iPhone's media archive to a Windows PC over USB (AFC), **read-only against the device**, built to move a worst-case ~400 GB / ~38k-file library reliably and resumably. The thing reviews must protect above all else: **the iPhone is never written to, and no copied byte is ever lost or corrupted.**

## Sacred invariants (auto-BLOCKER)

Any change that violates one of these is an automatic **BLOCKER**, no matter how unlikely the path:

1. **Read-only device contract (PROJECT_BRIEF §9.1).** No AFC/lockdown **write/delete/rename/truncate/mkdir/link** symbol may be referenced anywhere in `GetAndSee.Core`. `afc_file_open` may only be used in `FopenRdonly`. The canonical blocklist is in `docs/sprint-1/afc-library-decision.md` §5.5. Enforced by `tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs` (Mono.Cecil IL scan) — that test must exist, pass, and cover the **full** blocklist.
2. **No CLI flag that mutates the device.** No `--delete-after-copy`, `--move`, `--cleanup`, etc. Adding one is a design conversation, not a code change.
3. **Atomic, verified writes.** Destination files are staged (`.partial`), fsync'd, **size-verified against the AFC-reported size**, then atomically renamed. A final-path file must never be partial or size-mismatched. Breaking this = BLOCKER (data integrity).
4. **Journal/resume integrity.** An interrupted file is left non-`done` (resumable); it is never falsely marked `done`, and the manifest never exposes non-`done` rows. Breaking this = BLOCKER.

## Stack pins & conventions (flag any drift)

- **.NET 10 (LTS), C# 14.** Not .NET 8/9. `net10.0` TFM.
- **Classic `.sln`** (NOT `.slnx`) — the CI guard `if (Test-Path *.sln)` depends on it; an `.slnx` would make CI silently pass as a no-op.
- **AFC library:** `imobiledevice-net 1.3.17` with `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` (native DLLs). NOT "NetiMobileDevice" (that package does not exist).
- **Tests:** xUnit v3 + **Shouldly** + NSubstitute. **NOT FluentAssertions** (paid-commercial since v8/Jan 2025) — flag any reintroduction.
- **Build:** `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<LangVersion>14</LangVersion>`.
- Every public `GetAndSee.Core` type gets a one-line XML `<summary>`.
- Errors via typed `DeviceException` / `PreflightException` (and subclasses like `DeviceStallException`).

## Scale context (tunes the Performance lens)

- **400 GB / ~38k files / multi-hour single run.** Real QA run was 269 GB / 27,478 files.
- Sustained throughput is **USB-2.0-bound (~30 MB/s)** on the iPhone 12 Pro Lightning port — link-bound, not a copy-loop concern. Don't file throughput "bugs" against the hardware ceiling.
- The per-chunk copy loop and per-file journal ops are the hot paths. Anything added there must be O(1)/lock-free; nothing that grows with N may be rendered/scanned per item.
- A live dashboard must add **no measurable slowdown** (separate thread, ≤4 Hz, in-memory only).

## Accepted trade-offs — DO NOT re-flag

- **R20 — aging native deps in `imobiledevice-net 1.3.17`** (OpenSSL 1.1 EOL, 2021 libusb). Local-USB-only, read-only, no network → minimal exposure. **Accepted for v1.** Don't re-report unless a maintained fork appears (that's a Modernization note, not a finding).
- **Branch protection on `main` is discipline-only** (private free-tier repo). Documented in `docs/sprint-1/branch-protection-setup.md`. Not a bug.
- **Non-media files in `/DCIM/` are copied faithfully** (e.g. `ispRegDump.bin` → "Other" bucket). Working as designed — the tool archives all of `/DCIM/` read-only. Not a bug; a media-type filter is a backlog idea only.
- **`HostPower` P/Invoke is Windows-only** (guarded by `OperatingSystem.IsWindows()`). Correct for a Windows app.
- **No mid-run auto-reconnect** (watchdog stops cleanly + resumes on re-run). Deferred to Sprint 3 by design — not a gap to file.

## Recurring findings / known blind spots (check these FIRST)

- **DB opened read-write where read-only suffices.** Caught in the PR #16 review: `StatusCommand` opened the journal read-write though it only reads. For a read-only-ethos project, **any new code that opens `get-and-see.db` for reads should use `Mode=ReadOnly`.** Check every new `TransferJournal.Open()` call site.
- **Pluralization / user-facing copy.** "Live Photos: 1 pairs" slipped through (should be "1 pair"). Re-read any new `summary.txt` / console strings for grammar and for the N=0/N=1/N≥2 cases; expect explicit tests.
- **Untested sync paths on async-first types.** `WatchdogReadStream.Read()` (sync) was untested while only `ReadAsync` is used — a latent hole if a caller ever switches. Flag sync overrides that bypass the safety/timeout path.
- **CI no-op traps.** Anything that could make the CI guard silently skip build/test (e.g. solution-format drift) is high-severity even though "tests pass."

## Severity calibration notes

- **Any conceivable device-write/delete/rename path = BLOCKER**, even if currently unreachable. The guard having a *hole* (a §5.5 symbol missing from `ReadOnlyContractTests`) is a BLOCKER too — not just an actual call.
- **Data-integrity defects** (truncation, false-`done`, lost resume state, manifest leak) = BLOCKER.
- **Responsiveness/robustness gaps that are data-safe** (e.g. the original #11 unplug hang: journal + resume + read-only all held) = MAJOR, not BLOCKER.
- **User-facing copy bugs** = NIT unless they mislead about safety/data.
- Performance findings are graded **against the scale-context numbers**, not abstract ideals.

## Lens weighting

For this project: **Security/Safety and Correctness/Reliability dominate** (read-only contract + 400 GB of irreplaceable data). **Performance** matters specifically at the 400 GB / 38k-file scale and on the copy hot path. **Maintainability** is normal-weight. **Modernization** is advisory-only (and R20 is already the known item).

## Review history (newest first)

| Date | Change | Verdict | Blockers | Notable |
|------|--------|---------|----------|---------|
| 2026-06-07 | PR #16 — Sprint 2 (watchdog, dashboard, status, runs/devices, live-photo) | PASS-with-nits | 0 | 4-lens independent review. Verified: read-only contract intact, watchdog has no thread/handle leak (atomic dispose + abandon-continuation), dashboard doesn't slow a 400 GB copy, v1→v2 migration safe. Findings: [MINOR] status DB opened read-write; [NIT] "1 pairs" grammar; [NIT] scratch buffer not zeroed; [NIT] sync `Read()` untested; [MINOR] no `StatusCommandTests`. MINOR-1 + grammar folded pre-merge; rest → issue #17. |
| 2026-06-06 | PR #9 — Sprint 1 (core pipeline + safety contract) | PASS (inline review) | 0 | Pre-skill inline producer review. Verified the `ReadOnlyContractTests` Mono.Cecil IL approach (catches inlined write-mode enum constants), atomic copier, classic-`.sln` CI-guard catch. Gated on QA Stage 2 (269 GB real-hardware run, read-only proof device-unchanged). |
