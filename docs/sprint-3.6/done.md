# Sprint 3.6 — Done: productionize the `GAS_FAKE_DEVICE` EXE seam (real-`.exe` E2E)

> Handoff doc. Branch `feature/fake-device-exe-seam` (PR #57, continued from the spike; re-titled from
> `[SPIKE]`). `main` merged in first (#56 harness + #59 hardened CI + #62). ONE PR, **stopped at the producer
> review gate — not merged**. Owners: Sage (seam/device), Nova (CLI/E2E), Ivy (E2E asserts).

## The boundary (HARD RULE — read this first)

**This seam runs the real `get-and-see.exe` against a *software* fake. It is still not a cable yank.**

The real-`.exe` E2E proves the shipped binary's **process-level** contract — its exit codes and a
cross-process resume — driving the actual CLI against the `GAS_FAKE_DEVICE` fake with no hardware. It does
**not** reproduce the native `afc_file_close` core-pin or the real `TerminateProcess`, and it is **not** an
iPhone. **No hardware-parity is claimed.** The rare hardware cable-yank smoke (one yank → exit 3 + CPU to
idle, from `docs/sprint-3.5/done.md`) remains the authoritative native-disconnect check.

## What this seam tests vs. what the in-process harness covers (no overlap claim)

| | In-process harness (Sprint 3.5) | Real-`.exe` E2E (this sprint) |
|---|---|---|
| What runs | the copy **engine** (enumerate → journal → organizer → copier → watchdog → escape-hatch → summary), in the test process | the **shipped `get-and-see.exe`** as a separate OS process |
| Restarts a process? | **No** (can't — it's one process) | **Yes** — the whole point |
| Asserts | byte-identical copy/resume of the engine logic across every managed disconnect *shape*, fast | the real process's **exit codes** (0 clean / 3 lost-device) and **cross-process resume** (run → interrupt → re-run → byte-identical, prior files skipped) |
| Speed / lane | fast; runs in the gating unit job | slow (builds + spawns); its **own** `exe-e2e` job + `[Trait("Category","E2E")]` |

They are **complementary, not redundant**: the harness owns the fast inner-loop matrix; this lane owns the
one thing it structurally cannot do — restart the real binary.

## What the spike already nailed — KEPT (mechanism unchanged)

- **`#if FAKE_DEVICE` gating.** The seam compiles in **only** with `-p:FakeDevice=true`; the
  `GetAndSee.FakeDevice` `ProjectReference` is `Condition="'$(FakeDevice)'=='true'"`. A normal/release build
  defines nothing and references nothing → the shipped `get-and-see.exe` is **byte-identical** and contains
  no fake. `FakeDeviceGate.TryCreate()` reads `GAS_FAKE_DEVICE`; unset → `null` → real `AfcIPhoneClient`.
- **The fake is unified** in `src/GetAndSee.FakeDevice` (one assembly; the harness references the same one).
- **`FakeDeviceSeamTests`** unit tests (preset/modifier parsing, malformed → `FormatException`, gate-off-when-unset).

## The two gaps closed

### Gap 1 — the CI byte-identical guard (the load-bearing safety proof)
The csproj comment promised "CI asserts the Release output carries no `GetAndSee.FakeDevice` reference" — now
it's real. A **Release guard** step in the existing **`publish-smoke`** job (job name unchanged so required-check
enforcement keeps working) publishes the **normal** Release single-file EXE (`release.yml`-identical, **no**
`-p:FakeDevice`) and asserts the shipped artifact is fake-free:
- no `GetAndSee.FakeDevice*` file in the publish output (defends a non-single-file layout);
- the published EXE's bytes contain neither `GetAndSee.FakeDevice` (the assembly name in `get-and-see.dll`'s
  `AssemblyRef`) nor `FakeDeviceGate` (the seam type) — both UTF-8 metadata strings, present verbatim in the
  uncompressed single-file bundle **iff** `-p:FakeDevice=true` was used;
- a **positive control**: the bytes *must* contain a known-present name (`GetAndSee.Core`), else the scan has
  gone blind (publish layout changed) and the job fails loudly rather than passing vacuously.

**Proven load-bearing locally, both directions:** normal publish → `leaked: 0`, positive-control `True`, both
fake markers `False` → **PASS**; `-p:FakeDevice=true` publish → `leaked: 1`, both markers `True` → **FAILS**.

### Gap 2 — the real-process E2E (the seam's whole point)
`tests/GetAndSee.Tests/E2E/`:
- **`FakeDeviceExeFixture`** (class fixture) builds the `-p:FakeDevice=true` EXE **once** to an isolated temp
  dir via `-o` (never overwrites a normal build; deleted on dispose). The implicit restore runs **with**
  `FakeDevice=true` so the conditional project reference resolves.
