# Brainstorm — A great Terminal UI (post-v1.0)

> Multi-agent brainstorm convened by Remy (Producer) on the CEO's prompt:
> *"I need a great Terminal UI to make the app easier — simplify settings/options discovery, highlight
> copy progress (items count, current speed, total speed, ETA — you name it). Anything else a user can
> expect of a great TUI?"*
> **Status: ideation only — nothing is committed.** Voices debate, then converge on a roadmap. Decisions
> are the CEO's.

## The starting point (v1.0 behavior)

`get-and-see` is a `System.CommandLine` app with a **Spectre.Console live dashboard** that already shows
overall %, current file, current/avg MB/s, ETA, and copied/skipped/failed counts, with a `--no-dashboard`
text fallback (auto when output is redirected). Options today are flags: `--dest`, `--dry-run`,
`--verify-hash`, `--read-timeout`, `--no-dashboard`. Discovery is `--help` only.

Two distinct asks: **(A) discovery** — find and set options without memorizing flags; **(B) progress** — a
richer, more reassuring live view for a multi-hour run. Plus "what else?".

---

## Phase 1 — Free ideation

### Kira (Product / UX) — "A no-flags-needed guided run."
> The median user shouldn't need to know any flag exists. Run `get-and-see` with no args → an **interactive
> wizard**: detects the iPhone, shows what it found ("iPhone 12 Pro, 27,478 items, 269 GB"), asks **where to
> save** (with a sensible default + free-space check), offers a couple of plain-language toggles ("Verify
> every file with a checksum? slower" / "Just show me what would copy"), then a **confirmation screen**
> before it starts. Expert flags still work for scripts; the wizard is the friendly front door. Also: a
> crisp **end-of-run summary screen** ("21,402 copied · 6,072 already had · 0 failed · 2h41m — open folder?").

### Nova (CLI / Spectre implementation) — "Three layers, one toolkit."
> Spectre.Console gives us all of it: `SelectionPrompt`/`TextPrompt`/`ConfirmationPrompt` for the wizard,
> `Live`/`Progress` for the dashboard, `Table`/`Panel`/`Rule`/`FigletText` for layout. Structure it as
> three modes: **(1) interactive** (no args → wizard), **(2) direct** (flags → today's behavior), **(3)
> non-interactive** (redirected/CI → plain text). The progress view becomes a multi-panel layout: an overall
> bar, a current-file line, a live **metrics panel**, a **recent-files** rolling log, and an **errors** panel.
> The wizard just *builds the same options object* the flags do — no logic forks.

### Milo (Visual design / identity) — "Calm, legible, branded — not a Christmas tree."
> A great TUI feels *calm* over a long run. A title `Rule` with the app name + device, consistent color
> semantics (green=done, yellow=skipped/warn, red=failed, dim=idle), aligned columns, and **one** restrained
> accent color. A tiny **throughput sparkline** (last ~60s of MB/s) reads instantly and makes a slow link
> obvious. Respect `NO_COLOR` and narrow terminals — degrade gracefully, never wrap into garbage. The
> end-screen is our "ta-da" moment: a clean summary panel, maybe a small Figlet "Done".

### Sage (Backend / metrics) — "Feed it without touching the hot path."
> Everything the CEO listed I can emit from the existing `onBytesStreamed` heartbeat + journal counters with
> **zero** added device I/O: items done/total, bytes done/total, **current-file speed**, **session avg
> speed**, a **rolling-window speed** (smoother ETA than cumulative avg), ETA, elapsed, skipped, failed.
> Rule: the UI samples a snapshot on a timer (≤4 Hz, already capped) — the copy loop only does cheap
> `Interlocked` updates, never formatting. ETA should come from the **rolling** window, not lifetime
> average, so it reacts when the link slows. Don't let rendering ever block a read.

### Ivy (QA) — "Prove it degrades; never let pixels block bytes."
> A "great TUI" must not become a liability: it has to **fall back cleanly** when output is redirected, in
> CI, in a dumb terminal, under `NO_COLOR`, and on a narrow/resized window. The wizard must **never** hang a
> non-interactive invocation waiting for input (detect no-TTY → require flags → clear error). And the live
> render must not pin CPU or interfere with the disconnect watchdog/exit-3 path I just signed off. I'll want
> snapshot tests of the render model + a redirected-output test + a "wizard refuses to block in CI" test.

### Remy (Producer) — "Split the dream from the v1.x."
> Lots here is genuinely great; some is polish. Let me separate **must-haves** (wizard for discovery, the
> enriched metrics the CEO named, graceful fallback) from **nice-to-haves** (sparkline, recent-files log,
> notifications, themes) so we can ship value without gold-plating.

---

## Phase 2 — Discussion

**Kira → Nova:** The wizard's confirmation screen should double as the **plan** view — reuse `--dry-run`'s
output (counts + size + free-space) so "review then go" is one screen.

**Sage:** And the plan screen costs nothing — enumeration already runs before copy. Show "27,478 items,
269 GB, fits in 1.4 TB free" then a single confirm.

**Milo → Ivy:** On fallback — text mode should print the *same numbers* periodically (e.g., a line every N
files / N seconds), so a redirected log is still useful, just not animated. Agreed it must never emit
escape-code soup into a file.

**Ivy:** Critical line I'll hold: **interactive prompts only when `stdin`/`stdout` are a real TTY.** No TTY
→ no wizard, and a missing required option is a clean exit 2, not a hang. That keeps scripts and CI safe.

**Nova:** The recent-files log and errors panel are cheap and very reassuring on a long run ("is it stuck?"
— no, you see files scrolling). I'd put recent-files in must-have-ish, errors panel definitely in.

**Milo:** Notifications (a beep / toast on completion or on disconnect) are lovely for a multi-hour run you
walk away from — but OS-specific. Nice-to-have.

**Kira:** One more must-have: a **device + destination header** always visible (which phone, where it's
saving, free space) — orientation. And surface the **disconnect/resume** reality in the UI: if it exits 3,
the end screen should say plainly "Disconnected — re-run the same command to resume," not look like a crash.

**Sage:** ETA honesty: show rolling-window ETA and label it ("~", it's an estimate). Overpromising a precise
time on a variable USB link erodes trust.

**Remy:** Good. Locking the split.

---

## Phase 3 — Converged roadmap

### Must-have (the core "great TUI")
1. **Interactive wizard** (no args, TTY only): detect device → show device summary → prompt destination
   (default + free-space check) → plain-language toggles (verify-hash, dry-run) → **plan/confirm screen**
   (reuses dry-run counts) → run. Builds the *same* options object as the flags.
2. **Enriched live dashboard** — a clean multi-panel layout:
   - **Header:** device + destination + free space.
   - **Overall:** % bar, bytes done/total, items done/total.
   - **Current:** filename + this-file speed.
   - **Metrics:** current MB/s, **rolling-window MB/s**, session avg, **ETA (rolling, labeled "~")**, elapsed.
   - **Counts:** copied · skipped · failed.
   - **Errors panel:** last few failures (if any).
3. **End-of-run summary screen** — copied/skipped/failed, bytes, duration, and the right next step
   (success → "open folder?"; exit 3 → "Disconnected — re-run to resume"; some failed → "re-run to retry").
4. **Graceful degradation (Ivy's gate):** real-TTY detection; redirected/CI/`NO_COLOR`/narrow-terminal
   fallbacks print the same numbers as periodic text; the wizard never blocks a non-interactive run; render
   never blocks the copy loop or the disconnect/exit-3 path.

### Nice-to-have (polish / later)
- Throughput **sparkline** (last ~60s).
- **Recent-files** rolling log panel.
- **Completion/disconnect notification** (beep/toast).
- **Themes** / accent color; `--theme` or auto from terminal.
- A TUI for **`status`** (and, if built, **`search`**) — browse an existing archive interactively.
- Live **disk-space gauge** for the destination during the run.
- Pause/resume keybinding (only if it can't risk data safety — likely just "Ctrl+C is already safe").

### Hard guardrails (unchanged contracts)
Device stays **read-only**; UI adds **zero device I/O** and no per-chunk cost (snapshot on the existing
≤4 Hz timer via `Interlocked` counters); the disconnect watchdog / clean **exit 3** behavior (just shipped,
QA-signed) is untouched; everything degrades to plain text off-TTY.

### Open questions for the CEO
- **Default UX:** should bare `get-and-see` launch the **wizard**, or keep requiring `copy --dest` and add
  the wizard under an explicit verb (e.g. `get-and-see` → menu vs `get-and-see wizard`)?
- Which **nice-to-haves** matter to you (sparkline? recent-files log? completion sound? themes?)?
- Is an interactive **`status`/archive browser** interesting, or focus the TUI purely on the copy run?

### Proposed sprint (if greenlit)
**Sprint 5 — "TUI":** the interactive wizard (TTY-gated) + enriched dashboard (rolling-window speed/ETA,
metrics/counts/errors panels, header) + end-of-run summary + the degradation matrix, with snapshot tests of
the render model, a redirected-output test, and a "no-TTY never blocks" test. Sparkline / recent-files /
notifications / themes pulled in only as time allows. README + screenshots updated. (Pairs naturally with
`search` from `folder-organization.md` — a shared interactive surface.)
