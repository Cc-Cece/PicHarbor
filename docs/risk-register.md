# Risk Register — get-and-see

Source: brainstorm Session 2, Ivy's risk catalog. Living doc — append new risks as they're discovered.

| # | Risk | Likelihood @ 400GB | Impact | Mitigation | Owner |
|---|------|---|---|---|---|
| R1 | USB disconnect mid-stream | High | Partial file on disk | Atomic write: stream to `.partial`, fsync, size-verify, then rename | Sage |
| R2 | iOS sleeps / locks device | High | Stream stalls | Detect stall (read timeout), close handle, reconnect, retry that file once | Sage |
| R3 | Tool crashes / Ctrl+C | Medium | Unknown transfer state | SQLite journal with per-file state flushed on every transition; clean shutdown handler | Sage |
| R4 | Destination disk fills | Medium | Corrupt last file | Pre-flight free-space check (total + 5%); per-file detect ENOSPC and mark `failed` cleanly | Sage |
| R5 | Filename collision across DCIM subfolders | Medium | Silent overwrite | Journal-aware collision detection; append `_2`, `_3`; never overwrite | Nova |
| R6 | Windows MAX_PATH (260 chars) | Low | Cryptic IO error | All destination paths use `\\?\` long-path prefix | Nova |
| R7 | File truncated in transit (no exception raised) | Low | Silent data loss | **Mandatory** byte-count vs AFC-reported size; mismatch → leave `.partial`, mark failed | Sage |
| R8 | Re-run after partial failure | High | Would re-copy everything without journal | Journal-aware skip: pending / failed → retry, done → skip | Sage |
| R9 | Antivirus quarantines `.partial` mid-write | Low | Failed write | Catch IO exception, mark failed in journal, continue | Nova |
| R10 | User pulls cable on purpose | Certain | All in-flight files lost | Clean shutdown on AFC errors, journal already flushed for completed files | Sage |
| **R11** | **Accidentally writing to iPhone** | **Low (with discipline)** | **Could corrupt device library** | **Architectural read-only: AFC write methods not exposed; build-time test fails if referenced** | **Ivy** |
| R12 | Bit corruption in transit | Very low | Silent data loss | Trust AFC's CRC framing for default mode; `--verify-hash` (SHA-256) for paranoid mode | Sage |
| R13 | PC sleeps mid-transfer | Medium | Stalls, AFC connection lost | Document: user should disable sleep for the run. Resume covers the rest. | Kira (docs) |
| R14 | Battery dies (laptop) | Medium | Same as R13 | Pre-flight warning if on battery power | Sage |
| R15 | Permissions error on destination | Low | Hard fail | Pre-flight write test (create + delete a probe file) | Sage |
| R16 | EXIF date is in the future / nonsense | Low | File goes to weird folder | Validate parsed date in [1990, now+1day]; outside range → `unsorted/` | Nova |
| R17 | Two files with same name AND same size collide | Low | Wrongly treated as already-copied on resume | Journal stores source-side path + size; identity = source path, not name | Sage |
| R18 | Destination is on a network drive | Medium | Disconnect mid-write | Documented as supported but discouraged; resume handles it | Kira (docs) |
| R19 | User expects Apple Photos albums / keywords / favorites / People tags to be preserved | High | Trust collapse ("this didn't back up my organization") | README upfront: explicit "What this tool does NOT do" section per brainstorm Session 3. Wording locked in PROJECT_BRIEF.md Section 2. | Kira / Ivy |
| R20 | Aging native deps bundled in `imobiledevice-net 1.3.17` (OpenSSL 1.1 EOL, 2021 libusb) | Low | Latent security/compat risk in native layer | Local-USB-only, read-only, no network → minimal exposure. Accept for v1. Revisit if a maintained fork appears. Source: `docs/sprint-1/afc-library-decision.md` §5.4. | Sage |
| R21 | "Apple Devices" Store-app usbmuxd service is lazy — port 27015 closed until the app is launched once; looks identical to "no device" | Medium | Confusing "no iPhone found" when one is plugged in | Pre-flight probes `127.0.0.1:27015`; if unreachable, emit actionable error ("open Apple Devices app once, or install iTunes"). Source: decision doc §5.3. | Sage (check) / Kira (copy) |

## How this list is used
- **Sprint 1 (shipped + QA-verified):** R1, R3, R4, R7, R8, R10, R11, R17, R21 — plus R5 (collision) and R15 (writable-dest) which also landed in S1. Core copy pipeline.
- **Sprint 2:** R2 (read-stall watchdog — also closes #11), R9 (verify existing antivirus handling), R13 (PC-sleep doc warning), R14 (on-battery warning).
- **Sprint 3:** R6 (long-path), R12 (`--verify-hash`), R16 (already mitigated in S1; re-verify), R18 (network drive). Polish + verification.
- **Tracked / accept**: R20 (aging native deps — revisit only if a maintained fork appears).
- **QA**: writes a test case for every R# that's testable without real hardware (mocked AFC).
- **New risks**: append below, do not edit old rows.
