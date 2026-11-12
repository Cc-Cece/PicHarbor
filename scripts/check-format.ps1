param([string]$BaseSha = '')
$ErrorActionPreference = 'Stop'

# PRs gate the C# they change; complete solution builds/tests still run separately.
# This lets a Python-only PR avoid inheriting unrelated formatting debt in its base.
if ($BaseSha) {
    if ($BaseSha -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid format comparison base.' }
    $changed = @(git diff --name-only --diff-filter=ACMR "$BaseSha...HEAD")
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine PR formatting scope.' }
    $styleChanged = @($changed | Where-Object { $_ -match '(^|/)\.editorconfig$' }).Count -gt 0
    if (-not $styleChanged) {
        $sources = @($changed | Where-Object { $_ -match '\.cs$' })
        if ($sources.Count -eq 0) {
            Write-Output 'No C# changes in this PR; full build and tests remain required.'
            exit 0
        }
        & dotnet format --verify-no-changes --no-restore --include @sources
        exit $LASTEXITCODE
    }
}
& dotnet format --verify-no-changes --no-restore
exit $LASTEXITCODE
