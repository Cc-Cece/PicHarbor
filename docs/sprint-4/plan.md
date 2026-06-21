# Sprint 4 — Organize & find (flat `YYYY-MM` default + `search`)

**Type:** feature · **Gates:** none (post-v1.0) · **Branch:** `feature/sprint-4` (dev, off `main`)
**Owner:** Dev (Nova lead — CLI/organize; Sage — journal/manifest) · **Design:** `docs/brainstorm/folder-organization.md` · **Re-acceptance:** producer gate → QA (light hardware)

> **Read first:** `docs/brainstorm/folder-organization.md` (the converged design + CEO decisions), `PROJECT_BRIEF.md` §7/§8/§13.6, `docs/review/review-profile.md`.

## Why we're here (CEO decisions — locked 2026-06-21)

The nested `YYYY/YYYY-MM` layout is awkward to browse. The CEO chose:
- **Flip the default to flat `YYYY-MM`** (nested stays available as an option).
- **Ship a `search` command** (the real "hard to search" fix — folders only ever serve casual date-browsing).
- **Ship `reorganize`** (offline, no phone) — **but as Sprint 4.1**, not here (see *Sequencing*).

## Scope (this sprint)

1. **`--organize-by` presets** on the `copy` command, **default `month` (flat `YYYY-MM`)**.
2. **Per-archive scheme** recorded in the journal — a re-run never silently re-shuffles an existing archive.
3. **`search` command** over the manifest (read-only, no device).

### Sequencing — what is NOT in this sprint
- **`reorganize`** (migrate an existing archive between schemes) → **Sprint 4.1**, on its own. It is the
  first operation that **moves/renames files on the PC**, so it needs its own atomic/resumable/crash-safe
  design and its own QA. Bundling it would make a huge, hard-to-review PR. Sprint 4 deliberately ships the
  visible win (flat default + search) first; existing nested archives keep working untouched until 4.1.
- **TUI / wizard** → Sprint 5 (decisions already locked — see `docs/brainstorm/tui-experience.md`).

## Design

### 1. `--organize-by` (the path strategy)

Extend the single organizer seam (`DateFolderOrganizer.GetRelativeDestination`) to take the scheme. Pure
path function; the rest of the pipeline (staging, collision `_2/_3`, journal, long-path) is untouched.

| `--organize-by` | Relative path for a dated file | Notes |
|-----------------|--------------------------------|-------|
| `month` **(default)** | `YYYY-MM\name.ext` | flat — one click to any month, sorts chronologically |
| `year-month` | `YYYY\YYYY-MM\name.ext` | today's nested layout (now opt-in) |
| `year` | `YYYY\name.ext` | coarse, few folders |
| `flat` | `name.ext` | one folder; for tiny libraries / search-only users |

- **`unsorted\name.ext`** (no trustworthy capture date) is unchanged under **every** scheme.
- **Live Photo pairs** (`.HEIC`+`.MOV`) must resolve to the **same** folder under every scheme (they share
  a capture date, so this falls out naturally — add a test that pins it).
- Zero-padded `YYYY-MM` everywhere (lexical == chronological in Explorer).

### 2. Per-archive scheme (the migration-safety crux)

The layout is an **archive property**, decided once and persisted in the journal — **not** a free per-run
flag. Bump `TransferJournal.SchemaVersion` 2 → 3 and add a small `settings`/meta row (`organize_scheme`).

On `copy`, resolve the effective scheme:
- **Recorded scheme exists** → use it. If `--organize-by` was **explicitly passed** and differs, **warn**
  (`This archive is organized as '<recorded>'. Ignoring --organize-by '<other>'. Use 'reorganize' to
  change it (Sprint 4.1).`) and use the recorded one. A **defaulted** (not-passed) flag silently yields to
  the recorded scheme. (Use `System.CommandLine` parse state to tell *explicit* from *defaulted*.)
- **No recorded scheme, brand-new archive (no `done` rows)** → use `--organize-by` (default `month`) and
  record it.
- **No recorded scheme but the archive already has files (a pre-v3 / v1.0 archive)** → the v2→v3 migration
  **must stamp `organize_scheme = 'year-month'`** (the only layout that existed), so **existing nested
  archives keep resolving to their existing paths and resume byte-stable.** This back-compat rule is the
  single most important correctness point in the sprint — a v1.0 archive must never get re-shuffled or
  duplicated by the upgrade.

### 3. `search` command (read-only, no device)

`get-and-see search --dest <root> [filters] [--open]` — opens the journal **read-only**
(`TransferJournal.OpenReadOnly`, no schema create/alter) and queries the `manifest` view / `files` table.

