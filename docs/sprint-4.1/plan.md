# Sprint 4.1 — `reorganize` (offline, atomic, resumable layout migration)

**Type:** feature · **Gates:** producer gate → QA (light hardware) · **Branch:** `feature/sprint-4.1` (dev, off `main`)
**Owner:** Dev (Sage lead — journal/move engine; Nova — CLI) · **Design:** `docs/brainstorm/folder-organization.md` §"reorganize"

> **Read first:** `PROJECT_BRIEF.md` §7/§8 + §9 (safety rules), `docs/sprint-4/done.md` (what shipped),
> `docs/review/review-profile.md` (sacred invariants), and this plan. Sprint 4 shipped `--organize-by` +
> the per-archive recorded scheme; **this sprint adds the one operation Sprint 4 deliberately deferred:
> changing an existing archive's layout by moving files on the PC.**

## Why we're here (CEO decision — locked 2026-06-21)

The layout is an **archive property**: a `copy` re-run with a conflicting `--organize-by` warns and keeps the
recorded scheme — it never re-shuffles. The sanctioned way to actually change a layout is a dedicated
**`reorganize`** command. The CEO greenlit it as its **own sprint** precisely because it is **the first
operation in the product that moves/renames files on the PC** — so it earns its own atomic, resumable,
crash-safe design and its own QA. Data integrity is sacred: **a reorganize must never lose or corrupt a
single byte.**

## The one-line contract

> `reorganize` takes an **existing** archive and migrates it to a different `--organize-by` scheme by
> **moving files within the archive root only** (same volume → atomic OS rename), updating each file's
> journal `dest_path` as it goes, **fully offline (zero device/AFC calls)**, **idempotent** and
> **resumable** after any interruption, leaving the archive byte-identical in content and internally
> consistent in the journal.

## Scope (this sprint)

1. **`reorganize --dest <root> --organize-by <scheme> [--dry-run]`** — a new **offline** command (no
   `IPhoneClient`, no pre-flight device checks).
2. **A `Reorganizer` engine in `GetAndSee.Core`** — plans target paths, performs per-file atomic moves with
   journal updates + crash reconciliation, resolves new collisions deterministically, cleans up emptied
   folders, and stamps the new scheme.
3. **Journal additions** — `UpdateDestPath(...)`, an enumerator of `done` rows with their placement inputs,
   and a small **in-flight marker** so an interrupted reorganize is detectable/resumable.
4. **Cash the Sprint 4 gate nit** — make `FileCopier.organizeScheme` a **required** ctor param (drop the
   `= OrganizeScheme.YearMonth` default) so every caller threads the resolver's decision; add a `copy`
   guard that refuses/warns on an archive with an **incomplete** reorganize.

## Hard guardrails (sacred — do not violate)

- **Data integrity (BLOCKER if broken).** No moved file is ever lost, truncated, or corrupted. Every move is
  a **same-volume atomic rename** (`File.Move` within one root); the journal `dest_path` update follows, and
  a crash between the two is **reconciled** on resume (never a silent loss). A dry-run or optional verify
  proves byte-identity.
- **Offline / device read-only.** `reorganize` has **no device dependency at all** — it must not reference
  `IPhoneClient` or make any AFC/lockdown call. `ReadOnlyContractTests` stays green (nothing new in the
  device-write blocklist).
- **Journal/resume integrity.** `dest_path` is *always* an accurate pointer to where the file is on disk
  (or is healed to be, on resume). The `manifest` view never exposes a file at a path it isn't at. Never
  falsely mark a row.
- **Idempotent + deterministic.** Re-running a completed reorganize is a no-op; re-running an interrupted
  one finishes it; the final tree is identical regardless of where it was interrupted.
- **Long-path (R6).** Every move endpoint goes through `LongPath.ToExtended` (see `FileCopier` L133–138) so
  moves into/out of `>260`-char paths work.
- **Never move non-media.** Only journal `done` media rows move. `get-and-see.db`, `summary.txt`, the
  `.get-and-see-tmp/` staging dir, and any user file are **never** touched.

## Design

### 1. CLI — `ReorganizeCommand` (mirror `SearchCommand`/`CopyCommand` house style)

- Register in `Program.cs` (L17–22) alongside `copy`/`status`/`search`.
- Options: `--dest`/`-d` (required), `--organize-by` (**required here** — the *target* scheme; reuse
  `OrganizeSchemes.AllTokens` + `AcceptOnlyFromAmong`), `--dry-run`.
- Opens the journal **writable** (`TransferJournal.Open`, L47–65) — reorganize both reads and writes
  `dest_path` + `settings`. (Not `OpenReadOnly`.)
