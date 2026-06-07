# Brainstorm Session 3 — Destination Organization & End-User UX

**Date:** 2026-06-06  
**Topic:** How should the copied media actually be organized on disk? Is `YYYY/YYYY-MM/file.ext` enough, or are we building a "file lake"? What metadata makes the archive useful long-term — tags, EXIF, sidecars, a manifest?  
**Facilitator:** Kira (Product Designer / UX lead for this session)  
**Participants:** Kira, Nova, Sage, Ivy, Remy

---

## Phase 0 — Framing the Question (Kira)

The CEO is right to push on this. We've been treating "the destination" as an afterthought: "files copied = success." That's a copy-tool mindset, not a user mindset.

Real questions the user will ask their archive 6 months from now:
- "Where were we when we took that photo of the dog on the beach?"
- "Show me everything from August 2024."
- "Find the videos from the Tokyo trip."
- "Which photos did I take with the wide lens?"
- "How big is this thing? Can I fit it on the new drive?"

If our answer to any of these is "open Explorer and start clicking around 38,000 files in date folders" — we built a file lake with a thin date veneer. That's not enough.

But if our answer is "install our custom GUI" — we're a photo app now, and we explicitly said we're not.

So the question is really: **what's the minimum metadata + structure that makes this archive useful in any third-party tool the user already has?**

### What iPhone gives us via AFC/DCIM (free — already in the files)

| Metadata | Where | Useful for |
|----------|-------|-----------|
| **DateTimeOriginal** (EXIF) | inside every photo/video | date organization, timeline browsing |
| **GPS lat/long** (EXIF) | most photos (if Location was on) | "where was I?", reverse-geocoding to place names |
| **Camera model + lens** | EXIF | filtering ("show me ultra-wide shots") |
| **Exposure data** | EXIF | photographer filtering (ISO, aperture) |
| **Orientation** | EXIF | display correctness |
| **Apple MakerNote** | EXIF (Apple-specific subblock) | scene detection hints, sometimes burst info |
| **File mtime** | filesystem | fallback when EXIF missing (screenshots, etc.) |
| **Live Photo pairing** | matching basename: `IMG_1234.HEIC` + `IMG_1234.MOV` | reconstructing Live Photos |

### What iPhone does NOT give us via AFC/DCIM (and would require deeper, more invasive access)

| Missing | Why it's not in DCIM | Implication |
|---------|---------------------|-------------|
| Album names | Live in PhotoDB SQLite, accessed via `house_arrest` (much more intrusive, harder to keep read-only) | User's curated structure is **lost** |
| User-assigned keywords | PhotoDB | User's tags are **lost** |
| Favorites / hearts | PhotoDB | Lost |
| Face / People tags | PhotoDB + ML model output | Lost |
| iCloud Shared Album membership | Cloud only | Lost |
| Memories / auto-curated collections | PhotoDB + on-device ML | Lost |

**This is a real limitation we need to be honest about in the README.** The archive is faithful to the *raw camera roll*, not to the user's organizational work inside Apple Photos.

---

## Phase 1 — Free Ideation: How should the archive be structured?

### Kira's pitches

1. **"Boring is best" — date folders + nothing else.**  
   `YYYY/YYYY-MM/filename.ext`. Period. Universal. Works in Explorer, Lightroom, digiKam, Photo Mechanic, PhotoPrism, every cloud backup tool, every NAS. No lock-in. No metadata duplication. EXIF stays in the files. The user adopts whatever photo manager they want, points it at this folder, done. Our archive is a *substrate*, not an app.

2. **"Date folders + sidecar XMP files."**  
   Same date structure, but for each photo we write a `.xmp` sidecar containing the EXIF data extracted and normalized, plus reverse-geocoded location keywords (`Tokyo, Japan` from GPS coords). Adobe-standard format. Lightroom and digiKam read these natively. Users get rich search "for free" the moment they import into any modern photo app. Doubles the file count.

3. **"Date folders + single root manifest."**  
   Date structure for the files, plus ONE `manifest.json` (or `manifest.db`) at the root containing every file's path + EXIF date + GPS + camera + size + Live-Photo-pair-id. Searchable from a terminal in 5 seconds: `jq '.files[] | select(.gps.country == "Japan")' manifest.json`. No filesystem clutter (one file). Doesn't need an app — power users grep it; non-power users still have the date folders.

