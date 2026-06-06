# Sprint 1 — AFC Library Decision

**Status:** ✅ DECIDED — pending producer review
**Owner:** Sage (systems engineer)
**Date:** 2026-06-06
**Resolves:** [`docs/sprint-1/library-investigation.md`](library-investigation.md)
**Unblocks:** Sprint 1 Phases 1–5 (the .NET solution work)

---

## 1. Decision

**Option A — use `imobiledevice-net`.**

It is the only real candidate, and a live end-to-end smoke test proved it works on our
exact target stack (**.NET 10 on Windows 11, against an iPhone 12 Pro running iOS 26.5**).
The 5-year-old worry from the investigation doc ("unverified on iOS 18+") is now retired:
it reads `/DCIM/` cleanly on **iOS 26.5**.

| | |
|---|---|
| **Package** | `iMobileDevice-net` |
| **Exact version pin** | `1.3.17` |
| **Native runtime needed** | win-x64 (bundled in the package) |
| **Apple driver/service** | Apple Devices app (Store) **or** iTunes — see Prerequisites |

PackageReference (copy into `GetAndSee.Core.csproj` in Phase 1):

```xml
<PackageReference Include="iMobileDevice-net" Version="1.3.17" />
```

Options **B** (P/Invoke `libimobiledevice` ourselves), **C** (Python hybrid) and **D**
(pivot to Python) are **not** pursued. Option A clears the bar, so the multi-day /
distribution-model-changing options are unnecessary. See §6 for why B specifically would
not have helped.

---

## 2. Smoke test — what was run

Scratch project (untouched solution, per the task): `e:\scratch\netimobile-smoke\NetiMobileSmoke`

- `dotnet add package imobiledevice-net` → installed **iMobileDevice-net 1.3.17** cleanly.
- `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` added (see §5, caveat 2).
- ~55 lines in `Program.cs`: load native libs → enumerate devices → `idevice_new` →
  `lockdownd_client_new_with_handshake` → `lockdownd_start_service("com.apple.afc")` →
  `afc_client_new` → `afc_read_directory("/DCIM/")` → for the first 10 entries,
  `afc_get_file_info` and print size + mtime.
- **Read-only by construction:** the test calls only `idevice_*`,
  `lockdownd_client_new_with_handshake`, `lockdownd_start_service`, `afc_client_new`,
  `afc_read_directory`, `afc_get_file_info`. No write/delete/rename methods referenced.

SDK: `dotnet --version` → **10.0.204**. Build: **0 warnings, 0 errors**.

## 3. Smoke test — output proving DCIM listing works

```
[init] native libimobiledevice located: True
[1] idevice_get_device_list -> Success, count=1
    udid[0] = 00008101-XXXXXXXXXXXXXXXX
[ok] idevice_new -> Success
[ok] lockdownd handshake -> Success
[ok] start com.apple.afc -> Success
[ok] afc_client_new -> Success
[3] afc_read_directory(/DCIM/) -> Success, 42 raw entries
    109APPLE                        25888 bytes  mtime=2026-05-14 00:04:51Z
    133APPLE                        30304 bytes  mtime=2026-05-14 00:04:51Z
    125APPLE                        31040 bytes  mtime=2026-05-14 00:04:51Z
    105APPLE                           64 bytes  mtime=2020-07-31 21:32:13Z
    113APPLE                        25568 bytes  mtime=2026-05-14 00:04:51Z
    129APPLE                        29664 bytes  mtime=2026-05-14 00:04:51Z
    128APPLE                        27584 bytes  mtime=2026-05-14 00:04:51Z
    112APPLE                        31072 bytes  mtime=2026-05-14 00:04:51Z
    104APPLE                           64 bytes  mtime=2020-07-31 21:42:08Z
    124APPLE                        30688 bytes  mtime=2026-05-14 00:04:51Z
[done] listed 10 entries from /DCIM/.
```

Device confirmed via the bundled `ideviceinfo.exe`:

```
ProductType    : iPhone13,3   (iPhone 12 Pro)
ProductVersion : 26.5         (iOS 26.5)
DeviceName     : iPhone
```

