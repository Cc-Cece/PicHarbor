# Sprint 3.1 — Independent Code Review (PR #33, issue #25)

**Reviewer:** Remy (Producer), via the `code-review` skill — **5 independent lenses**, reviewers independent of the author, profile-tuned. Branch `fix/25-afc-open-watchdog` @ `9b29c3d`.
**Date:** 2026-06-07 · **Profile:** `docs/review/review-profile.md`

## Gate verdict: ✅ PASS (code review)

**0 blockers · 0 majors · 2 minors · 4 nits.** All CI green (build+test, gitleaks, EXE smoke). Merged to `main` (`2521e32`), closes #25. **v1.0 tag still gated on QA #21 Phase B** (hardware unplug re-test + deferred Sprint-3 checks).

| Lens | Verdict |
|------|---------|
| Security & Safety | **PASS** — read-only contract intact |
| Correctness & Reliability | PASS-WITH-NITS |
| Performance & Resources | **PASS** |
| Simplicity, Design & Architecture | PASS-WITH-NITS |
| Maintainability & Tests | PASS-WITH-NITS |

## The fix

`#25` = the Sprint-2 read-stall watchdog only guarded the byte-read of an already-open file, so a USB unplug relocated the #11 hang to the native `afc_file_open` (parked 194 s, force-kill). The fix extracts a shared `DeviceWatchdog` (race a blocking op against `--read-timeout` on an injectable clock; abandon the parked call; throw `DeviceStallException`) and routes the 3 blocking native calls (`afc_file_open`/`afc_read_directory`/`afc_get_file_info`) **and** the existing read stream through it. Adds `AfcErrors` (connection-fatal vs per-file classifier) + `DeviceConnectionLostException` so a yanked cable stops the run cleanly (exit 3, resumable) instead of marching every remaining file into its own hang.

## Strengths verified (not assumed)

- **Read-only contract intact** (Security lens) — no device-write/delete/rename symbol added; `afc_file_open` stays `FopenRdonly`-only; `ReadOnlyContractTests` still scans `GetAndSee.Core` (the new code is in-assembly) with the full §5.5 blocklist; the IL scan still walks the new closure/lambda bodies.
- **#25 closed at the mechanism level + the false-green retired** (Tests lens) — `DeviceWatchdogTests.A_parked_native_call_times_out_…` **parks a real blocking call on a `Task.Run` worker** (structurally identical to a hung `afc_file_open`), driven by `FakeTimeProvider` — deterministic, 14 tests in 511 ms, no wall-clock sleep. This is the exact lesson from #25 institutionalized: the test now fails the way production failed.
- **No use-after-free on teardown** (Correctness lens) — the reviewer loaded `imobiledevice-net 1.3.17` and confirmed `AfcClientHandle : SafeHandle`; the P/Invoke marshaller ref-counts the handle, so `afc_client_free` is deferred until a parked call releases its ref → **benign leak on an exiting process, not a crash**.
- **Bounded resources** — run stops on the **first** stall (catch outside the copy loop) → ≤1 abandoned worker thread per run; `Abandon` observes `settled.Exception` (no unobserved-exception crash); the `OpenReadAsync` `onAbandoned` hook disposes a late-returned `AfcReadStream` (closes the AFC handle — no leak).
- **Atomic/resume invariants hold** on all four interruption paths (open-stall, read-stall, connection-lost, Ctrl-C): in-flight file left non-`done` (resumable), `.partial` cleaned, manifest never exposes it. Ctrl-C → `OperationCanceledException` → exit 130, **not** a stall.
- **Right-sized DRY** (Design lens) — exactly **one** race+abandon implementation (`DeviceWatchdog`); the old `WatchdogReadStream.AbandonRead` was deleted and the read-stall tests are unchanged & green (behavior-preserving extraction). Rule-of-Three met (1 read + 3 native = 4 sites). `AfcErrors`/`DeviceConnectionLostException` are proportionate, conservatively allow-listed.
- **Scale-safe / zero-cost-when-healthy** (Performance lens) — `Task.Run`-per-open is negligible vs USB-2 (~30 MB/s) at 38k files; the timer only materializes when a call doesn't complete promptly; the `readTimeout<=0` path is byte-for-byte the old inline behavior.

## Findings (all non-blocking)

- **[MINOR — fast-follow before v1.0] Guard *wiring* has no regression test** (Tests lens). The watchdog *mechanism* is well-covered, but nothing asserts `OpenReadAsync`/`ListDirectoryAsync`/`GetFileInfoAsync` actually route through `GuardAsync` — deleting the wrapper keeps the suite green. This is the **same class of gap that shipped #25** (guard-to-native wiring with no structural guard). Hardware **QA #21 Phase B** verifies the wiring for *this* release; a cheap `ReadOnlyContractTests`-style structural assertion (or an `IAfcApi` seam) guards against *future* regression. → **issue #36**.
- **[MINOR] Native handle disposal on a lost-device teardown** (Correctness lens) — `device?.Dispose()`→`idevice_free` can run while an orphaned `afc_file_open` is parked (the AFC *client* handle is SafeHandle-ref-protected; the process is exiting). Optional defense-in-depth: suppress native release on the disconnect path. → **issue #34**.
- **[NIT]** `ConnectAsync` handshake calls (`idevice_new`, `lockdownd_*`, `afc_client_new`) remain unguarded — out of #25 scope (mid-run), connect is user-present startup. → **issue #35**.
- **[NIT]** One `FileCopierTests` stall case uses a real 200 ms timeout instead of a `FakeTimeProvider` — deterministic but a tiny real wait. → folded into #36.
- **[NIT]** A file hit by a *non-fatal* AFC error at the instant of unplug is tallied "Failed" (correctly retried next run, never falsely `done`; cosmetic interim summary only).
- **[NIT — pre-existing, out of scope]** Device UDID / device name reach the local console/exception text (no network egress). Already known. → backlog.

## Drift-watch updates

- **Timeout-race / abandon-on-stall → `resolved`** — `DeviceWatchdog` extracted (PR #33); one impl, old `AbandonRead` deleted, read-stall tests unchanged & green. Re-open only if a future consumer hand-rolls its own race.
- **`CopyCommand.ExecuteAsync` → stays `escalate` (open), NOT worsened by #33** — the change was a 1-line catch-filter widening (`DeviceStallException or DeviceConnectionLostException`) + a ctor arg; body unchanged (~118 LOC). Extraction **deferred** to a tracked follow-up (own PR, before the #23 reformat) — proportionate; not forced into an unplug-safety hotfix.

## Routing

- Blockers/majors → none.
- Minors/nits → issues #36 (wiring test + fake-clock nit), #34 (native-handle teardown), #35 (connect-time guard); ExecuteAsync extraction → tracked follow-up; UDID/device-name console EUII → backlog.
- **Merge:** done (gate PASS). **Next:** QA #21 Phase B hardware re-acceptance → PASS → merge PR #31 (final gate record) → cut **v1.0**.