Filters (all optional, combinable): `--from <date>` / `--to <date>` (capture date range), `--type
<photo|video|screenshot|other>` (derive from extension), `--camera <substr>` (matches `camera_make`/
`camera_model`), `--min-size` / `--max-size`, `--has-gps`. Output: a Spectre table (relative path, capture
date, size, type), with a count. `--open` opens the matches' containing folders in Explorer (Windows) —
put the "open in shell" action behind an injectable seam so it's unit-testable without launching Explorer.
No device connection is made; works on any existing archive.

## Tests (the bar)

- **Organizer per scheme:** `month`/`year-month`/`year`/`flat` each produce the right relative path;
  **default is `month`**; `unsorted` unchanged under each; Live Photo pair co-locates under each; collision
  `_2/_3` still works per scheme; long-path (R6) holds for each (deep dest + `year-month`/`year`).
- **Per-archive scheme + back-compat (critical):**
  - brand-new archive records the chosen (or default) scheme;
  - a **v2 fixture archive with existing `done` rows** opens, migrates to v3, and is stamped
    `year-month` — and the same files resolve to the **same paths** (resume byte-stable, no re-copy);
  - an explicit conflicting `--organize-by` on an existing archive **warns and keeps** the recorded scheme;
  - a defaulted flag on an existing archive is silently honored to the recorded scheme.
- **`search`:** each filter and a couple of combined filters over a seeded manifest; empty-result case; the
  `--open` action is invoked through the seam (mock) with the right targets; read-only (no schema change).
- **No false-positives:** `search` never opens the device; `copy` on an existing archive never re-shuffles.

## Hard rules (non-negotiable)

- **Device stays read-only** — this whole sprint is **destination-side** (path strategy + journal meta +
  manifest *read*). No AFC/lockdown change; `ReadOnlyContractTests` green; `search` makes **no** device call.
- **Resume byte-stable for existing archives** — the v2→v3 migration must not change where any already-done
  file lives (the `year-month` stamp guarantees this). Prove it with the v2-fixture test.
- **Long-path (R6)** holds for every scheme. **Healthy copy hot path unaffected.** No new `var`. XML
  `<summary>` on new public Core types. `dotnet format` + build 0/0 + tests green.
- **NEW — dev self-review before the PR** (three-layer review, `PROJECT_BRIEF` §13.6): run the
  `code-review` skill (default subagent, not `Explore`) on your own diff with a *find-problems* framing;
  fix what it finds; **summarize the findings in the PR**.

## Acceptance criteria

- [ ] `copy --organize-by {month|year-month|year|flat}` works; **default is flat `YYYY-MM`**; `--help` documents it.
- [ ] Scheme is recorded per-archive; an existing v1.0 (nested) archive **resumes unchanged** (v2-fixture test proves byte-stable paths); explicit conflicting flag warns + keeps recorded.
- [ ] `search` returns correct matches for each filter over the manifest, `--open` works via the seam, makes **no** device call.
- [ ] `ReadOnlyContractTests` green; CI green; build 0/0; `dotnet format` clean.
- [ ] Dev self-review run and summarized in the PR.
- [ ] **QA (light hardware):** a fresh real-device `copy` lands files in flat `YYYY-MM`; an existing nested archive re-runs and stays nested + resumes; `search` returns correct results on a real archive. (No disconnect testing needed — the device path is unchanged.)

## Process

Dev on `feature/sprint-4` off `main`. Failing tests first where it makes sense (e.g. the v2-fixture
back-compat test). Update `docs/sprint-4/progress.md` + `done.md`, and the **README** (`--organize-by`,
`search`) + `docs/user/manifest-schema.md` if the schema/migration notes change. **Self-review → open ONE
PR `sprint-4: organize & find (flat YYYY-MM default + search)`, then STOP for the producer review gate.**
Never push to `main`.

## Then (producer + QA)

1. **Producer independent gate** — one reviewer independent of the author; extra weight on the **v2→v3
   back-compat migration** (does an existing v1.0 archive resume byte-stable?), the explicit-vs-defaulted
   flag logic, `search` read-only, and long-path per scheme.
2. **QA** — light hardware as above (no disconnect needed).
3. On PASS → merge → update `PROJECT_BRIEF` §7/§8 → **then start Sprint 4.1 (`reorganize`)**, then Sprint 5 (TUI).

---

## Dev-session prompt (paste into a fresh `ai-team-dev` session — NOT a subagent)

