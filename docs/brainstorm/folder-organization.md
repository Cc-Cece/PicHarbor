# Brainstorm — Folder organization & searchability (post-v1.0)

> Multi-agent brainstorm convened by Remy (Producer) on the CEO's prompt:
> *"Usability of splitting photos by year-month subfolders — it's not easy to search. Maybe a single
> 'year-month' folder for groups without stacking?"*
> **Status: ideation only — nothing is committed.** Voices debate, then converge on a recommendation + a
> proposed sprint. Decisions are the CEO's.

## The starting point (v1.0 behavior)

Today every file lands at `…/YYYY/YYYY-MM/filename.ext` — a **two-level nested** tree:

```
D:\Photos\
  2024\
    2024-08\ IMG_4821.HEIC …
    2024-09\ …
  2025\
    2025-01\ …
  unsorted\            ← no trustworthy EXIF date
```

The CEO's friction: **searching/browsing is awkward** — you click into a year, then a month, to reach
anything; comparing across months means a lot of up-and-down. The question on the table: would a **flat
`YYYY-MM`** layout (no year folder above it) be easier?

The organizer is a single seam (`DateFolderOrganizer`), the journal records each file's `dest_path`, and
collisions are disambiguated `_2/_3`. Whatever we change has to stay **read-only on the device, resumable,
and long-path safe**, and must not silently re-shuffle an existing archive.

---

## Phase 1 — Free ideation

