# Sprint 3.5 — Progress

> Live tracker for the fake-device test harness. Branch `feature/sprint-3.5` off `main`.
> Owners: Sage (device/fake), Ivy (fault matrix), Nova (E2E).

## Status: dev complete — self-review done, ONE PR open, stopped at the producer gate.

## What was built (all in `tests/` — zero shipped-binary change)

All new code lives in `tests/GetAndSee.Tests/` (a test-only assembly). `GetAndSee.Core` and
`GetAndSee.Cli` are untouched, so the shipped `get-and-see.exe` is byte-identical and the build-time
read-only contract is unaffected.

### Test-support: the fake device + fault model (`tests/GetAndSee.Tests/TestSupport/`)
- **`FakeContent`** — deterministic, position-addressable synthetic content (`Fill(span, offset, seed)` /
  `Materialize(len, seed)`). A SplitMix64-style avalanche of `(seed, offset)`, so a 400 MB declared size
  needs no allocation (a parked read only materializes the few MB it delivers) and a test can recompute the
  exact bytes to assert a copy is byte-identical / verify a SHA-256.
- **`ReadFault` / `ReadFaultKind`** — the scriptable fault descriptor keyed by byte offset:
  `Slow`, `Empty`, `ParkAfter(bytes, observeCancellation)`, `PrematureEofAfter(bytes)`,
  `ConnectionFatalAfter(bytes)`, `PerFileErrorAfter(bytes)`, `SpinOnDisposeAfter(bytes)`.
- **`ScriptedReadStream`** — the ONE fault-scriptable read-only stream `OpenReadAsync` returns. Delivers
  `FakeContent` and injects one `ReadFault`. A park is a **real** blocking `Task`; a spinning close is a
  real `SpinWait` busy-loop in `Dispose`; sync `Read` is refused (async-only, like the shipped read path).
  **Folds in** the three ad-hoc fakes (`ControlledReadStream`, `ChunkThenStallReadStream`,
  `SpinOnDisposeStream`), preserving their exact park/spin repros.
- **`FakeDeviceFile` / `FakeDeviceSpec`** — the fluent virtual `/DCIM` spec: `AddFile`, presets
  (`AddSmallPhoto`, `AddLargeVideo`, `AddUnsorted`, `AddLivePhotoPair`, `AddSmallLibrary`), device-level
  faults (`DisconnectAfterFiles(n)`, `FailEveryReadWith(fault)`), `WithDevice` (synthetic UDID only), and
  `Healed()` (a fault-free copy for resume runs). Content seeds default to a process-stable hash of the
  path, so the same paths always produce identical content (the invariant byte-identical resume relies on).
- **`FakeAfcDevice : IPhoneClient`** — a full fake of the 5-member read-only contract. Builds a virtual
  tree from the spec; `ListDirectoryAsync`/`GetFileInfoAsync`/`OpenReadAsync` answer spec-faithfully;
  exposes only read operations. Exposes `ReadParked` / `DisposeSpinStarted` signals and
  `ReleaseStalledReads` / `ReleaseDisposeSpins` so stall/spin scenarios are deterministic with
  `FakeTimeProvider`.
- **`EmptyMetadataExtractor`** — returns `MediaMetadata.Empty`, so the real `DateFolderOrganizer` organizes
  by the spec's capture date via the `st_mtime` fallback (EXIF-byte parsing stays covered by the existing
  `ExifMetadataExtractor` fixture tests — see the boundary note in `done.md`).
- **`CopyPipelineHarness`** — drives the REAL pipeline end-to-end against the fake: the real
  `DcimEnumerator`, `TransferJournal`, `DateFolderOrganizer`, `FileCopier` (and through it the
  `ForwardProgressWatchdog`, `AbandonableReadStream`, `DisconnectEscapeHatch`), and `SummaryWriter`. It
  mirrors the `copy` verb's ~30-line loop (the shipped `CopyCommand` is deliberately NOT refactored — keeping
  the shipped binary byte-identical is a hard rule), returning a `HarnessResult` (exit code, counts).

