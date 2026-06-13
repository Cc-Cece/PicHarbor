# Sprint 3.4 — The disconnect escape-hatch (#45, the LAST disconnect round)

**Type:** hotfix — root-cause / final · **Gates:** v1.0 (#21) · **Branch:** `fix/sprint-3.4` (dev)
**Owner:** Dev (Sage — device/transfer) · **Design:** `docs/brainstorm/sprint-3.4-consilium.md` · **Re-acceptance:** QA #21 Phase B (hardware)

> **CEO hard cap:** this is the **last** disconnect round. If hardware finds a 6th manifestation, we ship v1.0 with a documented known-limitation — **no round 6.**

> **Read first:** `docs/brainstorm/sprint-3.4-consilium.md`, issue **#45** (+ #42 #38 #25 #11), `PROJECT_BRIEF.md` §9, `docs/review/review-profile.md`.

## Why we are here

Five rounds, five locations. QA captured **ground truth** this time (live thread stack + 139 MB dump): a mid-copy yank makes `afc_file_read` return `EmptyResponse`/0, the fault unwinds into the read stream's `await using` disposal → **`AfcReadStream.Dispose()` → synchronous native `afc_file_close()` on the dead transport → busy-spins forever** (confirmed: `AfcReadStream.cs:108`, the only call site, unguarded; `DeviceWatchdog` covers open/read/stat/list, not close). Our round-4 "non-returning read" was a harness *inference* that hardware falsified.

**The real class:** every synchronous native call on a dead transport can busy-spin, and graceful unwinding keeps finding the next unguarded one. **We stop enumerating native calls.**

## The fix — one escape-hatch (NOT band-aid #6)

When the byte heartbeat is dead, the device is **provably gone** and the data is **already safe**. So on `ForwardProgressWatchdog` trip, **do not unwind through native code** — the independent watchdog thread:
1. prints the one disconnect line ("Device disconnected. Progress saved — reconnect and re-run to resume."),
2. writes **and flushes** `summary.txt` from the journal (no device involved; journal is consistent),
3. **terminates the process with exit code 3 via a finalizer-skipping hard exit.**

### THE critical detail — the hard exit MUST skip finalizers
`Environment.Exit(3)` is **WRONG here**: it runs finalizers, and the `AfcReadStream` finalizer (`afc_file_close`) and the `AfcClientHandle` **SafeHandle critical finalizer** (`afc_client_free`) would **re-enter the spinning native layer** on the dead transport during shutdown → the exit itself hangs. Use a **finalizer-skipping terminate**: P/Invoke `TerminateProcess(GetCurrentProcess(), 3)` (kernel32), `OperatingSystem.IsWindows()`-guarded. The OS reaps every thread/handle; **no managed or critical finalizer runs**; nothing re-enters native device code.

### Testability — inject the terminate
You cannot `TerminateProcess` in a unit test. Put the terminate behind a seam (`IProcessTerminator` / `Action<int> hardExit`): real impl = `TerminateProcess`; test double = records the call. The watchdog (or its owner) invokes it.

## What stays / what NOT to do
- **Keep** the Sprint 3.3 `ForwardProgressWatchdog` (independent timer, byte heartbeat) + `AbandonableReadStream` — the right liveness signal. 3.4 only changes **what trip does**: terminate instead of cancel-and-hope-the-unwind-is-clean. If the token-cancel path is now fully redundant, you may simplify it — but the **terminate is the load-bearing guarantee**; keep it.
- **Do NOT** guard `afc_file_close` individually, and **do NOT** audit native calls one-by-one — the escape-hatch makes that unnecessary by construction. (That is exactly the band-aid that has failed five times.)
- **Do NOT** auto-reconnect or retry — out of scope for v1.0.

## Data-safety argument (must hold; state it in done.md)
- Journal: SQLite WAL + each `MarkDone` committed → **crash-safe**; un-closed connection recovers on next open. No corruption.
- `summary.txt`: explicitly written + flushed before terminate.
- `.partial`: transient staging, never published, `CleanStaging` removes it next run.
- Device: read-only throughout; terminate makes **no** device call.

## Tests (anti-false-green — this is round 6's bar)
- **The #45 repro:** a read that faults (`EmptyResponse`) followed by a **disposal/close that SPINS** (a fake stream whose `Dispose` blocks/spins). Assert: heartbeat dies → summary written → injected terminator invoked with **3** → host process survives → no hang. Deterministic (fake clock). **A stall double or a cancellation-ignoring read does NOT satisfy this sprint** — it must model a *spinning unwind*.
- **All prior manifestations** still end in a terminate-3 (or clean exit 3): park, between-file, intra-file-read, premature-EOF.
- **False-positive guards:** slow-but-alive (>0 bytes) never trips; short/0-byte file never trips; isolated per-file failures never trip; **a healthy run completes, exits 0, terminator NOT invoked.**

## Hard rules
- Read-only contract (`ReadOnlyContractTests` green; no AFC/lockdown write/delete/rename symbol; `TerminateProcess` is process-control, not a device symbol). Data integrity (interrupted file non-`done`/resumable; no published `.partial`; manifest only `done`). Healthy hot path unaffected. No new `var`. XML `<summary>` on new public Core types. `dotnet format` + build 0/0 + tests green.

## Acceptance criteria
- [ ] Watchdog trip → summary written+flushed → **finalizer-skipping `TerminateProcess(…, 3)`**; never re-enters native device code.
- [ ] The #45 **spinning-close** repro test fails-before / passes-after (injected terminator asserted), deterministic, host survives.
- [ ] All earlier manifestations + premature-EOF end in exit 3; false-positive guards hold; healthy run exits 0 with no terminate.
- [ ] `ReadOnlyContractTests` green; CI green.
- [ ] **Hardware (QA #21 Phase B):** yank mid-large-file **and** between files → CPU pinned then **drops to idle on exit 3**, `summary.txt` present, resume byte-identical, long-path + read-only hold.

## Branch / merge mechanics
Dev branches `fix/sprint-3.4` **off `fix/sprint-3.3`** (carries 3.2 long-path + 3.3 watchdog). One PR; **base it on `main`** so CI runs (the combined 3.2+3.3+3.4 line) — producer will confirm. On hardware PASS: merge the combined line → supersedes/closes PR #44 and #41 → merge PR #31 → update PROJECT_BRIEF §7/§8 → cut **v1.0**.

## Process
Commit a failing #45 spinning-close repro **before** the fix. `Closes #45`. Update `docs/sprint-3.4/progress.md` + `done.md` (incl. the data-safety argument). Open ONE PR **based on `main`** titled `sprint-3.4: disconnect escape-hatch (#45)`, then **STOP for the producer review gate**. Never push to `main`.

---

## Dev-session prompt (paste into a fresh `ai-team-dev` session — NOT a subagent)

```
You are the get-and-see dev team (Sage leads — device/transfer). Implement Sprint 3.4: the disconnect
ESCAPE-HATCH — the FINAL fix for the mid-copy USB-disconnect bug (now FIVE manifestations; CEO cap: if
hardware fails a 6th time we ship with a documented limitation, no round 6). Real CODE: failing repro
first, then fix, test, push ONE PR, STOP for the producer review gate. Do NOT merge. Never push to main.

WORK ONLY IN: e:\src\get-and-see-dev
  git fetch origin
  git checkout fix/sprint-3.3 && git pull        (branch OFF fix/sprint-3.3 — carries 3.2 long-path + 3.3 watchdog)
  git checkout -b fix/sprint-3.4
Read: docs/brainstorm/sprint-3.4-consilium.md, docs/sprint-3.4/plan.md, issue #45 (+ #42 #25 #11),
PROJECT_BRIEF.md §9, docs/review/review-profile.md.

GROUND TRUTH (QA captured live — thread stack + dump, NOT inferred): a mid-copy yank makes
afc_file_read return EmptyResponse/0; the fault unwinds into FileCopier.StreamToStagingAsync's
`await using` disposal -> AfcReadStream.Dispose() -> synchronous native afc_file_close() on the dead
transport -> BUSY-SPINS FOREVER. afc_file_close is unguarded (AfcReadStream.cs:108). The real class:
EVERY synchronous native call on a dead transport can spin; guarding them one-by-one has failed 5×.
STOP enumerating native calls.

THE FIX — ONE escape-hatch (do NOT band-aid afc_file_close):
  When the byte heartbeat is dead the device is provably gone and the data is already safe. On
  ForwardProgressWatchdog trip, the independent watchdog thread must: (1) print the disconnect line,
  (2) write AND FLUSH summary.txt from the journal, (3) TERMINATE the process with exit code 3 — WITHOUT
  unwinding through native code.

  CRITICAL: the hard exit MUST SKIP FINALIZERS. Environment.Exit(3) is WRONG — it runs the AfcReadStream
  finalizer (afc_file_close) and the AfcClientHandle SafeHandle CRITICAL finalizer (afc_client_free),
  both of which re-enter the SPINNING native layer on the dead transport -> the exit itself hangs. Use
  P/Invoke TerminateProcess(GetCurrentProcess(), 3) (kernel32), OperatingSystem.IsWindows()-guarded. The
  OS reaps the orphaned spinning thread; no finalizer runs.

  TESTABILITY: you cannot TerminateProcess in a unit test. Put the terminate behind an injectable seam
  (IProcessTerminator / Action<int>); real impl = TerminateProcess; test double records the call.

KEEP: the 3.3 ForwardProgressWatchdog (independent timer + byte heartbeat) + AbandonableReadStream — the
right liveness signal. 3.4 only changes what TRIP does (terminate, not cancel-and-hope). If the
token-cancel path is now redundant you may simplify, but the terminate is the load-bearing guarantee.
DO NOT guard afc_file_close individually, DO NOT audit native calls one-by-one, DO NOT auto-reconnect.

DATA-SAFETY (state in done.md): SQLite WAL + committed MarkDone = crash-safe (recovers on next open);
summary.txt flushed before terminate; .partial transient (CleanStaging next run); read-only throughout
(terminate makes no device call).

TESTS (anti-false-green — round 6 bar): commit a FAILING repro FIRST — a read that faults (EmptyResponse)
then a disposal/close that SPINS (fake stream whose Dispose blocks/spins); assert heartbeat-dead ->
summary written -> injected terminator invoked with 3 -> host survives -> no hang, deterministic (fake
clock). A STALL DOUBLE or cancellation-ignoring read does NOT satisfy this — model a SPINNING UNWIND.
Also: all earlier manifestations + premature-EOF end in exit 3; false-positive guards (slow-but-alive
>0 bytes, short/0-byte file, isolated failures) must NOT trip; a HEALTHY run exits 0, terminator NOT
invoked.

HARD RULES: read-only contract (ReadOnlyContractTests green; no device-write symbol); data integrity
(interrupted file non-done/resumable; no published .partial; manifest only done); healthy hot path
unaffected; no new var; XML <summary> on new public Core types; CI green.

PROCESS: failing #45 repro committed BEFORE the fix. Closes #45. Update docs/sprint-3.4/progress.md +
done.md. Open ONE PR BASED ON main titled "sprint-3.4: disconnect escape-hatch (#45)", then STOP for
the producer review gate.

REPORT BACK: how the escape-hatch terminates without re-entering native code (and why Environment.Exit
was rejected); the injected-terminator seam; how the spinning-close repro fails-before/passes-after;
the data-safety argument; read-only status; dotnet test counts + CI; the PR number.
```
