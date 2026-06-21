# Sprint 3.5 — Test harness: fake device & fault injection (kill the hardware bottleneck)

**Type:** infrastructure / test · **Gates:** none (enables the feature batch) · **Branch:** `feature/sprint-3.5` (dev, off `main`)
**Owner:** Dev — Sage (device/fake) + Ivy (QA/fault matrix) + Nova (E2E/CLI) · **Design:** `docs/brainstorm/fake-device-harness.md` · **Re-acceptance:** producer gate (it *is* the test infra)

> **Read first:** `docs/brainstorm/fake-device-harness.md` (the converged design), `PROJECT_BRIEF.md` §9 (read-only contract) + §13.6 (three-layer review), `docs/review/review-profile.md`, and the existing fakes in `tests/GetAndSee.Tests/TestSupport/`.

## Why we're here (CEO ask)

v1 needed heavy manual hardware QA — the disconnect bug alone took **five real cable-yank rounds**. The
feature batch ahead (organize/search, reorganize, wizard) will churn the copy pipeline. Build the automated
inner-loop net **now**: a scriptable **fake device** the whole pipeline runs against, deterministically, in
CI — so iteration stops depending on a phone.

## Scope (this sprint)

1. **`FakeAfcDevice : IPhoneClient`** — a full fake implementation of the 5-member read-only contract
   (`Device`, `ConnectAsync`, `ListDirectoryAsync`, `GetFileInfoAsync`, `OpenReadAsync`, `IDisposable`),
   in a **test-support assembly only** (never in `GetAndSee.Core`/`Cli`). Backed by a fluent **virtual `/DCIM`
   spec**: files with path / size / EXIF capture-date / deterministic content (seeded), plus small and
   large-ish library presets, `unsorted`-eligible (no/old date) files, and Live-Photo pairs.
2. **Fault injection** — a deterministic, scriptable model keyed by **call + file + byte-offset**, able to
   inject: `slow read`, `parked read` (a *real* blocking Task, not a stall double), `empty/0-bytes`,
   `premature-EOF` (short-return before the declared size), `connection-fatal AfcError`, a
   `between-file failure burst`, and `disconnect-at-file-N`. Driven by `FakeTimeProvider` so time is exact.
3. **In-process E2E harness (MUST-HAVE)** — drives the **real** copy pipeline (the `CopyCommand` core /
   `FileCopier` + journal + organizer + watchdog/escape-hatch) with the fake injected, and asserts
   end-to-end outcomes with **no device**: full-library copy correctness, **resume byte-identical** after an
   injected interruption, organize layout, collisions (`_2/_3`), `unsorted`, `--verify-hash`, the
   **watchdog trip → exit-3 logic**, the between-file breaker, and premature-EOF handling.
4. **Fold in** the existing ad-hoc fakes (`SpinOnDisposeStream`, `ChunkThenStallReadStream`,
   `ControlledReadStream`) so there is **one** fault model — preserving the exact spin/park repros.

