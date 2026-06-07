# Sprint 2 Consilium — UX, Resumability & Pre-flight

**Date:** 2026-06-07
**Format:** Consilium (pre-sprint validation). Each agent reviews the proposed Sprint 2 scope from their perspective, flags risks, and pushes to cut or re-order. We converge on a prioritized task list.
**Facilitator:** Remy (Producer)
**Participants:** Kira (Product/UX), Nova (App), Sage (Systems), Ivy (QA), Quill (Tech Writer), Remy (Producer)

---

## 0. Where Sprint 1 left us (the ground truth)

Sprint 1 shipped a working, QA-verified read-only copier (27,478 files / 269 GB real-hardware run, read-only contract proven device-unchanged). It carries forward:

- **Open #11** (`severity:major`): USB unplug mid-copy → native AFC read **hangs ~6 min, no timeout, no exit**. Data-safe (journal + resume + read-only all held), but a responsiveness hole. Already earmarked Sprint 2 **Task 1**.
- **Open #10** (`enhancement`): no live "current speed" readout during a multi-hour copy.
- **QA deferred (hardware-time, not code):** a single *uninterrupted* full-archive run (AC-7) and the *live* end-of-run `summary.txt` (AC-8/AC-17). Mechanisms proven; they just need a clean run — which #11's fix makes reliable.
- **QA observations:** ~30 MB/s sustained (USB-2.0 Lightning ceiling — link-bound, not our bug); a non-media `.bin` in `/DCIM/` faithfully copied (working as designed).

**Proposed Sprint 2 scope (from brief §7):** #11 watchdog · live dashboard (+#10) · `status` subcommand · `devices` + `runs` tables · Live-Photo pair detection · R2 stall-detect/reconnect. Plus risk-register S2 bucket: R2, R9, R13, R14 (note: R5 collision + R15 writable-dest already shipped in S1).

---

## 1. Agent reviews

### Sage (Systems) — "#11 and R2 are the same workstream; do them first, together."

