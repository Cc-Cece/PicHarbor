# Sprint 3.5 — Done: fake-device test harness

> Handoff doc. Branch `feature/sprint-3.5` off `main`. ONE PR, stopped at the producer review gate
> (it *is* the test infra). Owners: Sage (device/fake), Ivy (fault matrix), Nova (E2E).

## The native-disconnect boundary (HARD RULE — read this first)

**The fake is managed code. A green harness is NOT a cable yank.**

The fake reproduces every **managed-observable** failure — empty/0-byte, parked read, premature-EOF,
connection-fatal error, between-file failure burst, disconnect-at-file-N, and the **spinning-close**
analogue — and exercises the watchdog → escape-hatch → **exit-3 → resume** *logic* deterministically in CI.

It does **NOT** reproduce:
- the real **native `afc_file_close` core-pin** (a synchronous native call busy-spinning a core on a dead
  USB transport). `ScriptedReadStream` models the *shape* (a `SpinWait` busy-loop in `Dispose` that only
  process termination ends), but a managed `SpinWait` is not the native libimobiledevice call.
- the real **`TerminateProcess`** finalizer-skipping kill. The harness uses a `RecordingProcessTerminator`
  that records the exit code instead of killing the host.

**Therefore a small, rare hardware smoke (one cable-yank → exit 3 + CPU drops to idle) remains the
authoritative check for native disconnect.** It shrinks from a full multi-scenario matrix to a single
confirmation — but it does not go away. Do not claim hardware parity from a green harness.

## What shipped

A scriptable fake device + fault model + in-process E2E harness, **entirely in the test assembly**
`GetAndSee.Tests` (`tests/GetAndSee.Tests/TestSupport/` + `tests/GetAndSee.Tests/Harness/`). No file under
`src/` was added or changed.

### The `FakeAfcDevice` + spec API
```csharp
FakeDeviceSpec spec = FakeDeviceSpec.Create()
    .WithDevice("00008101-…", "Test iPhone", "iPhone13,3")   // synthetic identity only
    .AddSmallPhoto("/DCIM/100APPLE/IMG_0001.HEIC", aug2024)  // small dated photo preset
    .AddLargeVideo("/DCIM/126APPLE/IMG_6834.MOV", sep2024)   // >1 MiB multi-chunk preset
    .AddUnsorted("/DCIM/100APPLE/SCRATCH.DAT")               // no date → unsorted/
    .AddLivePhotoPair("/DCIM/101APPLE/IMG_0101", sep2024)    // .HEIC + .MOV, same folder
    .DisconnectAfterFiles(2);                                // device-level disconnect-at-N
FakeAfcDevice device = spec.Build(fakeClock);
```
- Implements the full 5-member read-only contract (`Device`, `ConnectAsync`, `ListDirectoryAsync`,
  `GetFileInfoAsync`, `OpenReadAsync`, `IDisposable`) — **read-only surface only**, nothing to mutate.
- Read-faithful: listing/stat/open all answer from the same spec the harness asserts against.
- Content is deterministic and lazy via `FakeContent.Fill(span, offset, seed)` — a 400 MB declared size
  costs nothing until read, and a test recomputes the exact bytes for a byte-identical / SHA-256 check.

### The fault taxonomy + how each is injected deterministically
Per-file `ReadFault` on a `ScriptedReadStream` (keyed by byte offset), time driven by `FakeTimeProvider`:

