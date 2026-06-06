# Sprint 1 — Library Investigation (BLOCKS Phase 1)

**Status:** OPEN — investigation in progress, owner: Sage  
**Filed:** 2026-06-06 by Remy after smoke-test discovery  
**Blocks:** Sprint 1 Phases 1-5 (entire .NET solution work)

## What we discovered

During the pre-Phase-1 smoke test (per producer's risk-reduction recommendation), Sage attempted:

```
dotnet new console --framework net10.0
dotnet add package NetiMobileDevice
→ error: There are no versions available for the package 'NetiMobileDevice'.
```

**`NetiMobileDevice` does not exist on NuGet.** This package name was incorrectly assumed in brainstorm Session 2 and propagated through PROJECT_BRIEF, docs/brainstorm/session-2.md, docs/brainstorm/session-3.md, and docs/sprint-1/plan.md. Producer error — caught early thanks to the smoke test, before any code was written.

## What actually exists (verified 2026-06-06 against nuget.org)

| Package | Last release | Target | Status |
|---------|-------------|--------|--------|
| `imobiledevice-net` (quamotion) | 2021-02-22 | .NET Standard 2.0 / .NET 5 | 336k downloads; 5 years stale; ships native DLLs; unverified on iOS 18+ / Windows 11 24H2+ |
| `iproxy` (quamotion) | 2021-12-02 | .NET 5 | Wrapper utility, not a general AFC client |
| `Kaponata.iOS` (quamotion) | 2021-08-02 | .NET 5 | Same author's newer attempt at a higher-level API, also stale |
| No other "pure-C# AFC" library exists on NuGet under any obvious name | — | — | — |

## Options to evaluate

### Option A — Use `imobiledevice-net` despite staleness
- **Pros:** Established (336k downloads), wraps battle-tested native `libimobiledevice` C library, easiest path
- **Cons:** 5 years without updates, .NET Standard 2.0 (works on .NET 10 but no modern features), ships native DLLs (defeats single-file publish goal somewhat), unverified on iOS 18+

### Option B — P/Invoke `libimobiledevice` ourselves
- **Pros:** `libimobiledevice` (the C library) IS actively maintained; full control; can target .NET 10 properly
- **Cons:** Significant marshaling work; have to bundle and load native DLLs ourselves; weeks of investment before getting first DCIM listing

### Option C — Hybrid: Python (`pymobiledevice3`) for device layer, C# CLI wraps it
- **Pros:** `pymobiledevice3` is active and well-maintained; we get the protocol for free
- **Cons:** Hybrid stack defeats "single-binary distribution" goal; user needs Python installed (or we embed an interpreter); deployment complexity explodes

### Option D — Pivot the entire project back to Python
- **Pros:** Brainstorm Session 1's stack already worked through this; `pymobiledevice3` is the proven choice
- **Cons:** CEO chose C# for a reason; reverses a decision; abandons all stack work in Session 2/3
- **Note:** Should only be considered if A, B, and C all fail validation

### Option E — Something we haven't found yet
- A modern .NET library exists in a private feed, a less-obvious search term, or as a recently-revived fork
- Worth one focused search before committing to A-D

## Investigation tasks (Sage)

1. **Verify Option A works at all on current hardware.** In `e:\scratch\netimobile-smoke`:
   ```pwsh
   dotnet add package imobiledevice-net
   ```
   Then write ~30 lines: enumerate devices, open AFC service, list `/DCIM/`, print first 10 entries with size + mtime. With iPhone 12 Pro plugged in (iOS version: whatever the CEO is running), unlocked, trusted.
   - **Success:** lists DCIM cleanly → Option A is viable
   - **Compiles but fails at runtime:** capture the exact error → may indicate need for newer native bindings → Option A degrades to Option B candidate
   - **Won't compile on .NET 10:** Option A is out

2. **Search GitHub + NuGet one more time for Option E.** Search terms: "lockdownd C#", "usbmuxd C#", "iphone usb C# 2024", "iphone usb C# 2025". Also: search for active forks of `imobiledevice-net` (the GitHub network graph view).

3. **If Option A fails, time-box Option B exploration.** Spend ~2 hours assessing: can we link the official `libimobiledevice` DLLs via P/Invoke from .NET 10? Is there a community example?

4. **Document outcome.** Write `docs/sprint-1/afc-library-decision.md` with: which option chose, why, what was tried, what failed, link to smoke test output, any caveats (e.g. "requires iTunes installed for native DLL X").

## Decision authority

- **Sage** decides Options A vs B based on smoke-test evidence and reports out
- **Anything else (C, D)** requires a producer + CEO conversation before committing — these change the project's distribution model

## Updates needed after decision

Producer (Remy) will, after Sage reports:
- Update PROJECT_BRIEF.md Section 3 (Tech Stack) with the actual library + version
- Update PROJECT_BRIEF.md Section 4 (Architecture) if Option C is chosen
- Strike `NetiMobileDevice` references from Sections 3, 4, 5 and from brainstorm sessions 2 & 3 (with a "corrected" note, preserving history)
- Update `docs/sprint-1/plan.md` Task 2 to name the chosen library
- File this finding as a lessons-learned note for future brainstorms: **always smoke-test claimed dependencies during the brainstorm, not after the brief is signed off**
