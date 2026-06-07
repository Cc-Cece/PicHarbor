# Troubleshooting

Common failure modes, the **exact message** `get-and-see` prints, and how to fix each. Every error here
is recoverable — the tool never leaves your iPhone or a half-written file in a bad state, and re-running
the same `copy` command resumes from where it stopped.

> **Reminder:** `get-and-see` is **read-only** against the iPhone. None of these failures can modify or
> delete anything on the device.

---

## "No iPhone detected"

```
No iPhone detected. Connect the device with a USB cable, unlock it, and make sure the Apple
device driver service is running (see the pre-flight guidance).
```

**Exit code:** `2`

**Causes & fixes, in order:**

1. **Cable / port.** Use a known-good USB cable and a direct port (avoid hubs). Try another port.
2. **Device locked.** Unlock the iPhone and leave it on the Home screen.
3. **Apple driver service not running** — this is the **most common** cause. See the next section.

---

## "iPhone driver service not running" (the #1 cause of a phantom "no device")

```
iPhone driver service not running — open the Apple Devices app once, or install iTunes.
```

**Exit code:** `2`

The USB driver service (`usbmuxd`, on TCP port `27015`) is provided by either **iTunes for Windows** or
the Microsoft Store **"Apple Devices"** app. The Store app's background service is **lazy**: after a
reboot it stays off until you launch the app at least once — which looks identical to "no iPhone found".

**Fix:**

- If you have the **"Apple Devices"** app: **open it once** (you can close it again afterward), then
  re-run `get-and-see`.
- If you don't have either: install **iTunes for Windows** *or* the **"Apple Devices"** app from the
  Microsoft Store. Both install the Apple Mobile Device USB driver.

---

## "Could not pair with the iPhone" / Trust This Computer

```
Could not pair with the iPhone. Unlock it and tap "Trust This Computer", then try again
(lockdown error: ...).
```

**Exit code:** `2`

The PC has not been trusted by the device yet.

**Fix:**

1. Unlock the iPhone.
2. A **"Trust This Computer?"** dialog should appear on the phone — tap **Trust** and enter your passcode.
3. If no dialog appears: unplug and replug the cable while unlocked; if it still doesn't appear, reset
   the trust list on the phone (**Settings → General → Transfer or Reset iPhone → Reset → Reset Location
   & Privacy**), then replug and tap **Trust**.
4. Re-run the command.

---

## "Not enough free space"

```
Not enough free space on D:\ — need about 282.5 GB (estimated transfer plus 5% headroom),
but only 110.3 GB is free.
```

**Exit code:** `2`

A pre-flight check refuses to start unless the destination drive has the estimated transfer size **plus
5% headroom** free. Nothing is written when this fires.

**Fix:** free space, or choose a `--dest` on a larger drive. You can copy in stages onto different drives;
each destination keeps its own journal and resumes independently.

---

## "Destination is not writable"

```
Destination "D:\Photos" is not writable: <reason>.
```

**Exit code:** `2`

The tool created and then deleted a probe file to confirm it can write — and that failed.

**Fix:** pick a folder you own (e.g. under your user profile or a data drive), check the drive isn't
read-only/locked, and make sure it isn't a disconnected network share. Network destinations are
*supported but discouraged* — prefer a local disk for a multi-hundred-GB run.

---

## The run stops part-way: "Device stopped responding"

```
Device stopped responding (asleep or disconnected). Progress saved — reconnect and run the
same command to resume.
```

**Exit code:** `3`

This is the **read-stall watchdog** working as designed. If the device sends no bytes for the
`--read-timeout` window (default **30s**) — because the cable was bumped, the iPhone slept, or the USB
bus reset — the tool stops the run **cleanly** instead of hanging forever. Everything copied so far is
already saved; the in-flight file is left incomplete (never published) and will be retried.

**Fix:**

1. Reconnect / unlock the device (open the Apple Devices app again if needed).
2. Re-run the **same** `copy --dest …` command. It skips everything already done and continues.

**Prevent it on long runs:**

- Disable **PC sleep** for the duration (Windows **Settings → System → Power**).
- Keep the laptop on **AC power** — the tool warns if you start on battery.
- If your setup is prone to brief hiccups, raise the tolerance, e.g. `--read-timeout 60`.

---

## Some files report "failed"

**Exit code:** `1` (the run still completes)

Individual files can fail (e.g. a transient I/O error or antivirus quarantining the staging `.partial`).
They are marked `failed` in the journal and **never** published half-written.

**Fix:** re-run the same command — failed and pending files are retried, done files are skipped. If a
file fails every time, check your antivirus isn't quarantining files under the destination's
`.get-and-see-tmp` staging folder, and add an exclusion for the destination if needed.

---

## Long file paths

Deeply nested destinations combined with long original filenames can exceed Windows' legacy
260-character path limit. `get-and-see` handles this transparently (it uses the Windows extended-length
`\\?\` path form internally), so **no action is needed** — a deep `--dest` works.

If you later browse the archive with **other** tools that don't support long paths, prefer a **shorter
`--dest`** (e.g. `D:\Photos` rather than a deeply nested folder) to keep every file comfortably under 260
characters.

---

## Interrupted with Ctrl+C

**Exit code:** `130`

Ctrl+C is handled cleanly: the journal is flushed and AFC handles are closed. The in-flight file is left
as a staging `.partial` (never as a final file). Re-run the same command to resume.

---

## Verifying integrity after a copy

- Every file is **size-verified** against the AFC-reported size before it's published — this is always on.
- For bit-level assurance, run with **`--verify-hash`** to record a SHA-256 of each file in the manifest
  (`get-and-see.db`, `sha256` column). See the [manifest schema](manifest-schema.md) for how to read it.

---

## Still stuck?

Open an issue with: the exact message printed, the exit code, your Windows version, whether you use
iTunes or the Apple Devices app, and the iPhone model/iOS version (`status --dest …` and `--help` output
are safe to attach; they contain no photo data).
