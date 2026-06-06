# Brainstorm Session 1 — "Get & See" (iPhone Media Archive → PC)

**Date:** 2026-06-06  
**Participants:** Kira (Product Design), Nova (App Developer), Sage (Backend/Systems), Ivy (QA), Remy (Producer)  
**Prompt:** Build a simple tool to copy the media archive from an iPhone 12 Pro to a Windows PC.

---

## Phase 1: Free Ideation

### Kira (Product Designer)
*Thinks about: user flow, simplicity, what "done" looks like for a normal person*

1. **"One-Click Archive"** — Plug in iPhone, run the tool, it copies everything organized by date folders (`2024/2024-01/2024-01-15/IMG_1234.jpg`). No config, no decisions. Progress bar shows what's happening. Done = a folder with all your media, browsable in Explorer.

2. **"Smart Sync Dashboard"** — A small GUI that shows what's on the phone vs. what's already on PC. Highlights new/missing files. User picks "copy all new" or selects specific albums. Shows thumbnails so you know what you're getting.

3. **"CLI Power Tool"** — For the user who just wants `get-and-see --all --dest D:\Photos`. Fast, scriptable, no GUI overhead. Add `--dry-run` to preview what would be copied. Keep it Unix-philosophy simple.

### Nova (App Developer)
*Thinks about: what's buildable, what frameworks exist, GUI vs CLI trade-offs*

1. **Python + pymobiledevice3** — Pure Python, talks to iPhone over USB using Apple's native protocols (AFC/lockdownd). No iTunes dependency. Works on Windows. Can enumerate all DCIM files, copy them preserving EXIF dates. CLI-first, optionally wrap in a simple tkinter or web UI later.

2. **Python + MTP via WPD (Windows Portable Devices)** — Use Windows' built-in MTP support via COM API. iPhone shows up as a portable device. More limited than AFC (can't access all paths), but zero extra dependencies. Just `comtypes` or `pythoncom`.

3. **Electron/Tauri app with libimobiledevice bindings** — Full desktop app. Overkill for "simple tool" but gives a nice UI. libimobiledevice is battle-tested but C-based, needs bindings. Tauri would keep it lightweight.

### Sage (Backend/Systems Engineer)
*Thinks about: reliability, file integrity, edge cases, performance*

1. **Checksum-based dedup** — iPhone media archives can have 50k+ files. Don't re-copy what's already there. Hash (xxhash for speed) + file size comparison. Keep a local manifest (SQLite or JSON) of what's been copied.

2. **HEIC handling** — iPhone 12 Pro shoots HEIC by default. Options: (a) copy as-is, (b) convert to JPEG on the fly via `pillow-heif`, (c) offer both. Must preserve EXIF metadata during conversion. Also handle Live Photos (.MOV paired with .HEIC).

3. **Resumable transfers** — If you unplug mid-copy or the tool crashes, it should pick up where it left off. Write a transfer manifest: file path, expected size, hash, status (pending/done/failed). On restart, skip completed files.

### Ivy (QA Engineer)
*Thinks about: what breaks, what's hard to test, user mistakes*

1. **Device detection edge cases** — What if iPhone is locked? What if "Trust this computer" hasn't been tapped? What if multiple Apple devices are connected? The tool needs clear error messages, not cryptic stack traces.

2. **File integrity verification** — After copy, verify: file size matches, file is readable (not truncated), EXIF data preserved. Especially important for large 4K videos (ProRes can be 6GB+).

3. **Space checking** — Before starting a 200GB copy, check destination disk space. Show warning if not enough. Also handle network drives (slower, may disconnect).

---

## Phase 2: Discussion & Refinement

**Remy:** Good spread. Let me facilitate. Core question first: CLI or GUI?

**Kira:** Start CLI, 100%. This is a utility, not an app store product. A clean CLI with good output formatting IS the UX. We can add a GUI later if needed, but a well-designed CLI that shows progress and organizes files by date — that's enough for v1.

**Nova:** Agreed. CLI-first with Python. But which device protocol? I lean toward **pymobiledevice3** over MTP. Here's why: MTP on Windows is flaky with iPhones — Apple doesn't fully support it, file enumeration is slow, and you can't always access the full DCIM tree. pymobiledevice3 uses Apple's native AFC protocol, same as what Finder/iTunes uses internally.