```
You are the get-and-see dev team (Nova leads — CLI/organize; Sage — journal). Implement Sprint 4:
"Organize & find" — flat YYYY-MM as the new default folder layout, a per-archive recorded scheme, and a
read-only `search` command. Real CODE: implement, self-review, open ONE PR, STOP for the producer gate.
Do NOT merge. Never push to main.

WORK IN: e:\src\get-and-see-dev
  git fetch origin && git checkout main && git pull
  git checkout -b feature/sprint-4
Read: docs/sprint-4/plan.md (this brief), docs/brainstorm/folder-organization.md (design + CEO decisions),
PROJECT_BRIEF.md §7/§8/§13.6, docs/review/review-profile.md.

DELIVER (three pieces):
1. --organize-by {month, year-month, year, flat} on `copy`, DEFAULT = month (flat YYYY-MM).
   Extend DateFolderOrganizer.GetRelativeDestination to take the scheme (pure path function; leave staging/
   collision/journal/long-path untouched):
     month       -> YYYY-MM\name.ext   (NEW DEFAULT)
     year-month  -> YYYY\YYYY-MM\name.ext  (today's layout, now opt-in)
     year        -> YYYY\name.ext
     flat        -> name.ext
   `unsorted\name.ext` unchanged under every scheme. Live Photo pairs MUST co-locate under every scheme.
2. PER-ARCHIVE SCHEME (the migration-safety crux): bump TransferJournal.SchemaVersion 2->3, persist
   `organize_scheme` (settings/meta row). On copy, resolve the effective scheme:
     - recorded scheme exists -> use it; if --organize-by was EXPLICITLY passed and differs, WARN
       ("This archive is organized as '<recorded>'. Ignoring --organize-by '<other>'. Use 'reorganize' to
       change it.") and use the recorded one; a DEFAULTED (not-passed) flag silently yields to recorded.
       (Use System.CommandLine parse state to tell explicit from defaulted.)
     - brand-new archive (no done rows) -> use --organize-by (default month) and record it.
     - PRE-EXISTING archive with files but no recorded scheme (a v1.0/v2 db) -> the v2->v3 migration MUST
       stamp organize_scheme='year-month' so existing nested archives resolve to the SAME paths and resume
       BYTE-STABLE. This is the most important correctness point — a v1.0 archive must never be re-shuffled
       or duplicated by the upgrade.
3. `search` command: `get-and-see search --dest <root> [--from] [--to] [--type photo|video|screenshot|other]
   [--camera <substr>] [--min-size] [--max-size] [--has-gps] [--open]`. Open the journal READ-ONLY
   (TransferJournal.OpenReadOnly — no schema create/alter), query the manifest/files (captured_at, size,
   camera_make/model, gps_latitude/longitude). Print a Spectre table (rel path, date, size, type) + a count.
   --open opens the matches' containing folders in Explorer via an INJECTABLE seam (so it's unit-testable
   without launching Explorer). NO device connection.

TESTS (write the v2-fixture back-compat test FIRST):
  - organizer per scheme (month/year-month/year/flat), default=month, unsorted unchanged, Live Photo pair
    co-locates, collision _2/_3 per scheme, long-path per scheme.
  - per-archive scheme: brand-new records scheme; a V2 FIXTURE with existing done rows migrates to v3,
    gets stamped year-month, and the same files resolve to the SAME paths (byte-stable, no re-copy);
    explicit conflicting --organize-by warns+keeps recorded; defaulted flag silently honors recorded.
  - search: each filter + combined filters over a seeded manifest, empty result, --open invoked through
    the seam with correct targets, read-only (no schema change).

HARD RULES: device read-only (this is all dest-side + manifest READ; ReadOnlyContractTests green; search
makes no device call); resume byte-stable for existing archives (prove with the v2-fixture test); long-path
per scheme; healthy hot path unaffected; no new var; XML <summary> on new public Core types; CI green.

SELF-REVIEW BEFORE PUSHING (new standing process, PROJECT_BRIEF §13.6): run the `code-review` skill
(default subagent, NOT Explore) on your diff with a "find problems" framing — extra attention on the
v2->v3 back-compat migration (does an existing v1.0 archive resume byte-stable, no re-shuffle?), the
explicit-vs-defaulted flag logic, and search being truly read-only. Fix what it finds; SUMMARIZE in the PR
what it flagged and how you resolved it.

PROCESS: update docs/sprint-4/progress.md + done.md, the README (--organize-by + search) and
docs/user/manifest-schema.md (v3 migration note). Open ONE PR "sprint-4: organize & find (flat YYYY-MM
default + search)" then STOP for the producer review gate.

REPORT BACK: the four schemes + the default flip; how the per-archive scheme is recorded and how an
existing v1.0 nested archive stays byte-stable (the v2-fixture test); the search filters + the --open seam;
the self-review summary; dotnet test counts + CI; the PR number.
```