### Tests added (`tests/GetAndSee.Tests/Harness/`)
- **`CopyPipelineHarnessTests`** (10) — the E2E net: full-library byte-identical copy + manifest; organize
  layout (`YYYY/YYYY-MM`, `unsorted`, Live-Photo co-location); collisions `_2/_3` (never overwritten);
  `--verify-hash` records SHA-256; connection-loss mid-copy → exit 3 → resume byte-identical; watchdog trip
  on a parked read → exit 3 (+ escape-hatch terminator) → resume; between-file failure burst → breaker →
  exit 3 → resume; premature-EOF → failed-then-retry; spinning-close (#45 managed shape) → terminator exit 3
  → resume; disconnect-at-file-N → exit 3 → resume.
- **`ScriptedReadStreamTests`** (10) — each fault shape asserted directly (the fold-in proof): no-fault full
  read, slow chunking, empty, premature-EOF, park (real block + release), park-observing-cancellation,
  connection-fatal, per-file error, spin-on-dispose (busy-spin until released), sync-read/writes refused.
- **`FakeAfcDeviceTests`** (9) — read-faithful listing/stat/open, the real enumerator walks the tree, empty
  library lists empty, unknown path → `DeviceException` (not connection-loss), `DisconnectAfterFiles`.

### Fold-in (one fault model)
Migrated all 9 call sites to `ScriptedReadStream` and deleted the three bespoke fakes:
- `AbandonableReadStreamTests` (×5) → `ScriptedReadStream` park (byte assertions now match `FakeContent`).
- `FileCopierTests` (×2) → park-after-0 (mid-file stall) and park-after-3 MB (intra-file freeze, #42).
- `Sprint34EscapeHatchTests` (×2) → spin-on-dispose-after-2 MB (#45) and park-after-3 MB.
Deleted: `ControlledReadStream.cs`, `ChunkThenStallReadStream.cs`, `SpinOnDisposeStream.cs`.

## Stretch decision — env-var EXE seam: DEFERRED
The `GAS_FAKE_DEVICE=<spec>` seam is deferred (see `done.md` for the full rationale). In short: the fake
lives in the test assembly, and wiring the real EXE to it cleanly needs a new opt-in assembly + a
reflection/plugin seam + gating tests — not cheap, and it touches the shipped CLI startup (risk to the
"byte-identical default run" hard rule). The in-process harness already automates the exit-3 logic and
byte-identical resume, which was the inner-loop win. The brief explicitly says to defer if it can't be
clean and cheap.

## Bugs / Issues Found (during dev)
- **Cross-thread read race in the harness result (fixed).** First cut exposed `HarnessResult.TerminatorExitCode`,
  read on the copy pool-thread while the escape-hatch sets the exit code on the test thread during
  `clock.Advance` — a flaky null. Fix: removed the racy field; the park/spin tests inject a
  `RecordingProcessTerminator` and assert it on the test thread (where the synchronous trip completed),
  matching the Sprint 3.4 pattern. Also made the harness's post-trip `summary.txt` write best-effort so it
  can never race the escape-hatch's authoritative durable write to the same path.
- **xUnit1051** flagged `Task.Run(stream.Dispose)` (method group, missing `CancellationToken`) — switched to
  a lambda with the test token.

## Self-review (§13.6) — PASS-WITH-NITS (0 blockers, 0 majors)
Ran the `code-review` skill (default subagent, find-problems). Fixed: dead `clock` field on the fake (MINOR);
unused `FirstReadStarted` (NIT); two migrated `var` lines → explicit types (NIT); added a `ReadFault.Slow`
E2E test (NIT, the watchdog-negative case). Accepted by design: the harness loop mirrors
`CopyCommand.ExecuteAsync` (the byte-identical hard rule forbids refactoring `CopyCommand`) — recorded as a
drift-watch hotspot in `done.md` to resolve when the `CopySession` extraction lands. Full summary in `done.md`.

## Gates (local, mirrors CI)
- `dotnet build -c Release` — 0 warnings / 0 errors.
- `dotnet test -c Release` (whole solution) — **163 passed** (161 `GetAndSee.Tests` + 2
  `GetAndSee.SafetyTests`), 0 failed. **`ReadOnlyContractTests` green.**
- `dotnet format --verify-no-changes` — clean (exit 0).
- No `.cs` added or changed under `src/` — shipped binary unaffected.