The unplug hang (#11) and the iOS-sleep stall (R2) are the **same root cause**: the native `afc_file_read` is a blocking call with no timeout, so any loss of the byte stream — cable yank, device sleep, USB bus reset — parks the thread forever. One mechanism fixes both:

- Wrap each chunk read in a **watchdog**: if no bytes arrive within N seconds (propose 30s default, configurable), cancel the read, surface a `DeviceException`, mark the in-flight file `failed`/`in_progress`, exit non-zero.
- For R2 specifically (transient sleep, device still present), optionally **reconnect + retry the file once** before giving up — but I want to scope that carefully. **My recommendation: Sprint 2 ships the watchdog (detect + fail cleanly + resumable). Auto-reconnect-and-retry is a fast-follow** — it's more state and more failure modes, and resume already recovers it on a manual re-run.

One caution: `imobiledevice-net`'s read is synchronous native. The watchdog has to run the read on a cancellable path (a worker task we can abandon) without leaking the native handle. I've a design for it but it needs a real-device test — this is not a pure-unit-test task.

On `devices` + `runs` tables: cheap and additive to the existing SQLite journal. `runs` is genuinely useful (audit trail; feeds `status`). `devices` matters for the multi-device-over-time story but we only have one device today — I'd build the schema, populate it, but not over-invest in UI for it.

### Kira (Product/UX) — "The dashboard is the headline. But it must not become a toy that slows a 4-hour copy."

The live dashboard is what makes a multi-hour transfer *feel* trustworthy. Spectre.Console `Live`/`Progress` gives us: overall % + bytes, current file with its own bar, **current MB/s (#10)**, average MB/s, ETA, counts (done/skipped/failed). That's the single biggest perceived-quality jump in the whole project.

Two hard constraints from me:
1. **It cannot slow the transfer.** Refresh ≤ 4×/sec, computed from data the copy loop already has (bytes/time). No extra device I/O. If Spectre's render ever contends with the copy, the copy wins.
2. **It must degrade gracefully** — redirected output (`> log.txt`), non-interactive terminal, or `--no-dashboard` must fall back to the Sprint 1 per-file text lines. CI and piping must still work.

#10 (current speed): show **both** `current` (rolling ~3s window) and `avg`, plus ETA from current. The rolling window is what answers "is it stuck?" — exactly the anxiety a 4-hour copy creates.

On `status`: yes, small and high-value. `get-and-see status --dest <path>` opens `get-and-see.db` and prints the last run + archive totals **without a device attached**. That's the "did my backup work?" answer Ivy and I both wanted in Session 3.

I'll also draft the copy for #11's new error message and the dashboard labels.

### Nova (App) — "Realistic load. I'd cut Live-Photo pairing to protect the dashboard."

Mapping to code:
- **Dashboard** → new `LiveDashboard` (Spectre) behind an `IProgressReporter` abstraction, so the copy engine stays UI-agnostic and testable. The Sprint 1 text output becomes the fallback reporter. This is the biggest single task.
- **`status` subcommand** → `System.CommandLine` verb + a read-only `TransferJournal` open + reuse `SummaryWriter`. Small.
- **`runs`/`devices` tables** → schema migration in `TransferJournal` (additive; bump a `schema_version`). Medium.
- **Live-Photo pairing** → organizer pass that links `IMG_1234.HEIC` + `IMG_1234.MOV` by basename + close mtime, populates `live_photo_pair_id`. **Honestly this is the most cuttable item.** It's nice for the manifest but invisible in the core "copy my photos" flow. If the dashboard + watchdog eat the sprint, this slips to Sprint 3 with zero user harm.

I want the watchdog (Sage) and the dashboard (me) as the two pillars; everything else flexes around them.

### Ivy (QA) — "Re-run the full acceptance once #11 lands, and give me a way to test the watchdog without a phone."

My priorities:
1. **#11 is the one that needs real-hardware re-test** — unplug + (if we can simulate) sleep. I can't fully mock a native blocking read, so Sage needs to expose the watchdog timeout as injectable (a tiny `TimeProvider`/timeout seam) so I can unit-test "no bytes for N → cancels + fails + resumable" against a mocked slow stream. Please build that seam in, or the watchdog is only testable by yanking a cable.
2. **Full uninterrupted archive run (AC-7 closure)** — once the watchdog makes long runs safe, I do the 269 GB end-to-end, capture the live `summary.txt`, and close the Sprint 1 deferrals. That's a Sprint 2 QA deliverable, not a code task.
3. **Dashboard**: I test the *fallback* path hard — piped output, `--no-dashboard`, non-TTY — because that's where these break. And I confirm the dashboard adds **no measurable slowdown** (compare MB/s with/without on the same subset).
4. **Read-only contract still holds** — any new code (reconnect logic especially) gets the same `ReadOnlyContractTests` scrutiny. Reconnect must re-open **read-only**; that's a `safety:read-only-contract` watch area.

SAFETY RULE #0 still governs: no device writes, ever, in any test.

### Quill (Tech Writer) — "Two user-facing surfaces ship this sprint. Don't let them ship undocumented."

The dashboard and `status` are the first things a user actually *sees and reads*. Even though full user docs are Sprint 3, I want **stub-level coverage now** so Sprint 3 isn't archaeology:
- A short `--help` review for the new `status` verb and any new flags (`--no-dashboard`, `--read-timeout`). I'll co-write with Nova/Kira.
- A note in the (placeholder) README that `status` exists and the dashboard is the default.
- Capture the new error-message strings (#11 watchdog, reconnect) in one place so the Sprint 3 troubleshooting guide can reference them.
This is light — a few paragraphs — but it stops the Sprint 3 docs sprint from reverse-engineering behavior.

### Remy (Producer) — "Two pillars, firm cut line, QA re-test baked in."

I'm hearing strong convergence: **#11/R2 watchdog and the dashboard are the two pillars.** Everything else is supporting or cuttable. Let me set the cut line explicitly so we ship on time.

---

## 2. Debate & convergence

**Remy:** Sage, Nova — is auto-reconnect-and-retry (the R2 "soft" recovery) in or out for Sprint 2?

**Sage:** Out, as an automatic behavior. **In** as: watchdog detects the stall, fails the file cleanly, and the run continues to the *next* files? No — if the device is asleep, every subsequent read also stalls. So on watchdog trip we should **stop the run cleanly** (not thrash), leave everything resumable, and tell the user to reconnect and re-run. That's honest and safe. True auto-reconnect is Sprint 3.

**Nova:** Agreed. "Detect → stop clean → resumable → clear message" is the Sprint 2 contract. Much less risk than mid-run reconnection.

**Kira:** I'm fine with that as long as the *message* is great — "Device stopped responding (asleep or disconnected). Progress saved — reconnect and run the same command to resume." That turns a scary hang into a calm, actionable stop.

**Ivy:** That version I can test with an injected timeout + a mocked stalling stream. 

**Remy:** Good. Live-Photo pairing — cut or keep?

**Nova:** Keep the **schema + detection** if the dashboard lands with time to spare; cut the moment it's at risk. It's the designated release valve.

**Kira:** Agree. Dashboard > pairing, every time.

**Sage:** `devices`/`runs` — I'll fold `runs` in (it directly feeds `status` and the `summary.txt` "Runs:" line, closing a Session-3 gap). `devices` schema too since it's trivial, but no dedicated UI.

**Ivy:** Don't forget the small pre-flight risks — R13 (PC sleep) and R14 (battery). Those are cheap warnings.

**Sage:** R14 (on-battery warning) is a few lines in pre-flight. R13 (PC sleep) — we can't prevent it portably without P/Invoke `SetThreadExecutionState`; I'd add a one-line *documented warning* in pre-flight ("disable sleep for long runs") and consider the API call in Sprint 3. R9 (antivirus) is already handled (catch IO → mark failed → continue); QA just verifies it.

**Remy:** That's our scope. Let me lock it.

---

## 3. Converged Sprint 2 scope

### Pillars (must ship)
1. **Read-stall watchdog (#11 + R2)** — Sage. Per-chunk read timeout → cancel → `DeviceException` → mark in-flight non-`done` → stop the run cleanly, resumable, non-zero exit, great message. Injectable timeout seam for QA. **No mid-run auto-reconnect** (Sprint 3).
2. **Live dashboard + current speed (#10)** — Nova + Kira. Spectre.Console live view: overall %/bytes, current file bar, **current MB/s (rolling ~3s) + avg + ETA**, counts. Behind `IProgressReporter`; **graceful fallback** to Sprint 1 text on non-TTY / piped / `--no-dashboard`. Zero added device I/O; ≤4 Hz refresh; must not slow the copy.

### Supporting (should ship)
3. **`status` subcommand** — Nova. `status --dest <path>` reads `get-and-see.db` read-only, prints last-run + archive totals **with no device attached**. Reuses `SummaryWriter`.
4. **`runs` + `devices` journal tables** — Sage. Additive schema (bump `schema_version`); populate per run/device; feed `status` + the `summary.txt` "Runs:"/"Devices:" lines (closes a Session-3 gap).
5. **Pre-flight: on-battery warning (R14)** + documented PC-sleep note (R13) — Sage/Kira.

### Cuttable (release valve — slip to Sprint 3 without harm)
6. **Live-Photo pair detection** — Nova. Link `.HEIC`/`.MOV` by basename + close mtime → `live_photo_pair_id` + `summary.txt` "Live Photos: N pairs". Cut first if the dashboard is at risk.

### QA deliverables (not code)
7. **Re-run full acceptance once the watchdog lands** — Ivy. Uninterrupted 269 GB run closes Sprint-1 deferrals (AC-7 full archive, live `summary.txt`/AC-17). Watchdog real-hardware test (unplug; sleep if reproducible). Dashboard fallback + no-slowdown verification. Re-affirm read-only contract on any reconnect/new code.

### Docs (light, now — not the Sprint 3 docs sprint)
8. **Stub docs** — Quill. `--help` review for `status` + new flags; README note that `status` exists & dashboard is default; collect new error strings for the Sprint 3 troubleshooting guide.

### Explicitly OUT (Sprint 3+)
- Auto-reconnect-and-retry mid-run (true R2 soft recovery)
- `SetThreadExecutionState` keep-awake (R13 prevention)
- `--verify-hash` (R12), long-path `\\?\` (R6), release workflow, full user docs, HEIC convert, geocoding, GUI

### Risk coverage this sprint
R2 (watchdog/detect), R9 (verify existing handling), R13 (doc warning), R14 (battery warning). Carry-over confidence: R1/R3/R7/R8/R10/R11/R17/R21 shipped + QA-verified in S1.

---

## 4. Consilium vote

- **Sage** ✅ "Watchdog first, one mechanism for #11 and R2, auto-reconnect deferred. Clean."
- **Nova** ✅ "Two pillars, Live-Photo pairing as the release valve. Realistic."
- **Kira** ✅ "Dashboard with hard 'don't slow the copy' + graceful fallback. This is the quality jump."
- **Ivy** ✅ "Injectable timeout seam = I can actually test the watchdog. Full re-run closes the S1 deferrals."
- **Quill** ✅ "Light stub docs now so Sprint 3 isn't archaeology."
- **Remy** ✅ "Scope is cut and ordered. Pillars protected, valve identified. Ship it."

**Unanimous. → `docs/sprint-2/plan.md`.**
