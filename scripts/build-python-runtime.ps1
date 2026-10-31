# Builds the isolated CPython runtime embedded in PicHarbor.Core.
# Output: src/PicHarbor.Core/GooglePhotos/python_runtime.zip
# The archive is committed. Re-run this script only when bumping CPython or the pinned packages.
param(
    [string]$PythonVersion = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$destination = Join-Path $repoRoot "src\PicHarbor.Core\GooglePhotos\python_runtime.zip"
$versionFile = Join-Path $repoRoot "src\PicHarbor.Core\GooglePhotos\python_runtime.VERSION.txt"
$work = Join-Path $env:TEMP ("picharbor-python-runtime-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

function Test-Url([string]$Url) {
    try {
        $request = [System.Net.HttpWebRequest]::Create($Url)
        $request.Method = "HEAD"
        $request.Timeout = 20000
        $response = $request.GetResponse()
        $response.Close()
        return $true
    }
    catch {
        return $false
    }
}

function Get-Latest312 {
    $index = (Invoke-WebRequest -Uri "https://www.python.org/ftp/python/" -UseBasicParsing).Content
    $found = [regex]::Matches($index, "3\.12\.\d+") |
        ForEach-Object { $_.Value } |
        Sort-Object { [version]$_ } -Unique -Descending
    foreach ($version in $found) {
        $url = "https://www.python.org/ftp/python/$version/python-$version-embeddable-amd64.zip"
        if (Test-Url $url) { return $version }
    }
    throw "Could not find a CPython 3.12 embeddable win-amd64 zip on python.org."
}

try {
    if ([string]::IsNullOrWhiteSpace($PythonVersion)) {
        $PythonVersion = Get-Latest312
    }

    $embedUrl = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embeddable-amd64.zip"
    $embedZip = Join-Path $work "embed.zip"
    Write-Host "Downloading $embedUrl"
    Invoke-WebRequest -Uri $embedUrl -OutFile $embedZip -UseBasicParsing

    $runtimeRoot = Join-Path $work "runtime"
    [System.IO.Compression.ZipFile]::ExtractToDirectory($embedZip, $runtimeRoot)

    $wheelDir = Join-Path $work "wheels"
    New-Item -ItemType Directory -Path $wheelDir | Out-Null
    Write-Host "Downloading requests, rich, six, and their dependencies for CPython $PythonVersion win-amd64"
    & python -m pip download requests rich six `
        --dest $wheelDir `
        --python-version $PythonVersion `
        --platform win_amd64 `
        --only-binary=:all: `
        --implementation cp
    if ($LASTEXITCODE -ne 0) { throw "pip download failed with exit $LASTEXITCODE" }

    $sitePackages = Join-Path $runtimeRoot "Lib\site-packages"
    New-Item -ItemType Directory -Path $sitePackages -Force | Out-Null
    $packageLines = New-Object System.Collections.Generic.List[string]
    Get-ChildItem $wheelDir -Filter *.whl | Sort-Object Name | ForEach-Object {
        Write-Host "Extracting $($_.Name)"
        [System.IO.Compression.ZipFile]::ExtractToDirectory($_.FullName, $sitePackages)
        $packageLines.Add($_.BaseName)
    }

    $stdlibZip = Get-ChildItem $runtimeRoot -Filter "python3*.zip" | Select-Object -First 1
    if (-not $stdlibZip) { throw "Embeddable archive has no python3*.zip." }
    $pth = Get-ChildItem $runtimeRoot -Filter "python*._pth" | Select-Object -First 1
    if (-not $pth) { throw "Embeddable archive has no ._pth file." }

    # Isolated mode: do not enable site, so the user site-packages and PYTHONPATH cannot leak in.
    # ../ is the PicHarbor runtime directory that holds gpmc and blackboxprotobuf.
    $pthText = @(
        $stdlibZip.Name
        "."
        "../"
        "Lib/site-packages"
    ) -join "`r`n"
    [System.IO.File]::WriteAllText($pth.FullName, $pthText + "`r`n", [System.Text.UTF8Encoding]::new($false))

    $notice = @(
        "PicHarbor bundled Python runtime"
        "CPython $PythonVersion embeddable win-amd64"
        "Source: $embedUrl"
        ""
        "Third-party wheels:"
    ) + $packageLines + @(
        ""
        "CPython is licensed under the PSF License (LICENSE.txt in this archive)."
        "Each wheel keeps its license in *.dist-info."
        "This runtime is private to PicHarbor. It is not added to PATH."
    )
    [System.IO.File]::WriteAllText((Join-Path $runtimeRoot "BUNDLED_RUNTIME.txt"), ($notice -join "`r`n") + "`r`n", [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($versionFile, ($notice -join "`r`n") + "`r`n", [System.Text.UTF8Encoding]::new($false))

    if (Test-Path $destination) { Remove-Item $destination -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($runtimeRoot, $destination, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    $sizeMb = [math]::Round((Get-Item $destination).Length / 1MB, 1)
    Write-Host "Wrote $destination ($sizeMb MB)"
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