| Fault | How it's injected | Pipeline outcome |
|-------|-------------------|------------------|
| `Slow` | deliver full content in 64 KiB chunks | copies fine; never trips the watchdog |
| `Empty` | first read returns 0 | size mismatch → file failed |
| `ParkAfter(n, observeCancellation)` | deliver `n` bytes, then block on a **real** `Task` | watchdog inactivity trip → exit 3 |
| `PrematureEofAfter(n)` | deliver `n`, then return 0 short of declared size | size mismatch → file failed (retryable) |
| `ConnectionFatalAfter(n)` | deliver `n`, then throw `DeviceConnectionLostException` | run stops → exit 3 |
| `PerFileErrorAfter(n)` | deliver `n`, then throw a non-fatal `DeviceException` | this file failed; run continues |
| `SpinOnDisposeAfter(n)` | deliver `n`, premature-EOF, then `SpinWait` in `Dispose` | escape-hatch terminate → exit 3 (#45 shape) |

Device-level (on the spec): `DisconnectAfterFiles(n)` (every open after `n` throws connection-fatal —
disconnect-at-N) and `FailEveryReadWith(fault)` (a whole-device burst — drives the between-file breaker).

### The E2E scenarios now automated (no device, in CI)
`CopyPipelineHarness` drives the real `DcimEnumerator` → `TransferJournal` → `DateFolderOrganizer` →
`FileCopier` (→ `ForwardProgressWatchdog`, `AbandonableReadStream`, `DisconnectEscapeHatch`) →
`SummaryWriter`. The 10 `CopyPipelineHarnessTests`:
1. **Full-library copy** — every file byte-identical, all `done`, manifest + `summary.txt` written.
2. **Organize layout** — `YYYY/YYYY-MM`, `unsorted/`, and Live-Photo pair co-located.
3. **Collisions** — `_2`/`_3` suffixes, never overwritten (distinct contents).
4. **`--verify-hash`** — SHA-256 recorded in the manifest for every file.
5. **Connection-loss mid-copy → exit 3 → RESUME byte-identical** (in-flight file left `in_progress`).
6. **Watchdog trip on a parked read → exit 3** (+ escape-hatch terminator) **→ resume byte-identical**.
7. **Between-file failure burst → breaker → exit 3 → resume** (stops early, doesn't churn every file).
8. **Premature-EOF → file failed → retry completes it** byte-identical.
9. **Spinning-close (#45 managed shape) → escape-hatch terminate exit 3 → resume** byte-identical.
10. **Disconnect-at-file-N → exit 3 → resume** (the 2 done files skipped).

Each prior disconnect manifestation's **managed shape** (park, between-file, premature-EOF/empty,
spinning-close) is reproduced ending in the exit-3 logic + a byte-identical resume.

### Fold-in (one fault model)
`ScriptedReadStream` subsumes the three ad-hoc fakes; all 9 call sites migrated and the three files deleted
(`ControlledReadStream`, `ChunkThenStallReadStream`, `SpinOnDisposeStream`). The exact park (#11/#42) and
spinning-close (#45) repros are preserved — `FileCopierTests`, `Sprint34EscapeHatchTests`, and
`AbandonableReadStreamTests` are green against the unified model.

## How the read-only contract + shipped binary stay untouched
- Every new/changed `.cs` is under `tests/`. `GetAndSee.Core` and `GetAndSee.Cli` have **no** source change,
  so `get-and-see.exe` is byte-identical and gains no fake/test code.
- `ReadOnlyContractTests` (Mono.Cecil IL scan of `GetAndSee.Core`) is **green** — the fake adds no
  device-mutation symbol to the shipped assembly because it isn't in the shipped assembly. The fake itself
  exposes only the read-only contract (no write/delete/rename surface exists to call).

## Stretch: env-var EXE seam — DEFERRED
`GAS_FAKE_DEVICE=<spec>` (run the real `get-and-see.exe` against the fake for real exit codes +
cross-process resume) is **deferred**, per the brief's "defer if it can't be clean and cheap":
- The fake lives in the test assembly. Wiring the real EXE to it cleanly needs a **new opt-in/debug-only
  assembly** plus a **reflection/plugin seam** in `CopyCommand`/`Program`, plus gating tests proving the
  default run never constructs the fake and can never reach a real device or a write path. That is not
  cheap, and it edits the shipped CLI startup — risking the "default run is byte-identical" hard rule.
- The unique value (real process exit codes + cross-process resume) is **largely already covered**: the
  in-process harness asserts the exit-3 *logic* and **byte-identical resume** (same journal, same code), and
  the CLI smoke (`--help` exit 0, in CI) + QA hardware cover real process exit. The marginal gain didn't
  justify touching the shipped binary.
- **Clean follow-up if wanted:** a separate `GetAndSee.FakeDevice` opt-in assembly + an env-var-gated
  factory seam in the CLI, with a test that a normal run constructs the real client and never the fake.

## Self-review (§13.6)
Ran the `code-review` skill (default subagent, find-problems framing) on the diff, with extra attention on:
read-only contract intact, shipped binary unaffected, the deferred env-var seam, and the fault model being
deterministic (no flaky timing). Findings + dispositions are summarized in the PR description.

## Gates (local, mirrors CI)
- `dotnet build -c Release` → 0/0. `dotnet test -c Release` → **162 passed** (160 + 2 safety), 0 failed,
  `ReadOnlyContractTests` green. `dotnet format --verify-no-changes` → clean.

## For the next sprint (Sprint 4 — organize & find)
Sprint 4 should build its organize-layout/resume/search E2E on this harness: add `--organize-by` schemes by
extending the spec's expected-path helper, and assert per-scheme layout + byte-stable resume + (later)
`search` against the manifest — all in-process, no device.