### Kira (Product / UX) — "Match how people actually look for a photo."
> People search photos one of three ways: *by date* ("summer 2024"), *by event* ("Anna's wedding"), or
> *by content* ("the beach ones"). Folders only ever serve the **date** axis — event and content belong to
> search, not the tree. So the folder question is narrow: what's the least-friction **date** layout?
> - Raw ideas: (a) **flat `YYYY-MM`** at the root — one click to any month; (b) **flat `YYYY`** only —
>   coarse but tiny; (c) keep nested but add a **`Latest\` shortcut** to the newest month; (d) let the user
>   pick with `--organize-by`. My gut: the nesting is what hurts; flat `YYYY-MM` is the sweet spot for a
>   human scrubbing a file manager.

### Nova (CLI / implementation) — "One option, a few clean presets."
> The organizer is already isolated, so this is a `--organize-by` enum feeding a strategy: `year-month`
> (nested, today's default), `month` (flat `YYYY-MM`), `year` (flat `YYYY`), `flat` (everything in one
> folder, last resort), maybe `day` (`YYYY-MM-DD`) for heavy shooters. Each is a pure path function; the
> rest of the pipeline (staging, collision, journal) is untouched. Cheap to build. The real questions are
> the **default** and **migration**, not the mechanism.

### Sage (Backend / data) — "The folder tree is a *view*; the manifest is the index."
> We're overloading folders to do a database's job. The journal already stores date, GPS, camera, size, and
> both paths for **every** file — that's the real searchable index. Even with a perfect folder layout,
> "find Christmas 2019 videos over 100 MB" is a SQL query, not a click-path. Two ideas: (1) ship a **`find`/
> `search` command** over the manifest (date range, type, size, camera) that prints or even *opens* matches;
> (2) whatever folder scheme we pick, the manifest makes it cosmetic — so optimize the tree purely for
> casual browsing and let search do the heavy lifting. Also: changing the layout must **not** rewrite
> existing archives — the journal's `dest_path` is the source of truth for resume.

### Milo (Information architecture) — "Flat, but zero-padded and bounded."
> Whatever's flat must **sort correctly** in Explorer: `YYYY-MM` is perfect (lexicographic == chronological).
> Avoid `YYYY-M`. A 12-year library is ~144 `YYYY-MM` folders in one pane — fine for a file manager, busy
> for a human. `YYYY` (flat) is ~12 folders, very scannable, but a single `2019\` can hold 8,000 files,
> which Explorer renders sluggishly. So there's a real **breadth-vs-depth** tension: nested = deep but
> bounded; flat-month = wide but each folder small; flat-year = narrow but each folder huge. No single
> answer fits everyone — which argues for a default plus an option.

### Ivy (QA) — "Whatever we do, prove migration is safe and paths still fit."
> My worries are all about **change**: (1) if someone re-runs an existing archive with a new
> `--organize-by`, do we re-copy everything into new folders (wasteful, confusing) or refuse/keep the old
> layout? It must be deterministic and data-safe. (2) Long-path: `flat` makes paths *shorter*, good; `day`
> makes them longer, watch R6. (3) Live Photo pairs (`.HEIC`+`.MOV`) must land in the **same** folder under
> every scheme. (4) `unsorted/` (no EXIF) must survive every layout. I want a test matrix per scheme +ts a
> migration test before any of this ships.

---

## Phase 2 — Discussion

**Kira:** Sage's point reframes it — if we had `search`, would the CEO still feel the pain? Partly. Search
finds a *known* thing; browsing is for *wandering* the library. We need both. But it means we shouldn't
over-engineer the tree.

**Sage:** Agreed. So: pick a browse-friendly default, don't chase "perfect," and add `search` for the
precise stuff. The two together fully answer "it's not easy to search."

**Milo:** Then the default should minimize clicks for the median user. Flat `YYYY-MM` is one click to any
month and still sorts/scrubs cleanly. Nested `YYYY/YYYY-MM` only earns its keep for *very* long libraries
where 150 folders in one pane annoys you — that's the minority.

**Nova:** I can make `month` (flat `YYYY-MM`) the default and keep `year-month` (nested) as an option for
the long-library folks. But flipping the default **changes the layout for existing users** on their next
run — that's the migration trap Ivy flagged.

**Ivy:** Right. Rule: **the layout is decided when an archive is created and recorded in the journal.** A
re-run *ignores* a conflicting `--organize-by` (warns, keeps the original) unless the user explicitly opts
into a separate `reorganize` action. Never silently fork an archive into two schemes.

**Kira:** Could `reorganize` move existing files into the new scheme locally (a pure PC-side file move, no
device)? That's the clean migration path and it's safe — it never touches the iPhone.

**Sage:** Yes — and because the manifest knows every `dest_path`, a local `reorganize` is a bounded,
resumable, fully-offline operation. Bigger than a preset, but it's the honest answer to "I want to switch."

**Remy:** Converging. Default to flat `YYYY-MM`; offer presets; record scheme in the journal; new archives
only, no silent re-shuffle; ship `search` (read manifest) as the real searchability win; treat `reorganize`
as a stretch. Let me write it up.

---

## Phase 3 — Converged recommendation

**1. Add `--organize-by` with these presets (path strategies on the existing organizer seam):**

| Value | Layout | Best for |
|-------|--------|----------|
| `month` *(proposed default)* | `YYYY-MM\IMG.ext` (flat) | most people — one click to any month, sorts chronologically |
| `year-month` | `YYYY\YYYY-MM\IMG.ext` (today's nested) | very large / many-year libraries |
| `year` | `YYYY\IMG.ext` (flat) | coarse, minimal folders |
| `flat` | `IMG.ext` (one folder) | tiny libraries / power users who only use search |

`unsorted\` (no trustworthy EXIF) is appended under every scheme. Live Photo pairs always co-locate.

**2. The layout is an archive property, not a per-run flag.** It's recorded in the journal `runs`/meta on
first run. A later run with a different `--organize-by` **warns and keeps the original** scheme (resume must
stay byte-stable) — it never creates a second parallel tree.

**3. Ship a `search`/`find` command over the manifest** (no device needed): filter by date range, media
type, size, camera, GPS-present; print results, or `--open` them in Explorer. *This* is the real fix for
"hard to search" — folders only ever serve casual date-browsing.

**4. Stretch: a `reorganize` command** — a safe, offline, resumable PC-side move of an existing archive into
a different scheme (updates the manifest paths; never touches the iPhone). Only if there's appetite.

**Hard guardrails (unchanged contracts):** device stays read-only; resume stays byte-stable; long-path (R6)
holds for every scheme (`day`/deep schemes get an explicit long-path test); no silent re-shuffle of an
existing archive.

### Open questions for the CEO
- **Default:** flip to flat `YYYY-MM`, or keep nested `YYYY/YYYY-MM` as default and just *offer* flat?
- Is **`search`** in-scope for the next batch (Sage and Kira think it's the bigger usability win than the
  folder shape)?
- Appetite for **`reorganize`** (migrate an existing archive), or presets-for-new-archives only for now?

### CEO decisions (locked 2026-06-21)
- **Default → flip to flat `YYYY-MM`** (nested `YYYY/YYYY-MM` stays available as `--organize-by year-month`).
- **`search` → in scope.**
- **`reorganize` → yes**, but sequenced as **Sprint 4.1** (it moves files on the PC — deserves its own atomic/resumable design + QA).
- Build order: **folder organization first, then the wizard.** Actionable plan: `docs/sprint-4/plan.md`.

### Proposed sprint (if greenlit)
**Sprint 4 — "Organize & find":** `--organize-by` presets (default decision per CEO) + journal-recorded
scheme + no-silent-reshuffle guard + per-scheme path/long-path/Live-Photo tests; **`search` command** over
the manifest with `--open`. `reorganize` deferred to a stretch/Sprint 4.1. README + manifest-schema docs
updated. (Pairs naturally with the TUI batch — see `tui-experience.md`.)
