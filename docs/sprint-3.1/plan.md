# Sprint 3.1 — Hotfix: unplug-safe AFC (issue #25)

**Type:** hotfix (single defect) · **Branch:** `fix/25-afc-open-watchdog` (dev) · **Gates:** v1.0 release (#21)
**Owner:** Dev (Sage — device layer) · **Review:** Producer gate · **Re-acceptance:** QA #21 Phase B (hardware)

> **Read first:** issue **#25**, `docs/qa/sprint-3-signoff.md` (Stage-2 BLOCKED record), `PROJECT_BRIEF.md` §9 (read-only contract), `docs/review/review-profile.md` (sacred invariants + the new *unguarded native `afc_*` parking* blind spot).

## Why this exists

QA Stage 2 (#21) passed **3 of 4** release criteria on real hardware (269 GB / 27,478 files): full atomic copy ✅, dashboard-no-slowdown ✅, read-only before/after proof ✅. It **failed** the cable-unplug criterion: on a physical USB yank mid-copy the tool **hung indefinitely** (194 s observed, force-kill required) against a 15 s `--read-timeout`. Data was never at risk — journal clean & resumable, no truncated/orphaned finals, read-only intact — so this is **`severity:major`, not blocker**, but it **blocks v1.0** because "real cable-unplug → clean resumable stop, no indefinite hang" is a named #21 criterion and the exact scenario Sprint 2 set out to fix.

## Root cause (confirmed in code)

The Sprint 2 watchdog (`WatchdogReadStream`) only guards the **byte-stream read of an already-open file**. The blocking **native AFC calls** in [`AfcIPhoneClient`](../../src/GetAndSee.Core/Device/AfcIPhoneClient.cs) each check `cancellationToken` only **before** the native call (a token can't interrupt a call already parked in native code):

- `OpenReadAsync` → `afc_file_open` ← **the observed hang**
- `ListDirectoryAsync` → `afc_read_directory`
- `GetFileInfoAsync` → `afc_get_file_info`

On a disconnect, the in-flight read fails fast, the loop advances, and the **next file's `afc_file_open` parks forever** — the watchdog stream for that file is never even constructed. The #11 hang didn't get fixed in Sprint 2; it **relocated** from the read to the open.

## The fix

Generalize the inactivity-timeout pattern that already lives in `WatchdogReadStream` so it also covers the blocking `afc_*` open/stat/list calls.

1. **Extract a shared device-watchdog helper.** The "run a blocking op, race it against an inactivity timeout on an injectable `TimeProvider`, abandon the orphaned call on timeout, throw `DeviceStallException`" logic currently exists once (for reads). It will now have **4 call sites** (read + open + stat + list) — a real Rule-of-Three-met extraction, not speculative. Suggested shape: a small `DeviceWatchdog.RunWithTimeoutAsync<T>(Func<T> blockingNativeCall, TimeSpan timeout, TimeProvider clock, CancellationToken)` that runs the native call on a worker (`Task.Run`) and races it against `Task.Delay(timeout, clock)`; on timeout it **abandons** the orphaned native call (schedules disposal/cleanup when it eventually returns, exactly as `WatchdogReadStream` does today) and throws `DeviceStallException`. Refactor `WatchdogReadStream` to use the shared helper so there is **one** tested implementation of the abandon-on-timeout logic, not two.
2. **Wrap the three native calls** in `AfcIPhoneClient.OpenReadAsync` / `ListDirectoryAsync` / `GetFileInfoAsync` with the helper, using the same `--read-timeout`. A stall on any of them → `DeviceStallException` → the existing `CopyCommand` catch → clean **exit 3**, in-flight file left non-`done` (resumable), `summary.txt` written.
3. **Don't advance the loop on a connection-fatal device error** (Ivy's secondary point). A device-side read error that signals the *connection* is gone (not just one bad file) should stop the run cleanly rather than marching every remaining file into its own open-hang. Keep it conservative: only short-circuit on errors that genuinely indicate disconnect; a single corrupt/unreadable file must still `MarkFailed` and continue (don't regress resilience).

## Hard constraints (non-negotiable)

- **Read-only contract holds.** The helper wraps the *same* read-only native calls — **no** AFC/lockdown write/delete/rename symbol may be introduced. `ReadOnlyContractTests` (IL scan, §5.5 blocklist) must stay green.
- **Test the REAL failure mode.** Sprint 2's watchdog test mocked a *stalling read* and passed green while the actual disconnect path stayed unguarded — that false-green is exactly why this shipped. The new tests **must** exercise a **blocking/parked `afc_*` open (and stat/list)** — e.g. an injected client/op whose open *parks* — and assert the timeout fires → `DeviceStallException` → exit 3 within ~`--read-timeout`. A test that only stalls a read does **not** close #25.
- **No watchdog handle/thread leak.** Preserve the current abandon-and-dispose-on-late-return behavior; an abandoned open must not leak the AFC handle if the native call later returns.
- **Zero hot-path cost when healthy.** Racing against a `Task.Delay` only materializes the timer when the native call doesn't complete promptly; a fast open must not pay a per-file penalty at 38k-file scale.
- **Explicit types (no `var`)** per the convention (enforcement still pending #23 — don't add new `var`).

## Tasks

| # | Task | Owner | Notes |
|---|------|-------|-------|
| 1 | Extract `DeviceWatchdog.RunWithTimeoutAsync<T>` (abandon-on-timeout + `DeviceStallException`) | Sage | one shared impl; `WatchdogReadStream` refactors onto it |
| 2 | Wrap `OpenReadAsync` / `ListDirectoryAsync` / `GetFileInfoAsync` with the helper | Sage | same `--read-timeout`; stall → exit 3 |
| 3 | Stop advancing the file loop on a connection-fatal device error | Sage/Nova | conservative; single bad file still continues |
| 4 | **Tests that park a native open/stat/list** → assert timeout→`DeviceStallException`→exit 3 | Sage | the criterion that actually closes #25; no read-only mock substituting |
| 5 | Keep `ReadOnlyContractTests` green; XML summary on any new public type | Sage | |
| 6 | `docs/sprint-3.1/progress.md` + `done.md`; close #25 in the commit | Sage | |

## Acceptance criteria

- [ ] A parked native **`afc_file_open`** (and `afc_read_directory`, `afc_get_file_info`) is detected within ~`--read-timeout` → `DeviceStallException` → clean **exit 3**, in-flight file non-`done`/resumable, `summary.txt` written — **unit/integration test proving it** (not a read-mock).
- [ ] `ReadOnlyContractTests` green; no new device-write symbol; `dotnet test` + CI (build/test, gitleaks, EXE smoke) green.
- [ ] One shared timeout-race implementation (no duplicated abandon-logic); `WatchdogReadStream` reuses it.
- [ ] Healthy-path copy unaffected (no measurable per-file slowdown).
- [ ] **Hardware (QA #21 Phase B):** real cable-unplug → clean resumable stop, **no indefinite hang**; then re-run resumes with zero loss.

## Process

Dev work on `fix/25-afc-open-watchdog` off current `main` (`3517808`, post-Sprint-3). Commit + update `docs/sprint-3.1/progress.md`; `Closes #25` in the commit; CI green; open ONE PR `fix: #25 unplug-safe AFC (watchdog over open/stat/list)` and **stop for the producer review gate**. Never push to `main`. The read-only contract and an honest unplug-safe promise beat speed.

## Then (producer + QA)

1. Producer independent review gate (extra weight: **Correctness/Reliability** + **read-only contract** + "does the test exercise the *real* parked-open path?").
2. **QA #21 Phase B (one combined hardware re-acceptance):** the unplug re-test (verifies #25) **plus** the deferred Sprint-3 checks (long-path, `--verify-hash`, published-EXE `--help` smoke). Ivy updates `docs/qa/sprint-3-signoff.md` → **PASS**.
3. Merge PR #31 (final PASS gate record) → cut **v1.0** → `release.yml` publishes the single-file EXE + SHA-256.
