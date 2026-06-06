#requires -Version 7
<#
.SYNOPSIS
    Sync GitHub repo labels from .github/labels.yml using the gh CLI.

.DESCRIPTION
    Idempotent: re-running is safe. Each label is created if missing, or
    updated in-place to match the YAML if it already exists (via `gh label
    create --force`). Run AFTER the Phase 0 bootstrap PR is merged to main.

    Requires:
      - GitHub CLI (`gh`) installed and on PATH (https://cli.github.com/)
      - `gh auth login` already run
      - Working directory inside a clone of the repo, OR pass -Repo owner/name

.PARAMETER LabelsFile
    Path to labels.yml. Defaults to the canonical location at
    <repo-root>/.github/labels.yml relative to this script.

.PARAMETER Repo
    Optional. Pass "owner/repo" to operate on a specific repo. When omitted,
    `gh` infers the repo from the current working directory's git remote.

.EXAMPLE
    pwsh ./docs/sprint-1/setup-labels.ps1

.EXAMPLE
    pwsh ./docs/sprint-1/setup-labels.ps1 -Repo denis-a-evdokimov/get-and-see
#>

[CmdletBinding()]
param(
    [string]$LabelsFile = (Join-Path $PSScriptRoot '..' '..' '.github' 'labels.yml'),
    [string]$Repo
)

$ErrorActionPreference = 'Stop'

# ── Preflight ────────────────────────────────────────────────────────────────
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "gh CLI not found. Install from https://cli.github.com/ and run 'gh auth login' first."
}

if (-not (Test-Path $LabelsFile)) {
    throw "Labels file not found: $LabelsFile"
}
$LabelsFile = (Resolve-Path $LabelsFile).Path

# ── Minimal YAML parser for our flat list-of-objects format ──────────────────
# Format expected (one entry):
#   - name: foo
#     color: aabbcc
#     description: Foo description
#
# Comment lines starting with # are ignored. Blank lines are ignored.
function Read-LabelsYaml {
    param([Parameter(Mandatory)][string]$Path)

    $labels = New-Object System.Collections.Generic.List[object]
    $current = $null

    foreach ($rawLine in Get-Content -LiteralPath $Path) {
        # Strip whole-line comments only — preserve # inside descriptions.
        if ($rawLine -match '^\s*#') { continue }
        if ([string]::IsNullOrWhiteSpace($rawLine)) { continue }

        if ($rawLine -match '^\s*-\s*name:\s*(.+?)\s*$') {
            if ($null -ne $current) { $labels.Add([pscustomobject]$current) }
            $current = @{ name = $Matches[1].Trim('"',"'"); color = ''; description = '' }
            continue
        }
        if ($rawLine -match '^\s+color:\s*(.+?)\s*$') {
            $current.color = $Matches[1].Trim('"',"'")
            continue
        }
        if ($rawLine -match '^\s+description:\s*(.+?)\s*$') {
            $current.description = $Matches[1].Trim('"',"'")
            continue
        }
    }
    if ($null -ne $current) { $labels.Add([pscustomobject]$current) }

    return $labels
}

# ── Main ─────────────────────────────────────────────────────────────────────
$labels = Read-LabelsYaml -Path $LabelsFile
if ($labels.Count -eq 0) {
    throw "No labels parsed from $LabelsFile — check the file format."
}
Write-Host "Loaded $($labels.Count) labels from $LabelsFile"

$repoArgs = @()
if ($Repo) {
    $repoArgs = @('--repo', $Repo)
    Write-Host "Targeting repo: $Repo"
} else {
    Write-Host "Targeting repo: (inferred from current git remote)"
}

$failed = 0
foreach ($label in $labels) {
    if (-not $label.name -or -not $label.color) {
        Write-Warning "Skipping malformed entry: $($label | ConvertTo-Json -Compress)"
        $failed++
        continue
    }
    Write-Host ("  -> {0,-32}  #{1}" -f $label.name, $label.color)
    & gh label create $label.name `
        --color       $label.color `
        --description $label.description `
        --force `
        @repoArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "gh label create failed for '$($label.name)' (exit $LASTEXITCODE)"
        $failed++
    }
}

if ($failed -gt 0) {
    throw "$failed label(s) failed to sync. See warnings above."
}

Write-Host ""
Write-Host "Done. $($labels.Count) labels synced successfully." -ForegroundColor Green