- **`ProcessRunner`** spawns the child via `ProcessStartInfo.ArgumentList` (no shell, no string concat → no
  quoting/injection surface), drains stdout+stderr concurrently with the wait (no full-pipe deadlock), and a
  per-run timeout kills the process tree (capturing partial output) so a wedged subprocess fails fast.
- **`FakeDeviceExeE2ETests`** (`[Trait("Category","E2E")]`), each spawning the **real EXE** with `GAS_FAKE_DEVICE`:
  1. **clean (`small`) → exit 0** + `summary.txt` on disk + archive **byte-identical** (`FakeContent` recompute).
  2. **`small;disconnect-after=2` → exit 3** (real process; `DeviceConnectionLostException` → exit-3 path).
  3. **cross-process resume:** run 1 interrupted (exit 3) → run 2 healed (exit 0), `Skipped: ≥2` parsed from
     the real run summary, archive **byte-identical**. Two separate process invocations.

### CI lane wiring
- Gating **`build + test`** job: `--filter "Category!=E2E"` (the fast unit suite is untouched; `!=` still runs
  every untagged test). Advisory **`coverage`** job: same filter (no double E2E run).
- New gating **`exe-e2e`** job runs `--filter "Category=E2E"` (the fixture builds the EXE), `timeout-minutes: 20`,
  uploads `.trx`/`.dmp` on failure.

## How byte-identical / read-only / off-by-default stay true

- **Byte-identical release — PROVEN** by the Gap-1 guard (positive + negative controls, shown to flip).
  `release.yml` is **unchanged** and never passes `-p:FakeDevice`.
- **Off by default** — no `GAS_FAKE_DEVICE` ⇒ real `AfcIPhoneClient`. In a normal build the `#if FAKE_DEVICE`
  block isn't even compiled, so the env var is never read.
- **Read-only** — the fake returns only an `IPhoneClient` (read-only surface), makes no real-device call;
  `ReadOnlyContractTests` untouched + green. The `GAS_FAKE_DEVICE` spec is parsed defensively (malformed →
  clean `FormatException`); it only selects in-memory presets/modifiers — no path/file/injection surface.

## Files changed

- `.github/workflows/ci.yml` — Release guard step in `publish-smoke`; `--filter "Category!=E2E"` on the gating
  `build + test` + advisory `coverage` jobs; new `exe-e2e` job.
- `tests/GetAndSee.Tests/E2E/ProcessRunner.cs` *(new)* — process spawn + capture + timeout-kill.
- `tests/GetAndSee.Tests/E2E/FakeDeviceExeE2ETests.cs` *(new)* — fixture (builds the fake EXE) + 3 E2E tests.
- `docs/sprint-3.6/progress.md`, `docs/sprint-3.6/done.md` *(new)*; `docs/review/sprint-3.6-review.md` *(self-review)*;
  `docs/review/review-profile.md` (Persist update); `PROJECT_BRIEF.md` §7 + §8.
- **No `src/` change** — the shipped binary is byte-identical (the spike's seam in `CopyCommand.cs` /
  `GetAndSee.FakeDevice` predates this sprint and is unchanged here).

## Validation (local, mirrors CI)

- `dotnet build -c Release` → **0 warning / 0 error**; `dotnet format --verify-no-changes` → clean.
- Fast lane (`--filter "Category!=E2E"`): **174 GetAndSee.Tests + 2 SafetyTests passed**, 0 failed.
- E2E lane (`--filter "Category=E2E"`): **3 passed** (builds the fake EXE + spawns it).
- **Total: 179** — 176 fast-lane (174 + 2) + 3 real-`.exe` E2E.
- Release guard verified PASS on a normal publish and FAIL on a `-p:FakeDevice=true` publish.

## Self-review (§13.6)

Independent `code-review` skill (default subagent) → **PASS-WITH-NITS** (0 BLOCKER · 0 MAJOR · 2 MINOR · 6 NIT);
all three extra-attention items confirmed. MINOR-1 (guard positive control) + the cheap NITs folded in; MINOR-2
(`CopyCommand.ExecuteAsync` extraction) tracked on the drift-watch, deliberately not refactored here (byte-identical
rule). Report: `docs/review/sprint-3.6-review.md`.

## Not done / next

- **Producer review gate** (independent) + this is where it stops. This touches shipped-startup safety, so it
  gets the full gate with extra weight on the byte-identical guard.
- No new feature work; Sprint 4 (organize & find) rides on **both** the in-process harness and this real-`.exe` E2E.
