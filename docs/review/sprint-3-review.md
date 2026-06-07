# Sprint 3 — Independent Code Review (PR #28)

**Reviewer:** Remy (Producer), via the `code-review` skill — 4 independent lenses, profile-tuned, run on `feature/sprint-3` @ `29252be`.
**Date:** 2026-06-07
**Profile:** `docs/review/review-profile.md`

## Gate verdict: ✅ PASS (code review)

**0 blockers · 0 majors · 0 minors · 2 nits (1 already tracked).**
Merge remains gated on **#21 QA Stage 2** (hardware acceptance) per PROJECT_BRIEF §13/§11 — code review is structural; the v1.0 tag still requires the behavioral hardware sign-off.

> Two reviewer findings were **false positives**, refuted by the producer against the actual code/history before relaying (see §"Producer verification"). The gate is run, not rubber-stamped — and not used to forward unverified claims to the dev team.

## Lens results

| Lens | Verdict (raw) | After verification |
|------|---------------|--------------------|
| Security & Safety | PASS-WITH-NITS | PASS (its 1 MINOR was a false positive) |
| Correctness & Reliability | BLOCKER-FOUND | **PASS** (the blocker was a false positive) |
| Performance & Resources | PASS (0 findings) | PASS |
| Maintainability & Tests | PASS-WITH-NITS | PASS (only the tracked `var` nit) |

## Strengths verified (not assumed)

- **Read-only contract intact.** No device-write/delete/rename symbol added; `--verify-hash` and long-path are PC-side/read-only; `ReadOnlyContractTests` still covers the full §5.5 blocklist (9 AFC + 6 lockdown mutators + write-mode `afc_file_open`).
- **`--verify-hash` (R12)** is single-pass (hashes the same buffer already streamed, no second device read), **read-only**, **zero-cost when the flag is absent** (no hasher allocation; default path byte-identical to Sprint 2), `IncrementalHash` disposed on all paths, written to the `sha256` manifest column atomically with the `done` transition. Tested: records hash, tamper-detect (byte flip), no-hash-when-off, multi-chunk (>5 MiB).
- **Long-path (R6)** helper is sound — prefixes `\\?\` once per copier (not per chunk), handles UNC/drive-rooted/already-prefixed/relative, guards non-Windows, no traversal/normalization bypass. Tested with a synthetic >260-char path.
- **Atomic + resume invariants intact** after the refactor: `.partial` → fsync → size-verify → `File.Move` → `MarkDone`; mismatch never publishes; interrupted file left non-`done`.
- **StatusCommand** opens the journal **read-only** (`Mode=ReadOnly`, no schema init) — closes the PR #16 blind spot. `StatusCommandTests` present (missing-db exit code, output sections, read-only-tolerant of pre-v2 db).
- **#17 polish complete:** watchdog scratch buffer zeroed (`CryptographicOperations.ZeroMemory`, bounded to bytesRead), sync `Read()` now throws (can't bypass the timeout), EUII fixture rename done (real name → "Sample iPhone").
- **Secrets/CI:** only `GITHUB_TOKEN`; **gitleaks** gate real (`--exit-code 1`, pinned v8.18.4, `--redact`); **published-EXE `--help` smoke** proves native self-extraction before any release; actions pinned to @v4. All three CI checks green.
- **#26 RID trim** correct (plural `<RuntimeIdentifiers>win-x64</>` restore-pin; CLI keeps its singular RID for publish) — silences the known Defender FP.
- **User docs (Track C)** accurate vs code: README has the **R19 "What this tool does NOT do"** section; troubleshooting carries the real error strings + exit codes; manifest-schema documents `files`/`runs`/`devices` + the `manifest` view + example SQL. No EUII in docs.
- **Conventions:** xUnit v3 + Shouldly + NSubstitute (no FluentAssertions); nullable enable; XML summaries on new public API.

## Producer verification (refuted findings)

- **[Correctness lens — claimed BLOCKER] "v1→v2 migration never adds the `sha256` column → opening a Sprint-1/2 DB fails."** **REFUTED.** `sha256` has been in the `files` table, the `manifest` view, and `MarkDone` **since Sprint 1** (reserved as a nullable column for exactly this feature; confirmed via `git show origin/main:.../TransferJournal.cs` and the Sprint-1 QA sign-off C-3 "sha256 present-but-NULL"). Existing archives already have the column — no `ALTER TABLE` needed. The reviewer reasoned carefully but from a wrong premise (that `sha256` is new in Sprint 3).
- **[Security lens — claimed MINOR] "`StatusCommandTests` does not exist."** **REFUTED.** The file `tests/GetAndSee.Tests/Cli/StatusCommandTests.cs` is in the PR diff and the correctness + maintainability lenses both cite its passing test methods by name.

## Real nits (non-blocking)

- **[NIT] No dedicated `LongPath` unit-test suite.** The helper is exercised via `FileCopierTests` (the >260-char test), so behavior is covered; a focused suite would document the path-safety model. Low value → optional, Sprint 4 polish.
- **[NIT — already tracked] 41 new `var` usages.** The "explicit types — never var" convention is documented but **enforcement is deferred to #23** (post-Sprint-3). Routed to #23 (count now ~164 + new). Not a finding for this PR.

## Routing

- Blockers/majors → none.
- Nits → LongPath test suite noted for Sprint 4; `var` → #23 (already tracked).
- Modernization → none this round.
- **Merge action:** code-review PASS → hold for **#21 QA Stage 2** sign-off → then merge → then the v1.0 tag.