The `/DCIM/` root holds the standard `NNNAPPLE` bucket directories (109APPLE, 133APPLE, …).
`st_size`/`st_mtime` parse correctly (AFC reports `st_mtime` in **nanoseconds** since epoch —
divide by 1e6 for ms). The mix of 2026 and 2020 mtimes on different buckets is expected.

## 4. Prerequisites

| Prerequisite | Detail |
|---|---|
| **.NET 10 SDK** | Verified on `10.0.204`. The library's lib targets are `netstandard2.0` / `netcoreapp3.0` / `net45`; .NET 10 binds the `netcoreapp3.0` assembly with no issues. |
| **Apple Mobile Device driver + usbmuxd service** | **Required.** `libimobiledevice` talks to a usbmuxd endpoint on `127.0.0.1:27015`. Provided by **either** the Microsoft Store **"Apple Devices"** app (winget id `9NP83LWLPZ9K`) **or** iTunes for Windows. **No classic "Apple Mobile Device Service" is needed if using the Store app** — but see the important caveat in §5.3. |
| **iPhone unlocked + trusted** | The "Trust This Computer" pairing must exist. On the test device it was already trusted, so `lockdownd_client_new_with_handshake` succeeded immediately. A first-time device would need the user to unlock and tap **Trust**, otherwise the handshake returns a pairing error. |
| **Architecture** | win-x64 native binaries ship in the package (also win-x86, osx-x64, ubuntu — irrelevant for us). We target win-x64. |
| **iOS version** | No floor found. Works on **iOS 26.5**. The 2021 native build is forward-compatible with current iOS for the AFC/lockdown read path we use. |

## 5. Caveats (read before Phase 1)

### 5.1 The library is stale, but functional
Last release **Feb 2021** (`1.3.17`, 336k downloads). No updates in 5 years. Despite that,
every call in our read path returned `Success` against current hardware + current iOS. We
only need a **small, stable** slice of the API (enumerate, lockdown handshake, start AFC,
read dir, file info, file read) — exactly the part that does not change across iOS releases.

### 5.2 Native DLL load needs a RID (the .NET-Standard-2.0 → .NET-10 friction)
The package ships native DLLs under `runtimes/win-x64/native/` (`imobiledevice.dll`,
`plist.dll`, `usbmuxd.dll`, `libusb-1.0.dll`, `libcrypto-1_1-x64.dll`, `libssl-1_1-x64.dll`,
`vcruntime140.dll`, …). Its DLL-copy MSBuild targets live under `build/net45/` and **only
apply when targeting net45**. On a framework-dependent `net10.0` build with **no RID**, the
natives may not be placed next to the app and you get `DllNotFoundException`.

**Fix (proven):** set a RID so the native assets are copied to the output folder:

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

With the RID, `imobiledevice.dll` + all dependents landed in
`bin/Debug/net10.0/win-x64/` and `NativeLibraries.Load()` reported `LibraryFound = True`.
This is **not a burden** — our distribution goal is a single-file win-x64 publish, which is
RID-specific anyway. Phase 1 should bake `win-x64` into the CLI project (and the
single-file publish profile) from the start.

### 5.3 usbmuxd endpoint: the Store app's service is lazy
`libimobiledevice` connects to **`127.0.0.1:27015`**. Behaviour differs by provider:

- **iTunes** installs the classic *Apple Mobile Device Service*, which auto-starts and
  listens on 27015 at boot. Most robust.
- **"Apple Devices" Store app** uses a **packaged background service** that is **not a
  classic auto-start Windows service**. In testing, port 27015 was **closed** until the
  Apple Devices app was launched at least once; after launching it, 27015 opened and the
  full AFC path worked. If the service is not up, `idevice_get_device_list` returns
  `NoDevice` with `count=0` — indistinguishable from "no phone plugged in".

**Phase 1 action:** the pre-flight check (`PreflightChecks`) should probe `127.0.0.1:27015`
and, if unreachable, emit a clear, actionable error — e.g. *"iPhone driver service not
running. Install the 'Apple Devices' app or iTunes, then open Apple Devices once."* This
turns the single most likely support ticket into a self-serve fix. (Hand this copy to
Kira/Quill for tone.)