- **No device, no dashboard.** Offline throughout.
- **Exit codes** consistent with the suite: `0` success (incl. clean no-op), `1` one or more files could not
  be moved (run completed, resumable), `2` usage/journal error (no archive, unreadable db), `130` Ctrl+C
  (resumable). No `3` (no device stall possible).

### 2. Target-path computation — reuse the organizer's folder rules (correctness crux)

`reorganize` must place a file **exactly** where `copy` would have under the target scheme, using only what
the journal already stores — **no device, no re-reading EXIF from disk**:

- **Folder date:** derive from the stored `exif_datetime_original` (applying the *same* trust window as
  `DateFolderOrganizer.ResolveDate`, L52–69) falling back to `source_mtime`, else `unsorted`. **Reuse the
  exact `ResolveDate` logic** — refactor it to accept the two stored timestamps (don't fork the rule), so
  reorganize and copy can never disagree on placement.
- **Leaf filename:** take the leaf of the file's **current `dest_path`** (this preserves any `_2/_3` suffix
  already assigned at copy time — identity stays stable). Do **not** re-derive from `source_path`.
- **Target relative path:** add a `DateFolderOrganizer.GetRelativeDestination(string fileName,
  DateTime? captureDate, OrganizeScheme)` overload (the current `RemoteFile`+metadata method delegates to
  it) and call it with the leaf + resolved date + target scheme.
