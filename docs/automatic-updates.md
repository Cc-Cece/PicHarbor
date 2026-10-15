# GitHub Releases desktop updates

The desktop app reads stable releases from `Cc-Cece/PicHarbor`. It downloads only
`PicHarbor.Gui.exe` and its matching `.sha256` attachment. The CLI is not updated.
The first version containing this feature must be downloaded manually.

Development and non-single-file runs still fetch and display the latest version when
checking; only downloading and installing are restricted to published single-file EXEs.
Version discovery does not require the GUI/checksum attachments to be present. Missing
attachments prevent installation and leave the discovered version visible.

## User behaviour

- **Automatic (default):** check once after the main window appears, download in
  the background, and install on normal exit. No periodic polling or automatic restart.
- **Manual:** the button checks, downloads, verifies, waits for active work, installs
  and restarts. Waiting/downloading can be cancelled.
- **Browser:** no background checking or downloading. Open GitHub Releases, download
  the EXE, close the app and replace it. If Vault is installed, use **Repair app lock
  service** in the updated app's update card, or run the updated EXE with
  `--repair-vault-service`, to update its protected service copy.

On exit, the permission dialog appears **only** for a verified, scheduled update
that requires administrator permission. Ordinary writable-directory updates without
Vault are silent. Confirm requests UAC; cancelling either dialog exits without updating.
Both checkbox preferences are saved even on cancellation: skipping suppresses this
version, while disabling automatic checks switches to manual mode. Disabling automatic
checks alone does not prevent a confirmed installation of the currently prepared update.

## Installation and recovery

The original executable starts an ordinary-user relay. A separate worker is elevated
only when necessary. Process IDs, start time, pipe peers and the old executable's hash
bind the request to the running application. The worker re-fetches the release checksum;
offline installation is therefore deferred safely, retaining the cached download.
No GitHub token, mirror, signing key or additional update server is required.

The existing network proxy address setting also applies to GitHub metadata, checksum
and EXE downloads, including asset redirects. Each check reads the latest setting;
the installation relay passes a fresh snapshot to its worker over the authenticated
pipe, including when UAC uses another account. Port-only/host:port shorthand and
HTTP, HTTPS and SOCKS proxy URLs are supported. Blank keeps the system networking
defaults. An invalid or unavailable explicit proxy fails the operation rather than
silently bypassing it. Browser download mode uses the browser's own proxy settings.

The worker validates length, SHA-256 and product version before probing or installing
the new executable. Elevated staging copies only the EXE into protected storage, not
neighbouring DLLs. A same-volume verified copy replaces the original while retaining
its filename and backup. An installed Vault service gets a separate protected image;
only its image path is changed, preserving service settings and archive protection.
Service validation uses the new binary's build identity. Failures restore the original
service image/running state and executable; rollback errors are reported explicitly.

Cache and diagnostics are under `%LocalAppData%\PicHarbor\Updates`. Each installation
job records `transaction.json`, `result.json` or `error.log`; general failures go to
`updates.log`. Recovery backups beside the original EXE have the suffix
`.picharbor-old-<id>`. Successful startup removes the matching backup when permitted.
Backups in administrator-only folders may remain because ordinary startup does not
request UAC solely for cleanup. Interrupted jobs and failure diagnostics are retained.
An externally killed installer or power loss may require recovery from the recorded
backup/service image; the installer does not silently roll back user data or settings.

HTTPS plus the release checksum verifies integrity against the trusted GitHub repository;
it does not authenticate the publisher independently of a compromised repository.

## Release contract and validation

The workflow probes the published EXE before creating a draft release, uploads all
attachments, then publishes. Prerelease tags are not marked as the latest stable release.
Keep the GUI asset/checksum names and the `--update-probe` contract compatible in future versions.
`--update-probe --details` reports deployment detection and permission requirements without
starting WPF or modifying Vault; its write-permission probe creates and removes a temporary file.

Automated coverage includes numeric versions, prerelease rejection, exact attachment
selection, checksums, download interruption/size limits, all exit-dialog conditions,
all consent/checkbox combinations, verification-before-replacement, occupied files and rollback.
`--render-update-preview <directory>` renders the actual settings and permission dialog
in all three languages without attaching the updater or entering the Vault startup flow.

Local verification used two real single-file builds (2.5.3 and a local MinVer override
2.5.4) in an isolated directory: verified replacement of a renamed EXE, launching its
probe, and restoring the old binary all passed. This does not publish either build.

Before shipping, complete Windows VM acceptance for real GitHub-driven upgrade/relaunch,
UAC acceptance/refusal, an installed Vault service (including stopped/disabled states),
protected directories and injected service/rollback failures. Do not run these scenarios
against a daily-use Vault service. No live service was modified by local validation.
