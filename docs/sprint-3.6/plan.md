# Sprint 3.6 — Productionize the `GAS_FAKE_DEVICE` EXE seam (real-`.exe` E2E)

**Type:** infrastructure / test · **Gates:** none · **Branch:** continue **`feature/fake-device-exe-seam`** (PR #57, re-title from `[SPIKE]`), **merge `main` in first**
**Owner:** Dev — Sage (seam/device) + Nova (CLI/E2E) + Ivy (E2E asserts) · **Design:** the spike (PR #57) + this plan · **Re-acceptance:** producer gate

> **Read first:** PR #57 (the spike), `docs/sprint-3.5/done.md` (the in-process harness this complements), `PROJECT_BRIEF.md` §9 (read-only) + §11.2 (the now-hardened CI) + §13.6, `docs/review/review-profile.md`.

## Why we're here

The CEO chose to **pursue** the spike. The in-process harness (Sprint 3.5) tests the copy *logic* with no device, but it can't restart a process — so it can't assert the **real `get-and-see.exe`'s exit codes** or **cross-process resume** (run → interrupt → re-run → byte-identical). The `GAS_FAKE_DEVICE` seam closes that last gap: the actual CLI runs end-to-end against the fake, no hardware.

## What the spike already nails — KEEP IT

- **Sound gating (do not change the mechanism).** The seam is `#if FAKE_DEVICE` (a compile constant set only by `-p:FakeDevice=true`); the `GetAndSee.FakeDevice` project reference is `Condition="'$(FakeDevice)'=='true'"`. A **normal/release build defines nothing and references nothing**, so the shipped `get-and-see.exe` is **byte-identical and cannot contain the fake**. `FakeDeviceGate.TryCreate()` reads `GAS_FAKE_DEVICE`; unset → returns `null` → real `AfcIPhoneClient`.
- **Fake unified** — moved out of `tests/` into the shared `src/GetAndSee.FakeDevice` assembly (the duplication the harness left is resolved); the in-process harness repointed to it. One fake, one source of truth.
- **Parser/gate unit-tested** — `FakeDeviceSeamTests` covers spec presets/modifiers, malformed specs → `FormatException`, and gate-off-when-unset.

## The two gaps to close (this sprint)

### Gap 1 — the CI byte-identical guard (the load-bearing safety proof)
The csproj comment *promises* "CI asserts the Release output carries no `GetAndSee.FakeDevice` reference" — **but no such CI step exists.** Add it: a CI step (in the now-hardened `ci.yml`) that **publishes the normal Release single-file EXE** (exactly as `release.yml` does, no `-p:FakeDevice`) and **asserts the publish output contains no `GetAndSee.FakeDevice.*`** and that the build did not define `FAKE_DEVICE`. This is the proof that the seam can never reach a shipped binary — without it, "byte-identical default" is an unverified claim.

### Gap 2 — the real-process E2E test (the seam's whole point)
Add a test that **builds the `-p:FakeDevice=true` EXE, runs it as a subprocess** with `GAS_FAKE_DEVICE=<spec>` + a temp destination, and asserts the things the in-process harness can't:
- **Exit codes from the real process:** a clean spec → **exit 0** with the files correctly organized on disk + a `summary.txt`; a `disconnect-after=N` spec → **exit 3**.
- **Cross-process resume:** run (interrupt via the disconnect spec) → re-run the same command against a *healed* spec → the archive is **byte-identical** and prior files are skipped (resumed across two real process invocations).
- Because it builds + spawns a process (slow), put it in its **own lane** — a `[Trait]`/category the unit suite excludes, run in a **dedicated CI job** (or the existing publish-smoke job), so the fast unit suite is unaffected.

### Freshen
**Merge `main` into the branch first** — it predates #59 (hardened CI) + #62. Add Gap 1/Gap 2 onto the *hardened* `ci.yml`.

## Hard rules (non-negotiable)

- **Byte-identical release, PROVEN.** The Gap-1 CI guard must actually publish a normal Release EXE and assert it's fake-free (no `GetAndSee.FakeDevice` assembly, no `FAKE_DEVICE` constant). The release path / `release.yml` must be **unchanged** and must never pass `-p:FakeDevice`.
- **Seam off by default** — no `GAS_FAKE_DEVICE` → the real `AfcIPhoneClient`, byte-identical behavior. The seam exists only in a `FakeDevice=true` build.
- **Read-only** — the fake returns only an `IPhoneClient` (read-only surface), makes no real-device call; `ReadOnlyContractTests` green. The `GAS_FAKE_DEVICE` spec string is parsed defensively (malformed → clean `FormatException`, never a path-escape/injection — it only selects in-memory presets/modifiers).
- No new `var`; XML `<summary>` on new public types; `dotnet format` + build 0/0 + tests green.
- **Self-review before the PR** (§13.6): run the `code-review` skill (default subagent, not `Explore`) on the diff — extra attention on the byte-identical guard being real, the seam being un-abusable/off-by-default, and the E2E genuinely spawning the real EXE. Summarize in the PR.

## Acceptance criteria

- [ ] **CI guard:** a CI step publishes a normal Release EXE and asserts **no `GetAndSee.FakeDevice`** in the output + **no `FAKE_DEVICE`** constant; fails if either appears.
- [ ] **Real-process E2E:** a test builds the `FakeDevice=true` EXE and runs it as a subprocess with `GAS_FAKE_DEVICE` → asserts exit **0** (clean) and **3** (disconnect spec) and **cross-process resume byte-identical**; lives in its own lane so the unit suite stays fast.
- [ ] Parser/gate unit tests still green; spec parsing rejects malformed input cleanly.
- [ ] `release.yml` unchanged; `ReadOnlyContractTests` green; shipped EXE byte-identical (guard proves it); CI green; self-review summarized in the PR.
- [ ] PR #57 re-titled from `[SPIKE]`; `done.md` states what the seam tests vs. what the in-process harness covers (no overlap-claim, no hardware-parity claim).

## Sequencing

Independent of the feature work — can run **now** (the spike is ~80% there) or in parallel with Sprint 4. Recommended: finish it next (the test-infra story is fresh and mostly built), then Sprint 4 rides on **both** the in-process harness *and* the real-`.exe` E2E.

## Process

Dev continues on `feature/fake-device-exe-seam` (PR #57); **merge `main` in first**. Self-review → push → the PR updates → **STOP for the producer review gate** (this touches shipped startup, so it gets a full gate — extra weight on the byte-identical guard). Never push to `main`.

---

## Dev-session prompt (paste into a fresh `ai-team-dev` session — NOT a subagent)

```
You are the get-and-see dev team (Sage leads — seam/device; Nova — CLI/E2E; Ivy — E2E asserts). PURSUE
the GAS_FAKE_DEVICE EXE-seam spike (PR #57) to production: close the 2 gaps it left, then STOP for the
producer review gate. Do NOT merge. Never push to main.

WORK IN: e:\src\get-and-see-dev
  git fetch origin && git checkout feature/fake-device-exe-seam && git pull
  git merge origin/main          # bring in #56 harness + #59 hardened CI + #62 (the branch predates them)
Read: docs/sprint-3.6/plan.md (this brief), the spike's own files, PROJECT_BRIEF.md §9/§11.2/§13.6.

KEEP (the spike already nailed these — do NOT change the mechanism):
- #if FAKE_DEVICE gating: the seam compiles in ONLY with -p:FakeDevice=true; the GetAndSee.FakeDevice
  ProjectReference is Condition='$(FakeDevice)'=='true'. A normal/release build references nothing and
  emits no seam → shipped get-and-see.exe is byte-identical and contains no fake. FakeDeviceGate.TryCreate()
  reads GAS_FAKE_DEVICE; unset → null → real AfcIPhoneClient.
- The fake unified into src/GetAndSee.FakeDevice (tests/ copies deleted, harness repointed) — keep it ONE.
- FakeDeviceSeamTests unit tests (parser presets/modifiers, malformed → FormatException, gate-off).

CLOSE GAP 1 — the CI byte-identical GUARD (load-bearing; the csproj comment promises it but it doesn't
exist). Add a step to the hardened ci.yml that PUBLISHES the NORMAL Release single-file EXE (no
-p:FakeDevice, same as release.yml) and ASSERTS the publish output contains no GetAndSee.FakeDevice.* and
that FAKE_DEVICE was not defined. Fail the job if either appears. This proves the seam can never ship.

CLOSE GAP 2 — the REAL-PROCESS E2E test (the seam's whole point; the in-process harness can't restart a
process). Build the -p:FakeDevice=true EXE and run it as a SUBPROCESS with GAS_FAKE_DEVICE=<spec> + a temp
dest; assert: clean spec → exit 0 + files organized on disk + summary.txt; disconnect-after=N spec → exit
3; CROSS-PROCESS RESUME → run (interrupt) then re-run against the healed spec → archive BYTE-IDENTICAL,
prior files skipped. Put it in its OWN lane ([Trait]/category the unit suite excludes; run it in a
dedicated CI job or the publish-smoke job) so the fast unit suite is unaffected.

HARD RULES: byte-identical release PROVEN by the Gap-1 guard; release.yml unchanged + never passes
-p:FakeDevice; seam OFF by default (no env var → real AfcIPhoneClient); fake/seam NEVER in a release build;
read-only (fake returns only IPhoneClient, no device call); GAS_FAKE_DEVICE spec parsed defensively
(malformed → clean FormatException, only selects in-memory presets — no path/injection); no new var; XML
<summary> on new public types; CI green.

SELF-REVIEW BEFORE PUSHING (§13.6): run the code-review skill (default subagent, NOT Explore) on your diff
— extra attention on the byte-identical guard being REAL (actually publishes + greps), the E2E genuinely
spawning the real EXE (not in-process), and the seam being un-abusable/off-by-default. Fix what it finds;
SUMMARIZE in the PR.

PROCESS: re-title PR #57 from "[SPIKE]" to "sprint-3.6: GAS_FAKE_DEVICE EXE seam — real-process E2E";
update docs/sprint-3.6/progress.md + done.md (what the seam tests vs the in-process harness; no
hardware-parity claim). Push, then STOP for the producer review gate.

REPORT BACK: the CI byte-identical guard (how it proves the release is fake-free); the real-process E2E
(how it spawns the EXE + asserts exit 0/3 + cross-process resume); confirmation the fake stays unified +
off-by-default; the self-review summary; dotnet test counts + CI; the PR number.
```