### Stretch (only if cheap + provably gated)
5. **Env-var EXE seam** `GAS_FAKE_DEVICE=<spec>` — swaps the real client for the fake so the **actual
   `get-and-see.exe`** runs end-to-end against the fake (real exit codes + **cross-process resume**: run,
   interrupt, re-run, assert byte-identical). **Hard gating:** a normal run (no env var) is byte-identical and
   never constructs the fake; the seam makes **no** real-device call and can never become a write path; decide
   placement so the fake does **not** bloat or endanger the shipped binary (e.g. a separate opt-in assembly /
   debug-only path — dev's design call). If it can't be made clean and cheap, **defer it** — the in-process
   harness is the win.

## The honest boundary (HARD RULE — book the round-4 lesson)

The fake is **managed code**. It reproduces every *managed-observable* failure (empty/parked/premature/
connection-fatal/between-file/disconnect-at-N), exercising the watchdog → escape-hatch → **exit-3 → resume**
*logic* deterministically. It does **NOT** reproduce the real native `afc_file_close` **core-pin** or the real
`TerminateProcess` kill. So a **small, rare hardware smoke** (one cable-yank → exit 3 + CPU drops to idle)
**remains the authoritative check for native disconnect** — it just shrinks from a full matrix to a single
confirmation. **State this explicitly in `done.md`.** A green harness is not a cable yank.

## Hard rules (non-negotiable)

- **The fake + harness live in TEST assemblies only.** **No** behavior change in `GetAndSee.Core`/`Cli` that
  ships. `ReadOnlyContractTests` stays green (the fake is in test code; the shipped Core gains no
  device-mutation symbol). If the stretch env-var seam is built, the **default/real run must be byte-identical**
  and the seam cannot reach a real device or become a write path.
- **The fake is read-faithful:** sizes, EXIF dates, content, and `RemoteFileInfo` match what the pipeline
  expects, so a harness pass is meaningful. Determinism: seeded content + `FakeTimeProvider`.
- No new `var`; XML `<summary>` on new public test-support types where it aids reuse; `dotnet format` + build
  0/0 + tests green.
- **NEW — dev self-review before the PR** (§13.6): run the `code-review` skill (default subagent, not
  `Explore`) on your diff with a *find-problems* framing — extra attention on **read-only contract intact**,
  **shipped binary unaffected**, and the env-var seam's gating if built. Summarize findings in the PR.

## Acceptance criteria

- [ ] `FakeAfcDevice` implements the full `IPhoneClient` contract, backed by a fluent virtual `/DCIM` spec (presets + Live-Photo pairs + unsorted-eligible files), in a test assembly.
- [ ] Fault injection supports slow / parked / empty / premature-EOF / connection-fatal / between-file-burst / disconnect-at-N, deterministically (`FakeTimeProvider`).
- [ ] In-process E2E harness asserts, with no device: full-copy correctness, **resume byte-identical** after an injected mid-copy interruption, organize layout, collisions, `unsorted`, `--verify-hash`, **watchdog trip → exit-3 logic**, between-file breaker, premature-EOF.
- [ ] Each prior disconnect manifestation's **managed shape** (park, between-file, premature-EOF/empty) is reproduced in CI ending in exit-3 logic + byte-identical resume.
- [ ] Existing ad-hoc fakes folded into the one fault model; their exact repros preserved.
- [ ] `ReadOnlyContractTests` green; **shipped `get-and-see.exe` behavior unchanged**; build 0/0; CI green; dev self-review summarized in the PR.
- [ ] `done.md` states the **native-disconnect hardware-smoke boundary** explicitly.

## Sequencing

Recommended: **Sprint 3.5 runs next, before Sprint 4** (organize/search) — so the whole feature batch is built
on the harness and tested against it. Sprint 4 then *uses* the harness (organize layout + resume + search E2E
land as fake-device tests). Order: **3.5 (harness) → 4 (organize & find) → 4.1 (reorganize) → 5 (wizard).**

## Process

Dev on `feature/sprint-3.5` off `main`. Update `docs/sprint-3.5/progress.md` + `done.md` (incl. the boundary
statement). Self-review → open ONE PR `sprint-3.5: fake-device test harness` → STOP for the producer gate.
Never push to `main`.

---

## Dev-session prompt (paste into a fresh `ai-team-dev` session — NOT a subagent)

```
You are the get-and-see dev team (Sage leads — device/fake; Ivy — fault matrix; Nova — E2E). Implement
Sprint 3.5: a FAKE-DEVICE TEST HARNESS so the whole pipeline can be tested without a real iPhone, to speed up
the inner loop. Real CODE: implement, self-review, open ONE PR, STOP for the producer gate. Do NOT merge.
Never push to main.

WORK IN: e:\src\get-and-see-dev
  git fetch origin && git checkout main && git pull
  git checkout -b feature/sprint-3.5
Read: docs/sprint-3.5/plan.md (this brief), docs/brainstorm/fake-device-harness.md (design), PROJECT_BRIEF.md
§9 (read-only contract) + §13.6, docs/review/review-profile.md, and tests/GetAndSee.Tests/TestSupport/ (the
existing fakes: SpinOnDisposeStream, ChunkThenStallReadStream, ControlledReadStream).

THE SEAM: IPhoneClient (src/GetAndSee.Core/Device/IPhoneClient.cs) is the read-only contract — 5 members:
Device { get; }, ConnectAsync, ListDirectoryAsync, GetFileInfoAsync, OpenReadAsync, IDisposable. The fake
implements exactly these.

DELIVER:
1. FakeAfcDevice : IPhoneClient in a TEST-SUPPORT assembly ONLY (never GetAndSee.Core/Cli — the shipped binary
   and read-only contract stay pristine). Back it with a fluent virtual /DCIM spec: files with
   path/size/EXIF-date/seeded-content; small + large-ish presets; unsorted-eligible (no/old date) files; Live
   Photo pairs. GetFileInfoAsync/ListDirectoryAsync return spec-faithful data; OpenReadAsync returns a
   fault-scriptable read stream.
2. FAULT INJECTION — deterministic, scriptable by call + file + byte-offset: slow read; PARKED read (a REAL
   blocking Task, not a stall double); empty/0-bytes; premature-EOF (short-return before declared size);
   connection-fatal AfcError; between-file failure burst; disconnect-at-file-N. Drive time with
   FakeTimeProvider. Fold SpinOnDisposeStream/ChunkThenStallReadStream/ControlledReadStream INTO this one
   fault model (preserve their exact spin/park repros).
3. IN-PROCESS E2E HARNESS (must-have): drive the REAL copy pipeline (CopyCommand core / FileCopier + journal +
   organizer + watchdog/escape-hatch) with the fake injected; assert with NO device: full-copy correctness,
   RESUME byte-identical after an injected mid-copy interruption, organize layout, collisions _2/_3, unsorted,
   --verify-hash, WATCHDOG TRIP -> exit-3 logic, between-file breaker, premature-EOF. Each prior disconnect
   manifestation's MANAGED shape (park / between-file / premature-EOF) reproduced in CI ending exit-3 +
   byte-identical resume.

STRETCH (only if cheap + provably gated): env-var seam GAS_FAKE_DEVICE=<spec> so the REAL get-and-see.exe runs
end-to-end against the fake (real exit codes + cross-process resume). HARD gating: normal run (no env var) is
byte-identical and never builds the fake; the seam makes no real-device call and can never be a write path;
keep the fake OUT of the shipped binary's normal path (separate opt-in/debug-only — your design). If it can't
be clean + cheap, DEFER it and say so.

HONEST BOUNDARY (hard rule — book the round-4 lesson): the fake is managed code. It reproduces managed-
observable failures (empty/parked/premature/connection-fatal/between-file/disconnect-at-N) and the exit-3 ->
resume LOGIC. It does NOT reproduce the real native afc_file_close core-pin or the real TerminateProcess kill
— a small hardware smoke stays authoritative for native disconnect. STATE THIS in done.md. Do NOT claim
hardware parity.

HARD RULES: fake + harness in TEST assemblies ONLY; NO shipped-binary behavior change; ReadOnlyContractTests
green; if the env-var seam is built it must be gated so the default run is byte-identical and can't reach a
real device / become a write path; no new var; XML <summary> on reusable public test-support types; CI green.

SELF-REVIEW BEFORE PUSHING (§13.6): run the code-review skill (default subagent, NOT Explore) on your diff,
find-problems framing — extra attention on read-only contract intact, shipped binary unaffected, env-var seam
gating (if built), and the fault model being deterministic (no flaky timing). Fix what it finds; SUMMARIZE in
the PR.

PROCESS: update docs/sprint-3.5/progress.md + done.md (incl. the native-disconnect boundary). Open ONE PR
"sprint-3.5: fake-device test harness" then STOP for the producer review gate.

REPORT BACK: the FakeAfcDevice + spec API; the fault taxonomy + how each is injected deterministically; the
E2E scenarios now automated (esp. resume byte-identical + the disconnect managed-shapes); whether you built or
deferred the env-var seam (and why); how the read-only contract + shipped binary stay untouched; the
self-review summary; dotnet test counts + CI; the PR number.
```
