# Sprint 3.6 — Progress: productionize the `GAS_FAKE_DEVICE` EXE seam

> Branch `feature/fake-device-exe-seam` (PR #57, continued from the spike). `main` merged in first
> (#56 harness + #59 hardened CI + #62). Owners: Sage (seam/device), Nova (CLI/E2E), Ivy (E2E asserts).
> Stopped at the producer review gate — **not merged**.

## Goal

Close the two gaps the spike left so the opt-in EXE seam is production-grade:
1. the **CI byte-identical guard** the csproj comment promised but didn't exist, and
2. the **real-process E2E** the in-process harness can't do (it can't restart a process).

## What the spike already nailed (KEPT — mechanism unchanged)

- `#if FAKE_DEVICE` gating: the seam compiles in **only** with `-p:FakeDevice=true`; the
  `GetAndSee.FakeDevice` `ProjectReference` is `Condition="'$(FakeDevice)'=='true'"`. A normal/release build
  references nothing and emits no seam → shipped `get-and-see.exe` is byte-identical, contains no fake.
- `FakeDeviceGate.TryCreate()` reads `GAS_FAKE_DEVICE`; unset → `null` → real `AfcIPhoneClient` (off by default).
- The fake is unified in `src/GetAndSee.FakeDevice` (one assembly; the harness references the same one).
- `FakeDeviceSeamTests` unit tests (preset/modifier parsing, malformed → `FormatException`, gate-off-when-unset).

## Phase 1 — Gap 1: CI byte-identical guard ✅

Added a **Release guard** step to the existing **`publish-smoke`** job in `.github/workflows/ci.yml` (job name
left unchanged so required-check enforcement keeps working). It runs right after the normal single-file
publish (`release.yml`-identical command, **no** `-p:FakeDevice`) and asserts the shipped artifact is fake-free:
- **(1)** no `GetAndSee.FakeDevice*` file in the publish output (defends a non-single-file layout), and
- **(2)** the published single-file EXE's bytes contain neither `GetAndSee.FakeDevice` (the assembly name in
  `get-and-see.dll`'s `AssemblyRef`) nor `FakeDeviceGate` (the seam type) — both are UTF-8 metadata strings,
  present verbatim in the uncompressed single-file bundle **iff** `-p:FakeDevice=true` was used. Either match
  fails the job.

**Proven load-bearing locally (both directions):**
- normal publish → `leaked: 0`, `contains 'GetAndSee.FakeDevice': False`, `contains 'FakeDeviceGate': False` → **PASS**.
- `-p:FakeDevice=true` publish → `leaked: 1`, both markers `True` → the guard **FAILS** (catches the leak).

## Phase 2 — Gap 2: real-process E2E lane ✅

New `tests/GetAndSee.Tests/E2E/`:
- `ProcessRunner.cs` — spawns a child via `ProcessStartInfo.ArgumentList` (no shell / no string concat →
  no quoting/injection surface), drains stdout+stderr concurrently with the wait (no full-pipe deadlock),
  per-run timeout kills the process tree so a wedged subprocess fails fast.
- `FakeDeviceExeE2ETests.cs`:
  - `FakeDeviceExeFixture` (class fixture) builds the `-p:FakeDevice=true` EXE **once** to an isolated temp
    dir (never overwrites a normal build; deleted on dispose). Implicit restore runs **with** `FakeDevice=true`
    so the conditional project reference resolves.
  - `[Trait("Category", "E2E")]` so the fast unit suite excludes it. Three tests, each spawning the **real EXE**:
    1. **clean (`small`) → exit 0** + `summary.txt` on disk + archive **byte-identical** (`FakeContent` recompute).
    2. **`small;disconnect-after=2` → exit 3** (real process; `DeviceConnectionLostException` → exit-3 path).
    3. **cross-process resume:** run 1 interrupted (exit 3) → run 2 healed (exit 0), `Skipped: ≥2` parsed from
       the run summary, archive **byte-identical**. Two separate process invocations — the thing only the
       real-`.exe` lane can prove.

## Phase 3 — CI lane wiring ✅

- Gating **`build + test`** job: added `--filter "Category!=E2E"` (keeps the fast unit suite untouched;
  `!=` still includes every untagged test).
- New gating **`exe-e2e`** job: `dotnet test … --filter "Category=E2E"` (the fixture builds the EXE),
  `timeout-minutes: 20`, uploads `.trx`/`.dmp` on failure.

## Validation (local, mirrors CI)

- `dotnet build -c Release` → **0 warning / 0 error**.
- `dotnet format --verify-no-changes` → clean (exit 0).
- `dotnet test -c Release --filter "Category!=E2E"` → **174 passed** (GetAndSee.Tests) + **2 passed** (SafetyTests), 0 failed.
- `dotnet test … --filter "Category=E2E"` → **3 passed** (builds the fake EXE + spawns it).
- Guard verified PASS on a normal publish and FAIL on a `-p:FakeDevice=true` publish (see Phase 1).
- **Full count: 177 GetAndSee.Tests (174 unit/harness + 3 E2E) + 2 SafetyTests = 179.**

## Decisions / notes

- Guard lives in `publish-smoke` (already does the normal single-file publish) rather than a new job — one
  publish, faithful to the shipped artifact, and it gates. Job name kept stable.
- E2E builds the EXE in the fixture (not `dotnet run`) so it genuinely spawns the **real** `get-and-see.exe`.
  Build output is isolated to a temp dir via `-o`, so a normal build is never overwritten.
- Markers are the two **UTF-8 metadata** names (`GetAndSee.FakeDevice`, `FakeDeviceGate`); the `GAS_FAKE_DEVICE`
  const is a UTF-16 user-string literal, so it is deliberately **not** used as a byte-grep marker (would be missed).
- `release.yml` is **unchanged** and still never passes `-p:FakeDevice`.

## Bugs / Issues Found

- None. Pre-flight design checks confirmed: `ExifMetadataExtractor.Extract` swallows all exceptions →
  `MediaMetadata.Empty` → mtime fallback, so the real EXE exits **0** on the synthetic `small` library; and
  `disconnect-after=N` throws `DeviceConnectionLostException` on the (N+1)th open → caught in the copy loop →
  **exit 3** (the escape-hatch's `TerminateProcess` path also uses exit 3, so the code is 3 either way).

## Self-review (§13.6)

Ran the `code-review` skill (independent default subagent, not Explore) on the diff →
**PASS-WITH-NITS** (0 BLOCKER · 0 MAJOR · 2 MINOR · 6 NIT). Full report: `docs/review/sprint-3.6-review.md`.
All three extra-attention items confirmed: the guard is **real** (publishes the genuine artifact + greps,
empirically flips both ways), the E2E **spawns the real `.exe`** (separate OS process, not `dotnet run`/in-process),
and the seam is **off-by-default + un-abusable** (double-gated; spec parsed defensively, presets only).

**Folded in pre-push:**
- **MINOR-1 (guard self-blindness):** added a **positive control** to the Release guard — it now also asserts
  the EXE bytes *do* contain `GetAndSee.Core`; if even that is missing the publish layout changed and the
  absence-only scan has gone blind, so the job fails loudly instead of passing vacuously. Re-verified: normal
  publish → positive-control `True`, both fake markers `False`, `leaked: 0` (PASS).
- **NIT (diagnosability):** `ProcessRunner` now captures partial stdout/stderr on the timeout-kill path and
  includes it in the `TimeoutException`.
- **NIT (advisory coverage):** the `coverage` job also gets `--filter "Category!=E2E"` so the E2E build+spawn
  doesn't run a second time for no coverage gain.
- **NIT (clarity):** documented in `AssertArchiveByteIdentical` why the expected layout is mtime-driven
  (ExifMetadataExtractor returns `MediaMetadata.Empty` on synthetic bytes).

**Tracked, deliberately NOT done (would violate the byte-identical rule / anti-pattern to refactor shipped
code in a test-infra PR):** MINOR-2 — `CopyCommand.ExecuteAsync` extraction stays a drift-watch follow-up
(the shipped `#else` body is unchanged). Recorded in `docs/review/review-profile.md`.
