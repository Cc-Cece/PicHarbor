# Code Review — Sprint 3.6 (productionize the `GAS_FAKE_DEVICE` EXE seam: real-`.exe` E2E)

> Independent producer-gate review (reviewer did **not** author the change). Read-only on code/CI/tests.
> Change set: the **Sprint 3.6** work on `feature/fake-device-exe-seam` (PR #57) — the two gaps the spike
> left: **Gap 1** the CI byte-identical guard (`.github/workflows/ci.yml`) and **Gap 2** the real-process
> E2E lane (`tests/GetAndSee.Tests/E2E/ProcessRunner.cs` + `FakeDeviceExeE2ETests.cs`). The spike's seam
> (FakeDevice project, `#if FAKE_DEVICE` in `CopyCommand`, gate, parser, csproj gating) was read as
> grounding context for the off-by-default / un-abusable checks. Calibrated against
> `docs/review/review-profile.md` + `PROJECT_BRIEF.md` §9 / §11.2 / §13.6.

---

## VERDICT: ✅ PASS-WITH-NITS

No **BLOCKER**, no **MAJOR**. The change is safe to merge. The three high-stakes items the brief called out
all hold (guard is real and load-bearing; the E2E genuinely spawns the shipped-shape `.exe`; the seam is
off by default and un-abusable). There are **2 MINOR** and **6 NIT** findings; the producer should consider
folding in **MINOR-1** (a positive control on the safety guard — one line) before merge, and tracking the rest.

| Severity | Count |
|----------|-------|
| BLOCKER  | 0 |
| MAJOR    | 0 |
| MINOR    | 2 |
| NIT      | 6 |

---

## High-stakes verifications (so a clean pass is trustworthy)

These are the items the brief flagged for extra attention. Each was checked against the actual code, not the
progress notes.

- **✅ Sacred invariant — read-only device contract HOLDS.** `GetAndSee.FakeDevice` references **only**
  `GetAndSee.Core` (its `.csproj` has a single `ProjectReference` to Core, no `imobiledevice-net` package),
  so it physically **cannot** bind a native AFC write — there is no `library.Afc.afc_*` call anywhere in it.
  `FakeAfcDevice` implements `IPhoneClient` with an in-memory tree and exposes **only** read ops
  (`ListDirectoryAsync` / `GetFileInfoAsync` / `OpenReadAsync` / `ConnectAsync` / `Dispose`) — no
  write/delete/rename surface exists to call. `tests/GetAndSee.SafetyTests/ReadOnlyContractTests.cs` is
  **unchanged** (confirmed: not in the branch diff) and still scans the compiled `GetAndSee.Core` assembly —
  the correct scope, because the real `AfcIPhoneClient` (the only native binding) lives in Core and stays
  write-free. Even in a `-p:FakeDevice=true` build, both Core (write-free) and the in-memory fake are present,
  so the opt-in build still cannot write to a device. No new device-write path is introduced.

- **✅ Gap-1 guard is REAL and load-bearing (not a vacuous pass).** [ci.yml](../../.github/workflows/ci.yml#L150-L184)
  runs in the existing `publish-smoke` job **after** the normal single-file publish (the `release.yml`-identical
  command, **no** `-p:FakeDevice`), so it scrutinizes the genuine shipped artifact. The byte-grep reasoning is
  sound: (1) a single-file publish **bundles the managed assemblies into the `.exe`** (the `.dll` is not
  separate), and with `-p:FakeDevice=true` the `GetAndSee.FakeDevice.dll` is bundled in and `get-and-see.dll`
  gains an `AssemblyRef` to it — both surface the pure-ASCII metadata names `GetAndSee.FakeDevice` and
  `FakeDeviceGate` (the latter also as a `TypeRef`/`MemberRef` from the `#if FAKE_DEVICE` call site). (2)
  `Encoding.ASCII.GetString` is reliable **here** because both needles are pure ASCII and stored as
  contiguous UTF-8 bytes in the metadata heap (UTF-8 == ASCII for those bytes), so `.Contains` matches even
  though neighbouring non-ASCII heap bytes decode to `?`. (3) `release.yml` does **not** enable
  `EnableCompressionInSingleFile` (default is off; confirmed absent), so the bundled assemblies are stored
  verbatim and the scan is meaningful. Most convincing: the dev team **empirically proved the guard flips** —
  normal publish → both markers `False` (PASS); `-p:FakeDevice=true` publish → both markers `True` → guard
  **FAILS**. It would genuinely catch a leak. (One robustness gap → **MINOR-1**.)

- **✅ Gap-2 E2E genuinely spawns the real, separate-process `.exe`.** `FakeDeviceExeFixture.InitializeAsync`
  ([FakeDeviceExeE2ETests.cs](../../tests/GetAndSee.Tests/E2E/FakeDeviceExeE2ETests.cs#L31-L57)) builds the
  `-p:FakeDevice=true` EXE to an **isolated temp dir** (`-o <Guid temp>`, deleted on dispose), then
  `ProcessRunner.RunAsync` launches `fixture.ExePath` via `ProcessStartInfo` with `UseShellExecute=false` —
  a real OS child process, **not** `dotnet run` and **not** in-process. Output is isolated by `-o` so a normal
  build is never overwritten (one caveat about `obj/` → **NIT-4**, harmless). The three asserts are sound: clean
  `small` → exit 0 + `summary.txt` + byte-identical archive; `small;disconnect-after=2` → exit 3; cross-process
  resume (interrupt then re-run healed) → exit 0, `Skipped: ≥ 2`, byte-identical.

- **✅ Seam is OFF by default and un-abusable.** Two independent gates: in a **normal build** the
  `#if FAKE_DEVICE` block in [CopyCommand.cs](../../src/GetAndSee.Cli/Commands/CopyCommand.cs#L80-L135) is
  **not compiled at all** (the constant is set only by `-p:FakeDevice=true`, and the `GetAndSee.FakeDevice`
  `ProjectReference` is `Condition="'$(FakeDevice)'=='true'"`), so `GAS_FAKE_DEVICE` is never even read; and
  **even in a FakeDevice build**, `FakeDeviceGate.TryCreate()` returns `null` when the env var is unset →
  real `AfcIPhoneClient`. The spec string is parsed **defensively**
  ([FakeDeviceSpecParser.cs](../../src/GetAndSee.FakeDevice/FakeDeviceSpecParser.cs)): unknown preset/modifier
  or a bad `disconnect-after=N` → clean `FormatException`; it only selects **in-memory presets** (`small` /
  `empty`) and modifiers — it never touches the filesystem, builds no path from input, and has **no
  injection / path-traversal surface** (the only paths are hard-coded `/DCIM/...` literals). `Directory.Build.props`
  defines no global `FakeDevice` default.

- **✅ `release.yml` is UNCHANGED by this branch** (confirmed: not in `git diff main...HEAD`) and never passes
  `-p:FakeDevice`. **✅ No new `var`** (grep of the new `E2E/` + `FakeDevice/` files → 0 matches; explicit
  types throughout). **✅ XML `<summary>` present** on every new public type (`ProcessRunner`,
  `ProcessRunResult`, `FakeDeviceExeFixture`, `FakeDeviceExeE2ETests`, and the FakeDevice types).
  **✅ The fast unit lane really excludes E2E** ([ci.yml L90](../../.github/workflows/ci.yml#L90),
  `--filter "Category!=E2E"`; `!=` keeps every untagged test) and the E2E runs in its own gating job
  ([ci.yml L217](../../.github/workflows/ci.yml#L217)). **✅ No secrets; no real EUII** — the fake's identity
  is synthetic (`00008101-…` / `Test iPhone`, explicitly commented "never real EUII"); gitleaks job intact.

---

## Findings by lens

### 1. Security & Safety — PASS (1 MINOR)

- **[MINOR-1] The Gap-1 guard has no _positive control_, so it could rot into a silent vacuous pass.**
  [ci.yml L178-L184](../../.github/workflows/ci.yml#L178-L184). The guard only asserts the **absence** of two
  strings. It is correct and proven load-bearing **today**, but its correctness is implicitly coupled to the
  current publish shape: no single-file compression, no trimming, managed assemblies stored verbatim. If a
  future change ever enables `EnableCompressionInSingleFile`/`PublishTrimmed` or alters the publish layout,
  the markers would vanish from the byte stream and the guard would **pass vacuously** while a real leak could
  ship. By this project's own calibration ("CI no-op traps … high-severity even though tests pass") a guard on
  a sacred-invariant-adjacent property should be able to detect its own blindness.
  **Fix (one line):** add a positive control — assert the haystack **does** contain a string that is always
  present in a healthy publish (e.g. `GetAndSee.Core`), and `throw` if it is missing ("guard can no longer see
  bundled assembly metadata — detection is broken, refusing to pass"). Cheap; converts a future silent failure
  into a loud one. Worth folding in before merge.

- **✅ Checked and holds:** read-only contract (above); spec parser has no path/file/network/injection surface;
  no secrets introduced; the synthetic device identity is not EUII; `GAS_FAKE_DEVICE` is set on the **child
  only** (`ProcessStartInfo.Environment`, parent untouched — no leak into sibling in-process tests).

### 2. Correctness & Reliability — PASS (2 NIT)

- **✅ Checked and holds:** the disconnect path is deterministic — `FakeAfcDevice.OpenReadAsync` throws
  `DeviceConnectionLostException` synchronously on the `(N+1)`th open, which `CopyCommand`'s
  `catch (… is DeviceStallException or DeviceConnectionLostException)` turns into exit 3 (the escape-hatch path
  also exits 3, so it is 3 either way — no flakiness). Cross-process resume is deterministic because
  `FakeAfcDevice` enumerates via ordinal-sorted `SortedSet`s, so run 1 finishes the first 2 files and run 2
  skips exactly those. `ParseSkippedCount` correctly handles the multi-space, `N0`-formatted `Skipped:` line.

- **[NIT-1] On the `ProcessRunner` timeout path, captured output is discarded.**
  [ProcessRunner.cs L76-L86](../../tests/GetAndSee.Tests/E2E/ProcessRunner.cs#L76-L86). When the timeout fires,
  the method kills the tree and throws `TimeoutException` **before** awaiting `readStandardOutput` /
  `readStandardError`, so a hung child's partial stdout/stderr (the most useful diagnostic for a CI hang) is
  lost. The orphaned read tasks themselves are benign (the killed child closes the pipes, so they complete),
  so this is purely diagnosability. **Fix:** best-effort `await` both reads in a `try/catch` and append a
  truncated tail to the `TimeoutException` message.

- **[NIT-2] The byte-identical oracle silently depends on EXIF extraction _failing_ on synthetic content.**
  [FakeDeviceExeE2ETests.cs L190-L205](../../tests/GetAndSee.Tests/E2E/FakeDeviceExeE2ETests.cs#L190-L205). The
  test recomputes the expected destination with `MediaMetadata.Empty`, which matches the real EXE **only**
  because `ExifMetadataExtractor.Extract` swallows the parse failure on `FakeContent`'s random bytes →
  `Empty` → mtime (= `CaptureDate`) fallback. True today and noted in `progress.md`, but the coupling is
  invisible in the test. **Fix:** a one-line comment at the `MediaMetadata.Empty` call explaining the
  dependency, so a future change to the extractor that breaks this assertion is diagnosable.

### 3. Performance & Resources — PASS (1 NIT)

- **✅ Checked and holds:** nothing added touches the product hot path; all cost is CI-only. The guard reads
  the ~84 MB EXE into a byte array + ASCII string (~250 MB transient) — fine on a CI runner. No per-item or
  N-scaled work.

- **[NIT-3] The advisory `coverage` job does not exclude `Category=E2E`.**
  [ci.yml L286](../../.github/workflows/ci.yml#L286) runs `dotnet test … --collect "XPlat Code Coverage"` with
  no `--filter`, so the E2E tests (which build the FakeDevice EXE and spawn a subprocess) run a **second** time
  under coverage instrumentation — slower and redundant with the dedicated `exe-e2e` job. It is `continue-on-error`
  / non-gating, so this is only wasted time. **Fix:** add `--filter "Category!=E2E"` to the coverage test step
  (or consciously accept it, since the job is advisory).

### 4. Simplicity, Design & Architecture (+ drift-watch) — PASS (1 MINOR, 2 NIT)

- **✅ The `#if FAKE_DEVICE` compile seam is the _right_ design for the constraint.** The sacred requirement is
  a **byte-identical shipped binary with zero seam footprint**. A runtime seam (an injectable `IDeviceFactory`)
  would necessarily put test-seam code into the shipped EXE — exactly what the design forbids. Conditional
  compilation + a conditional `ProjectReference` is the correct trade-off here; it is well-commented in both
  [CopyCommand.cs](../../src/GetAndSee.Cli/Commands/CopyCommand.cs#L80-L98) and
  [GetAndSee.Cli.csproj](../../src/GetAndSee.Cli/GetAndSee.Cli.csproj). The fake is **unified** in one assembly
  (no duplication with the harness).

- **[MINOR-2 / drift-watch, tracking — does NOT block] `CopyCommand.ExecuteAsync` was touched.** The drift-watch
  has `ExecuteAsync` in **escalate** (≈118 LOC, over the ~90 budget). This change adds the `#if FAKE_DEVICE`
  device-acquisition branch to it
  ([CopyCommand.cs L125-L135](../../src/GetAndSee.Cli/Commands/CopyCommand.cs#L125-L135)). Per the drift
  protocol I record the touch — **but I am deliberately not escalating it to a blocking "refactor now"**, for
  two calibrated reasons: (a) the **shipped** body is unchanged — the `#else` branch is byte-for-byte the
  original two lines, and the copy `foreach` was not edited; the growth is a compile-time conditional that
  isn't emitted in a release build (directly analogous to the profile's own "NOT worsened by Sprint 3.1/#33"
  precedent); and (b) forcing a `CopyCommand` extraction into a **test-infrastructure** PR is the
  "merge-block every feature PR on a refactor" anti-pattern. The `CopySession` extraction stays a tracked
  follow-up. **Action for Persist (author):** update the drift-watch entry to note Sprint 3.6 touched
  `ExecuteAsync` with a compile-time seam (shipped body unchanged), extraction still deferred.

- **[NIT-4] The E2E build isolates output (`-o`) but shares `obj/` intermediates.**
  [FakeDeviceExeE2ETests.cs L40-L46](../../tests/GetAndSee.Tests/E2E/FakeDeviceExeE2ETests.cs#L40-L46). The
  fixture builds with `-p:FakeDevice=true -o <temp>`, but the implicit restore/compile still writes
  `src/GetAndSee.Cli/obj/`. This is **harmless**: CI jobs run on isolated runners (the guard's publish is a
  separate job that re-restores fresh), and the seam is gated on the **`FAKE_DEVICE` _property_** at compile
  time — a normal build/publish never passes it, so a stale `obj/` cannot emit the seam into a shipped binary.
  The only effect is a local dev incremental build briefly holding FakeDevice intermediates. **Optional fix:**
  pass a unique `IntermediateOutputPath` too, or leave a comment noting CI isolation makes it moot.

- **[NIT-5] Optional: isolate the seam to keep `ExecuteAsync` `#if`-free.** The `#if/#else/#endif` inside
  `ExecuteAsync` could move into a `partial`-class file compiled only under `FAKE_DEVICE` (an
  `AcquireDeviceAsync` partial), keeping the hot orchestration method free of preprocessor branching. Purely
  stylistic; current form is clear and commented.

### 5. Maintainability & Readability — PASS (1 NIT)

- **✅ Checked and holds:** tests are well-named and **deterministic** (no `Thread.Sleep`/real-clock waits —
  process exit + bounded timeouts only); failures dump exit code + stdout + stderr via `Diagnostics(run)`;
  `ProcessRunner` drains both pipes **before** `WaitForExitAsync` (no full-pipe deadlock) and kills the tree on
  timeout; XML summaries on all new public types; consistent with house conventions.

- **[NIT-6] Doc count drift.** `progress.md` cites "174 + 3 + 2 = 179" while `PROJECT_BRIEF.md` §8 still reads
  "163". Reconcile the brief's count when the handoff (§7/§8) is finalized (repo gotcha: the handoff protocol is
  a standing requirement — also confirm `PROJECT_BRIEF` §7/§8 are updated on this branch before the gate closes).

### 6. Modernization & Tech-Debt (advisory — never blocks)

- Nothing notable. No new dependencies; the additions (`coverlet.collector` + `GitHubActionsTestLogger` on the
  two test projects) align with the existing CI-hardening posture. No items for `docs/ideas-backlog.md`.

---

## Producer call

- **Mergeable.** No blocker/major. Recommend folding **MINOR-1** (positive control on the guard — one line that
  hardens a safety check against silent future rot) in before merge; it is cheap and on the safety surface.
- **Track** MINOR-2 (drift-watch note on `ExecuteAsync` — author updates the profile in Persist; the
  `CopySession` extraction remains its own future PR) and the NITs (fold in NIT-1/NIT-2/NIT-3 if cheap; the
  rest are optional).
- **Still required after this gate:** QA / behavioral confirmation is **not** displaced by this lane — the E2E
  proves the shipped binary's _process-level_ contract against a software double, and the report rightly claims
  **no hardware parity**; the small real cable-yank smoke stays authoritative for the native `afc_file_close`
  core-pin. Confirm `PROJECT_BRIEF` §7/§8 handoff is updated on the branch (NIT-6).

_Reviewer was independent of the authors. Read-only: no code, CI, test, or profile files were modified; the
profile's Persist update is left to the author per the skill._