4. **"Smart-folder views via symlinks/junctions."**  
   Primary store is date folders. Secondary read-only views generated alongside: `by-location/Tokyo/`, `by-camera/iPhone-12-Pro/`, `by-year/2024/`. Each is junctions/symlinks back to the canonical file. Beautiful Explorer browsing, no data duplication. But: Windows symlinks need admin or Developer Mode, junctions are folder-only, and this creates a maintenance burden if files move.

5. **"Just a file lake with a flat structure + heavy metadata."**  
   Drop everything in one folder named by date prefix: `2024-08-15_IMG_1234.HEIC`. No subfolders. Rely entirely on photo-management apps for organization. Pros: trivially simple, no folder-count explosion. Cons: 38,000 files in one folder is hostile to both Explorer and many tools.

### Nova's pitches (engineering reality check)

1. **"Manifest is cheap; sidecars are not."**  
   Writing one manifest.json at the end of a transfer: trivial — single file write, easy to update incrementally via the journal. Writing 38,000 XMP sidecars: doubles inodes, doubles backup load, doubles cloud-sync time, breaks naive backup tools that copy "newer of two files" with stale timestamps. Manifest first.

2. **"Manifest format: SQLite, not JSON."**  
   We already have SQLite (the journal). Adding a `manifest` view to the same DB costs nothing. SQL queries beat jq for ad-hoc search. And a 38k-row JSON parses slow.

3. **"Don't reinvent IPTC/XMP."**  
   If we ever do sidecars, they MUST conform to XMP standard. Don't invent our own `.gas` (get-and-see) format. The whole point is interop with existing tools.

### Sage's pitches (data faithfulness)

1. **"The destination is read-only-to-third-parties data."**  
   Whatever we write, we should be able to regenerate from the source files. The manifest is derived data. EXIF is in the files. If the user deletes our manifest, nothing is lost — they can re-run our tool with `--rebuild-manifest`. This keeps our footprint additive and reversible.

2. **"Live Photo pairing is real and broken in option 1."**  
   Vanilla date folders work — `IMG_1234.HEIC` and `IMG_1234.MOV` land in the same `YYYY-MM/` folder by virtue of having the same EXIF date. Good. But we should explicitly document this and make sure the journal/manifest links them as a pair, so apps that understand Live Photos can use them.

3. **"GPS reverse-geocoding has a network/data-source cost."**  
   To turn `(35.6762, 139.6503)` into `"Tokyo, Japan"` we need either an offline geo database (~100 MB download) or online API calls (no network access policy). For v1, store the raw coords. Geocoding is a strict v2 feature.

### Ivy's pitches (testing & user mental model)

1. **"How does the user know the archive is complete?"**  
   We need a `summary.txt` at the root of the destination, human-readable, listing: total files, total bytes, date range covered, devices encountered (model + serial), last-run timestamp. One-line answer to "is everything backed up?". Trivial to generate from the journal.

2. **"The Apple-Photos-lost-organization gap will burn users."**  
   We have to say this loudly in the README: "This tool copies your camera roll faithfully — but does not preserve albums, keywords, favorites, or face tags from the Apple Photos app. Those live in iCloud / Apple's internal database and require a different export path." If we don't, the first user complaint will be "where are my albums?"

3. **"Test with a real 5-year-old user library."**  
   Edge cases I want to verify before locking org strategy: photos with no EXIF date (screenshots), photos with GPS but no date, photos with corrupt EXIF, deleted-but-recoverable items, Burst photos (a sequence of related shots), Portrait mode (depth data), Cinematic mode video (depth track).

### Remy's pitch (scope discipline)