**Sage:** +1 on pymobiledevice3. I've seen MTP choke on large libraries. AFC gives us direct DCIM access, proper file metadata, and it's faster for enumeration. Only downside: requires the user to pair/trust the device first, but that's a one-time thing and we get a clear error if they haven't.

**Ivy:** My concern with pymobiledevice3 — does it work reliably on Windows? Last I checked, it needs a USB driver (Apple Mobile Device Support, installed with iTunes). We need to document that prerequisite clearly, or detect it and tell the user what to install.

**Nova:** Good point. iTunes installs the required USB drivers. We can either require iTunes to be installed (most iPhone-on-Windows users have it), or point them to Apple's "Apple Devices" app from the Microsoft Store which also installs the drivers. We should detect this at startup and give a helpful message.

**Sage:** On the dedup question — for v1, I say skip hash-based dedup. Just compare filename + file size + modification date. It's fast, it's good enough for 99% of cases, and avoids the complexity of maintaining a hash database. If someone runs the tool twice, it skips files that already exist with matching size. Simple.

**Kira:** I like that. For file organization, I propose: `<dest>/YYYY/YYYY-MM/filename.ext`. Don't rename files — keep original iPhone filenames (IMG_1234.HEIC). Date comes from EXIF or file modification time. If no date available, put in `<dest>/unsorted/`.

**Nova:** Should we convert HEIC to JPEG?

**Kira:** Make it optional. Default: copy as-is (preserve originals). Flag: `--convert-heic` to also generate JPEGs alongside. Don't delete originals.

**Sage:** Agree. Conversion is a nice-to-have, not core. For v1, just copy files faithfully. HEIC is widely supported now anyway. We can add `--convert-heic` in Sprint 2.

**Ivy:** What about Live Photos? Those are an HEIC + MOV pair. They should stay together — same destination folder. And ProRes video — those files are HUGE (several GB). We need progress reporting per-file for large files, not just per-batch.

**Nova:** pymobiledevice3's AFC service supports streaming file reads, so we can show progress per-file. I'll implement a progress bar using `rich` — it handles multi-line progress displays beautifully in terminals.

**Remy:** Alright, converging. Let me summarize what I'm hearing for v1 scope:

---

## Phase 3: Final Pitches — Converged Vision

### The Tool: "get-and-see"
A Python CLI tool that copies your iPhone media archive to your Windows PC over USB.

**Core v1 Features:**
1. Connect to iPhone via USB (pymobiledevice3 / AFC protocol)
2. Enumerate all media in DCIM (photos, videos, Live Photos)
3. Copy to PC organized by date: `YYYY/YYYY-MM/filename.ext`
4. Skip already-copied files (filename + size match)
5. Show per-file progress with `rich` progress bars
6. Handle errors gracefully: device locked, not trusted, disk full, USB disconnect
7. Resumable: re-run and it picks up where it left off
8. `--dry-run` mode to preview what would be copied
9. `--dest` to specify destination (default: `./iPhone-Media/`)

**Deferred to v2:**
- HEIC → JPEG conversion (`--convert-heic`)
- Album-based organization (DCIM doesn't have album info via AFC)
- GUI wrapper
- Hash-based dedup
- Thumbnails / preview

**Tech Stack:**
- Python 3.11+
- pymobiledevice3 (device communication)
- rich (terminal UI, progress bars)
- click (CLI framework)
- pathlib + shutil (file operations)
- Pillow or exifread (EXIF date extraction)

**Team Vote:**
- Kira: ✅ "Simple, focused, does one thing well. Love the date-folder organization."
- Nova: ✅ "Buildable in 2 sprints. pymobiledevice3 is solid."
- Sage: ✅ "Resumable + skip-existing is the right reliability model for v1."
- Ivy: ✅ "Clear error messages are the make-or-break. I'll hammer the edge cases."
- Remy: ✅ "Tight scope, clear deliverables. Let's ship it."

**Unanimous: BUILD IT.**
