# Brainstorm Session 2 — New Constraints: C#, Read-Only, 400GB Reliability

**Date:** 2026-06-06  
**Trigger:** CEO added three hard constraints after Session 1:
1. **Prefer C#** over Python
2. **Read-only by default** — zero risk of damaging the iPhone
3. **~400 GB of data** to transfer — MTP/Windows Explorer is known unreliable. Must use validated, battle-tested protocols.

> **Correction (2026-06-06, same day):** Initial draft of this brainstorm targeted .NET 8 + C# 12. Verified against Microsoft's official support policy (last updated 2026-05-14): **.NET 8 entered Maintenance support in May 2026 and reaches EOL on Nov 10, 2026** (~5 months out). .NET 9 (STS) is also in maintenance with the same EOL. The current LTS in Active support is **.NET 10 (released Nov 11, 2025, EOL Nov 14, 2028)** with **C# 14**. All sections below have been updated to .NET 10 + C# 14. Additional 2026 corrections: **xUnit v3** (GA 2025) replaces xUnit v2; **Shouldly** replaces FluentAssertions (which moved to a paid commercial license in v8, Jan 2025).

---

## Phase 1: Free Ideation — Reacting to the New Constraints

### Sage (Systems Engineer) — protocol reliability is the headline issue

The 400GB number changes everything. MTP over Windows is unreliable even for 5GB — for 400GB it's a guaranteed failure. Anyone who's tried to drag a large DCIM folder out of an iPhone via Explorer knows: random disconnects, hung enumeration, files that "copy" as 0 bytes, no error reporting.

There are two protocol families that actually work:

1. **AFC (Apple File Conduit)** — the protocol Finder, iTunes, iMazing, and pymobiledevice3 all use under the hood. Runs over `usbmuxd`. Streams cleanly, has stable file enumeration, and is what every "serious" iPhone tool builds on. **This is what we must use.**
2. **MTP via WPD COM API** — what Windows Explorer uses. We are explicitly avoiding this.

For C# specifically, there are two viable AFC libraries:
- **NetiMobileDevice** — pure C# port of pymobiledevice3, no native DLLs. Most modern and idiomatic for .NET.
- **imobiledevice-net** — C# bindings around the native libimobiledevice C library. Battle-tested but ships native DLLs.

Recommendation: try NetiMobileDevice first. If it has gaps for our use case, fall back to imobiledevice-net. Both are AFC-based, so the protocol layer is the same.

### Nova (App Developer) — C# stack mapping

Going to C# / **.NET 10 (LTS)** — the current Active-support LTS. Mapping the Python stack to C# equivalents:

| Python (Session 1) | C# (Session 2, corrected) |
|---|---|
| pymobiledevice3 | **NetiMobileDevice** (primary), imobiledevice-net (fallback) |
| click | **System.CommandLine** (stable in .NET 10) |
| rich | **Spectre.Console** (excellent progress bars, tables, prompts) |
| exifread / Pillow | **MetadataExtractor** |
| pytest | **xUnit v3** (GA 2025, modern target) |
| pytest-mock | **NSubstitute** (MIT-licensed) |
| (assertions) | **Shouldly** (BSD; not FluentAssertions — paid since v8) |
| pip + venv | **dotnet** SDK 10, single-file publish |

Bonus: .NET 10 has mature single-file self-contained publish + significantly improved Native AOT. We can ship `get-and-see.exe` as one file with zero install.

### Ivy (QA) — risk catalog for 400GB transfers

Reliability at this scale means enumerating every failure mode and defining behavior for each one. Here's my draft risk register:

| # | Risk | Likelihood @ 400GB | Impact | Mitigation |
|---|---|---|---|---|
| R1 | USB disconnect mid-stream | High | Partial file on disk | Atomic write: write to `.partial`, rename on success |
| R2 | iOS sleeps / locks device | High | Stream stalls | Detect stall, reconnect, resume that file |
| R3 | Tool crashes / Ctrl+C | Medium | Unknown transfer state | Persistent journal (SQLite) tracks each file's status |
| R4 | Destination disk fills | Medium | Corrupt last file | Pre-flight space check + per-file space check |
| R5 | Filename collision (different files, same name across DCIM folders) | Medium | Silent overwrite | Detect collision, append disambiguator, never overwrite |
| R6 | Windows MAX_PATH (260 chars) | Low | Cryptic IO error | Use `\\?\` long path prefix or enable LongPathsEnabled |
| R7 | File truncated in transit (no error raised) | Low | Silent data loss | **Mandatory** size check; optional SHA-256 verify |
| R8 | Re-run after partial failure | High | Re-copies everything | Journal-aware skip-existing |
| R9 | Antivirus / Defender quarantines a file mid-write | Low | Failed write | Catch, log, retry once, then mark failed |
| R10 | User pulls cable on purpose | Certain | All in-flight files | Clean shutdown handler, journal flush |
| R11 | **Writing to iPhone by accident** | Low (with discipline) | **Could corrupt device library** | **Read-only architecture: no write APIs imported or called.** |
| R12 | Bit corruption in transit | Very low | Silent data loss | Trust AFC framing (it has CRC), optional verify mode |

R11 is the one the CEO specifically called out. It deserves a hard architectural rule, not just a code review note.

### Kira (Product Designer) — UX for a multi-hour operation

400GB at typical USB 3.0 / Lightning speeds (~30–50 MB/s real-world for iPhone) is **3 to 4 hours minimum**. The UX has to assume the user will:
- Walk away from the machine
- Come back hours later and want to know exactly what happened
- Want to interrupt cleanly and resume tomorrow
- Want a single-glance "did it work?" answer at the end

So the CLI needs:
- **Live dashboard** (Spectre.Console live display): overall % done, files done / total, bytes done / total, current file with its own bar, MB/s, ETA
- **Two-line summary at end**: "397 GB copied across 38,412 files in 4h 12m. 0 failed. Resume not needed."
- **Status file**: even after the tool exits, `journal.db` and a human-readable `last-run.log` tell you exactly what happened
- **Verbose mode (`-v`)** prints each file as it completes
- **Quiet mode (`-q`)** just a single progress line, suitable for piping to a log file

### Remy (Producer) — scope discipline

Three explicit decisions to lock in before we move on:
- **CLI only for v1** — no GUI. GUI on top of a reliable CLI is a v2 conversation.
- **C# 14 / .NET 10 (LTS)** — confirmed. Single-file self-contained publish for the final binary.
- **Read-only is architectural, not a flag.** The word "read-only" should not appear as a CLI flag the user can disable, because v1 will simply not contain any write code paths.

---

## Phase 2: Discussion & Refinement

**Remy:** Let's pressure-test the read-only claim. What does "read-only by design" actually mean in code?

**Sage:** Concretely:
1. We use the AFC service of NetiMobileDevice, but we only ever call its read methods: `ListDirectory`, `GetFileInfo`, `OpenFile(read mode)`, `ReadStream`, `Close`. We do not import, alias, or call: `WriteFile`, `Remove*`, `MakeDirectory`, `Rename`, or any of the `house_arrest` write modes.
2. We add a static analyzer rule or a unit test that scans the compiled code for any reference to AFC write methods. If anyone ever adds one, the build fails.
3. The CLI has no `--delete-after-copy`, no `--move`, no `--cleanup` flag. Ever. In v1.
4. Document it loud in the README: "This tool never writes to or deletes from your iPhone. Period."

**Nova:** I'll add the unit test in Sprint 1. Easy to do with reflection over our own assembly.

**Ivy:** I want item 2 to be a CI gate, not just a unit test. If we ever add CI, that check is one of the first ones.

**Remy:** Agreed. Goes in the ideas backlog as a CI item.

**Kira:** On the journal — should we expose it to the user, or hide it?

**Sage:** Hide it by default but make it discoverable. Store it at `<dest>/.get-and-see/journal.db`. Add a `get-and-see status <dest>` subcommand that opens the journal and prints "Last run: <date>, 38,412 files, 397 GB, 0 failed". Power users can query the SQLite directly if they want.

**Nova:** Subcommands in System.CommandLine are clean. Default verb is `copy`, plus `status` for journal inspection. Maybe `verify` in v2 for hash recheck.

**Ivy:** What about the "size check is mandatory" claim — what if the iPhone reports a wrong size? Does AFC give us a reliable size?

**Sage:** AFC's `GetFileInfo` returns size from the iOS filesystem layer. It's reliable. The risk isn't the reported size being wrong — it's the bytes transferred not matching. We verify by counting bytes received from the stream and comparing to the AFC-reported size. If they don't match, fail the file and leave `.partial` for debugging.

**Kira:** Spectre.Console can absolutely do the dashboard I described. I've seen tools using it that look like proper TUI apps.

**Remy:** OK, scope is converging. One more thing — should we attempt the transfer in a single AFC session, or open/close per file?

**Sage:** Per-file open/close. AFC sessions can get into a bad state over hours; per-file is more robust. The cost is a handful of milliseconds per file. With 38,412 files that's maybe 6 minutes of overhead — negligible vs. a 4-hour transfer, and we get crash resilience in exchange.

**Nova:** Concurrency? Multiple files in parallel?

**Sage:** **No.** A single iPhone AFC connection is not safely concurrent in our experience, and USB bandwidth is the bottleneck anyway. Sequential, one file at a time, full reliability. Maybe revisit in v2 after we have telemetry.

**Ivy:** Pre-flight checklist before we start a 400GB transfer:
1. Device detected and trusted
2. Destination path writable
3. Destination has free space ≥ (estimated total + 5% headroom)
4. AFC service starts cleanly
5. Can enumerate root of DCIM
6. Power profile note: warn if PC is on battery

If any check fails, refuse to start. Bail clearly.

**Remy:** Love it. That goes in the plan as a discrete task.

---

## Phase 3: Final Pitch — Converged v1

### Product: **get-and-see**
A C# 14 / **.NET 10 (LTS)** single-file CLI that reads the entire media archive from an iPhone over USB using Apple's native AFC protocol, organizes it into date folders on a Windows PC, and is architecturally incapable of modifying the iPhone.

### Core Principles
1. **Read-only by design** — no AFC write methods are referenced anywhere in the codebase. Enforced by a unit test that fails the build.
2. **Reliable at 400GB** — AFC (not MTP), atomic file writes, SQLite journal, resumable, per-file pre/post verification.
3. **Single-glance UX** — live Spectre.Console dashboard during transfer, clean summary at end, journal preserved for inspection.
4. **One binary, zero install** — .NET 10 single-file self-contained publish. Drop the EXE on a flash drive.

### v1 Feature List
- `get-and-see copy --dest <path>` — main command
- `get-and-see copy --dest <path> --dry-run` — enumerate + plan, no transfer
- `get-and-see copy --dest <path> --verify-hash` — opt-in SHA-256 verification (slower)
- `get-and-see status --dest <path>` — read journal, print last run summary
- AFC connection via NetiMobileDevice (with imobiledevice-net fallback path documented)
- Pre-flight checks (device trust, disk space, writable dest, AFC handshake, USB stability)
- Per-file: open → stream to `.partial` → fsync → rename → mark journal done
- Date-folder organization (`YYYY/YYYY-MM/filename.ext`) from EXIF DateTimeOriginal or file mtime
- Filename collision handling (append `_N`, never overwrite)
- Long-path support (`\\?\` prefix)
- Live progress dashboard (overall + per-file, ETA, MB/s)
- Clean Ctrl+C handling (flush journal, exit cleanly, resumable next run)
- Final summary table

### Tech Stack (final, validated 2026-06-06)
- **.NET 10 (LTS)**, **C# 14** — current Active-support LTS, EOL Nov 2028
- **NetiMobileDevice** — primary AFC library (pure C#)
- **imobiledevice-net** — documented fallback (native bindings)
- **System.CommandLine** — CLI parsing and subcommands
- **Spectre.Console** — live progress, tables, formatting
- **Microsoft.Data.Sqlite** (10.x) — transfer journal
- **MetadataExtractor** — EXIF date parsing
- **xUnit v3** + **Shouldly** + **NSubstitute** — tests (FluentAssertions intentionally excluded; paid commercial since v8/Jan 2025)
- **dotnet format** + built-in analyzers — code style

### What's NOT in v1
- HEIC → JPEG conversion (v2)
- GUI (v2 if demanded)
- Multi-device support (v2)
- Album / smart album organization (v2 — needs PhotoDB access, harder)
- Concurrent file transfers (v2 after telemetry)
- Hash dedup across re-organizations (v3)
- Selective by date range (v2)

### Team Vote
- **Kira** ✅ "Dashboard + clean summary will make 4-hour transfers feel manageable."
- **Nova** ✅ "C# 14 / .NET 10 single-file publish is the right shipping format for Windows."
- **Sage** ✅ "AFC + journal + atomic writes is the proven recipe. Read-only is enforceable."
- **Ivy** ✅ "Risk register is concrete and testable. The build-time check on write methods is the kind of guarantee I want."
- **Remy** ✅ "Tight scope, validated tech, explicit safety posture. Locked."

**Unanimous: BUILD IT.**