The CEO asked a UX question, not a "build a photo manager" question. Our promised value is a **reliable, read-only, well-organized copy** of the iPhone media archive. We should:
- Lock in dates folders as the canonical structure (Kira #1) — no debate.
- Add a manifest (Kira #3 / Nova #1) — small, high-leverage, doesn't expand surface.
- Add `summary.txt` (Ivy #1) — half-hour of work, enormous UX payoff.
- Document the Apple-Photos gap (Ivy #2) — README discipline.
- **Defer** XMP sidecars, smart folder views, geocoding to v2 / ideas backlog.

This keeps us a copy tool that produces a *well-described* archive, not an app trying to be Photos.

---

## Phase 2 — Discussion & Refinement

**Kira:** I'll concede the manifest argument. JSON vs. SQLite — Nova, you have a point about reusing the journal DB, but I want a *human-readable* artifact too. People will open the destination folder in Explorer and look around. They need to see SOMETHING that explains what's there.

**Nova:** Compromise: SQLite as the canonical manifest (queryable, performant), PLUS a human-readable `summary.txt` (Ivy's idea). Two artifacts, different purposes. The SQLite manifest can literally be a VIEW or table inside `journal.db` — same file, zero new files at root.

**Sage:** Or we move journal.db OUT of `.get-and-see/` and rename it to `<dest>/get-and-see.db` so it's visible. The hidden-folder convention is good for an in-progress journal but bad for a long-term metadata source the user might want to query.

**Kira:** Strong yes. Hidden = "I might be temporary, ignore me." Visible at root = "I'm part of this archive, here's structured info about it." Rename `journal.db` → `get-and-see.db` and let it serve double duty.

**Ivy:** Schema for the manifest view, then. What fields do we commit to?

**Sage:** Minimum viable:
```sql
CREATE VIEW manifest AS SELECT
  source_path,           -- original DCIM path on device
  dest_path,             -- relative path from destination root
  size_bytes,
  source_mtime,
  exif_datetime_original,  -- ISO 8601 string or NULL
  gps_latitude,            -- REAL or NULL
  gps_longitude,           -- REAL or NULL
  camera_make,             -- "Apple"
  camera_model,            -- "iPhone 12 Pro"
  live_photo_pair_id,      -- INTEGER linking HEIC + MOV pairs, or NULL
  copied_at,               -- ISO 8601
  sha256                   -- NULL unless --verify-hash was used
FROM files WHERE state = 'done';
```

**Nova:** That's exactly right. Note `sha256` is nullable so we don't pay the hash cost unless asked. And `live_photo_pair_id` lets external tools find pairs without filename-matching gymnastics.

**Ivy:** The Apple-Photos gap section in the README — I want to draft that wording now so it's not forgotten:

> **What this tool does NOT do**
> 
> `get-and-see` copies your iPhone's raw camera roll (the `DCIM/` directory). It does **not** preserve information that lives inside the Apple Photos app:
> 
> - Albums and Smart Albums
> - User-added keywords or descriptions
> - Favorites / hearts
> - People and Face tags
> - iCloud Shared Album membership
> - Memories and auto-curated collections
> 
> That information lives in Apple's internal photo database, which is not accessible via the read-only file protocol this tool uses. To export those, you would need a tool that accesses the Photos library directly (different scope, different safety profile).
> 
> What you get is **complete and faithful** at the raw-file level: every photo, video, Live Photo, screen recording, and screenshot in your camera roll, with all original EXIF metadata preserved, organized by date.

**Remy:** Approved. That's honest and sets correct expectations.

**Kira:** On Live Photo pairing — Sage's point matters. Two files (`.HEIC` + `.MOV`, same basename, same EXIF date) get grouped into the same date folder naturally. We should ALSO populate `live_photo_pair_id` in the manifest so query tools can find them. And in `summary.txt` we should count them: "32,401 photos, 4,289 videos (including 1,876 Live Photo pairs), 1,722 screenshots."

**Nova:** Detecting Live Photos = same basename + one is image (.HEIC/.JPG) + other is .MOV + EXIF dates within a second or two of each other. Easy heuristic. Add to the organizer.

**Sage:** One more thing. The manifest should record the **device identity** (UDID + name + model) at the top, so a user with multiple devices over time can answer "which iPhone did this come from?" Add a `devices` table.

**Ivy:** And `runs` table — every invocation of the tool, with start/end time, command line, exit code. So the archive has a full audit trail of how it was assembled.

**Sage:** Agreed. Three tables:
- `devices` — UDID, name, model, first/last seen
- `runs` — every tool invocation, what it did
- `files` — what we already have, with the manifest view on top

**Remy:** This is getting close to scope creep on the journal/manifest. Let me draw a line.

**Locked for Sprint 1:**
- `get-and-see.db` (renamed from `.get-and-see/journal.db`, lives at destination root, visible)
- `files` table (already planned)
- `manifest` view on top of `files`
- `summary.txt` at destination root, human-readable, regenerated at end of every run

**Locked for Sprint 2 or 3:**
- `devices` table
- `runs` table
- Live Photo pair detection + `live_photo_pair_id` column

**Deferred to v2 / backlog:**
- XMP sidecars
- Reverse-geocoding (offline DB)
- Smart-folder symlink views
- Burst / Portrait / Cinematic awareness
- Any kind of GUI on top of the manifest

---

## Phase 3 — Converged Vision

### The Destination, Designed

```
D:\Photos\
├── get-and-see.db          ← SQLite manifest. Open with any SQLite tool.
│                              Contains: files, manifest view, devices, runs.
├── summary.txt             ← Human-readable archive summary, regenerated each run.
├── README.md               ← (Sprint 3) Documents the layout + Apple-Photos gap.
│
├── 2024\
│   ├── 2024-01\
│   │   ├── IMG_4501.HEIC
│   │   ├── IMG_4501.MOV    ← Live Photo motion track, paired by basename
│   │   ├── IMG_4502.HEIC
│   │   └── ...
│   ├── 2024-02\
│   └── ...
├── 2025\
│   └── ...
└── unsorted\               ← Files with no extractable date (rare)
```

### What `summary.txt` looks like

```
get-and-see archive at D:\Photos
Last updated: 2026-06-06 14:32:11 UTC

Total: 38,412 files (397.2 GB)
  Photos:        32,401  (HEIC: 30,118 · JPG: 2,283)
  Videos:         4,289  (MOV: 4,289)
  Live Photos:    1,876 pairs
  Screenshots:    1,722

Date range: 2017-03-14 to 2026-06-05
Devices: iPhone 12 Pro (<device name>)
Runs: 4 (first run 2026-06-01, latest 2026-06-06)

Last run: 8,221 files copied · 30,191 skipped (already done) · 0 failed · 4h 12m
```

### What the manifest enables (sample SQL queries — these work in any sqlite3 CLI)

```sql
-- "Show me everything from August 2024"
SELECT dest_path FROM manifest
WHERE exif_datetime_original LIKE '2024-08%' ORDER BY exif_datetime_original;

-- "Find photos near Tokyo (rough bbox)"
SELECT dest_path, gps_latitude, gps_longitude FROM manifest
WHERE gps_latitude BETWEEN 35.5 AND 35.8 AND gps_longitude BETWEEN 139.4 AND 139.9;

-- "All Live Photo pairs"
SELECT dest_path FROM manifest WHERE live_photo_pair_id IS NOT NULL
ORDER BY live_photo_pair_id;

-- "Biggest files"
SELECT dest_path, size_bytes / 1024 / 1024 AS mb FROM manifest
ORDER BY size_bytes DESC LIMIT 20;
```

### Honest Limitations (in the README)

This tool is a copier, not a photo manager. We deliberately do NOT:
- Preserve Apple Photos albums, keywords, favorites, or People tags (those live in a different database the tool does not access).
- Tag, rate, or auto-curate files.
- Provide a browsing UI — use Windows Explorer, or import the date folders into any photo app (Lightroom, digiKam, Photoprism, etc.).

### Tech Stack Implication

No new dependencies. We're already using `Microsoft.Data.Sqlite` for the journal. The manifest is a view on top of the existing schema.

### Team Vote
- **Kira** ✅ "Substrate, not app. Date folders + visible manifest + summary.txt nails the UX without us pretending to be Photos."
- **Nova** ✅ "Manifest as a view on top of the existing SQLite is essentially free. summary.txt is the right size."
- **Sage** ✅ "Renaming `journal.db` → `get-and-see.db` at the destination root is the right framing. The DB is a feature, not implementation detail."
- **Ivy** ✅ "I'll write the Apple-Photos-gap section into the README spec and add it as an acceptance criterion for Sprint 3."
- **Remy** ✅ "Locked. Sprint 1 gets the rename + summary.txt + manifest view. Heavier metadata (devices, runs, live-photo pair detection) → Sprint 2. XMP / geocoding → v2."

**Unanimous.**

---

## Action items pulled into the plan

1. **PROJECT_BRIEF Section 4** (architecture): show `get-and-see.db` and `summary.txt` at destination root.
2. **PROJECT_BRIEF Section 5** (Key Files): note the renamed `get-and-see.db` and `summary.txt`.
3. **PROJECT_BRIEF Section 2** (concept): add the "what we don't do" honesty paragraph.
4. **Sprint 1 plan**: rename journal location, add `manifest` SQL view, add `summary.txt` generator at end-of-run.
5. **Sprint 2 plan** (when written): add `devices` table, `runs` table, Live Photo pair detection.
6. **Ideas backlog**: add XMP sidecars, reverse-geocoding, symlink smart folders, Burst/Portrait/Cinematic awareness, Apple Photos export via house_arrest (separate-tool scope).
7. **Risk register**: add R19 (user expects Apple Photos albums to be preserved → file lake confusion) with mitigation = README upfront disclaimer.
