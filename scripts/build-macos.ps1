param(
    [ValidateSet("all", "osx-arm64", "osx-x64")]
    [string]$Runtime = "all"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot "artifacts\macos"
$stagingRoot = Join-Path $projectRoot "artifacts\macos-staging"
$runtimes = if ($Runtime -eq "all") { @("osx-arm64", "osx-x64") } else { @($Runtime) }

function Get-Rcodesign {
    $version = "0.29.0"
    $toolRoot = Join-Path $projectRoot ".tools\apple-codesign-$version"
    $executable = Join-Path $toolRoot "apple-codesign-$version-x86_64-pc-windows-msvc\rcodesign.exe"
    if (Test-Path $executable) { return $executable }

    New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
    $archivePath = Join-Path $toolRoot "apple-codesign.zip"
    $downloadUrl = "https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F$version/apple-codesign-$version-x86_64-pc-windows-msvc.zip"
    $expectedHash = "54bb500e2da7a8de02fcae0f331d1cac6e6d7173b4281042ff9c528ba3159aaa"

    Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $archivePath
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "rcodesign download hash mismatch. Expected $expectedHash, got $actualHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $toolRoot -Force
    Remove-Item -LiteralPath $archivePath -Force
    if (-not (Test-Path $executable)) { throw "rcodesign.exe was not found after extraction." }
    return $executable
}

function Set-ZipUnixHost {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $endOfCentralDirectory = -1
    for ($offset = $bytes.Length - 22; $offset -ge [Math]::Max(0, $bytes.Length - 65557); $offset--) {
        if ($bytes[$offset] -eq 0x50 -and $bytes[$offset + 1] -eq 0x4b -and
            $bytes[$offset + 2] -eq 0x05 -and $bytes[$offset + 3] -eq 0x06) {
            $endOfCentralDirectory = $offset
            break
        }
    }
    if ($endOfCentralDirectory -lt 0) { throw "ZIP end-of-central-directory record was not found." }

    $entryCount = [System.BitConverter]::ToUInt16($bytes, $endOfCentralDirectory + 10)
    $offset = [System.BitConverter]::ToUInt32($bytes, $endOfCentralDirectory + 16)
    for ($entryIndex = 0; $entryIndex -lt $entryCount; $entryIndex++) {
        if ($bytes[$offset] -ne 0x50 -or $bytes[$offset + 1] -ne 0x4b -or
            $bytes[$offset + 2] -ne 0x01 -or $bytes[$offset + 3] -ne 0x02) {
            throw "Invalid ZIP central-directory entry at offset $offset."
        }

        $bytes[$offset + 5] = 3 # Unix host system; makes macOS honor ExternalAttributes.
        $nameLength = [System.BitConverter]::ToUInt16($bytes, $offset + 28)
        $extraLength = [System.BitConverter]::ToUInt16($bytes, $offset + 30)
        $commentLength = [System.BitConverter]::ToUInt16($bytes, $offset + 32)
        $offset += 46 + $nameLength + $extraLength + $commentLength
    }

    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function New-MacZip {
    param(
        [string]$AppPath,
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    if (Test-Path $DestinationPath) { Remove-Item -LiteralPath $DestinationPath -Force }
    $parent = Split-Path -Parent $AppPath
    $zipStream = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $zipStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            Get-ChildItem -LiteralPath $AppPath -Recurse -File | ForEach-Object {
                $relative = [System.IO.Path]::GetRelativePath($parent, $_.FullName).Replace('\', '/')
                $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
                $isExecutable = $_.FullName -like "*\Contents\MacOS\IsraeliAuthorStudio" -or
                    $_.Extension -eq ".dylib" -or
                    ($_.FullName -like "*\Contents\MacOS\*" -and [string]::IsNullOrEmpty($_.Extension))
                $unixMode = if ($isExecutable) { 33261 } else { 33188 } # 0100755 or 0100644
                $entry.ExternalAttributes = $unixMode -shl 16
                $input = $_.OpenRead()
                $output = $entry.Open()
                try { $input.CopyTo($output) }
                finally { $output.Dispose(); $input.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $zipStream.Dispose() }
    Set-ZipUnixHost -Path $DestinationPath
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$rcodesign = Get-Rcodesign

foreach ($rid in $runtimes) {
    $runtimeStage = Join-Path $stagingRoot $rid
    $publishPath = Join-Path $runtimeStage "publish"
    $appPath = Join-Path $runtimeStage "Israeli Author Studio.app"
    $macOsPath = Join-Path $appPath "Contents\MacOS"
    $contentsPath = Join-Path $appPath "Contents"

    if (Test-Path $runtimeStage) { Remove-Item -LiteralPath $runtimeStage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $macOsPath | Out-Null

    dotnet publish (Join-Path $projectRoot "IsraeliAuthorStudio.csproj") `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        --output $publishPath `
        -p:PublishSingleFile=false `
        -p:PublishReadyToRun=false `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    Copy-Item -Path (Join-Path $publishPath "*") -Destination $macOsPath -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot "Packaging\macos\Info.plist") -Destination (Join-Path $contentsPath "Info.plist") -Force

    $signingOutput = & $rcodesign sign $appPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Ad-hoc code signing failed for ${rid}:`n$($signingOutput -join [Environment]::NewLine)"
    }
    Write-Host "Ad-hoc signed $appPath"

    $architecture = if ($rid -eq "osx-arm64") { "arm64" } else { "x64" }
    $zipPath = Join-Path $artifactRoot "IsraeliAuthorStudio-macos-$architecture.zip"
    New-MacZip -AppPath $appPath -DestinationPath $zipPath
    Write-Host "Created $zipPath"
}

if (Test-Path $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