- **`unsorted\` and Live Photos fall out for free:** `unsorted` is identical under every scheme → those
  files compute `target == current` → never move. A Live-Photo `.HEIC`+`.MOV` pair shares a capture date →
  both compute the same target folder → they move together.

### 3. The move engine — atomic rename + journal update + crash reconcile

Plan first, then execute deterministically:

1. **Plan:** enumerate all `done` rows `(source_path, source_size, dest_path, exif_datetime_original,
   source_mtime)`, ordered **deterministically** (e.g. by `source_path`) so collision-suffix assignment is
   reproducible. Compute each file's `target` relative path (§2). Partition into **already-placed**
   (`target == current`, skip) and **to-move**.
2. **New collisions:** a coarser target scheme can map two differently-foldered files to the same target
   (e.g. `2024\2024-03\IMG.HEIC` + `2024\2024-08\IMG.HEIC` → `year` → both want `2024\IMG.HEIC`). Resolve
   with the **same `_2/_3` rule** as `FileCopier.ResolveUniqueRelativePath` (L350–364), assigning in the
   deterministic plan order, and checking against both the in-run assigned set **and** on-disk existence.
   Factor that helper so both copier and reorganizer share it (don't duplicate the rule).
3. **Per-file move (the atomic step):** for each to-move file, in plan order:
   - Ensure the target parent directory exists.
   - **`File.Move(currentAbs, targetAbs)`** — same volume ⇒ atomic OS rename. (Both endpoints via
     `LongPath.ToExtended`.)
   - **`journal.UpdateDestPath(source_path, source_size, target)`** — one `UPDATE files SET dest_path=… `.
   - **Crash reconcile (resume):** if `currentAbs` is missing but `targetAbs` exists, the move already
     happened before the journal caught up → just re-apply the `UpdateDestPath` (heal). If **both** are
     missing → record a per-file failure (state stays `done` with old `dest_path`; never drop the row), so
     the user can investigate; do **not** invent data.
4. Because every move is a same-volume rename and the journal update is a single statement, the worst a crash
   can do is leave **one** file physically-moved-but-not-yet-recorded — healed idempotently on the next run.
   **Cross-volume moves are impossible by construction** (one archive root) — no copy-then-delete path.

### 4. Resume & crash-safety marker

- At **start**, write a settings marker `reorganize_target = <token>` (in-flight). Keep `organize_scheme`
  unchanged during the moves (per-file `dest_path` is the truth; the archive is merely *visually* mixed
  mid-run, never inconsistent).
- On **completion**, `SetOrganizeScheme(target)` **and clear** `reorganize_target` — atomically enough that
  the terminal state is unambiguous.
- On **resume** (`reorganize` re-run): if `reorganize_target` is set, continue toward it; recompute the plan
  (already-placed files skip themselves) and finish.
- The marker is a **settings key only — no schema-version bump** (settings is k/v; no DDL change).

### 5. Empty-folder cleanup

After all moves, remove folders that the migration **emptied** (e.g. old `2024\2024-08\`, then a now-empty
`2024\`). Rules: only remove directories that are **genuinely empty**; **never** remove the archive root,
`unsorted\`, `.get-and-see-tmp\`, or any directory still holding a file (moved-in, failed, or user).
Prune bottom-up. This is best-effort cosmetics — a failure to remove an empty dir is **not** a run failure.

### 6. Cash the gate nit + guard `copy`

- **`FileCopier` ctor (L76–112):** drop `organizeScheme = OrganizeScheme.YearMonth` → make it **required**.
  Update the sole caller in `CopyCommand` (already passes it explicitly) + any test constructing a copier.
- **`copy` guard:** on start, if `reorganize_target` is set and `!= organize_scheme`, an earlier reorganize
  is **incomplete** — `copy` should **refuse with an actionable message** (`This archive has an unfinished
  reorganize (→ '<target>'). Run 'reorganize --dest … --organize-by <target>' to finish it first.`) rather
  than copy new files into a half-migrated tree. Exit `2`.

### 7. Dry-run + output

- `--dry-run`: print the plan — `N files to move` (old → new samples), new-collision count, empty-dirs to
  remove — and **write nothing** (no move, no journal write, no marker). Mirrors `copy --dry-run` honesty.
- Live run: a concise Spectre summary — moved / already-placed / collisions-resuffixed / dirs-removed /
  failed — and the final `organize_scheme`.

## Journal additions (all parameterized, `GetAndSee.Core`)

- `void UpdateDestPath(string sourcePath, long sourceSize, string newRelativeDestPath)` — single `UPDATE`.
- `IReadOnlyList<…> EnumerateDoneForReorganize()` — `source_path, source_size, dest_path,
  exif_datetime_original, source_mtime` `WHERE state='done'` (deterministic `ORDER BY source_path`).
- `string? GetReorganizeTarget()` / `SetReorganizeTarget(OrganizeScheme)` / `ClearReorganizeTarget()` —
  settings k/v (reuse the `settings` upsert pattern at L238–248).
- Reuse `GetUsedDestPaths()` (L383–397) for the on-disk/in-journal collision check.

## Test plan (weighted — this MOVES files, so integrity dominates)

Leverage the **in-process harness**: build a real archive with `CopyPipelineHarness` + `FakeAfcDevice`
(no device), then run the `Reorganizer` against it on disk and assert. Add a **real-`.exe` E2E** round-trip.

1. **Byte-integrity (highest):** after `reorganize`, **every** file is byte-identical at its new path
   (SHA-256 before/after), `0` lost, `0` corrupted, count conserved.
2. **Idempotent + resumable / crash-safe (highest):** interrupt after *k* moves (and specifically **between
   the `File.Move` and the `UpdateDestPath`**) → re-run **completes**; final tree == a clean run's tree;
   no dup, no loss; the reconcile path is exercised (file at target, journal healed).
3. **Round-trip:** `year-month → month → year-month` returns the archive to its original paths + journal
   (modulo deterministic suffixes). Also `year-month → year` (coarsening, forces new collisions) →
   `month`.
4. **New-collision determinism:** two same-named files from different months → `year` → one gets `_2`,
   stable across re-runs and independent of interruption point.
5. **No-op:** `reorganize` to the already-recorded scheme → "already organized as '<x>'", `0` moves, exit
   `0`, journal + disk untouched.
6. **`unsorted\` never moves; Live-Photo pair co-locates and moves together.**
7. **Long-path (R6):** reorganize into and out of a `>260`-char path (nested/`day`-like) works.
8. **Journal accuracy:** post-run, `manifest.dest_path` matches disk for **every** row; `organize_scheme ==
   target`; `reorganize_target` cleared.
9. **Offline / read-only:** the reorganize path makes **zero** device calls; `ReadOnlyContractTests` green;
   `Reorganizer` has no `IPhoneClient` dependency.
10. **`copy` guard:** `copy` on an archive with `reorganize_target` set (simulated interrupted reorg) refuses
    with the actionable message + exit `2`; after `reorganize` completes, `copy` resumes byte-stable.
11. **Cleanup:** emptied scheme folders removed; root, `unsorted\`, `.get-and-see-tmp\`, `get-and-see.db`,
    `summary.txt`, and non-empty/user dirs left intact.
12. **CLI/dry-run:** `reorganize --help`; `--dry-run` previews and writes nothing (no move, no marker, no
    journal change); exit codes.
13. **Required-param regression:** `FileCopier` no longer compiles without an explicit scheme; the `copy`
    call site + tests updated; `copy` behaviour unchanged (fast lane + E2E stay green).

## Sequencing — NOT in this sprint

- **No device/copy changes** beyond the required-param + the `copy` incomplete-reorg guard.
- **No cross-volume / "reorganize to a different root"** — reorganize is in-place within one archive root by
  definition (keeps every move an atomic rename). Moving an archive to a new drive is a separate idea.
- **TUI / wizard → Sprint 5** (`docs/brainstorm/tui-experience.md`).
- **Human-friendly `--min/max-size`** and the `search` date-frame polish remain backlog items.

## Definition of done

- `reorganize` ships behind the guardrails above; all 13 test areas green; build `0/0`
  (`TreatWarningsAsErrors`); `dotnet format` clean; `ReadOnlyContractTests` green; the fast lane + the
  real-`.exe` E2E lane green.
- `FileCopier.organizeScheme` is required; `copy` is unchanged except the incomplete-reorg guard.
- Dev self-review (layer 1, `code-review` skill, extra weight on move-integrity + crash-resume) →
  **stop at the producer gate** (do not merge). Handoff `docs/sprint-4.1/done.md` + `progress.md`.
- README (`reorganize` section) + `docs/user/manifest-schema.md` (the `reorganize_target` settings key)
  updated.

---

## Dev execution prompt (paste into the dev chat)

> **You are the get-and-see dev team (Sage lead — journal/move engine; Nova — CLI). Execute Sprint 4.1 —
> `reorganize`.** A .NET 10 / C# Windows CLI that copies an iPhone's media archive over USB (read-only,
> resumable). This sprint adds an **offline** command that migrates an existing archive between
> `--organize-by` layouts by **moving files on the PC** — the first file-moving operation in the product,
> so **data integrity is sacred**.
>
> **First:** read `PROJECT_BRIEF.md` §7/§8/§9, `docs/sprint-4/done.md`, `docs/review/review-profile.md`, and
> **`docs/sprint-4.1/plan.md`** (the full design — follow it). Work on a new branch **`feature/sprint-4.1`
> off `main`** (re-sync `main` first — it has Sprint 4 merged).
>
> **Build, in this order:**
> 1. Refactor `DateFolderOrganizer` to expose `GetRelativeDestination(fileName, captureDate, scheme)` and a
>    reusable `ResolveDate(exif, mtime)`; extract the `_2/_3` collision helper so copier + reorganizer share
>    it. No behavior change to `copy`.
> 2. Journal: `UpdateDestPath`, `EnumerateDoneForReorganize` (deterministic order), and the
>    `reorganize_target` settings marker get/set/clear. Parameterized SQL only; **no schema-version bump**.
> 3. `Reorganizer` in `GetAndSee.Core`: plan → per-file **atomic same-volume `File.Move`** → `UpdateDestPath`
>    → **crash-reconcile** (file-at-target ⇒ heal; missing-both ⇒ per-file fail, never drop) → deterministic
>    new-collision suffixes → empty-dir cleanup → stamp scheme + clear marker on success. Every path via
>    `LongPath.ToExtended`. **Zero `IPhoneClient` — offline.**
> 4. `ReorganizeCommand` (mirror `SearchCommand`/`CopyCommand`): `--dest` (req), `--organize-by` (req,
>    target), `--dry-run`; opens the journal **writable**; exit codes `0/1/2/130`.
> 5. **Cash the gate nit:** make `FileCopier.organizeScheme` **required**; update the `copy` call site + any
>    test copier. Add the `copy` **incomplete-reorganize guard** (refuse + actionable message + exit `2`).
>
> **Tests (write the byte-integrity + crash-resume ones FIRST):** cover all 13 areas in the plan — build an
> archive with the `CopyPipelineHarness` (+`FakeAfcDevice`, no device) then reorganize it on disk and assert
> **byte-identity (SHA-256), resumability across an interruption between the move and the journal update,
> round-trip, deterministic collisions, no-op, unsorted-never-moves, long-path, journal accuracy, offline
> (zero device calls / `ReadOnlyContractTests` green), the copy guard, and cleanup**. Add a real-`.exe` E2E
> reorganize round-trip. Keep `--dry-run` provably side-effect-free.
>
> **Rules:** explicit types in `src/` (no `var`); `dotnet format` clean; build `0/0` under
> `TreatWarningsAsErrors`; commit in logical chunks; keep `docs/sprint-4.1/progress.md` updated (log every
> bug + fix). **Then run the layer-1 self-review** (the `code-review` skill on your diff, find-problems
> framing, extra weight on move-integrity + crash-resume + the required-param change), fix what it finds,
> write `docs/sprint-4.1/done.md`, open a PR to `main`, and **STOP at the producer gate — do not merge.**