### 5.4 Old native crypto/usb stack
The win-x64 natives are built on **OpenSSL 1.1** (EOL) and 2021-era **libusb**. They are
local-USB-only, never touch the network, and run read-only — so the security exposure is
minimal — but this should be logged in [`docs/risk-register.md`](../risk-register.md) as
"aging bundled native dependencies; revisit if a maintained fork appears."

### 5.5 The library DOES expose write/delete methods — the read-only guard is mandatory
`imobiledevice-net` is a full binding; it exposes device-mutating calls. Our architectural
read-only rule (Brief §9.1) is enforced by `ReadOnlyContractTests`. That reflection guard
must fail the build if any of these symbols are referenced anywhere in our assemblies:

```
afc_file_write              afc_truncate                afc_make_directory
afc_file_truncate           afc_remove_path             afc_make_link
afc_remove_path_and_contents  afc_rename_path           afc_set_file_time
afc_file_open (write/append modes: AfcFileMode.FopWr/FopRw/FopWrong/FopAppend/FopRdAppend)
```

Plus the lockdown/state mutators we must never call: `lockdownd_set_value`,
`lockdownd_remove_value`, `lockdownd_pair`, `lockdownd_unpair`, `lockdownd_activate`,
`lockdownd_deactivate`. (Handed to Ivy for the safety test.) Our wrapper `IPhoneClient`
should bind **only** the six read calls listed in §2.

## 6. Why not Option B (P/Invoke ourselves)

The investigation framed B as the fallback if A failed at runtime. A did **not** fail.
Just as important: B would not have dodged the one real friction point. The hard part on
Windows is not the managed bindings — it is the **usbmuxd/driver layer** (§5.3), which is
the *same* Apple service regardless of whether we call it through `imobiledevice-net` or
through our own P/Invoke. B would mean re-marshalling the entire libimobiledevice surface
for **zero** capability we don't already have. Revisit only if `imobiledevice-net` ever
blocks us on a *specific* missing/broken call — none found.

## 7. Option E (one more search) — closed

Re-queried the NuGet search API on 2026-06-06:

- `q=imobiledevice` → **3** packages, all Quamotion: `imobiledevice-net` (1.3.17, 336,979
  downloads — chosen), `iproxy` (1.3.24, a port-forward utility, not an AFC client),
  `Kaponata.iOS` (0.3.410, 3,751 downloads — same author's higher-level API, also stale).
- `q=usbmuxd lockdownd` → **0** packages.

No modern, maintained pure-.NET AFC library exists. `imobiledevice-net` is both the most
complete and the most used of the three. Option E yields nothing better.

## 8. Hand-off to producer (Remy)

With this merged, please:

1. **Brief §3 (Tech Stack):** replace the `⚠️ AFC iPhone client / TBD` row with
   `imobiledevice-net | 1.3.17 | Talk to iPhone over USB (AFC) | Only validated C# AFC
   client; smoke-tested on .NET 10 + iOS 26.5. Stale (2021) but functional; requires RID
   win-x64 + Apple driver service.` Remove the open-question warning banner.
2. **Brief §4 (Architecture):** the AFC box label `NetiMobileDevice (pure C# AFC client)`
   is wrong twice over — it's `imobiledevice-net`, and it is a **binding over native
   libimobiledevice**, not pure C#. Update the diagram note.
3. **Brief §5 / §6:** s/NetiMobileDevice/imobiledevice-net/ where it names the library.
4. **`docs/sprint-1/plan.md` Task 2:** name `imobiledevice-net 1.3.17` and add the
   `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` requirement to the scaffolding step.
5. **Risk register:** add §5.4 (aging native deps) and §5.3 (lazy Store-app service) as
   tracked risks; both have concrete mitigations above.
6. **Ivy:** §5.5 gives the exact symbol blocklist for `ReadOnlyContractTests`.

**Phase 1 starts after this is merged.** No solution scaffolding, `GetAndSee.Core`, or
`GetAndSee.Cli` was created as part of this investigation — by design.
