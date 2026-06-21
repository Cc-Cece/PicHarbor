# Brainstorm — A fake-device test harness (kill the hardware bottleneck for the inner loop)

> Multi-agent consilium convened by Remy (Producer) on the CEO's ask:
> *"Can developers deliver a mocking system so we can test without a real device? We did the manual work
> for v1 — now speed up the inner loop with more automation."*
> **Status: ideation → converged design. Decisions are the CEO's.**

## Why now

v1.0/v1.0.1 shipped on the back of **heavy manual hardware QA** — the mid-copy disconnect bug alone took
**five real-cable-yank rounds** (#11 → #25 → #38 → #42 → #45), each one a slow hardware loop. The feature
batch ahead (organize/search, reorganize, the wizard) will churn the copy pipeline repeatedly. Without an
automated end-to-end net, every iteration risks another slow hardware round. The single highest-leverage
automation investment is a **scriptable fake device** the whole pipeline can run against, deterministically,
in CI.

We already have *unit-level* mocks (NSubstitute `IPhoneClient` + ad-hoc fake streams: `SpinOnDisposeStream`,
`ChunkThenStallReadStream`, `ControlledReadStream`). What's missing is a **cohesive, scriptable device** that
drives the **whole** pipeline end-to-end (enumerate → organize → copy → journal → resume → watchdog →
escape-hatch) with **injectable faults**.

## The voices

### Ivy (QA) — the biggest beneficiary, and the keeper of the honesty boundary
> I've run the unplug matrix on real hardware five times. Most of what I test is **deterministic logic** that
> a fake could exercise in seconds: full-library copy correctness, **resume byte-identical**, organize layout,
> collisions, `unsorted`, `--verify-hash`, the watchdog **trip → exit 3** decision, the between-file breaker,
> premature-EOF handling. Give me a fake device that can **script every managed-observable failure** — slow
> read, parked read, `EmptyResponse`/0-bytes, premature EOF, connection-fatal `AfcError`, a burst of
> between-file failures, "disconnect at file N / byte offset Z" — and ~90% of my matrix moves into CI.
>
> **But** I am the one who proved *harness conclusion ≠ hardware truth* (round 4: we "concluded" a
> non-returning read by elimination; hardware falsified it — the spin was in native `afc_file_close`). So I
> will say it loudly: **a managed fake CANNOT reproduce the real native core-pin or the real
> `TerminateProcess`.** The harness must model the *logic* of disconnect handling, and we keep a **small,
> rare hardware smoke** for the native reality. Automate the matrix; do **not** let a green harness be mistaken
> for a cable yank.

### Sage (Backend / device) — owns the seam and the fake
> The `IPhoneClient` interface is already the seam — that's the whole point of having built it read-only and
> abstract. I'll add a **`FakeAfcDevice : IPhoneClient`** in a **test-support assembly** (never in
> `GetAndSee.Core`/`Cli` — the shipped binary and the read-only contract stay pristine), backed by a fluent
> **virtual `/DCIM` spec** (files with path/size/EXIF-date/content; presets for a small and a large-ish
> library). Faults are a scriptable hook keyed by **call type + file + byte offset**: each read can be told to
> be slow, to block (a *real* parked Task), to return empty, to short-return before the declared size, or to
> throw a connection-fatal error. Deterministic via `FakeTimeProvider`. The existing `SpinOnDisposeStream` /
> `ChunkThenStallReadStream` repros fold into this fault model so we're not maintaining two zoos.

### Nova (CLI) — wants the real EXE under test
> The cheapest, fastest layer is an **in-process E2E harness**: construct the real copy pipeline with the fake
> injected and assert outcomes — no phone, milliseconds. That covers the logic. **But** there's real value in
> running the **actual `get-and-see.exe`** against the fake too: it's the only way to assert the real
> **exit codes**, the dashboard, and **resume across a process restart** (kill mid-run, re-run, verify
> byte-identical). I'd add a **hidden, env-var-gated seam** (`GAS_FAKE_DEVICE=<spec>`) that swaps the real
> client for the fake — tightly gated so a normal run is byte-identical and the seam can never touch a real
> device. Treat it as a **scoped stretch**, not the core.

### Remy (Producer) — scope and the boundary
> Converged. The **must-have** is the fake + fault-injection + in-process E2E harness — that's the inner-loop
> win and the regression net for the whole feature batch. The **env-var EXE seam is a stretch**: do it only if
> it's cheap and provably gated (it ships in the binary, so footprint + safety matter). And we **book Ivy's
> boundary as a hard rule**: the harness covers managed-observable behavior; a small hardware smoke stays for
> the native core-pin + `TerminateProcess`. No full device emulator, no gold-plating — model only the
> behaviors the pipeline actually depends on.

## Converged design

1. **`FakeAfcDevice : IPhoneClient`** in a **test-support assembly only** (zero shipped-binary change), backed
   by a fluent **virtual `/DCIM` spec** (path, size, EXIF date, content/seed; small + large-ish presets).
2. **Fault injection** — a deterministic, scriptable model: at a given call / file / byte-offset, inject
   `{ slow, park (real blocking Task), empty/0-bytes, premature-EOF, connection-fatal AfcError,
   between-file-failure-burst, disconnect-at-N }`. Driven by `FakeTimeProvider`.
3. **In-process E2E harness (MUST-HAVE)** — drives the **real** copy pipeline with the fake injected and
   asserts end-to-end: full-copy correctness, **resume byte-identical**, organize schemes, collisions,
   `unsorted`, `--verify-hash`, **watchdog trip → exit-3 logic**, between-file breaker, premature-EOF. All in
   CI, deterministic, no phone.
4. **Env-var EXE seam (STRETCH)** — `GAS_FAKE_DEVICE=<spec>` swaps the real client for the fake so the actual
   `get-and-see.exe` can be tested end-to-end (real exit codes + cross-process resume). **Tightly gated**: a
   normal run is byte-identical; the seam makes no real-device call; it cannot become a write path.
5. **Fold in** the existing ad-hoc fakes so we maintain one fault model, preserving the exact spin/park repros.

## The honest boundary (hard rule — book the round-4 lesson)
The fake runs in **managed code**. It reproduces every *managed-observable* failure (empty/parked/premature/
connection-fatal/between-file), which exercises the watchdog → escape-hatch → **exit-3 → resume** *logic*
deterministically. It does **NOT** reproduce the real **native `afc_file_close` core-pin** or the real
**`TerminateProcess`** finalizer-skipping kill. Therefore a **small, rare hardware smoke** (one cable-yank →
exit 3 + CPU drops to idle) **remains the authoritative check for native disconnect** — but it shrinks from a
full multi-scenario matrix to a single confirmation. **A green harness is not a cable yank.**

## What it buys us
- Ivy's manual matrix → **mostly CI**; the hardware pass shrinks to a small native-disconnect smoke.
- Every future sprint (organize/search, reorganize, wizard) gets an **automated end-to-end regression net**.
- Disconnect-handling **logic** regressions are caught in seconds, in CI, not in a hardware round.

## Open question for the CEO
- **Sequencing:** do the harness **first** (as **Sprint 3.5**, before the organize/search Sprint 4) so the
  whole feature batch is built on top of it — recommended — or run it in parallel?
- **Env-var EXE seam (stretch #4):** want true cross-process-resume tests of the real binary, or keep the
  harness purely in-process for now?

## Proposed sprint
**Sprint 3.5 — "Test harness: fake device & fault injection"** (infra; before Sprint 4). Plan:
`docs/sprint-3.5/plan.md`.
